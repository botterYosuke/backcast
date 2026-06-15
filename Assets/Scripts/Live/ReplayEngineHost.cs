// ReplayEngineHost.cs — issue #59 "Backcast workspace root" (DURABLE Replay orchestration)
//
// The durable owner of the PRODUCTION Replay engine path, EXTRACTED from the throwaway
// ScenarioStartupHitlHarness so the scene-authored BackcastWorkspaceRoot can WIRE it instead of
// absorbing Python orchestration into the composition root (findings 0025 §5; ADR-0009). A PLAIN
// C# class (NOT a MonoBehaviour): it owns the embedded-Python lifecycle, the launcher + poll
// threads, and the transport RPCs; the root drives ChartView / ReplayLifecycle / the footer from
// the raw state this host publishes (the root keeps the VM/lifecycle, findings 0025 §5).
//
// PRODUCTION PATH (CONTEXT "backcast Replay 起動経路", verbatim from ScenarioStartup): on Run a
// launcher thread calls DataEngine.load_replay_data (IDLE→LOADED, primes the catalog) then the
// InprocLiveServer.start_engine RPC (LOADED→RUNNING, streams). start_engine BLOCKS, but engine_run's
// per-bar sleep releases the GIL so the poll thread reads get_state_json bar-by-bar. No RustBacktestSink.
//
// THREADING (ADR-0001 decision 4): the main thread is GIL-free (BeginAllowThreads); only the
// launcher/poll/transport touch Python, each under Py.GIL(). Only C# strings/flags cross back to main.
//
// TEARDOWN (findings 0025 §10): Stop() is idempotent and bounded — force_stop_replay (REQUIRED to end
// the launcher's synchronous start_engine; skipped only before the server is published) → stop poll →
// bounded join poll+launcher → warn-don't-block on join failure → NEVER PythonEngine.Shutdown()
// (S0-sanctioned: the interpreter dies with the process, ADR-0001).

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Python.Runtime;

public sealed class ReplayEngineHost
{
    const int POLL_INTERVAL_MS = 150;
    const int POLL_JOIN_MS = 500;
    const int LAUNCHER_JOIN_MS = 3000;

    // A run request snapshot, taken on main before the launcher starts (the worker never touches
    // the controller). Mirrors ScenarioStartup's _runInstruments/_runStart/... snapshot.
    public struct RunRequest
    {
        public string[] Instruments;
        public string Start;
        public string End;
        public string Granularity;
        public string StrategyPath;
    }

    static bool s_pythonBootstrapped;

    // launcher → main (only C# strings/flags cross)
    string _startError;
    Thread _launcher;
    Thread _poll;
    volatile bool _running;        // a run is in flight: ignore re-entrant Run clicks
    volatile bool _runFinished;    // launcher reached its finally (run done/failed)
    volatile bool _aborting;       // teardown requested: launcher must NOT start_engine after this
    volatile bool _pollStop;
    volatile bool _pollServerReady;
    volatile PyObject _pollServer;
    volatile string _latestStateJson;

    RunRequest _req;

    readonly OnceGate _stopGate = new OnceGate();   // Stop() idempotency latch (probe-testable)

    // ---- engine lifecycle ----

    public bool PythonInitialized => s_pythonBootstrapped;

    // Initialize the embedded interpreter (idempotent). The CALLER decides ownership BEFORE calling
    // this (WorkspaceOwnership.ShouldClaim) — the host does not re-decide. Configures the runtime
    // locator, Initialize()s, and releases the GIL on main so the worker threads can take it.
    public void InitializePython()
    {
        if (s_pythonBootstrapped) return;
        PythonRuntimeLocator.ConfigureBeforeInitialize();
        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();
        s_pythonBootstrapped = true;
        Debug.Log("[ReplayEngineHost] Python initialized; main is GIL-free.");
    }

    // ---- run state (read on main) ----

    public bool IsRunning => Volatile.Read(ref _running);
    public bool RunFinished => Volatile.Read(ref _runFinished);
    public string StartError => Volatile.Read(ref _startError);
    public bool ServerReady => Volatile.Read(ref _pollServerReady);
    public string LatestStateJson => Volatile.Read(ref _latestStateJson);

