// LiveRpcLanes.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// The 3 physical RPC lanes that drive the embedded Python InprocLiveServer
// (findings 0012 D2). The order RPCs block (place_order waits up to 40s while the
// secret is collected), so they MUST NOT share a thread with submit_secret — a single
// worker stuck inside place_order could never dequeue submit_secret and would
// deadlock. Hence three independent threads:
//
//   1. Order-write lane  — place/cancel/modify, serialized (one thread), long block.
//   2. Urgent-secret lane — submit_secret only, a SEPARATE thread so it runs WHILE the
//      order-write lane is blocked inside place_order (the GIL is released during the
//      blocking future.result(), so this lane can take it and reply).
//   3. Poll lane          — get_state_json into a latest-wins slot (badge/market state).
//
// Each lane takes Py.GIL() only around its own pythonnet calls; the Unity main thread
// stays GIL-free (it drains the sink and reads LatestState without the GIL). venue_logout
// is NOT a lane — it is lifecycle coordination (D7): LiveLogoutCoordinator gates it and
// StopAndJoin() runs the teardown sequence (stop write intake → stop secret → drain/join
// write → stop+join poll), after which the owner calls venue_logout under the GIL.
//
// The only irreducible plaintext is the transient string built at the submit_secret
// pythonnet boundary; it is never stored on a field/log, and the caller's char[] payload
// is zeroized the moment the RPC returns (D5).
using System;
using System.Collections.Concurrent;
using System.Threading;
using Python.Runtime;

public struct OrderRpcResult
{
    public bool Success;
    public string ErrorCode;
    public string Status;     // order_event.status (e.g. "FILLED"); "" if none
    public string OrderId;
}

public class LiveRpcLanes
{
    readonly PyObject _server;
    readonly LiveLogoutCoordinator _coord;
    readonly int _pollIntervalMs;

    readonly BlockingCollection<Action> _writeQueue = new BlockingCollection<Action>();
    readonly BlockingCollection<Action> _secretQueue = new BlockingCollection<Action>();
    Thread _writeThread, _secretThread, _pollThread;
    volatile bool _pollStop;
    volatile string _latestState;
    volatile string _latestPortfolio;   // #65: Replay portfolio snapshot (latest-wins, like _latestState)
    PyObject _pyNone;

    public string LatestState => _latestState;
    public string LatestPortfolio => _latestPortfolio;   // #65
    public long PollCount;   // Interlocked

    public LiveRpcLanes(PyObject server, LiveLogoutCoordinator coord, int pollIntervalMs = 50)
    {
        _server = server;
        _coord = coord;
        _pollIntervalMs = pollIntervalMs;
    }

    public void Start()
    {
        using (Py.GIL())
        using (PyObject builtins = Py.Import("builtins"))
            _pyNone = builtins.GetAttr("None");   // the None singleton (reused for None args)

        _writeThread = new Thread(WriteLane) { IsBackground = true, Name = "LiveOrderWriteLane" };
        _secretThread = new Thread(SecretLane) { IsBackground = true, Name = "LiveUrgentSecretLane" };
        _pollThread = new Thread(PollLane) { IsBackground = true, Name = "LivePollLane" };
        _writeThread.Start();
        _secretThread.Start();
        _pollThread.Start();
    }

    // ---- Order-write lane (serialized, blocking) -----------------------------------
    public void SubmitPlaceOrder(string venue, string iid, string side, double qty,
                                 double? price, string orderType, string tif,
                                 Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallPlace(venue, iid, side, qty, price, orderType, tif), onResult);

    // Shared write-lane scaffold: enqueue (or Stopped() after teardown), coord book-keeping,
    // exception → LANE_EXC, deliver. `call` does its OWN Py.GIL() marshaling — never hoist
    // PyObject construction out here (the pre-GIL hoist was the cancel-lane segfault).
    void EnqueueWrite(Func<OrderRpcResult> call, Action<OrderRpcResult> onResult)
    {
        if (_writeQueue.IsAddingCompleted) { onResult?.Invoke(Stopped()); return; }
        _writeQueue.Add(() =>
        {
            _coord.BeginWrite();
            OrderRpcResult r;
            try { r = call(); }
            catch (Exception e) { r = new OrderRpcResult { Success = false, ErrorCode = "LANE_EXC:" + e.Message }; }
            finally { _coord.EndWrite(); }
            onResult?.Invoke(r);
        });
    }

