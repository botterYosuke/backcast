// WorkspaceEngineHost.cs — issue #59 workspace root, generalized for #39 Live/Auto (DURABLE).
//
// Step 2 of the #39→#59 integration: the SINGLE durable engine host for the Backcast workspace
// root, generalized from the Replay-only ReplayEngineHost to own BOTH the Replay path AND the live
// seam on ONE persistent server (findings 0025 §5; ADR-0009). It is a PLAIN C# class (not a
// MonoBehaviour): the scene-authored BackcastWorkspaceRoot WIRES Views to it and drives ChartView /
// the footer / the venue badge from the state this host publishes.
//
// WHY ONE PERSISTENT, LIVE-CONFIGURED SERVER (decision 1, verified GREEN by
// python/tests/test_live_configured_server_replay_intact.py): InprocLiveServer carries both Replay
// and Live RPCs on one façade, and a live-configured server (DataEngine + set_rust_event_sink +
// InprocLiveServer(de, venue)) runs the Replay path unchanged. Live NEEDS the server BEFORE any run
// (connect venue → set mode → start), so — unlike ReplayEngineHost which built the server per-run in
// the launcher — this host builds it ONCE in InitializePython() and the Replay run only calls
// load_replay_data + start_engine on it.
//
// POLL: LiveRpcLanes owns the get_state_json poll (50 ms) AND the venue/order/secret lanes, so there
// is NO separate poll thread here (ReplayEngineHost's PollLoop is retired); LatestStateJson reads
// _lanes.LatestState. The live push events (order/fill/lifecycle/secret) arrive on the
// LiveBackendEventSink and the root drains them into LivePanelViewModel each frame (DrainLiveEvents).
//
// THREADING (ADR-0001 decision 4): the server+lanes are built under Initialize()'s GIL, then
// BeginAllowThreads() leaves main GIL-free; the launcher (Replay run) and every live RPC run on
// background threads, each under Py.GIL(). Only C# strings/flags + result callbacks cross back; the
// root marshals those callbacks to the main thread before touching any VM.
//
// OWNERSHIP (decision 2): this host OWNS the live seam (event sink / LivePanelViewModel /
// VenueConnectionViewModel / SecretModalController / LiveRpcLanes); the root wires Views/VMs to the
// exposed seam and orchestrates the footer. The host only DECIDES nothing about the footer — it
// marshals RPCs and publishes state.
//
// OWNER-COMPILE NOTE (Step 2 is C#, unverifiable in this dev env): signatures are mirrored 1:1 from
// the proven ProductionLiveShell.Start/ConnectEnv/SendMode handlers and ReplayEngineHost; the merged
// teardown (force_stop_replay + lanes.StopAndJoin + server.close + final-state capture + launcher
// join) and the build-at-init GIL ordering are the parts to verify first via the host AFK probe.

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Python.Runtime;

public sealed class WorkspaceEngineHost
{
    const int LAUNCHER_JOIN_MS = 3000;

    // A Replay run request snapshot, taken on main before the launcher starts (unchanged from
    // ReplayEngineHost so BackcastWorkspaceRoot.OnRun is untouched).
    public struct RunRequest
    {
        public string[] Instruments;
        public string Start;
        public string End;
        public string Granularity;
        public string StrategyPath;
    }

    // ── durable live seam (OWNED here; the root wires Views/VMs to these) ──
    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    readonly LivePanelViewModel _panel = new LivePanelViewModel();
    readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    readonly SecretModalController _modal = new SecretModalController();
    LiveRpcLanes _lanes;

    public LivePanelViewModel Panel => _panel;
    public VenueConnectionViewModel Conn => _conn;
    public LiveLogoutCoordinator Coord => _coord;
    public SecretModalController Modal => _modal;
    public LiveRpcLanes Lanes => _lanes;

    static bool s_pythonBootstrapped;

