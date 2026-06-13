// LiveAdapterTracerProbe.cs — editor-only throwaway AFK gate for #20 "Live adapter tracer"
// docs/findings/0011-live-adapter-tracer.md (D1–D6). Live analogue of #9's S1 replay seam tracer.
//
// Proves the full LIVE backend-event seam under Unity Mono + pythonnet, mock venue, NO real venue:
//   * C# is the lifecycle OWNER (D2): the drive worker calls the InprocLiveServer façade
//     individually (register_live_strategy → venue_login → set_execution_mode(LiveAuto) →
//     start_live_strategy → … → stop_live_strategy → venue_logout → close). Mock injection
//     (kline/fill-outcome/depth/account) goes through the throwaway helper spike.live_adapter
//     .mock_inject; the production façade is NOT extended with mock-only methods.
//   * D1 push path: the engine's production publish_backend_event → _backend_event_to_wire_dict
//     (externally-tagged) → push_json(bytes) lands on the C# LiveBackendEventSink. Main drains the
//     GIL-free ConcurrentQueue into LivePanelViewModel via LiveBackendEventDecoder and VALUE-asserts
//     OrderEvent FILLED×2 / AccountEvent position / LiveStrategyEvent / LiveStrategyTelemetry.
//   * D4: main NEVER takes the GIL (BeginAllowThreads); a heartbeat tracks the worst stall (< 200ms).
//   * D5: a separate poll worker calls InprocLiveServer.get_state_json() (latest-wins string slot);
//     main GIL-free-decodes the kline-derived last price + DepthCache-derived bid/ask for the
//     instrument. push and poll are distinct channels.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod LiveAdapterTracerProbe.Run
//
// Exit 0 => PASS ([LIVE ADAPTER TRACER PASS]), 1 => FAIL (self-failing gate). The real-GPU panel
// render leg is a separate owner-only default-disabled playmode harness (not this AFK gate).

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class LiveAdapterTracerProbe
{
    const string IID          = "8918.TSE";
    const long   MAX_STALL_MS = 200;
    const double EPS          = 1e-6;

    static readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    static readonly LivePanelViewModel   _vm   = new LivePanelViewModel();

    // created on the drive worker under the GIL; read by poll + teardown. NOT disposed.
    static volatile PyObject _server;
    static volatile PyObject _mi;
    static volatile bool   _serverReady;
    static volatile string _driveError;
    static volatile bool   _driveDone;
    static volatile string _runId;

    // Medium-1: stop_live_strategy is a GATED assertion, not fire-and-forget.
    static volatile bool   _stopAcked;   // stop returned an ACK (no exception)
    static volatile bool   _stopAckOk;   // that ACK had success=true
    static volatile string _stopError;   // ACK failure / exception detail

    static volatile bool   _stopPolling;
    static volatile bool   _pollStopped;
    static volatile string _pollError;
    static volatile string _latestStateJson;

    static long _appliedFills;                 // Interlocked: main publishes, drive worker reads
    static volatile bool _accountPositionSeen;
    static volatile bool _depthSeen;

    // real-value captures (written only by main in Pump())
    static double _acctPosQty = double.NaN;
    static string _acctPosSymbol;
    static double _msPrice = double.NaN, _msBid = double.NaN, _msAsk = double.NaN;

    static IntPtr _mainThreadState;

    public static void Run()
    {
        bool passed = false;
        bool engineStarted = false;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            _mainThreadState = PythonEngine.BeginAllowThreads();
            Debug.Log("[LIVE ADAPTER TRACER MARK] initialized; main GIL-free; starting drive + poll workers");

            var drive = new Thread(DriveWorker) { IsBackground = true, Name = "LiveTracerDrive" };
            var poll  = new Thread(PollWorker)  { IsBackground = true, Name = "LiveTracerPoll" };
            drive.Start();
            poll.Start();

            long maxStall = HeartbeatUntil(() => _driveDone, 120000);

            // Medium-2: HeartbeatUntil returns either because the drive worker finished
            // (_driveDone) OR because its 120s budget expired. A blocking Join AFTER budget
            // expiry would stall main OUTSIDE the measured heartbeat and hide that stall from
            // maxStall. So: treat budget-expiry as FAIL, and keep BOTH joins short/non-blocking
            // (when _driveDone is set the worker is already at its last line -> joins return at
            // once; all real waiting stayed inside the measured heartbeat loop).
            bool heartbeatTimedOut = !_driveDone;
            bool dj = drive.Join(heartbeatTimedOut ? 0 : 2000);
            _stopPolling = true;
            bool pj = poll.Join(heartbeatTimedOut ? 0 : 5000);

            Pump();   // final tail drain + state decode (GIL-free)

            string why = Validate(dj, pj, maxStall, heartbeatTimedOut);
            if (why != null)
            {
                Debug.LogError("[LIVE ADAPTER TRACER FAIL] " + why);
            }
            else
            {
                passed = true;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[LIVE ADAPTER TRACER PASS] fills={0} acct={1}x{2} telem(realized={3},fills={4}) " +
                    "lifecycle={5} state(price={6},bid={7},ask={8}) maxStall={9}ms — C# drove " +
                    "InprocLiveServer facade; backend_events drained GIL-free; market-state polled (mock LiveAuto)",
                    _vm.FilledOrderCount, _acctPosSymbol, _acctPosQty,
                    _vm.LatestTelemetry.RealizedPnl, _vm.LatestTelemetry.FillCount,
                    _vm.LifecycleCount, _msPrice, _msBid, _msAsk, maxStall));
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[LIVE ADAPTER TRACER FAIL] driver: " + e);
        }
        finally
        {
            try
            {
                if (engineStarted && _driveDone)
                {
                    PythonEngine.EndAllowThreads(_mainThreadState);
                    PythonEngine.Shutdown();
                    Debug.Log("[LIVE ADAPTER TRACER MARK] PythonEngine.Shutdown OK (clean teardown)");
                }
                else if (engineStarted)
                {
                    Debug.LogWarning("[LIVE ADAPTER TRACER] drive worker did not finish; skipping Shutdown to avoid GIL deadlock");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LIVE ADAPTER TRACER] shutdown cleanup: " + e);
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Drive worker: owns the live lifecycle by calling the InprocLiveServer façade directly,
    // injecting mock events through the throwaway helper. Holds the GIL only inside each
    // using(Py.GIL()) block; between blocks it releases the GIL so the engine's asyncio
    // live-loop thread can run the scheduled callbacks and push_json into the C# sink.
    static void DriveWorker()
    {
        try
        {
            using (Py.GIL())
            {
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sp = sys.GetAttr("path"))
                {
                    sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }

                PyObject de;
                using (PyObject coreMod = Py.Import("engine.core"))
                using (PyObject deCls = coreMod.GetAttr("DataEngine"))
                    de = deCls.Invoke();

                using (PyObject sinkPy = PyObject.FromManagedObject(_sink))
                    de.InvokeMethod("set_rust_event_sink", sinkPy).Dispose();

                using (PyObject inproc = Py.Import("engine.inproc_server"))
                using (PyObject srvCls = inproc.GetAttr("InprocLiveServer"))
                    _server = srvCls.Invoke(de, new PyString("MOCK"));
                de.Dispose();

                _mi = Py.Import("spike.live_adapter.mock_inject");
                string twin = _mi.GetAttr("TWIN_PATH").As<string>();
                _serverReady = true;

                string strategyId;
                using (PyObject reg = _server.InvokeMethod("register_live_strategy", new PyString(twin), new PyString(twin)))
                {
                    if (!reg["success"].As<bool>()) { _driveError = "register_live_strategy failed: " + reg.ToString(); return; }
                    strategyId = reg["strategy_id"].As<string>();
                }
                using (PyObject login = _server.InvokeMethod("venue_login", new PyString("MOCK"), new PyString("env"), new PyString("")))
                    if (!login["success"].As<bool>()) { _driveError = "venue_login failed: " + login.ToString(); return; }
                using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveAuto")))
                    if (!mode["success"].As<bool>()) { _driveError = "set_execution_mode(LiveAuto) failed: " + mode.ToString(); return; }

                _mi.InvokeMethod("arm_order", _server, new PyString("FILLED"), new PyFloat(100.0), new PyFloat(8.0)).Dispose();
                using (PyObject start = _server.InvokeMethod("start_live_strategy", new PyString(strategyId), new PyString(IID), new PyString("MOCK")))
                {
                    if (!start["success"].As<bool>()) { _driveError = "start_live_strategy failed: " + start.ToString(); return; }
                    _runId = start["run_id"].As<string>();
                }
                for (int i = 1; i < 4; i++)
                    _mi.InvokeMethod("inject_kline", _server, new PyInt(i), new PyFloat(8.0)).Dispose();
            }

            if (!WaitFills(1, 15000)) { if (_driveError == null) _driveError = "timeout waiting for 1st fill"; return; }

            using (Py.GIL())
            {
                _mi.InvokeMethod("arm_order", _server, new PyString("FILLED"), new PyFloat(100.0), new PyFloat(10.0)).Dispose();
                for (int i = 4; i < 41; i++)
                    _mi.InvokeMethod("inject_kline", _server, new PyInt(i), new PyFloat(10.0)).Dispose();
            }

            if (!WaitFills(2, 15000)) { if (_driveError == null) _driveError = "timeout waiting for 2nd fill"; return; }

            using (Py.GIL())
            {
                _mi.InvokeMethod("emit_depth", _server, new PyInt(41), new PyFloat(9.9), new PyFloat(10.1)).Dispose();
                _mi.InvokeMethod("arm_account_position", _server,
                    new PyFloat(9000000.0), new PyFloat(9000000.0),
                    new PyString("8918"), new PyInt(100), new PyFloat(8.0), new PyFloat(200.0)).Dispose();
                _server.InvokeMethod("force_account_snapshot").Dispose();
            }

            // wait until main has decoded the market-state depth AND the account position (pre-teardown)
            WaitFlag(() => _depthSeen && _accountPositionSeen, 10000);
        }
        catch (Exception e)
        {
            if (_driveError == null) _driveError = "drive: " + e;
        }
        finally
        {
            // Stop the poll worker BEFORE teardown so get_state_json cannot race close().
            _stopPolling = true;
            WaitFlag(() => _pollStopped, 5000);
            if (_server != null)
            {
                try
                {
                    using (Py.GIL())
                    {
                        // Medium-1: capture stop's ACK success + any exception so Validate() can
                        // require a real STOPPED (run reached STOPPED + run_id match). Re-marshalling
                        // stop back to fire-and-forget must now FAIL the gate, not silently pass.
                        if (_runId != null)
                        {
                            try
                            {
                                using (PyObject stop = _server.InvokeMethod("stop_live_strategy", new PyString(_runId)))
                                {
                                    _stopAcked = true;
                                    _stopAckOk = stop["success"].As<bool>();
                                    if (!_stopAckOk) _stopError = "stop_live_strategy ACK success=false: " + stop.ToString();
                                }
                            }
                            catch (Exception e) { _stopError = "stop_live_strategy threw: " + e; }
                        }
                        else { _stopError = "no run_id captured before teardown; stop_live_strategy not called"; }
                        try { _server.InvokeMethod("set_execution_mode", new PyString("Replay")).Dispose(); } catch {}
                        try { _server.InvokeMethod("venue_logout").Dispose(); } catch {}
                        try { _server.InvokeMethod("close").Dispose(); } catch {}
                    }
                }
                catch (Exception e) { Debug.LogWarning("[LIVE ADAPTER TRACER] teardown: " + e); }
            }
            _driveDone = true;
        }
    }

    // Poll worker (D5): latest-wins get_state_json string slot. Takes the GIL only for the call;
    // main decodes GIL-free. Continues on a transient poll error (records it for diagnostics).
    static void PollWorker()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!_serverReady && sw.ElapsedMilliseconds < 30000 && _driveError == null && !_stopPolling)
                Thread.Sleep(5);

            while (!_stopPolling)
            {
                if (_serverReady && _server != null)
                {
                    try
                    {
                        using (Py.GIL())
                        using (PyObject s = _server.InvokeMethod("get_state_json"))
                            _latestStateJson = s.As<string>();
                    }
                    catch (Exception e)
                    {
                        _pollError = "poll: " + e;
                    }
                }
                Thread.Sleep(40);
            }
        }
        catch (Exception e)
        {
            _pollError = "poll: " + e;
        }
        finally
        {
            _pollStopped = true;
        }
    }

    // GIL-free on main: drain the sink into the view-model + decode the latest polled state.
    static void Pump()
    {
        while (_sink.TryDequeue(out string j))
        {
            _vm.Apply(j);
            if (_vm.HasAccount && _vm.LatestAccount.Positions != null && _vm.LatestAccount.Positions.Count > 0)
            {
                _accountPositionSeen = true;
                _acctPosSymbol = _vm.LatestAccount.Positions[0].symbol;
                _acctPosQty    = _vm.LatestAccount.Positions[0].qty;
            }
        }
        Interlocked.Exchange(ref _appliedFills, _vm.FilledOrderCount);

        string s = _latestStateJson;
        if (!string.IsNullOrEmpty(s))
        {
            double p   = LastPrice(s);
            double bid = DepthTop(s, "bids");
            double ask = DepthTop(s, "asks");
            if (!double.IsNaN(p)   && p   > 0) _msPrice = p;
            if (!double.IsNaN(bid) && bid > 0) _msBid   = bid;
            if (!double.IsNaN(ask) && ask > 0) _msAsk   = ask;
            if (_msPrice > 0 && _msBid > 0 && _msAsk > 0) _depthSeen = true;
        }
    }

    static long HeartbeatUntil(Func<bool> done, int budgetMs)
    {
        var total = Stopwatch.StartNew();
        var beat  = Stopwatch.StartNew();
        long maxStall = 0;
        while (!done() && total.ElapsedMilliseconds < budgetMs)
        {
            long gap = beat.ElapsedMilliseconds;
            if (gap > maxStall) maxStall = gap;
            beat.Restart();
            Pump();
            Thread.Sleep(2);
        }
        return maxStall;
    }

    static bool WaitFills(int n, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (_driveError != null) return false;
            if (Interlocked.Read(ref _appliedFills) >= n) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    static bool WaitFlag(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    static string Validate(bool driveJoined, bool pollJoined, long maxStall, bool heartbeatTimedOut)
    {
        if (_driveError != null) return _driveError;
        if (heartbeatTimedOut)
            return $"heartbeat budget (120s) expired before the drive worker completed — main-stall " +
                   $"window closed (maxStall so far={maxStall}ms); refusing to hide a stall in a blocking join";
        if (!driveJoined)        return "drive worker did not join within budget";
        if (_vm.FilledOrderCount < 2) return $"FilledOrderCount={_vm.FilledOrderCount} < 2";
        if (!_vm.HasOrder || _vm.LatestOrder.Status != "FILLED")
            return $"latest order not FILLED (status='{_vm.LatestOrder.Status}')";
        if (Math.Abs(_vm.LatestOrder.FilledQty - 100.0) > EPS)
            return $"latest order FilledQty={_vm.LatestOrder.FilledQty} != 100";
        if (!_accountPositionSeen) return "no AccountEvent with a position was decoded";
        if (_acctPosSymbol != "8918") return $"account position symbol='{_acctPosSymbol}' != 8918";
        if (Math.Abs(_acctPosQty - 100.0) > EPS) return $"account position qty={_acctPosQty} != 100";
        if (!_vm.HasTelemetry) return "no LiveStrategyTelemetry decoded";
        if (Math.Abs(_vm.LatestTelemetry.RealizedPnl - 200.0) > EPS)
            return $"telemetry realized_pnl={_vm.LatestTelemetry.RealizedPnl} != 200";
        if (_vm.LatestTelemetry.FillCount != 2)
            return $"telemetry fill_count={_vm.LatestTelemetry.FillCount} != 2";
        if (!_vm.HasLifecycle || _vm.LifecycleCount < 1)
            return $"no LiveStrategyEvent lifecycle decoded (count={_vm.LifecycleCount})";
        // Medium-1: stop must be DRIVEN + ACKed, and the run must actually reach STOPPED (final
        // lifecycle event via backend_events) with the run_id matching the started run. A gate
        // that only checks start-time events would pass even if C# stopped marshalling stop.
        if (!_stopAcked) return "stop_live_strategy returned no ACK (exception or skipped): " + (_stopError ?? "unknown");
        if (!_stopAckOk) return _stopError ?? "stop_live_strategy ACK success=false";
        if (_vm.LatestLifecycle.Status != "STOPPED")
            return $"final lifecycle status='{_vm.LatestLifecycle.Status}' != STOPPED (stop not reflected through backend_events)";
        if (_vm.LatestLifecycle.RunId != _runId)
            return $"final lifecycle run_id='{_vm.LatestLifecycle.RunId}' != started run_id '{_runId}'";
        if (!_depthSeen)
            return "market-state poll never yielded price+bid+ask (pollError=" + (_pollError ?? "none") + ")";
        if (Math.Abs(_msPrice - 10.0) > EPS) return $"market-state last_price={_msPrice} != 10.0 (kline-derived)";
        if (Math.Abs(_msBid - 9.9)   > EPS) return $"market-state bid={_msBid} != 9.9 (DepthCache-derived)";
        if (Math.Abs(_msAsk - 10.1)  > EPS) return $"market-state ask={_msAsk} != 10.1 (DepthCache-derived)";
        if (maxStall >= MAX_STALL_MS)
            return $"main heartbeat stalled {maxStall}ms (>= {MAX_STALL_MS}ms) — main must stay GIL-free";
        if (!pollJoined) return "poll worker did not join (teardown ordering)";
        return null;
    }

    // ---- throwaway string extractors for get_state_json (JsonUtility cannot bind its
    // instrument-id-keyed dicts; we read the few fixed values the D5 assertion needs) ----

    static double LastPrice(string s)
    {
        int sec = s.IndexOf("\"last_prices\"", StringComparison.Ordinal);
        if (sec < 0) return double.NaN;
        return NumAfter(s, sec, "\"" + IID + "\"");
    }

    static double DepthTop(string s, string sideKey)
    {
        int pi = s.IndexOf("\"per_instrument\"", StringComparison.Ordinal);
        if (pi < 0) return double.NaN;
        int depth = s.IndexOf("\"depth\"", pi, StringComparison.Ordinal);
        if (depth < 0) return double.NaN;
        int side = s.IndexOf("\"" + sideKey + "\"", depth, StringComparison.Ordinal);
        if (side < 0) return double.NaN;
        return NumAfter(s, side, "\"price\"");
    }

    // Finds `key` at/after `from`, then parses the JSON number that follows the ':'.
    static double NumAfter(string s, int from, string key)
    {
        int i = s.IndexOf(key, from, StringComparison.Ordinal);
        if (i < 0) return double.NaN;
        i += key.Length;
        while (i < s.Length && (s[i] == ':' || s[i] == ' ' || s[i] == '"')) i++;
        int start = i;
        while (i < s.Length)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E') i++;
            else break;
        }
        if (i == start) return double.NaN;
        return double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : double.NaN;
    }
}
