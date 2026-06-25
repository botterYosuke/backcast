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
using System.Collections.Generic;
using System.Threading;
using Python.Runtime;

public struct OrderRpcResult
{
    public bool Success;
    public string ErrorCode;
    public string Status;     // order_event.status (e.g. "FILLED"); "" if none
    public string OrderId;
}

// #34 (findings 0101 D1): get_orders の 1 行（resting 注文一覧）。symbol/side/qty/price は
// #29 Slice3a の静的属性、filled_qty は約定済み（訂正の減数下限）。price は MARKET で HasPrice=false。
public struct RestingOrderRpcRow
{
    public string OrderId;
    public string Symbol;
    public string Side;
    public double Qty;
    public double FilledQty;
    public bool HasPrice;
    public double Price;
}

public struct OrdersRpcResult
{
    public bool Success;
    public string ErrorCode;
    public System.Collections.Generic.List<RestingOrderRpcRow> Orders;
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
    volatile string _latestRunSummary;  // #100 Slice ① (findings 0077): Replay run summary snapshot
    PyObject _pyNone;

    public string LatestState => _latestState;
    public string LatestPortfolio => _latestPortfolio;   // #65
    public string LatestRunSummary => _latestRunSummary; // #100 Slice ①
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

    // OrderRpcResult-typed shim over the generic scaffold (below). Stopped() と LANE_EXC は
    // どちらも {Success=false, ErrorCode=<code>} なので uniform fail() で両方を厳密に再現する。
    void EnqueueWrite(Func<OrderRpcResult> call, Action<OrderRpcResult> onResult)
        => EnqueueWrite<OrderRpcResult>(call,
            code => new OrderRpcResult { Success = false, ErrorCode = code },
            onResult);

