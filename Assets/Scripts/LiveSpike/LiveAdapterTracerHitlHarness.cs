// LiveAdapterTracerHitlHarness.cs — issue #20 "Live adapter tracer" (THROWAWAY playmode render leg)
//
// The owner-only, DEFAULT-DISABLED real-GPU render leg that complements the headless AFK gate
// (LiveAdapterTracerProbe). The probe proves the backend-event seam under -batchmode -nographics;
// this harness lets the owner SEE the decoded Live panel values + polled market-state render on a
// real Unity frame. It runs the SAME C#-owned InprocLiveServer façade drive + mock injection the
// probe runs (findings 0011 D1–D5), but instead of asserting + exiting it renders the decoded
// LivePanelViewModel + market-state via OnGUI each frame.
//
// PLAY OWNERSHIP: spawned ONLY via Tools > Backcast > Live Adapter Tracer HITL (no auto-bootstrap),
// so it never collides with the single Play owner (mirrors StrategyEditorHitlMenu). Enter Play mode,
// run the menu, watch the panel populate (fills=2, position 8918x100, telemetry realized=200,
// market-state price/bid/ask), then exit Play. Mirrors S2SpikeLiveLoopHarness GIL discipline:
// the workers take the GIL, main renders GIL-free.
using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class LiveAdapterTracerHitlHarness : MonoBehaviour
{
    const string IID = "8918.TSE";

    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    readonly LivePanelViewModel   _vm   = new LivePanelViewModel();

    volatile PyObject _server;
    volatile PyObject _mi;
    volatile bool   _serverReady;
    volatile bool   _stopPolling;
    volatile bool   _pollStopped;
    volatile bool   _driveDone;
    volatile string _driveError;
    volatile string _pollError;
    volatile string _runId;
    volatile string _status = "starting…";
    volatile string _latestStateJson;

    long _appliedFills;
    volatile bool _accountPositionSeen;
    volatile bool _depthSeen;

    // written + read on main only (Pump in Update, read in OnGUI)
    string _acctPosSymbol = "-";
    double _acctPosQty = double.NaN, _msPrice = double.NaN, _msBid = double.NaN, _msAsk = double.NaN;

    Thread _drive, _poll;
    IntPtr _mainThreadState;
    bool _engineStarted;

    void Start()
    {
        try
        {
            // Medium-3: SINGLE PLAY-OWNER guard (mirrors ReplayPanelsHarness §4). The menu only
            // prevents a SECOND harness of this type in the same Play; it does NOT stop a foreign
            // bootstrap (e.g. ReplayPanelsHarness, or a prior harness that left the interpreter up)
            // from already owning the interpreter. A second PythonEngine.Initialize() on top of that
            // double-inits / contends the GIL. So if Python is already initialized, FAIL LOUDLY
            // instead of silently piling on. (Unlike ReplayPanelsHarness this harness Shuts the
            // interpreter down on clean teardown, so it expects to be the sole owner each run and
            // needs no s_pythonBootstrapped reuse branch.)
            if (PythonEngine.IsInitialized)
            {
                _driveError = "double-init: PythonEngine already initialized by another bootstrap " +
                              "(only one Play-owner allowed; is ReplayPanelsHarness or another harness running?)";
                _status = "ERROR: " + _driveError;
                Debug.LogError("[LIVE ADAPTER TRACER HITL FAIL] " + _driveError);
                return;
            }

            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;
            _mainThreadState = PythonEngine.BeginAllowThreads();

            _drive = new Thread(DriveWorker) { IsBackground = true, Name = "LiveTracerHitlDrive" };
            _poll  = new Thread(PollWorker)  { IsBackground = true, Name = "LiveTracerHitlPoll" };
            _drive.Start();
            _poll.Start();
            Debug.Log("[LIVE ADAPTER TRACER HITL] Initialize OK; workers started; main renders GIL-free.");
        }
        catch (Exception e)
        {
            _driveError = "init: " + e;
            _status = "ERROR: " + e;
            Debug.LogError("[LIVE ADAPTER TRACER HITL FAIL] init: " + e);
        }
    }

    void Update()
    {
        Pump();
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = false };
        string text =
            "Live Adapter Tracer HITL — mock venue / LiveAuto (backcast #20)\n" +
            "status: " + _status + "\n\n" +
            $"order fills (FILLED): {_vm.FilledOrderCount}\n" +
            (_vm.HasOrder
                ? $"latest order: status={_vm.LatestOrder.Status} qty={_vm.LatestOrder.FilledQty} @ {_vm.LatestOrder.AvgPrice} (strat={_vm.LatestOrder.StrategyId})\n"
                : "latest order: (none yet)\n") +
            $"account position: {_acctPosSymbol} x {_acctPosQty}\n" +
            (_vm.HasTelemetry
                ? $"telemetry: realized={_vm.LatestTelemetry.RealizedPnl} unrealized={_vm.LatestTelemetry.UnrealizedPnl} orders={_vm.LatestTelemetry.OrderCount} fills={_vm.LatestTelemetry.FillCount}\n"
                : "telemetry: (none yet)\n") +
            $"lifecycle events: {_vm.LifecycleCount}" + (_vm.HasLifecycle ? $" (latest status={_vm.LatestLifecycle.Status})\n" : "\n") +
            $"market-state: price={_msPrice}  bid={_msBid}  ask={_msAsk}\n\n" +
            "(backend_events drained GIL-free on main; market-state via a separate poll worker)";
        GUI.Label(new Rect(20, 20, 1000, 460), text, style);
    }

    // ---- drive: C# owns the live lifecycle via the InprocLiveServer façade (D2) ----
    void DriveWorker()
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
                _status = "logging in…";

                string strategyId;
                using (PyObject reg = _server.InvokeMethod("register_live_strategy", new PyString(twin), new PyString(twin)))
                {
                    if (!reg["success"].As<bool>()) { _driveError = "register failed: " + reg.ToString(); return; }
                    strategyId = reg["strategy_id"].As<string>();
                }
                using (PyObject login = _server.InvokeMethod("venue_login", new PyString("MOCK"), new PyString("env"), new PyString("")))
                    if (!login["success"].As<bool>()) { _driveError = "venue_login failed: " + login.ToString(); return; }
                using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveAuto")))
                    if (!mode["success"].As<bool>()) { _driveError = "set_execution_mode failed: " + mode.ToString(); return; }

                _status = "running: buy phase (inject @8.0)";
                _mi.InvokeMethod("arm_order", _server, new PyString("FILLED"), new PyFloat(100.0), new PyFloat(8.0)).Dispose();
                using (PyObject start = _server.InvokeMethod("start_live_strategy", new PyString(strategyId), new PyString(IID), new PyString("MOCK")))
                {
                    if (!start["success"].As<bool>()) { _driveError = "start failed: " + start.ToString(); return; }
                    _runId = start["run_id"].As<string>();
                }
                for (int i = 1; i < 4; i++)
                    _mi.InvokeMethod("inject_kline", _server, new PyInt(i), new PyFloat(8.0)).Dispose();
            }

            if (!WaitFills(1, 15000)) { if (_driveError == null) _driveError = "timeout waiting 1st fill"; return; }

            _status = "running: sell phase (inject @10.0)";
            using (Py.GIL())
            {
                _mi.InvokeMethod("arm_order", _server, new PyString("FILLED"), new PyFloat(100.0), new PyFloat(10.0)).Dispose();
                for (int i = 4; i < 41; i++)
                    _mi.InvokeMethod("inject_kline", _server, new PyInt(i), new PyFloat(10.0)).Dispose();
            }

            if (!WaitFills(2, 15000)) { if (_driveError == null) _driveError = "timeout waiting 2nd fill"; return; }

            _status = "emitting depth + account snapshot…";
            using (Py.GIL())
            {
                _mi.InvokeMethod("emit_depth", _server, new PyInt(41), new PyFloat(9.9), new PyFloat(10.1)).Dispose();
                _mi.InvokeMethod("arm_account_position", _server,
                    new PyFloat(9000000.0), new PyFloat(9000000.0),
                    new PyString("8918"), new PyInt(100), new PyFloat(8.0), new PyFloat(200.0)).Dispose();
                _server.InvokeMethod("force_account_snapshot").Dispose();
            }

            WaitFlag(() => _depthSeen && _accountPositionSeen, 10000);
            _status = "roundtrip complete — tearing down…";
        }
        catch (Exception e)
        {
            if (_driveError == null) _driveError = "drive: " + e;
        }
        finally
        {
            _stopPolling = true;
            WaitFlag(() => _pollStopped, 5000);
            if (_server != null)
            {
                try
                {
                    using (Py.GIL())
                    {
                        if (_runId != null) { try { _server.InvokeMethod("stop_live_strategy", new PyString(_runId)).Dispose(); } catch {} }
                        try { _server.InvokeMethod("set_execution_mode", new PyString("Replay")).Dispose(); } catch {}
                        try { _server.InvokeMethod("venue_logout").Dispose(); } catch {}
                        try { _server.InvokeMethod("close").Dispose(); } catch {}
                    }
                }
                catch (Exception e) { Debug.LogWarning("[LIVE ADAPTER TRACER HITL] teardown: " + e); }
            }
            _status = _driveError != null ? ("ERROR: " + _driveError) : "DONE (roundtrip complete; panel shows final values)";
            _driveDone = true;
        }
    }

    void PollWorker()
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
                    catch (Exception e) { _pollError = "poll: " + e; }
                }
                Thread.Sleep(40);
            }
        }
        catch (Exception e) { _pollError = "poll: " + e; }
        finally { _pollStopped = true; }
    }

    void Pump()
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

    bool WaitFills(int n, int ms)
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

    bool WaitFlag(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

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
        return double.TryParse(s.Substring(start, i - start),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : double.NaN;
    }

    void OnDestroy()
    {
        try
        {
            _stopPolling = true;
            bool driveStopped = true;
            if (_drive != null && _drive.IsAlive) driveStopped = _drive.Join(15000);
            if (_poll != null && _poll.IsAlive) _poll.Join(5000);

            if (!driveStopped)
            {
                Debug.LogWarning("[LIVE ADAPTER TRACER HITL] OnDestroy: drive worker did not stop; skipping Python shutdown to avoid GIL deadlock");
            }
            else if (_engineStarted && _driveDone)
            {
                if (_mainThreadState != IntPtr.Zero) PythonEngine.EndAllowThreads(_mainThreadState);
                PythonEngine.Shutdown();
            }
            else if (_engineStarted)
            {
                Debug.LogWarning("[LIVE ADAPTER TRACER HITL] OnDestroy: drive did not finish cleanly; skipping Python shutdown (process exits next)");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[LIVE ADAPTER TRACER HITL] OnDestroy cleanup: " + e);
        }
    }
}