    // ── persistent engine handles (KEPT, never disposed: load_replay_data is on the DataEngine, the
    // run + live RPCs + replay transport are on the InprocLiveServer) ──
    volatile PyObject _de;
    volatile PyObject _server;
    string _venue = "MOCK";

    // ── run state (Replay launcher; read on main) ──
    // NOTE: these cross-thread flags use NO `volatile` keyword on purpose — every access goes through
    // Volatile.Read/Write(ref …), which provides the acquire/release barrier. Marking the field
    // `volatile` AND passing it by ref to Volatile.* is CS0420 (the ref drops volatility); the
    // explicit Volatile.* calls are the modern, single-source barrier. (_de/_server stay `volatile`
    // because they are read directly, never by ref.)
    string _startError;
    Thread _launcher;
    bool _running;
    bool _runFinished;
    bool _serverReady;
    RunRequest _req;

    // ── live RPC single-flight (mode / auto lifecycle; venue login has its own _loginRunning) ──
    bool _liveRpcInFlight;
    bool _loginRunning;

    // ── teardown ──
    const int TEARDOWN_DRAIN_MS = 2000;        // bounded wait for in-flight live RPCs before close
    bool _closing;                             // teardown started: reject new RPCs + launcher must not start_engine
    bool _teardownComplete;
    string _finalStateJson;
    readonly OnceGate _stopGate = new OnceGate();
    readonly object _rpcLock = new object();   // guards the live-RPC single-flight check-and-set

    // ---- public reads (main) ----
    public bool PythonInitialized => s_pythonBootstrapped;
    public bool ServerReady => Volatile.Read(ref _serverReady);
    public bool IsRunning => Volatile.Read(ref _running);
    public bool RunFinished => Volatile.Read(ref _runFinished);
    public string StartError => Volatile.Read(ref _startError);
    public bool LiveRpcInFlight => Volatile.Read(ref _liveRpcInFlight);
    public bool LoginRunning => Volatile.Read(ref _loginRunning);
    public bool TeardownComplete => Volatile.Read(ref _teardownComplete);
    public string FinalStateJson => Volatile.Read(ref _finalStateJson);

    // The poll is owned by LiveRpcLanes (get_state_json @ 50 ms). After teardown the lanes are gone,
    // so the single post-logout snapshot is served instead (mirrors ProductionLiveShell).
    public string LatestStateJson =>
        Volatile.Read(ref _teardownComplete) ? Volatile.Read(ref _finalStateJson)
        : (Volatile.Read(ref _serverReady) && _lanes != null ? _lanes.LatestState : null);

    // ---- bring-up: build the PERSISTENT live-configured server ONCE (decision 1) ----
    // The CALLER decides ownership BEFORE calling this (WorkspaceOwnership.ShouldClaim). venue has a
    // default so the root's existing `_host.InitializePython()` call stays source-compatible.
    public void InitializePython(string venue = "MOCK")
    {
        // Guard on the INSTANCE (_serverReady), not the static bootstrap: a re-Play with domain reload
        // disabled creates a FRESH host whose _de/_server/_lanes are null, while s_pythonBootstrapped
        // (static) is still true from the prior Play. The static guard covers only PythonEngine.Initialize
        // (one per process); the server/lanes are per-host and must be (re)built each time.
        if (Volatile.Read(ref _serverReady)) return;
        _venue = string.IsNullOrEmpty(venue) ? "MOCK" : venue;

        if (!s_pythonBootstrapped)
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();   // main GIL-free for the rest of the process
            s_pythonBootstrapped = true;
        }