    // Base write-lane scaffold for all order/read RPCs (#34 get_orders et al.): enqueue (or
    // fail("LANES_STOPPED") after teardown), coord book-keeping, exception → fail("LANE_EXC:"+msg),
    // deliver. Typed via `fail(code)`; the non-generic EnqueueWrite(OrderRpcResult) shim above
    // delegates here. `call` does its OWN Py.GIL() marshaling — never hoist PyObject construction
    // out here (the pre-GIL hoist was the cancel-lane segfault).
    void EnqueueWrite<T>(Func<T> call, Func<string, T> fail, Action<T> onResult)
    {
        if (_writeQueue.IsAddingCompleted) { onResult?.Invoke(fail("LANES_STOPPED")); return; }
        _writeQueue.Add(() =>
        {
            _coord.BeginWrite();
            T r;
            try { r = call(); }
            catch (Exception e) { r = fail("LANE_EXC:" + e.Message); }
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

    // #107: LiveManual 突入時の universe 一括購読を engine 側で逐次 gather する batch RPC 1 回に畳む
    // (subscribe_market_data_batch・方針 ADR-0022)。狙いは (1) kabu の累積 re-register を N 個別 RPC の
    // O(N²) から O(N) に減らす、(2) register rate-limit gate を engine 側でまとめて尊重し burst(4001006)を
    // 避けること。⚠️ batch は他の order RPC と同じ write lane で 1 タスクとして走る＝その間 place/cancel は
    // 待つ（_coord book-keeping を order と共有するため。突入直後の一括購読なので許容）。venue 実上限は
    // per-id typed エラーで surface するが、ここでは aggregate の success/error_code だけを OrderRpcResult に畳む。
    public void SubmitSubscribeMarketDataBatch(IReadOnlyList<string> instrumentIds,
                                               Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallSubscribeMarketDataBatch(instrumentIds), onResult);

    OrderRpcResult CallSubscribeMarketDataBatch(IReadOnlyList<string> instrumentIds)
    {
        using (Py.GIL())
        using (var lst = new PyList())
        {
            if (instrumentIds != null)
                foreach (string id in instrumentIds)
                    using (var s = new PyString(id ?? ""))
                        lst.Append(s);
            using (PyObject res = _server.InvokeMethod("subscribe_market_data_batch", lst))
            {
                var r = new OrderRpcResult { Status = "", OrderId = "" };
                using (PyObject ok = res.GetItem("success")) r.Success = ok.As<bool>();
                using (PyObject ec = res.GetItem("error_code")) r.ErrorCode = ec.As<string>() ?? "";
                return r;
            }
        }
    }

    // ADR-0031 S5 (#145): remove → venue unsubscribe. Blocking (closes the WS / re-registers the
    // reduced set), so it rides the write lane like subscribe — serial against orders, same _coord
    // book-keeping. server signature `unsubscribe_market_data(instrument_id)`; the engine drops the
    // price/depth caches + reducer per-id state (orchestrator D20/A0).
    public void SubmitUnsubscribeMarketData(string instrumentId,
                                            Action<OrderRpcResult> onResult)
        => EnqueueWrite(() => CallUnsubscribeMarketData(instrumentId), onResult);

    OrderRpcResult CallUnsubscribeMarketData(string instrumentId)
    {
        using (Py.GIL())
        using (var pi = new PyString(instrumentId))
        using (PyObject res = _server.InvokeMethod("unsubscribe_market_data", pi))
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

    // #34 (findings 0101 D1): resting 注文一覧 read (get_orders)。orchestrator.get_orders は
    // facade.list_orders ＋ adapter.fetch_working_orders をマージするので blocking ＝ write lane に
    // 載せて order 操作と直列化する（subscribe_market_data と同じ理由）。OrderEvent ではなく list を
    // 返すので専用 result 型 + dispatch helper を持つ。callback は write lane スレッドで走るので、
    // 受け手（root）は volatile に stash し main スレッドの Drive で uGUI に反映する（place/cancel と同型）。
    public void SubmitGetOrders(string venue, Action<OrdersRpcResult> onResult)
        => EnqueueWrite(
            () => CallGetOrders(venue),
            code => new OrdersRpcResult { Success = false, ErrorCode = code, Orders = new List<RestingOrderRpcRow>() },
            onResult);

    OrdersRpcResult CallGetOrders(string venue)
    {
        using (Py.GIL())
        using (var pv = new PyString(venue))
        using (PyObject res = _server.InvokeMethod("get_orders", pv))
        {
            var r = new OrdersRpcResult { Orders = new List<RestingOrderRpcRow>() };
            using (PyObject ok = res.GetItem("success")) r.Success = ok.As<bool>();
            using (PyObject ec = res.GetItem("error_code")) r.ErrorCode = ec.As<string>() ?? "";
            using (PyObject orders = res.GetItem("orders"))
            {
                int n = (int)orders.Length();
                for (int i = 0; i < n; i++)
                    using (PyObject o = orders.GetItem(i))
                        r.Orders.Add(ParseOrderRow(o));
            }
            return r;
        }
    }

    static RestingOrderRpcRow ParseOrderRow(PyObject o)
    {
        var row = new RestingOrderRpcRow { OrderId = "", Symbol = "", Side = "" };
        using (PyObject v = o.GetItem("order_id")) row.OrderId = v.As<string>() ?? "";
        using (PyObject v = o.GetItem("symbol")) row.Symbol = v.As<string>() ?? "";
        using (PyObject v = o.GetItem("side")) row.Side = v.As<string>() ?? "";
        using (PyObject v = o.GetItem("qty")) row.Qty = v.As<double>();
        using (PyObject v = o.GetItem("filled_qty")) row.FilledQty = v.As<double>();
        using (PyObject v = o.GetItem("price"))
        {
            // price は MARKET 注文等で None。None なら HasPrice=false。
            row.HasPrice = !v.IsNone();
            row.Price = row.HasPrice ? v.As<double>() : 0.0;
        }
        return row;
    }

    // ---- Urgent-secret lane (separate thread) --------------------------------------
    // payload is owned by the caller (SecretModalController.Submit()); we zeroize it the
    // moment the RPC returns. The transient string is the irreducible pythonnet boundary.
    // Late call after teardown: the queue is CompleteAdding()'d. Don't throw — report a
    // graceful stop so a UI racing disconnect surfaces it instead of crashing.

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
                    {
                        using (PyObject p = _server.InvokeMethod("get_portfolio_json"))
                            _latestPortfolio = p.As<string>();
                        // #100 Slice ① (findings 0077): poll the run-summary alongside the
                        // portfolio under the SAME GIL hold so the running view (counts +
                        // realized/unrealized) and the full-stats view (fills/sharpe/dd) never
                        // disagree at a bar boundary.  Honest-empty ("" ) until the per-cell bt
                        // run finalizes — the C# tile decoder treats null/empty as "running view"
                        // (no full-stats), so a stale prior run never re-renders on a re-press.
                        using (PyObject rs = _server.InvokeMethod("get_run_summary_json"))
                            _latestRunSummary = rs.As<string>();
                    }
                }
                Interlocked.Increment(ref PollCount);
            }
            catch (Exception) { /* transient; keep polling until stopped */ }
            Thread.Sleep(_pollIntervalMs);
        }
    }

    // #100 Slice ① (findings 0077): document-boundary reset for File→New / File→Open.  Clears the
    // polled snapshots locally so the next 50 ms gap between user gesture and the next poll renders
    // honest-empty (instead of the prior doc's last polled values).  The backend-side reset is done
    // by WorkspaceEngineHost.ClearReplayRunView calling the clear_run_view RPC under the GIL —
    // this method is the lane-side mirror, callable from the UI thread without the GIL.
    public void ResetReplaySnapshot()
    {
        _latestPortfolio = null;
        _latestRunSummary = null;
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