    void WriteLane()
    {
        foreach (Action task in _writeQueue.GetConsumingEnumerable()) task();
    }

    OrderRpcResult CallPlace(string venue, string iid, string side, double qty,
                             double? price, string orderType, string tif)
    {
        using (Py.GIL())
        {
            // _pyNone is the reused None singleton — never dispose it. Every other arg
            // is a fresh PyObject disposed here so the durable order path leaks no refs.
            PyObject priceArg = price.HasValue ? (PyObject)new PyFloat(price.Value) : _pyNone;
            try
            {
                using (var pv = new PyString(venue))
                using (var pi = new PyString(iid))
                using (var ps = new PyString(side))
                using (var pq = new PyFloat(qty))
                using (var pt = new PyString(orderType))
                using (var pf = new PyString(tif))
                using (PyObject res = _server.InvokeMethod("place_order", pv, pi, ps, pq, priceArg, pt, pf,
                           _pyNone /* second_secret: authoritative path uses SecretRequired, never this arg */))
                    return ParseOrderResult(res);
            }
            finally
            {
                if (price.HasValue) priceArg.Dispose();
            }
        }
    }

    static OrderRpcResult ParseOrderResult(PyObject res)
    {
        var r = new OrderRpcResult { Status = "", OrderId = "" };
        using (PyObject ok = res.GetItem("success")) r.Success = ok.As<bool>();
        using (PyObject ec = res.GetItem("error_code")) r.ErrorCode = ec.As<string>() ?? "";
        try
        {
            using (PyObject oe = res.GetItem("order_event"))
            {
                if (oe != null && !oe.IsNone())
                {
                    using (PyObject st = oe.GetItem("status")) r.Status = st.As<string>() ?? "";
                    using (PyObject oid = oe.GetItem("order_id")) r.OrderId = oid.As<string>() ?? "";
                }
            }
        }
        catch (PythonException) { /* no order_event key on this result shape */ }
        return r;
    }

    // cancel/modify share the SAME order-write lane (D2): serialized with place, and
    // (on tachibana) they also trigger SecretRequired, handled by the urgent-secret lane.
    public void SubmitCancelOrder(string venue, string orderId, Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallCancel(venue, orderId), onResult);

    OrderRpcResult CallCancel(string venue, string orderId)
    {
        // PyString construction calls PyUnicode_DecodeUTF16 → must hold the GIL. Build the args
        // INSIDE Py.GIL() like CallPlace/CallModify; hoisting them before the lock segfaults the
        // allocator (the bug this guards).
        using (Py.GIL())
        using (var pv = new PyString(venue))
        using (var po = new PyString(orderId))
        using (PyObject res = _server.InvokeMethod("cancel_order", pv, po, _pyNone))
            return ParseOrderResult(res);
    }

    public void SubmitModifyOrder(string venue, string orderId, double? newQty, double? newPrice,
                                  Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallModify(venue, orderId, newQty, newPrice), onResult);

    // #85 follow-up: piggyback EC subscription on per-ticker FD WS (e-station style).
    // subscribe_market_data is blocking (opens WS, awaits handshake) — must run off the
    // main thread. Reusing the write lane gives serial ordering against order operations
    // and the same _coord book-keeping; the call neither triggers SecretRequired nor
    // emits an OrderEvent, so we collapse its ack into OrderRpcResult.{Success,ErrorCode}.
    // (server signature is `subscribe_market_data(instrument_id)` — venue is implicit
    // via the configured live session.)
    public void SubmitSubscribeMarketData(string instrumentId,
                                          Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallSubscribeMarketData(instrumentId), onResult);

    OrderRpcResult CallSubscribeMarketData(string instrumentId)
    {
        using (Py.GIL())
        using (var pi = new PyString(instrumentId))
        using (PyObject res = _server.InvokeMethod("subscribe_market_data", pi))
        {
            var r = new OrderRpcResult { Status = "", OrderId = "" };
            using (PyObject ok = res.GetItem("success")) r.Success = ok.As<bool>();
            using (PyObject ec = res.GetItem("error_code")) r.ErrorCode = ec.As<string>() ?? "";
            return r;
        }
    }