    // Launch the production Replay path. Returns false (and does nothing) if a run is already in
    // flight (re-entrancy guard: a second start_engine would fail load_replay_data and flip the
    // live run to FAILED). The caller validates + writes the sidecar BEFORE calling this.
    public bool TryStartRun(RunRequest req)
    {
        // Refuse not just on _running but while the PREVIOUS launcher is still alive: its finally
        // writes _running=false BEFORE _runFinished=true, so a new run starting in that gap would
        // have the old thread's late _runFinished=true mis-latch the fresh run as Done.
        if (Volatile.Read(ref _running)) return false;
        if (_launcher != null && _launcher.IsAlive) return false;

        _req = req;
        Volatile.Write(ref _startError, null);
        Volatile.Write(ref _runFinished, false);
        Volatile.Write(ref _latestStateJson, null);
        // A re-run must not poll the PREVIOUS run's (finished) server until the new launcher
        // republishes it — clear readiness so PollLoop/transport idle until load_replay_data lands.
        Volatile.Write(ref _pollServerReady, false);
        _pollServer = null;

        Volatile.Write(ref _running, true);
        _launcher = new Thread(Launcher) { IsBackground = true, Name = "ReplayEngineLauncher" };
        _launcher.Start();

        if (_poll == null)
        {
            _poll = new Thread(PollLoop) { IsBackground = true, Name = "ReplayEnginePoll" };
            _poll.Start();
        }
        return true;
    }

    // Launcher: build engine + load_replay_data (publish server for polling), THEN run the
    // synchronous start_engine in a second GIL block so the per-bar sleep lets polls interleave.
    void Launcher()
    {
        try
        {
            PyObject server;
            using (Py.GIL())
            {
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }

                PyObject coreMod = Py.Import("engine.core");
                PyObject inprocMod = Py.Import("engine.inproc_server");
                PyObject dataEngCls = coreMod.GetAttr("DataEngine");
                PyObject inprocCls = inprocMod.GetAttr("InprocLiveServer");

                // #50 / ADR-0006: nautilus catalog retired. DataEngine() resolves the J-Quants
                // DuckDB root from env and Replay streams via the nautilus-free kernel.
                PyObject dataEngine = dataEngCls.Invoke(Array.Empty<PyObject>());
                server = inprocCls.Invoke(dataEngine);

                using (PyList insts = new PyList())
                {
                    foreach (string id in _req.Instruments) insts.Append(new PyString(id));
                    using (PyObject res = dataEngine.InvokeMethod(
                        "load_replay_data", insts, new PyString(_req.Start), new PyString(_req.End), new PyString(_req.Granularity)))
                    using (PyObject ok = res[0])
                    {
                        if (!ok.As<bool>())
                            using (PyObject msg = res[1]) Volatile.Write(ref _startError, "load_replay_data: " + msg.As<string>());
                    }
                }

                if (Volatile.Read(ref _startError) == null)
                {
                    _pollServer = server;
                    Volatile.Write(ref _pollServerReady, true);
                }
            } // GIL released → poll thread can run.

            if (Volatile.Read(ref _startError) != null) return;

            // Teardown requested during load_replay_data (before the server was published, so Stop()
            // could not force_stop): do NOT enter the blocking start_engine, or it would run the
            // engine unattended on this leaked thread for the rest of the session (findings 0025 §10).
            if (Volatile.Read(ref _aborting)) return;

            // start_engine RPC (synchronous). engine_run's per-bar sleep releases the GIL so the
            // poll thread reads the incrementally-streamed chart between bars.
            using (Py.GIL())
            using (PyDict cfg = new PyDict())
            {
                cfg.SetItem("strategy_file", new PyString(_req.StrategyPath));
                using (PyObject res = server.InvokeMethod("start_engine", cfg))
                using (PyObject success = res["success"])
                {
                    if (!success.As<bool>())
                    {
                        using (PyObject ec = res["error_code"])
                        using (PyObject em = res["error_message"])
                            Volatile.Write(ref _startError, $"start_engine: {ec.As<string>()} {em.As<string>()}");
                    }
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

    void PollLoop()
    {
        while (!Volatile.Read(ref _pollStop))
        {
            if (Volatile.Read(ref _pollServerReady))
            {
                try
                {
                    using (Py.GIL())
                    using (PyObject js = _pollServer.InvokeMethod("get_state_json"))
                        Volatile.Write(ref _latestStateJson, js.As<string>());
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ReplayEngineHost] poll error (non-fatal): " + e.Message);
                }
            }
            Thread.Sleep(POLL_INTERVAL_MS);
        }
    }

    // ---- transport RPCs (best-effort, brief main-thread GIL acquire; the per-bar sleep keeps the
    // GIL available and the calls are O(1)). No-op before the server is published. ----

    public void Pause() => CallTransport("pause_replay");
    public void Resume() => CallTransport("resume_replay");
    public void Step() => CallTransport("step_replay");
    public void ForceStop() => CallTransport("force_stop_replay");

    public void SetSpeed(int mult)
    {
        if (!Volatile.Read(ref _pollServerReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _pollServer.InvokeMethod("set_replay_speed", new PyInt(mult)))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    Debug.LogWarning($"[ReplayEngineHost] set_replay_speed({mult}) rejected");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ReplayEngineHost] set_replay_speed error (non-fatal): {e.Message}");
        }
    }

    void CallTransport(string method)
    {
        if (!Volatile.Read(ref _pollServerReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _pollServer.InvokeMethod(method))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    using (PyObject em = res["error_message"])
                        Debug.LogWarning($"[ReplayEngineHost] {method} rejected: {em.As<string>()}");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ReplayEngineHost] {method} error (non-fatal): {e.Message}");
        }
    }

