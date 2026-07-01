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
    // #100 Slice ① (findings 0077): _runSummaryJson REMOVED.  Was a C#-side set-at-return field
    // populated by (a) the launcher (title-bar Run path), (b) HostNotebookCellExecutor
    // (per-cell run_summary key) and cleared by TryStartRun(RunRequest).  After #95 Phase 6
    // sunset of TryStartRun(RunRequest) the per-cell path had no clear, so a re-press carried
    // run1's stats through run2's running view.  The single source is now Python's
    // engine.last_run_summary, polled by LiveRpcLanes (LatestRunSummary), symmetric with
    // last_portfolio / LatestPortfolio (#65).  RunSummaryJson below reads the poll cache.
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
    // #31 Slice review F6: picker provider observes IsClosing so a background fetch that
    // completes after teardown begins drops its result (avoids writing into a stale cache /
    // racing the _server PyObject while venue_logout runs under the GIL).
    public bool IsClosing => Volatile.Read(ref _closing);
    public string FinalStateJson => Volatile.Read(ref _finalStateJson);

    // The poll is owned by LiveRpcLanes (get_state_json @ 50 ms). After teardown the lanes are gone,
    // so the single post-logout snapshot is served instead (mirrors ProductionLiveShell).
    public string LatestStateJson =>
        Volatile.Read(ref _teardownComplete) ? Volatile.Read(ref _finalStateJson)
        : (Volatile.Read(ref _serverReady) && _lanes != null ? _lanes.LatestState : null);

    // #65 AFK test seam: edit-mode probes (HakoniwaBaseModeProbe) have no live poll lane (_lanes ==
    // null), so they inject the Replay portfolio / summary snapshots here to drive PushReplayTiles.
    // Null in production — never assigned outside the editor probe, so LatestPortfolioJson/RunSummaryJson
    // fall through to their real sources with zero behavioural change.
    internal string TestPortfolioJsonOverride;
    internal string TestRunSummaryJsonOverride;

    // #65: the Replay portfolio snapshot, polled alongside the chart state (get_portfolio_json @ 50 ms,
    // Replay-only). null before the first poll / outside Replay → the base panels show honest-empty.
    public string LatestPortfolioJson =>
        TestPortfolioJsonOverride
        ?? (Volatile.Read(ref _serverReady) && _lanes != null ? _lanes.LatestPortfolio : null);

    // #65 → #100 Slice ① (findings 0077): the RunResult full-stats source.  Was originally
    // populated by the launcher / per-cell executor and cleared by TryStartRun(RunRequest); the
    // per-cell path lost the clear when Phase 6 sunset TryStartRun, so a re-press kept showing
    // run1's stats during run2's running view.  Single source consolidated to Python's
    // engine.last_run_summary (cleared at on_run_begin, set at _finalize_run), polled by
    // LiveRpcLanes.LatestRunSummary — same model as LatestPortfolioJson above.  The probe override
    // (TestRunSummaryJsonOverride) is preserved so NBHAKO can still inject summaries directly.
    public string RunSummaryJson =>
        TestRunSummaryJsonOverride
        ?? (Volatile.Read(ref _serverReady) && _lanes != null ? _lanes.LatestRunSummary : null);

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
            // #33: in deploy, verify ADR-0001 d3 executor in-proc parity before any engine
            // work runs. Editor parity is already covered by S1/S2 probes (findings 0001/0013).
            if (!Application.isEditor)
                ExecutorOrphanAbsenceAssert.AssertInProcParity();
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
        // CONTRACT (findings 0050 / #83): the literal string below is what the CI Player smoke
        // step greps for to gate draft Release publish. Changing this log message requires
        // updating .github/workflows/shippable-build.yml's smoke regex in the same PR.
        Debug.Log("[WorkspaceEngineHost] live-configured server built; main GIL-free; lanes polling.");
    }

    // ======================= #81 cell-synthesis seam (ADR-0013 Decision 3) =======================
    // Synthesise/decompose the notebook through marimo (engine.strategy_runtime.cell_synthesis) on
    // the SINGLE Python owner (ADR-0009) — no second interpreter. MAIN-THREAD only (UI Save/Open);
    // main is GIL-free after BeginAllowThreads, so each call acquires the GIL explicitly. sys.path
    // (ProjectRoot + VenvSite) is already inserted by InitializePython, which the root runs in Awake
    // before any Save/Open. Returns null on a not-initialised engine or any Python error; decompose
    // additionally returns null when Python returns None (a broken/non-marimo `.py`, fail-soft).

    // N cells (body+name+config JSON array) -> one canonical marimo `.py` (generate_filecontents).
    public string SynthesizeCells(string cellsJson)
    {
        if (!PythonInitialized) return null;
        try
        {
            using (Py.GIL())
            using (PyObject mod = Py.Import("engine.strategy_runtime.cell_synthesis"))
            using (PyObject fn = mod.GetAttr("synthesize_json"))
            using (PyObject res = fn.Invoke(new PyString(cellsJson ?? "[]")))
                return res.As<string>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] synthesize_json failed: " + e.Message);
            return null;
        }
    }

    // One `.py` -> N cells (body+name+config JSON array) via load_app. Returns null with a user-facing
    // `error` when the source is NOT a marimo notebook (#113 "marimo or error" at Open time):
    //   * decompose_json returns None (loadable non-marimo `.py` / empty file) -> "not a marimo notebook";
    //   * decompose_json raises SyntaxError (broken syntax) -> "syntax error: <detail>" (a DISTINCT
    //     failure, #113 AC#2 — never masked as a silent wrap);
    //   * Python not ready -> "python not ready".
    // `error` is null on success. Mirrors the run layer (live_cell_runtime: NOT_A_MARIMO_NOTEBOOK vs
    // raw SyntaxError) so the editor surfaces the same distinction open〜run.
    public string DecomposeCells(string py, out string error)
    {
        error = null;
        if (!PythonInitialized) { error = "python not ready"; return null; }
        try
        {
            // #113: decompose_for_open returns a STRUCTURED envelope ({"status","cells"/"detail"}) so the
            // failure KIND is classified in Python (version-independent) — no PythonException message
            // parsing. status: ok -> cells JSON; not_marimo -> "not a marimo notebook"; syntax_error ->
            // "syntax error: <detail>" (a DISTINCT failure, AC#2). Mirrors the run layer's NOT_A_MARIMO_
            // NOTEBOOK vs raw SyntaxError so the editor surfaces the same distinction open〜run.
            using (Py.GIL())
            using (PyObject mod = Py.Import("engine.strategy_runtime.cell_synthesis"))
            using (PyObject fn = mod.GetAttr("decompose_for_open"))
            using (PyObject res = fn.Invoke(new PyString(py ?? "")))
            using (PyObject statusObj = res.GetItem("status"))
            {
                string status = statusObj.As<string>();
                if (status == "ok")
                {
                    using (PyObject cells = res.GetItem("cells"))
                        return cells.As<string>();
                }
                if (status == "syntax_error")
                {
                    using (PyObject detail = res.GetItem("detail"))
                        error = "syntax error: " + detail.As<string>();
                    return null;
                }
                error = "not a marimo notebook";   // status == "not_marimo"
                return null;
            }
        }
        catch (Exception e)
        {
            // An UNEXPECTED seam failure (pythonnet marshalling fault / import error / transient engine
            // hiccup) — NOT a content verdict. Fail closed (Open fails rather than wrapping), but give it a
            // DISTINCT message so a valid notebook hit by a transient fault is not misdiagnosed as "not a
            // marimo notebook" (which would send the user to edit a file that is actually fine).
            error = "notebook decode failed (engine error)";
            Debug.LogWarning("[WorkspaceEngineHost] decompose_for_open failed: " + e.Message);
            return null;
        }
    }

    // #95 Phase 2 土台: per-cell RUN (pure compute) through the persistent in-proc marimo kernel
    // (engine.inproc_server.run_cell -> NotebookSession). NOTEBOOK-RUN WORKER THREAD ONLY
    // (NotebookRunLane): marimo's RuntimeContext is thread-local, so the session must be built + run
    // from ONE consistent thread; the lane guarantees that (never main, never the Replay launcher).
    // `source` is the LIVE synthesised marimo `.py` text (unsaved buffer ok); `pressedIndex` is the
    // cell-order index. Returns the backend JSON string ({"ok","ran":[{"index","output","ok"}...],
    // "error"}), or null on a not-ready server / Python error (the executor maps null to fail-soft).
    public string InvokeRunCell(string source, int pressedIndex, string scenarioJson = null, string strategyPath = null)
    {
        if (!Volatile.Read(ref _serverReady)) return null;
        try
        {
            // An empty scenario string is falsy on the Python side (no bt built) — same as omitting
            // it — so a pure-compute press needs no None marshaling.  An empty strategyPath is
            // likewise falsy (the backend leaves __file__ at the marimo default — pure-compute or
            // unbound-editor press), so no None marshaling is needed there either.
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod(
                "run_cell",
                new PyString(source ?? ""),
                new PyInt(pressedIndex),
                new PyString(scenarioJson ?? ""),
                new PyString(strategyPath ?? "")))
                return res.As<string>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] run_cell failed: " + e.Message);
            return null;
        }
    }

    // ===================== ADR-0031 S1 (#141): bt.universe.* registry bridge =====================
    // MAIN-THREAD only (BackcastWorkspaceRoot.DriveUniverseBridge in Update): main is GIL-free after
    // BeginAllowThreads, so each call acquires the GIL. DrainUniverseEdits pops the cell's pending
    // bt.universe.* edit ops as a JSON array ("" / "[]" when none); PushUniverseIds writes the C#
    // registry's current Ids back into the engine mirror so bt.universe.list() reads the SoT (D2).

    // Drain pending bt.universe.* edits (JSON array of {"op","id"}). Returns "" when the server is
    // not ready or on a seam error, which UniverseBridge.ParseEdits treats as "no edits".
    public string DrainUniverseEdits()
    {
        if (!Volatile.Read(ref _serverReady)) return "";
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod("drain_universe_edits"))
                return res.As<string>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] drain_universe_edits failed: " + e.Message);
            return "";
        }
    }

    // Push the registry's current Ids (JSON array of str) into the engine mirror for bt.universe.list().
    // Returns true iff the push actually reached the engine — so the caller clears its dirty flag ONLY on
    // a confirmed push (a no-op while !_serverReady must NOT latch the edit as pushed, or a seed made
    // during the not-ready window would be lost forever).
    public bool PushUniverseIds(string idsJson)
    {
        if (!Volatile.Read(ref _serverReady)) return false;
        try
        {
            // Honor the server ack (mirrors CallUnsubscribeMarketData): a rejected push (BAD_JSON)
            // returns false so the caller keeps its dirty flag set instead of latching the edit as
            // pushed — the "clear dirty ONLY on a CONFIRMED push" invariant (findings 0113 §Review
            // F2/F4/F5). Dispose the argument PyString too (no finalizer-thread reclamation).
            using (Py.GIL())
            using (var arg = new PyString(idsJson ?? "[]"))
            using (PyObject res = _server.InvokeMethod("push_universe_ids", arg))
            using (PyObject ok = res.GetItem("success"))
                return ok.As<bool>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] push_universe_ids failed: " + e.Message);
            return false;
        }
    }

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): edit-time stale projection through the persistent
    // in-proc session (engine.inproc_server.notebook_restage -> IncrementalNotebookSession). NOTEBOOK-RUN
    // WORKER THREAD ONLY (NotebookRunLane): the IncrementalNotebookSession is thread-guarded to the
    // thread that first drove it (the lane worker, via run_cell), so a restage from Unity main would be
    // rejected — it MUST ride the same lane. `source` is the LIVE synthesised marimo `.py` text; returns
    // the backend JSON ({"stale":[indices],"error"}), or null on a not-ready server / Python error (the
    // executor maps null to an empty stale set).
    public string InvokeNotebookRestage(string source)
    {
        if (!Volatile.Read(ref _serverReady)) return null;
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod(
                "notebook_restage",
                new PyString(source ?? "")))
                return res.As<string>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] notebook_restage failed: " + e.Message);
            return null;
        }
    }

    // #31 / #46: instrument-picker supply seam. Calls list_instruments on the persistent server
    // and returns a typed result; `success=false` carries `error_code` (LOCAL_UNIVERSE_UNAVAILABLE
    // when BACKCAST_JQUANTS_DUCKDB_ROOT is unset / listed_info.duckdb missing, LIVE_VENUE_NOT_LOGGED_IN
    // / LIVE_UNIVERSE_UNSUPPORTED / LIVE_UNIVERSE_PENDING for the live source). The provider runs
    // this on a background thread so the picker hot path never blocks UI under the GIL — DuckDB scan
    // for ~4.4k listed rows takes ~100ms.
    public struct InstrumentListResult
    {
        public bool Success;
        public string ErrorCode;
        public string[] InstrumentIds;
        // Issue #46 / review finding A5: parallel to InstrumentIds. Carries the human-readable
        // name (listed_info CompanyName for kabu/TSE, the venue's instrument name otherwise) so
        // the picker can filter on either id or name. May contain "" for individual rows where
        // the source did not provide a name (e.g. NULL CompanyName); the picker falls back to
        // the id for display in that case.
        public string[] InstrumentNames;
    }

    public InstrumentListResult InvokeListInstruments(string source, string endDate)
    {
        if (!Volatile.Read(ref _serverReady))
            return new InstrumentListResult { Success = false, ErrorCode = BackendErrorCodes.ServerNotReady, InstrumentIds = Array.Empty<string>(), InstrumentNames = Array.Empty<string>() };
        try
        {
            // Slice review F5: every PyString lives in a `using` so the picker hot path doesn't
            // leak Python refs on each fetch (matches the codebase-wide PyString hygiene; see
            // BackcastWorkspaceRoot.cs:184-185 and LiveRpcLanes.cs:116-120).
            using (Py.GIL())
            using (PyString pSrc = new PyString(source ?? "local"))
            using (PyString pDate = new PyString(endDate ?? ""))
            using (PyObject res = _server.InvokeMethod("list_instruments", pSrc, pDate))
            {
                bool success;
                using (PyObject s = res["success"]) success = s.As<bool>();
                string errorCode = "";
                using (PyObject ec = res["error_code"]) errorCode = ec.As<string>() ?? "";
                // Single walk over result.instruments[*]; id + name come from the same Python
                // InstrumentInfo row so the two parallel arrays stay index-aligned by construction
                // (the pre-rewrite double-loop + while-pad defended against skew that can't happen
                // when both fields are sourced from the same row). result["instrument_ids"] is
                // equivalent — _snapshot_to_list_result builds it from the same instruments list
                // — so we don't need to walk both.
                List<string> ids;
                List<string> names;
                using (PyObject instObj = res["instruments"])
                {
                    long n = instObj.Length();
                    ids = new List<string>((int)n);
                    names = new List<string>((int)n);
                    for (int i = 0; i < n; i++)
                    {
                        using (PyObject row = instObj[i])
                        using (PyObject idObj = row["id"])
                        using (PyObject nameObj = row["name"])
                        {
                            ids.Add(idObj.As<string>() ?? "");
                            names.Add(nameObj.As<string>() ?? "");
                        }
                    }
                }
                return new InstrumentListResult
                {
                    Success = success,
                    ErrorCode = errorCode,
                    InstrumentIds = ids.ToArray(),
                    InstrumentNames = names.ToArray(),
                };
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WorkspaceEngineHost] list_instruments failed: " + e.Message);
            return new InstrumentListResult { Success = false, ErrorCode = BackendErrorCodes.RpcError, InstrumentIds = Array.Empty<string>(), InstrumentNames = Array.Empty<string>() };
        }
    }

    // #100 Slice ① (findings 0077): document-boundary reset.  Called by BackcastWorkspaceRoot on
    // File→New / File→Open so the 4 Replay tiles drop to honest-empty when the user switches
    // strategy documents.  Backend side (clear_run_view RPC) clears engine.last_portfolio +
    // engine.last_run_summary; lane side (ResetReplaySnapshot) clears the polled snapshots so the
    // 50 ms gap between gesture and next poll renders honest-empty too.
    public void ClearReplayRunView()
    {
        if (!Volatile.Read(ref _serverReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod("clear_run_view"))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    Debug.LogWarning("[WorkspaceEngineHost] clear_run_view rejected");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] clear_run_view error (non-fatal): {e.Message}"); }
        finally { _lanes?.ResetReplaySnapshot(); }
    }

    // AFK test seam — Python-FREE probes intercept the RPC here. Null in production.
    internal Action<string, string, string, string> TestReplayPreviewOverride;
    internal Action<string, long, string> TestReplayPreviewLeftOverride;
    int _previewLeftInFlight;

    // Per-iid cold preview; IDLE/Replay guard delegated to Python (populate_replay_preview).
    // CONTRACT: start/end/granularity MUST be non-null (PyString(null) throws). The sole caller
    // BackcastWorkspaceRoot.RequestChartPreviewsForAllLiveCharts normalises Params.Start/End via
    // `?? ""` and maps GranularityChoice to literal "Minute"/"Daily" before calling. New callers
    // must do the same — null is not coalesced here (review M-S1: caller-side normalisation is SoT).
    public void RequestReplayChartPreview(string instrumentId, string start, string end, string granularity)
    {
        if (string.IsNullOrEmpty(instrumentId)) return;

        var perfTotal = System.Diagnostics.Stopwatch.StartNew();
        long memBefore = GC.GetTotalMemory(false);
        int threadId = Thread.CurrentThread.ManagedThreadId;
        var hook = TestReplayPreviewOverride;
        if (hook != null) { hook(instrumentId, start, end, granularity); return; }

        if (!Volatile.Read(ref _serverReady)) return;
        try
        {
            var perfGilAndPython = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;
            string errorCode = "";
            int barCount = -1;
            using (Py.GIL())
            using (PyString pIid = new PyString(instrumentId))
            using (PyString pStart = new PyString(start))
            using (PyString pEnd = new PyString(end))
            using (PyString pGran = new PyString(granularity))
            using (PyObject res = _server.InvokeMethod(
                "populate_replay_preview", pIid, pStart, pEnd, pGran))
            using (PyObject ok = res["success"])
            {
                success = ok.As<bool>();
                try { using (PyObject ec = res["error_code"]) errorCode = ec.As<string>() ?? ""; } catch { errorCode = ""; }
                try { using (PyObject bc = res["bar_count"]) barCount = bc.As<int>(); } catch { barCount = -1; }
                // Most no-ops (LiveManual/LiveAuto, LOADED, RUNNING, NO_DATA) are expected and
                // benign — surface only the truly-unexpected ones in DEBUG. error_code is the
                // single source of truth for "what did the engine do".
                if (!success && Debug.isDebugBuild)
                {
                    Debug.Log($"[WorkspaceEngineHost] preview {instrumentId}: {errorCode}");
                }
            }
            perfGilAndPython.Stop();
            perfTotal.Stop();
            long memAfter = GC.GetTotalMemory(false);
            Debug.Log($"[PERF chart-preview] host.preview iid={instrumentId} success={success} error={errorCode} bars={barCount} thread={threadId} gil_python_ms={perfGilAndPython.Elapsed.TotalMilliseconds:F1} total_ms={perfTotal.Elapsed.TotalMilliseconds:F1} managed_mem_delta_kb={(memAfter - memBefore) / 1024.0:F1}");
        }
        catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] preview error (non-fatal): {e.Message}"); }
    }

    public void RequestReplayPreviewLeft(string instrumentId, long beforeOpenTimeMs, string granularity)
    {
        if (string.IsNullOrEmpty(instrumentId) || beforeOpenTimeMs <= 0) return;

        var hook = TestReplayPreviewLeftOverride;
        if (hook != null) { hook(instrumentId, beforeOpenTimeMs, granularity); return; }

        if (!Volatile.Read(ref _serverReady) || Volatile.Read(ref _closing)) return;
        if (Interlocked.Exchange(ref _previewLeftInFlight, 1) == 1) return;

        new Thread(() =>
        {
            try
            {
                bool success = false;
                string errorCode = "";
                int barCount = -1;

                using (Py.GIL())
                using (PyString pIid = new PyString(instrumentId))
                using (PyInt pBefore = new PyInt(beforeOpenTimeMs))
                using (PyString pGran = new PyString(granularity ?? "Daily"))
                using (PyObject res = _server.InvokeMethod("extend_replay_preview_left", pIid, pBefore, pGran))
                using (PyObject ok = res["success"])
                {
                    success = ok.As<bool>();
                    try { using (PyObject ec = res["error_code"]) errorCode = ec.As<string>() ?? ""; } catch { errorCode = ""; }
                    try { using (PyObject bc = res["bar_count"]) barCount = bc.As<int>(); } catch { barCount = -1; }
                }

                Debug.Log($"[PERF chart-preview] host.extend_left iid={instrumentId} success={success} error={errorCode} bars={barCount} before_ms={beforeOpenTimeMs}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorkspaceEngineHost] preview extend-left error (non-fatal): {e.Message}");
            }
            finally
            {
                Volatile.Write(ref _previewLeftInFlight, 0);
            }
        }) { IsBackground = true, Name = "ReplayPreviewExtendLeft" }.Start();
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

    // ======================= Replay run + force-stop teardown =======================

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
        // #100 Slice ① (findings 0077): the prior run's stats are cleared by Python's
        // _start_engine_duckdb (engine.last_run_summary = None) before LOADED→RUNNING; the poll
        // lane picks that up via get_run_summary_json.  No C# field to clear here.
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
                    // #100 Slice ① (findings 0077): the summary_json returned by start_engine is
                    // ignored here — Python's _finalize_run wrote it to engine.last_run_summary
                    // before returning, and the lane is already polling get_run_summary_json so
                    // the C# tile sees it on the next 50 ms poll.  Single source = Python.
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

    // #76 S6b-β-clean U6: the user replay transport (pause/resume/step/set_speed) is retired from the
    // production surface (ADR-0012 reactive model — a reactive drain runs to completion, there is nothing
    // to scrub). force_stop_replay is the LONE surviving server transport call: a TEARDOWN RPC that ends
    // a synchronous start_engine so the launcher thread can exit (host lifecycle; the S6-2 teardown
    // invariant — Stop() calls it to unblock the launcher's blocking run).
    public void ForceStop()
    {
        if (!Volatile.Read(ref _serverReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _server.InvokeMethod("force_stop_replay"))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    using (PyObject em = res["error_message"])
                        Debug.LogWarning($"[WorkspaceEngineHost] force_stop_replay rejected: {em.As<string>()}");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WorkspaceEngineHost] force_stop_replay error (non-fatal): {e.Message}"); }
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

    // ---- #181 / ADR-0040: Unity uGUI modal-driven headless login RPCs --------------------
    // The login GUI lives in Unity now (no tkinter). The modal drives these three: form_init
    // (prefill), probe_station (kabu 本体起動確認), and submit (validate → headless auth →
    // finalize). All run off-main under Py.GIL; the result callback fires on the WORKER thread,
    // so the root marshals to main (DriveVenueLoginModal) before touching any VM/overlay.

    // venue_login_form_init → prefill the modal (open / mode-switch re-derive). Best-effort:
    // a failure yields a default init (empty prefill) so the modal still opens.
    public void VenueLoginFormInit(string venue, string mode, Action<VenueLoginFormInit> onResult)
    {
        var init = new VenueLoginFormInit { Venue = (venue ?? "").ToUpperInvariant(), InitialMode = mode ?? "" };
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady)) { onResult?.Invoke(init); return; }
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                using (PyObject res = _server.InvokeMethod("venue_login_form_init",
                           new PyString(venue), new PyString(mode ?? "")))
                {
                    init.InitialMode = _DictStr(res, "initial_mode", init.InitialMode);
                    init.AuthIdPrefill = _DictStr(res, "auth_id_prefill", "");
                    init.KeyPathPrefill = _DictStr(res, "key_path_prefill", "");
                    init.StationPort = _DictInt(res, "station_port", 0);
                    init.ApiPasswordPrefill = _DictStr(res, "api_password_prefill", "");
                }
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] venue_login_form_init error: " + e.Message); }
            onResult?.Invoke(init);
        }) { IsBackground = true, Name = "VenueLoginFormInit" }.Start();
    }

    // venue_login_probe_station → kabu 本体起動確認（再確認/open/mode-switch）。probe 不能なら running=true
    // を返す（非 kabu と同じ・OK 有効判定を塞がない）。
    public void VenueLoginProbeStation(string venue, string mode, Action<bool, int> onResult)
    {
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady)) { onResult?.Invoke(true, 0); return; }
        new Thread(() =>
        {
            bool running = true; int port = 0;
            try
            {
                using (Py.GIL())
                using (PyObject res = _server.InvokeMethod("venue_login_probe_station",
                           new PyString(venue), new PyString(mode ?? "")))
                {
                    running = _DictBool(res, "running", true);
                    port = _DictInt(res, "port", 0);
                }
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] venue_login_probe_station error: " + e.Message); }
            onResult?.Invoke(running, port);
        }) { IsBackground = true, Name = "VenueLoginProbe" }.Start();
    }

    // submit_venue_login → validate → headless auth → finalize. On success set_execution_mode(LiveManual)
    // (mirrors VenueLogin). SECRET 規律: the kabu API password arrives as a char[] copy (ADR-0042: the modal's
    // backing store is now a managed string in the InputField — that plaintext is NOT zeroable; only this caller-
    // owned char[] copy and the pythonnet-boundary transient string are zeroized the moment the RPC returns).
    public void SubmitVenueLogin(string venue, string mode, string fieldsJson, char[] secret,
                                 Action<VenueLoginSubmitResult> onResult)
    {
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady))
        {
            if (secret != null) Array.Clear(secret, 0, secret.Length);
            onResult?.Invoke(new VenueLoginSubmitResult { Success = false, ErrorCode = "server not ready", StatusText = "サーバ準備中です", AllowRetry = true });
            return;
        }
        if (Volatile.Read(ref _loginRunning))
        {
            if (secret != null) Array.Clear(secret, 0, secret.Length);
            onResult?.Invoke(new VenueLoginSubmitResult { Success = false, ErrorCode = "login in flight", StatusText = "ログイン処理中です", AllowRetry = true });
            return;
        }
        Volatile.Write(ref _loginRunning, true);
        new Thread(() =>
        {
            var result = new VenueLoginSubmitResult { Success = false, ErrorCode = "", StatusText = "", AllowRetry = true };
            try
            {
                using (Py.GIL())
                {
                    string transient = new string(secret ?? Array.Empty<char>());
                    using (var pv = new PyString(venue))
                    using (var pm = new PyString(mode ?? ""))
                    using (var pf = new PyString(fieldsJson ?? "{}"))
                    using (var ps = new PyString(transient))
                    using (PyObject res = _server.InvokeMethod("submit_venue_login", pv, pm, pf, ps))
                    {
                        result.Success = _DictBool(res, "success", false);
                        result.ErrorCode = _DictStr(res, "error_code", "");
                        result.StatusText = _DictStr(res, "status_text", "");
                        result.AllowRetry = _DictBool(res, "allow_retry", true);
                        if (result.Success)
                            using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
                            {
                                if (!_DictBool(m, "success", false))
                                {
                                    result.Success = false;
                                    result.ErrorCode = "set_execution_mode: " + _DictStr(m, "error_code", "");
                                    result.StatusText = "モード切替に失敗しました";
                                }
                            }
                    }
                }
            }
            catch (Exception e)
            {
                result.Success = false; result.ErrorCode = "login: " + e.Message;
                result.StatusText = "ログインに失敗しました"; result.AllowRetry = true;
            }
            finally
            {
                if (secret != null) Array.Clear(secret, 0, secret.Length);   // zeroize the transient char[]
                Volatile.Write(ref _loginRunning, false);
                onResult?.Invoke(result);
            }
        }) { IsBackground = true, Name = "WorkspaceVenueLoginSubmit" }.Start();
    }

    // dict 読みヘルパ（キー不在は default・pythonnet GetItem は不在で throw する）。
    static string _DictStr(PyObject d, string key, string dflt)
    {
        try { using (PyObject v = d.GetItem(key)) return v.As<string>(); } catch { return dflt; }
    }
    static int _DictInt(PyObject d, string key, int dflt)
    {
        try { using (PyObject v = d.GetItem(key)) return v.As<int>(); } catch { return dflt; }
    }
    static bool _DictBool(PyObject d, string key, bool dflt)
    {
        try { using (PyObject v = d.GetItem(key)) return v.As<bool>(); } catch { return dflt; }
    }

    // venue_logout (Venue→Disconnect; findings 0027 D5). Mirrors ProductionLiveShell.Disconnect: gated
    // by the LiveLogoutCoordinator quiet-lane (D7 Wall 1) so it can't fire while a write is in flight,
    // runs off-main under the GIL, and reuses _loginRunning so login/logout are mutually exclusive. The
    // persistent server + replay survive (ADR-0010: venue_state and replay_state are independent); the
    // continuous poll then reports DISCONNECTED and DriveFooter's G1 tears down any active LiveAuto run.
    public void VenueLogout(Action<bool> onResult)
    {
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady)) { onResult?.Invoke(false); return; }
        if (!_coord.RequestLogout()) { onResult?.Invoke(false); return; }      // write in flight → defer
        if (!_coord.ConsumePendingLogout()) { onResult?.Invoke(false); return; }
        // Join the SAME single-flight as set/start/stop (not just _loginRunning) so a Disconnect can't
        // interleave venue_logout with another live RPC under the GIL (review High). The disconnect path
        // with an active run goes through StopLiveThenLogout instead.
        if (!BeginLiveRpc()) { onResult?.Invoke(false); return; }
        new Thread(() =>
        {
            bool ok = false;
            try
            {
                // Match the shell: the venue_logout return is not contractually a {success} dict, so a
                // clean (exception-free) invoke IS the success signal; the poll carries the new state.
                using (Py.GIL())
                using (_server.InvokeMethod("venue_logout")) { }
                ok = true;
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] venue_logout error: " + e.Message); }
            finally { EndLiveRpc(); onResult?.Invoke(ok); }
        }) { IsBackground = true, Name = "WorkspaceVenueLogout" }.Start();
    }

    // Disconnect WITH an active LiveAuto run (review High): stop the run, leave Live (Replay), THEN
    // venue_logout — all on ONE worker under ONE single-flight + the logout quiet-lane gate. Sequencing
    // it here means venue_logout's _teardown_live_components (live_orchestrator.py:848-851) can't race the
    // footer's poll-driven auto-replay recovery, and logout can't interleave with another live RPC. The
    // venue always leaves regardless of the graceful-stop result (venue_logout's teardown is the backstop);
    // ok reports whether the whole disconnect completed without throwing.
    public void StopLiveThenLogout(string runId, Action<bool> onResult)
    {
        if (string.IsNullOrEmpty(runId)) { VenueLogout(onResult); return; }
        if (Volatile.Read(ref _closing) || !Volatile.Read(ref _serverReady)) { onResult?.Invoke(false); return; }
        if (!_coord.RequestLogout()) { onResult?.Invoke(false); return; }
        if (!_coord.ConsumePendingLogout()) { onResult?.Invoke(false); return; }
        if (!BeginLiveRpc()) { onResult?.Invoke(false); return; }
        new Thread(() =>
        {
            bool ok = false;
            try
            {
                using (Py.GIL())
                {
                    bool stopped;
                    using (PyObject s = _server.InvokeMethod("stop_live_strategy", new PyString(runId)))
                        stopped = s["success"].As<bool>();
                    if (!stopped) Debug.LogWarning("[WorkspaceEngineHost] disconnect: graceful stop_live_strategy failed; venue_logout teardown is the backstop");
                    // Leave Live + drop the venue regardless — the user asked to disconnect.
                    using (_server.InvokeMethod("set_execution_mode", new PyString("Replay"))) { }
                    using (_server.InvokeMethod("venue_logout")) { }
                    ok = true;
                }
            }
            catch (Exception e) { Debug.LogWarning("[WorkspaceEngineHost] stop-then-logout error: " + e.Message); }
            finally { EndLiveRpc(); onResult?.Invoke(ok); }
        }) { IsBackground = true, Name = "WorkspaceStopThenLogout" }.Start();
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
                    {
                        if (r["success"].As<bool>()) sid = r["strategy_id"].As<string>();
                        else Debug.LogWarning("[WorkspaceEngineHost] register_live_strategy failed: " + r["error_code"].As<string>()
                                              + " — " + r["error_message"].As<string>());
                    }
                    if (sid != null)
                        using (PyObject s = _server.InvokeMethod("start_live_strategy",
                                   new PyString(sid), new PyString(instrumentId), new PyString(venue)))
                        {
                            ok = s["success"].As<bool>();
                            if (ok) runId = s["run_id"].As<string>();
                            // A clean success=false (no exception) was previously SILENT — surface the
                            // engine's reason so a footer ▶ that doesn't start is diagnosable.
                            else Debug.LogWarning("[WorkspaceEngineHost] start_live_strategy failed (iid=" + instrumentId +
                                                  ", venue=" + venue + "): " + s["error_code"].As<string>()
                                                  + " — " + s["error_message"].As<string>());
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