    OrderRpcResult CallModify(string venue, string orderId, double? newQty, double? newPrice)
    {
        using (Py.GIL())
        {
            PyObject qtyArg = newQty.HasValue ? (PyObject)new PyFloat(newQty.Value) : _pyNone;
            PyObject priceArg = newPrice.HasValue ? (PyObject)new PyFloat(newPrice.Value) : _pyNone;
            try
            {
                using (var pv = new PyString(venue))
                using (var po = new PyString(orderId))
                using (PyObject res = _server.InvokeMethod("modify_order", pv, po, qtyArg, priceArg, _pyNone))
                    return ParseOrderResult(res);
            }
            finally
            {
                if (newQty.HasValue) qtyArg.Dispose();
                if (newPrice.HasValue) priceArg.Dispose();
            }
        }
    }

    // ---- Urgent-secret lane (separate thread) --------------------------------------
    // payload is owned by the caller (SecretModalController.Submit()); we zeroize it the
    // moment the RPC returns. The transient string is the irreducible pythonnet boundary.
    // Late call after teardown: the queue is CompleteAdding()'d. Don't throw — report a
    // graceful stop so a UI racing disconnect surfaces it instead of crashing.
    static OrderRpcResult Stopped() => new OrderRpcResult { Success = false, ErrorCode = "LANES_STOPPED" };

    public void SubmitSecret(string requestId, char[] payload, Action<bool> onAck)
    {
        if (_secretQueue.IsAddingCompleted)
        {
            if (payload != null) Array.Clear(payload, 0, payload.Length);
            onAck?.Invoke(false);
            return;
        }
        _secretQueue.Add(() =>
        {
            bool ok = false;
            try { ok = CallSubmitSecret(requestId, payload); }
            catch (Exception) { ok = false; }
            finally { if (payload != null) Array.Clear(payload, 0, payload.Length); }
            onAck?.Invoke(ok);
        });
    }

    void SecretLane()
    {
        foreach (Action task in _secretQueue.GetConsumingEnumerable()) task();
    }

    bool CallSubmitSecret(string requestId, char[] payload)
    {
        using (Py.GIL())
        {
            string transient = new string(payload ?? Array.Empty<char>());
            using (var rid = new PyString(requestId))
            using (var sec = new PyString(transient))
            using (PyObject res = _server.InvokeMethod("submit_secret", rid, sec))
            using (PyObject ok = res.GetItem("success"))
                return ok.As<bool>();
        }
    }

    // ---- Poll lane -----------------------------------------------------------------
    void PollLane()
    {
        while (!_pollStop)
        {
            try
            {
                using (Py.GIL())
                {
                    string state;
                    using (PyObject s = _server.InvokeMethod("get_state_json"))
                        state = s.As<string>();
                    _latestState = state;
                    // #65: in Replay, also poll the portfolio snapshot for the base panels. Kept on
                    // a SEPARATE call from the chart's get_state_json (TTWR's StateJson/Status
                    // 2-channel split — findings 0044 §2/§7-a), under the same GIL hold so a bar
                    // never advances mid-pair. Gated to Replay: Live panels are driven by
                    // _host.Panel (LivePanelViewModel); execution_mode is the only field whose value
                    // is the quoted literal "Replay". Match the KEY+value pair so a stray string field
                    // that happened to equal "Replay" can't false-trigger (pydantic dumps with no
                    // spaces, so the exact "execution_mode":"Replay" pair is reliable).
                    if (state != null && state.Contains("\"execution_mode\":\"Replay\""))
                        using (PyObject p = _server.InvokeMethod("get_portfolio_json"))
                            _latestPortfolio = p.As<string>();
                }
                Interlocked.Increment(ref PollCount);
            }
            catch (Exception) { /* transient; keep polling until stopped */ }
            Thread.Sleep(_pollIntervalMs);
        }
    }

    // ---- D7 teardown sequence ------------------------------------------------------
    // Stop new write intake → stop secret intake → drain+join write → drain+join secret
    // → stop+join poll. The owner then calls venue_logout under the GIL (no lane is left
    // touching _server). Returns only after all three lane threads have joined.
    /// Returns true ONLY if all three lane threads exited within joinMs. The caller MUST
    /// check this: a false means a lane (e.g. a place still blocked on a secret) is still
    /// alive, so running venue_logout would race the live write — do not proceed on false.
    public bool StopAndJoin(int joinMs = 8000)
    {
        _writeQueue.CompleteAdding();
        _secretQueue.CompleteAdding();
        bool w = _writeThread == null || _writeThread.Join(joinMs);
        bool s = _secretThread == null || _secretThread.Join(joinMs);
        _pollStop = true;
        bool p = _pollThread == null || _pollThread.Join(joinMs);
        return w && s && p;
    }
}