    // ---- teardown (findings 0025 §10) ----
    //
    // Idempotent + bounded. force_stop_replay is REQUIRED to end the launcher's synchronous
    // start_engine — but only callable once the server is published (skip otherwise). Then stop the
    // poll and bounded-join both threads; on join failure warn and DO NOT block. NEVER Shutdown the
    // interpreter (it dies with the process, ADR-0001). Safe to call multiple times (Stop/Dispose
    // converge here): the latch ensures force_stop and the join run at most once.
    public void Stop()
    {
        if (!_stopGate.TryEnter()) return;

        Volatile.Write(ref _aborting, true);   // launcher must not enter start_engine after this

        if (Volatile.Read(ref _running) && Volatile.Read(ref _pollServerReady))
            ForceStop();   // unblock the launcher's already-running start_engine

        Volatile.Write(ref _pollStop, true);

        if (_poll != null && _poll.IsAlive && !_poll.Join(POLL_JOIN_MS))
            Debug.LogWarning("[ReplayEngineHost] poll thread did not join in time; not blocking.");
        if (_launcher != null && _launcher.IsAlive && !_launcher.Join(LAUNCHER_JOIN_MS))
            Debug.LogWarning("[ReplayEngineHost] launcher thread did not join in time; not blocking.");

        Debug.Log("[ReplayEngineHost] Stop: threads joined (best-effort); interpreter left alive.");
    }
}

// WorkspaceOwnership — the PURE single-Play-owner decision (findings 0025 §7), separated so the AFK
// probe can verify it WITHOUT initializing Python (it injects pythonAlreadyInitialized). The root
// owns Python only when it is the configured owner, not headless, and nobody else holds the
// interpreter; otherwise a per-part HITL launched while the root runs is safely refused.
public static class WorkspaceOwnership
{
    // The root claims Python when it is the configured owner and not headless, AND either the
    // interpreter is free OR THIS workspace's host already bootstrapped it. The second clause is
    // what lets a re-Play with domain reload disabled ("Enter Play Mode Options") RECLAIM the
    // interpreter it left alive (ReplayEngineHost never Shutdown()s), instead of locking itself out;
    // a per-part HITL that initialized Python (not via our host) leaves weAlreadyOwn=false, so the
    // root correctly DECLINES and the HITL keeps ownership (findings 0025 §7).
    public static bool ShouldClaim(bool ownPlay, bool isBatchMode, bool pythonAlreadyInitialized, bool weAlreadyOwn)
        => ownPlay && !isBatchMode && (!pythonAlreadyInitialized || weAlreadyOwn);
}

// OnceGate — a one-shot latch guaranteeing a guarded action runs AT MOST ONCE across repeated calls.
// Guards the workspace teardown's "save layout once" and the host's "force_stop + join once"
// (findings 0025 §10) so OnApplicationQuit + OnDestroy converging do not double-save / double-stop.
// A plain class (not a struct) so it is shared by reference and the AFK probe can drive it directly.
public sealed class OnceGate
{
    bool _used;
    public bool Entered => _used;
    public bool TryEnter() { if (_used) return false; _used = true; return true; }
}
