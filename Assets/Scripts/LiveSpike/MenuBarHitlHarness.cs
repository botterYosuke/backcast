// MenuBarHitlHarness.cs — issue #42 "menu bar（全体メニュー）" (verification: AFK probe + HITL)
//
// The canonical #42 home (findings 0017 §8). ProductionLiveShell hosts the venue-submenu + mode
// side-effects (AC②/AC③) but holds no editor/scenario/canvas, so the FULL-workspace File→New
// clear and the layout/strategy round-trip are proven HERE, where the harness composes the whole
// workspace (StrategyProviderRegistry + ScenarioStartupController) over a real in-proc engine.
//
// AFK probe (deterministic, headless-style — runs on a worker, logs [MENU BAR HITL PASS]/[FAIL]):
//   A. pure-VM gates (no engine): prod grey-out (*_ALLOW_PROD), File→New refuse-when-running.
//   B. full-workspace clear: register provider + seed universe → File→New → registry 1→0,
//      universe emptied, scenario buffer cleared.
//   C. engine mode side-effects: MOCK connect → CONNECTED → File→Open WHILE Live → engine
//      execution_mode == LiveAuto (get_state_json); File→New → LiveManual.
// HITL: the owner can fire the same menu actions by hand and watch the mode label converge.
using System;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class MenuBarHitlHarness : MonoBehaviour
{
    const string IID = "8918.TSE";

    // ── workspace under test ──────────────────────────────────────────────────
    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    readonly StrategyProviderRegistry _registry = new StrategyProviderRegistry();
    readonly ScenarioStartupController _scenario = new ScenarioStartupController();
    VenueMenuViewModel _menu;
    MenuBarViewModel _menuBar;

    // ── engine + drive state ──────────────────────────────────────────────────
    volatile PyObject _server;
    volatile bool _serverReady;
    volatile string _status = "starting…";
    volatile string _driveError;
    volatile bool _driveDone;
    volatile string _execMode = "Replay";
    volatile bool _autoRunning;
    volatile bool _replayRunning;
    volatile int _pass, _fail;
    Thread _drive;
    IntPtr _mainTs;
    bool _engineStarted;

    // a trivial provider so the registry has something to Unregister on File→New.
    sealed class StubProvider : IStrategyFileProvider
    { public bool TryGetStrategyFile(out string path) { path = null; return false; } }

    void Start()
    {
        _menu = new VenueMenuViewModel(_conn, _coord);
        _menuBar = new MenuBarViewModel(_menu, _conn,
            currentMode: () => _execMode,
            isLiveAutoRunning: () => _autoRunning,
            isReplayRunning: () => _replayRunning);
        try
        {
            if (PythonEngine.IsInitialized)
            {
                // single Play-owner (findings 0025 §7): another owner (the Backcast workspace root)
                // holds the interpreter — refuse rather than double-init. Log it so the refusal is
                // observable in the Console (disable BackcastWorkspaceRoot to run this harness solo).
                _driveError = "double-init: PythonEngine already owned";
                _status = "ERROR";
                Debug.LogWarning("[MENU BAR HITL] refused: PythonEngine already owned by another Play-owner " +
                                 "(single Play-owner). Disable BackcastWorkspaceRoot to run this harness solo.");
                return;
            }
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;
            _mainTs = PythonEngine.BeginAllowThreads();
            _drive = new Thread(DriveWorker) { IsBackground = true, Name = "MenuBarDrive" };
            _drive.Start();
        }
        catch (Exception e) { _driveError = "start: " + e; _status = "ERROR"; Debug.LogError("[MENU BAR HITL FAIL] " + e); }
    }

    void Check(bool cond, string what)
    {
        if (cond) { _pass++; Debug.Log("[MENU BAR HITL PASS] " + what); }
        else { _fail++; Debug.LogError("[MENU BAR HITL FAIL] " + what); }
    }

    void DriveWorker()
    {
        try
        {
            // ---- A. pure-VM gates (no engine needed) ----
            // prod grey-out: a VM whose ALLOW_PROD predicate is false refuses prod; true allows it.
            var denyVm = new VenueMenuViewModel(_conn, _coord, prodAllowed: _ => false);
            var allowVm = new VenueMenuViewModel(_conn, _coord, prodAllowed: _ => true);
            Check(!denyVm.CanConnectEnv("TACHIBANA", "prod"), "A1 prod grey-out when *_ALLOW_PROD unset");
            Check(denyVm.CanConnectEnv("TACHIBANA", "demo"), "A2 demo always connectable when disconnected");
            Check(allowVm.CanConnectEnv("KABU", "prod"), "A3 prod enabled when KABU_ALLOW_PROD set");

            // File→New refused while a run is in flight (ADR-0001 safety).
            _replayRunning = true;
            var dRun = _menuBar.FileNew(out _, out string refuse);
            Check(dRun == FileNewDecision.RefusedRunning && !string.IsNullOrEmpty(refuse), "A4 File→New refused while running");
            _replayRunning = false;

            // ---- B. full-workspace clear ----
            _registry.Register("strategy_editor:region_001", new StubProvider());
            _scenario.Universe.ReplaceAll(new[] { "7203.TSE", "6758.TSE" });
            _scenario.SetStart("2025-01-01");           // dirty the buffer
            var dClear = _menuBar.FileNew(out string modeB, out _);
            Check(dClear == FileNewDecision.ClearWorkspace, "B1 File→New decides ClearWorkspace when idle");
            // host performs the in-memory clear (the orchestration each host owns over its surfaces):
            _registry.Unregister("strategy_editor:region_001");
            _scenario.Clear();
            Check(_registry.Count == 0, "B2 provider registry cleared");
            Check(_scenario.Universe.Count == 0, "B3 universe emptied");
            Check(!_scenario.Params.Dirty && string.IsNullOrEmpty(_scenario.Params.Start), "B4 scenario buffer reset");
            Check(modeB == null, "B5 mode side-effect is no-op while disconnected (TTWR observable no-op)");

            // ---- C. engine mode side-effects (real in-proc) ----
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
                _serverReady = true;

                using (PyObject login = _server.InvokeMethod("venue_login", new PyString("MOCK"), new PyString("env"), new PyString("")))
                    if (!login["success"].As<bool>()) { _driveError = "venue_login failed: " + login; return; }
                using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
                    if (!m["success"].As<bool>()) { _driveError = "LiveManual failed: " + m; return; }
                _execMode = "LiveManual";
                _conn.ApplyStatePoll(StateJson());
                Check(_menuBar.LiveModeAllowed, "C1 LiveModeAllowed once venue CONNECTED/SUBSCRIBED");

                // File→Open WHILE Live → LiveAuto side-effect (the engine-touching parity behaviour).
                string side = _menuBar.FileOpenModeSideEffect();
                Check(side == "LiveAuto", "C2 File→Open-while-Live computes LiveAuto side-effect");
                using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(side)))
                    if (!m["success"].As<bool>()) { _driveError = "LiveAuto failed: " + m; return; }
                _execMode = "LiveAuto";
                Check(ModeIs("LiveAuto"), "C3 engine execution_mode == LiveAuto after File→Open");

                // File→New (connected) → LiveManual side-effect, engine transitions back.
                var dNew = _menuBar.FileNew(out string modeC, out _);
                Check(dNew == FileNewDecision.ClearWorkspace && modeC == "LiveManual", "C4 File→New computes LiveManual when connected");
                using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(modeC)))
                    if (!m["success"].As<bool>()) { _driveError = "back-to-LiveManual failed: " + m; return; }
                _execMode = "LiveManual";
                Check(ModeIs("LiveManual"), "C5 engine execution_mode == LiveManual after File→New");
            }
            _status = _fail == 0 ? $"ALL PASS ({_pass})" : $"{_fail} FAIL / {_pass} pass";
        }
        catch (Exception e) { if (_driveError == null) _driveError = "drive: " + e; }
        finally
        {
            if (_server != null)
                try { using (Py.GIL()) { try { _server.InvokeMethod("set_execution_mode", new PyString("Replay")).Dispose(); } catch {} try { _server.InvokeMethod("venue_logout").Dispose(); } catch {} try { _server.InvokeMethod("close").Dispose(); } catch {} } }
                catch (Exception e) { Debug.LogWarning("[MENU BAR HITL] teardown: " + e); }
            if (_driveError != null) { _status = "ERROR: " + _driveError; Debug.LogError("[MENU BAR HITL FAIL] " + _driveError); }
            _driveDone = true;
        }
    }

    string StateJson()
    {
        using (PyObject s = _server.InvokeMethod("get_state_json")) return s.As<string>();
    }

    // tolerant check on the canonical execution_mode field (avoids a JSON dep for one key).
    bool ModeIs(string mode)
    {
        string s = StateJson().Replace(" ", "");
        return s.Contains("\"execution_mode\":\"" + mode + "\"");
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, 12, 560, 260), GUI.skin.box);
        GUILayout.Label("<b>Menu Bar HITL / AFK probe (#42)</b>");
        GUILayout.Label("status: " + _status + (_driveDone ? "  (done)" : ""));
        GUILayout.Label($"pass={_pass}  fail={_fail}  mode={_execMode}  badge={_menu?.BadgeText}");
        GUILayout.Space(6);
        GUILayout.Label("AFK probe runs on Play; the labels above converge to ALL PASS.");
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (_drive != null && _drive.IsAlive) _drive.Join(3000);
        if (_engineStarted && _mainTs != IntPtr.Zero) { try { PythonEngine.EndAllowThreads(_mainTs); } catch {} }
    }
}