        // Build the persistent live-configured server for THIS host under an explicit GIL acquire
        // (main is GIL-free after BeginAllowThreads — works on both first Play and re-Play).
        using (Py.GIL())
        {
            using (PyObject sys = Py.Import("sys"))
            using (PyObject sp = sys.GetAttr("path"))
            {
                sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
            }
            using (PyObject core = Py.Import("engine.core"))
            using (PyObject deCls = core.GetAttr("DataEngine"))
                _de = deCls.Invoke();
            using (PyObject sinkPy = PyObject.FromManagedObject(_sink))
                _de.InvokeMethod("set_rust_event_sink", sinkPy).Dispose();
            using (PyObject inproc = Py.Import("engine.inproc_server"))
            using (PyObject srvCls = inproc.GetAttr("InprocLiveServer"))
                _server = srvCls.Invoke(_de, new PyString(_venue));
            // NOTE: do NOT dispose _de — load_replay_data is called on it per run.
        }

        _lanes = new LiveRpcLanes(_server, _coord);
        _lanes.Start();                      // poll (get_state_json) + venue/order/secret lanes
        Volatile.Write(ref _serverReady, true);
        Debug.Log("[WorkspaceEngineHost] live-configured server built; main GIL-free; lanes polling.");
    }

    // ---- live push events: drain the sink into LivePanelViewModel; return true if a NEW
    // secret-required appeared (the root opens the secret modal). Called on main each frame. ----
    public bool DrainLiveEvents()
    {
        if (!Volatile.Read(ref _serverReady)) return false;
        long before = _panel.SecretRequiredCount;
        while (_sink.TryDequeue(out string wire)) _panel.Apply(wire);
        return _panel.SecretRequiredCount > before;
    }

    // ======================= Replay run + transport (unchanged API) =======================

    // Launch the production Replay path on the persistent server. Refuses while a run is in flight or
    // the previous launcher is still alive (re-entrancy guard, unchanged from ReplayEngineHost).
    public bool TryStartRun(RunRequest req)
    {
        if (!Volatile.Read(ref _serverReady)) return false;
        if (Volatile.Read(ref _running)) return false;
        if (_launcher != null && _launcher.IsAlive) return false;

        _req = req;
        Volatile.Write(ref _startError, null);
        Volatile.Write(ref _runFinished, false);
        Volatile.Write(ref _running, true);
        _launcher = new Thread(Launcher) { IsBackground = true, Name = "WorkspaceEngineLauncher" };
        _launcher.Start();
        return true;
    }

    // Launcher: load_replay_data on the engine (re-primes the catalog), then the synchronous
    // start_engine on the server (the per-bar sleep releases the GIL so the lanes poll interleaves).
    void Launcher()
    {
        try
        {
            using (Py.GIL())
            using (PyList insts = new PyList())
            {
                foreach (string id in _req.Instruments) insts.Append(new PyString(id));
                using (PyObject res = _de.InvokeMethod(
                    "load_replay_data", insts, new PyString(_req.Start), new PyString(_req.End), new PyString(_req.Granularity)))
                using (PyObject ok = res[0])
                {
                    if (!ok.As<bool>())
                        using (PyObject msg = res[1]) Volatile.Write(ref _startError, "load_replay_data: " + msg.As<string>());
                }
            }
            if (Volatile.Read(ref _startError) != null) return;

            // Teardown requested during load: do NOT enter the blocking start_engine (findings 0025 §10).
            if (Volatile.Read(ref _closing)) return;

            using (Py.GIL())
            using (PyDict cfg = new PyDict())
            {
                cfg.SetItem("strategy_file", new PyString(_req.StrategyPath));
                using (PyObject res = _server.InvokeMethod("start_engine", cfg))
                using (PyObject success = res["success"])
                {
                    if (!success.As<bool>())
                        using (PyObject ec = res["error_code"])
                        using (PyObject em = res["error_message"])
                            Volatile.Write(ref _startError, $"start_engine: {ec.As<string>()} {em.As<string>()}");
                }
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "launcher: " + e);
        }
        finally
        {
            Volatile.Write(ref _running, false);
            Volatile.Write(ref _runFinished, true);
        }
    }

    public void Pause() => CallTransport("pause_replay");
    public void Resume() => CallTransport("resume_replay");
    public void Step() => CallTransport("step_replay");
    public void ForceStop() => CallTransport("force_stop_replay");

    public void SetSpeed(int mult)
    {
        if (!Volatile.Read(ref _serverReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod("set_replay_speed", new PyInt(mult)))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>()) Debug.LogWarning($"[WorkspaceEngineHost] set_replay_speed({mult}) rejected");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] set_replay_speed error (non-fatal): {e.Message}"); }
    }

    void CallTransport(string method)
    {
        if (!Volatile.Read(ref _serverReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod(method))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    using (PyObject em = res["error_message"])
                        Debug.LogWarning($"[WorkspaceEngineHost] {method} rejected: {em.As<string>()}");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] {method} error (non-fatal): {e.Message}"); }
    }

    // ======================= Live RPCs (#39 footer + venue) =======================
    // Each runs on a background thread under Py.GIL(); the result callback is invoked ON THAT WORKER
    // thread — the ROOT's callback must marshal to main before touching any VM. The single-flight
    // (_liveRpcInFlight) serializes the mode/auto RPCs against each other so a mode switch can't race
    // a register→start under the GIL (the #39 review's High finding); venue login has _loginRunning.

    // venue_login → on success set_execution_mode(LiveManual). Mirrors ProductionLiveShell.ConnectEnv.
    public void VenueLogin(string venue, string credentialsSource, string environmentHint, Action<bool, string> onResult)
    {
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady)) { onResult?.Invoke(false, "server not ready"); return; }
        if (Volatile.Read(ref _loginRunning)) return;
        Volatile.Write(ref _loginRunning, true);
        new Thread(() =>
        {
            bool ok = false; string ec = "";
            try
            {
                using (Py.GIL())
                using (PyObject res = _server.InvokeMethod("venue_login",
                           new PyString(venue), new PyString(credentialsSource), new PyString(environmentHint ?? "")))
                {
                    ok = res["success"].As<bool>();
                    ec = ok ? "" : res["error_code"].As<string>();
                    if (ok)
                        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
                        {
                            if (!m["success"].As<bool>()) { ok = false; ec = "set_execution_mode: " + m["error_code"].As<string>(); }
                        }
                }
            }
            catch (Exception e) { ok = false; ec = "login: " + e.Message; }
            // NOTE: do NOT touch _conn (a main-thread VM) from this worker. The caller applies the ack
            // on the main thread from onResult — e.g. `_host.Conn.ApplyLoginAck(ok, ec)` in DriveFooter.
            finally { Volatile.Write(ref _loginRunning, false); onResult?.Invoke(ok, ec); }
        }) { IsBackground = true, Name = "WorkspaceVenueLogin" }.Start();
    }

    // set_execution_mode (footer mode segment; D1). onResult(ok) on the worker thread.
    public void SetExecutionMode(string mode, Action<bool> onResult)
    {
        if (!BeginLiveRpc()) { onResult?.Invoke(false); return; }
        new Thread(() =>
        {
            bool ok = false;
            try
            {
                using (Py.GIL())
                using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(mode)))
                    ok = m["success"].As<bool>();
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] set_execution_mode error: " + e.Message); }
            finally { EndLiveRpc(); onResult?.Invoke(ok); }
        }) { IsBackground = true, Name = "WorkspaceSetMode" }.Start();
    }

    // LiveAuto ▶ at rest → register_live_strategy → start_live_strategy (the 2-stage StartLiveAuto).
    // onResult(ok, runId) on the worker thread (runId is "" on failure).
    public void RegisterAndStartLiveAuto(string strategyFile, string originalPath, string instrumentId, string venue, Action<bool, string> onResult)
    {
        if (!BeginLiveRpc()) { onResult?.Invoke(false, ""); return; }
        new Thread(() =>
        {
            bool ok = false; string runId = "";
            try
            {
                using (Py.GIL())
                {
                    // Register first; only start if it succeeded. NO early return inside the try — the
                    // single `finally` is the ONLY cleanup site (an inner return would double-run it,
                    // double-clearing the single-flight and double-delivering the result).
                    string sid = null;
                    using (PyObject r = _server.InvokeMethod("register_live_strategy",
                               new PyString(strategyFile), new PyString(originalPath ?? "")))
                        if (r["success"].As<bool>()) sid = r["strategy_id"].As<string>();
                    if (sid != null)
                        using (PyObject s = _server.InvokeMethod("start_live_strategy",
                                   new PyString(sid), new PyString(instrumentId), new PyString(venue)))
                        {
                            ok = s["success"].As<bool>();
                            if (ok) runId = s["run_id"].As<string>();
                        }
                }
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] register/start error: " + e.Message); ok = false; }
            finally { EndLiveRpc(); onResult?.Invoke(ok, runId); }
        }) { IsBackground = true, Name = "WorkspaceLiveAutoStart" }.Start();
    }

    public void PauseLiveStrategy(string runId, Action<bool> onResult) => CallLiveControl("pause_live_strategy", runId, onResult);
    public void ResumeLiveStrategy(string runId, Action<bool> onResult) => CallLiveControl("resume_live_strategy", runId, onResult);
    public void StopLiveStrategy(string runId, Action<bool> onResult) => CallLiveControl("stop_live_strategy", runId, onResult);

    void CallLiveControl(string method, string runId, Action<bool> onResult)
    {
        if (string.IsNullOrEmpty(runId)) { onResult?.Invoke(false); return; }
        if (!BeginLiveRpc()) { onResult?.Invoke(false); return; }
        new Thread(() =>
        {
            bool ok = false;
            try
            {
                using (Py.GIL())
                using (PyObject s = _server.InvokeMethod(method, new PyString(runId)))
                    ok = s["success"].As<bool>();
            }
            catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] {method} error: {e.Message}"); }
            finally { EndLiveRpc(); onResult?.Invoke(ok); }
        }) { IsBackground = true, Name = "WorkspaceLiveControl" }.Start();
    }

    // D2: leaving LiveAuto with an active run → stop_live_strategy FIRST; switch mode only on stop
    // success (else stay + report failure). One worker keeps the two RPCs sequential. onResult(stopped
    // && switched) — false means stayed in LiveAuto (no "Replay display over a live run" orphan).
    public void StopLiveThenSetMode(string runId, string targetMode, Action<bool> onResult)
    {
        if (string.IsNullOrEmpty(runId)) { SetExecutionMode(targetMode, onResult); return; }
        if (!BeginLiveRpc()) { onResult?.Invoke(false); return; }
        new Thread(() =>
        {
            bool settled = false;
            try
            {
                using (Py.GIL())
                {
                    bool stopped;
                    using (PyObject s = _server.InvokeMethod("stop_live_strategy", new PyString(runId)))
                        stopped = s["success"].As<bool>();
                    if (stopped)
                        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(targetMode)))
                            settled = m["success"].As<bool>();
                }
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] stop-then-switch error: " + e.Message); }
            finally { EndLiveRpc(); onResult?.Invoke(settled); }
        }) { IsBackground = true, Name = "WorkspaceStopThenSwitch" }.Start();
    }

    // single-flight for the mode/auto live RPCs (serialize against each other under the GIL).
    bool BeginLiveRpc()
    {
        lock (_rpcLock)
        {
            if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady) || Volatile.Read(ref _liveRpcInFlight))
                return false;
            Volatile.Write(ref _liveRpcInFlight, true);
            return true;
        }
    }

    void EndLiveRpc() => Volatile.Write(ref _liveRpcInFlight, false);

    // ======================= teardown (findings 0025 §10) =======================
    // Idempotent + bounded. Capture a final get_state_json snapshot (for the footer/badge after the
    // lanes stop), force_stop_replay to end a running start_engine, stop the lanes + live components,
    // join the launcher. NEVER PythonEngine.Shutdown() (the interpreter dies with the process,
    // ADR-0001). Safe to call multiple times.
    public void Stop()
    {
        if (!_stopGate.TryEnter()) return;
        // Reject NEW live RPCs / the launcher's start_engine immediately, while the server stays alive
        // for ForceStop/snapshot/close below (_serverReady is cleared only AFTER close).
        Volatile.Write(ref _closing, true);

        // Let any IN-FLIGHT live-RPC / login worker finish (bounded) before we touch the server, so a
        // worker that passed BeginLiveRpc just before _closing can't hit a closed server (use-after-close).
        DrainInFlight(TEARDOWN_DRAIN_MS);

        // capture the final snapshot while the server is still alive (badge/footer converge on it).
        try
        {
            if (Volatile.Read(ref _serverReady) && _server != null)
                using (Py.GIL())
                using (PyObject js = _server.InvokeMethod("get_state_json"))
                    Volatile.Write(ref _finalStateJson, js.As<string>());
        }
        catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] final snapshot failed (non-fatal): " + e.Message); }

        if (Volatile.Read(ref _running) && Volatile.Read(ref _serverReady))
            ForceStop();   // unblock the launcher's synchronous start_engine

        try { if (_lanes != null && !_lanes.StopAndJoin()) Debug.LogWarning("[WorkspaceEngineHost] lanes did not stop in time."); }
        catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] lanes.StopAndJoin failed: " + e.Message); }

        // Join the launcher BEFORE closing the server, so start_engine has fully exited the server
        // before close() tears down the loop/runner/account-sync it is returning through.
        if (_launcher != null && _launcher.IsAlive && !_launcher.Join(LAUNCHER_JOIN_MS))
            Debug.LogWarning("[WorkspaceEngineHost] launcher thread did not join in time; not blocking.");

        Volatile.Write(ref _serverReady, false);   // server about to close: nothing may InvokeMethod after this
        try
        {
            if (_server != null)
                using (Py.GIL()) _server.InvokeMethod("close").Dispose();
        }
        catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] server.close failed (non-fatal): " + e.Message); }

        Volatile.Write(ref _teardownComplete, true);
        Debug.Log("[WorkspaceEngineHost] Stop: drained + lanes/launcher joined; server closed; interpreter left alive.");
    }

    // Bounded spin until no live-RPC / login worker is in flight (teardown only; main thread, app quitting).
    void DrainInFlight(int budgetMs)
    {
        int waited = 0;
        while ((Volatile.Read(ref _liveRpcInFlight) || Volatile.Read(ref _loginRunning)) && waited < budgetMs)
        {
            Thread.Sleep(20);
            waited += 20;
        }
        if (waited >= budgetMs)
            Debug.LogWarning("[WorkspaceEngineHost] live RPC still in flight after drain budget; closing anyway.");
    }
}

// WorkspaceOwnership — the PURE single-Play-owner decision (findings 0025 §7), separated so the AFK
// probe verifies it WITHOUT initializing Python. The root owns Python only when it is the configured
// owner, not headless, and nobody else holds the interpreter (or this host already bootstrapped it).
public static class WorkspaceOwnership
{
    public static bool ShouldClaim(bool ownPlay, bool isBatchMode, bool pythonAlreadyInitialized, bool weAlreadyOwn)
        => ownPlay && !isBatchMode && (!pythonAlreadyInitialized || weAlreadyOwn);
}

// OnceGate — a one-shot latch guaranteeing a guarded action runs AT MOST ONCE across repeated calls
// (workspace teardown's "save layout once" / the host's "stop once"). A plain class so it is shared
// by reference and the AFK probe can drive it directly.
public sealed class OnceGate
{
    bool _used;
    public bool Entered => _used;
    public bool TryEnter() { if (_used) return false; _used = true; return true; }
}
