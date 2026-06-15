// ScenarioStartupHitlHarness.cs — issue #29 "Replay 実行設定パネル" (HITL playmode gate)
//
// Owner-run playmode harness for the PRODUCTION Replay path configured FROM THE UI (AC⑤:
// no harness-hardcoded run params). Press Play → a Hakoniwa grid shows a Startup tile
// (slot 0; ScenarioStartupTile) + a chart tile. The owner edits granularity / cash /
// start / end / universe, presses Run, and candles advance bar-by-bar as the engine runs.
//
// PRODUCTION PATH (CONTEXT "backcast Replay 起動経路"): on Run the controller validates +
// writes the v3 sidecar, then a launcher thread calls DataEngine.load_replay_data (IDLE→
// LOADED, primes the catalog) and the InprocLiveServer.start_engine RPC (LOADED→RUNNING,
// reads the sidecar via load_scenario, streams). NO RustBacktestSink — a poll thread reads
// get_state_json and the chart is decoded GIL-free on main (ReplayBarDecoder). start_engine
// blocks, but engine_run's per-bar sleep releases the GIL so polls interleave (bar-by-bar).
//
// VERIFICATION NOTE: this needs a display + a real catalog, so it is OWNER-RUN (HITL). The
// Python-FREE config/persist/restore/validation/run-gate logic is covered headless by
// ScenarioStartupProbe (AFK). Guarded out of batchmode so the headless COMPILE gate
// (`-batchmode -nographics -quit`) never inits Python or renders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using Python.Runtime;

public class ScenarioStartupHitlHarness : MonoBehaviour
{
    // Per-machine paths come from .env / env vars (owner 2026-06-14), NOT hardcoded — external
    // storage differs per 端末. Resolved at Start() via EnvConfig:
    //   * catalog (external): BACKCAST_CATALOG_PATH (full path) OR ARTIFACTS_PATH + /jquants-catalog
    //     (the same ARTIFACTS_PATH key engine.paths.jquants_catalog_path reads). NOT a tile field
    //     (CONTEXT "catalog_path（環境/配置の関心）").
    //   * strategy (repo fixture, machine-independent): BACKCAST_HITL_STRATEGY OR the repo
    //     spike_buy_sell.py derived from PythonRuntimeLocator.ProjectRoot (= <repo>/python).
    string _strategyFile;
    string _catalogPath;

    const int POLL_INTERVAL_MS = 150;

    static bool s_pythonBootstrapped;

    readonly ScenarioStartupController _ctrl = new ScenarioStartupController();
    ScenarioStartupTile _tile;

    // launcher → main (only C# strings/flags cross)
    string _startError;
    Thread _launcher;
    Thread _poll;
    volatile bool _running;     // a run is in flight: ignore re-entrant Run clicks
    volatile bool _runFinished; // launcher reached its finally (run done/failed) — for terminal status
    bool _finishedHandled;      // main: terminal status shown once per run
    volatile bool _pollStop;
    volatile bool _pollServerReady;
    volatile PyObject _pollServer;
    volatile string _latestStateJson;

    // run-params snapshot (taken on main before the launcher thread starts)
    string[] _runInstruments;
    string _runStart, _runEnd, _runGran, _runStrategy;

    // #30 transport footer. ReplayLifecycle + ReplayTransportViewModel + ReplayFooterView are the
    // DURABLE parity surface; this owner-run harness is the throwaway host that drives them (poll
    // → lifecycle, launcher result → terminal, footer clicks → InprocLiveServer transport RPCs).
    readonly ReplayLifecycle _lifecycle = new ReplayLifecycle();
    ReplayTransportViewModel _transport;
    ReplayFooterView _footer;
    ReplayPhase? _lastFooterPhase;   // gate footer Refresh to actual phase changes (not 60fps)

    [Serializable] struct _StateLite { public string replay_state; }

    // chart render — the reusable production widget (#53) owns candle/axis render + #44 theme-follow.
    RectTransform _chartArea;
    ChartView _chartView;
    Text _statusText;
    Font _font;
    string _lastPayload;
    int _renderedCount;
    bool _errLogged;

    // issue #44: the bars-count status text color sources the theme. Chart colors moved into ChartView (#53).
    static readonly Color TEXT = ThemeService.Current.colors.text;

    // #59: DEMOTED. The Backcast workspace root (BackcastWorkspaceRoot, scene-authored) is now the
    // single normal Play entry + Python owner (ADR-0009); the Replay engine path is extracted into
    // the durable ReplayEngineHost. This throwaway harness no longer auto-claims Play (findings 0025
    // §5). It is kept for reference; it is reachable only if re-enabled by hand.
    const bool AutoBootstrapEnabled = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;
        if (Application.isBatchMode) return;
        var go = new GameObject("ScenarioStartupHitlHarness");
        DontDestroyOnLoad(go);
        go.AddComponent<ScenarioStartupHitlHarness>();
    }

    void Start()
    {
        try
        {
            ResolvePaths();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUi();

            // populate the tile from the sidecar (or defaults). The .py inline SCENARIO
            // fallback would need a pythonnet load_scenario read; deferred — sidecar/defaults
            // cover the slice.
            _ctrl.Populate(_strategyFile, DateTime.Now);
            _tile.SyncFieldsFromController();

            if (!s_pythonBootstrapped)
            {
                // #59 single Play-owner guard (findings 0025 §7): if another owner (the workspace
                // root) already holds the interpreter, refuse rather than double-init (SIGSEGV).
                if (PythonEngine.IsInitialized)
                {
                    Debug.LogWarning("[SCENARIO STARTUP HITL] PythonEngine already owned — refusing engine init (disable BackcastWorkspaceRoot to run this harness solo).");
                    return;
                }
                PythonRuntimeLocator.ConfigureBeforeInitialize();
                PythonEngine.Initialize();
                PythonEngine.BeginAllowThreads();
                s_pythonBootstrapped = true;
                Debug.Log("[SCENARIO STARTUP HITL] Python initialized; main is GIL-free.");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[SCENARIO STARTUP HITL] FAIL (init): " + e);
        }
    }

    // Resolve per-machine paths from .env / env vars (owner: external storage differs per 端末).
    void ResolvePaths()
    {
        // catalog: explicit full path wins, else ARTIFACTS_PATH/jquants-catalog (engine.paths parity),
        // else repo-relative artifacts/jquants-catalog (engine.paths default — likely empty, surfaces).
        string explicitCatalog = EnvConfig.Get("BACKCAST_CATALOG_PATH");
        if (!string.IsNullOrEmpty(explicitCatalog))
        {
            _catalogPath = explicitCatalog;
        }
        else
        {
            string artifacts = EnvConfig.Get("ARTIFACTS_PATH",
                System.IO.Path.Combine(Directory.GetParent(PythonRuntimeLocator.ProjectRoot).FullName, "artifacts"));
            _catalogPath = System.IO.Path.Combine(artifacts, "jquants-catalog");
        }

        // strategy: kernel-native repo fixture (machine-independent) unless overridden.
        // ADR-0006 (#49): the production Replay now runs through the nautilus-free kernel, so
        // the default must be a engine.kernel.strategy.Strategy subclass — the nautilus
        // spike_buy_sell.py would fail the kernel loader and re-introduce nautilus.
        _strategyFile = EnvConfig.Get("BACKCAST_HITL_STRATEGY",
            System.IO.Path.Combine(PythonRuntimeLocator.ProjectRoot, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"));

        Debug.Log($"[SCENARIO STARTUP HITL] catalog={_catalogPath} strategy={_strategyFile}");
    }

    // Run button → validate + supplyability gate, then launch the production path.
    void OnRun()
    {
        // Re-entrancy guard: a run is synchronous on the engine side and gated to IDLE→LOADED,
        // so a second click while running would fail load_replay_data and flip the live run to
        // FAILED. Ignore clicks until the current run finishes (Launcher clears _running).
        if (Volatile.Read(ref _running)) return;

        RunGateResult gate;
        try
        {
            var provider = new BoundStrategyFileProvider(_strategyFile);
            gate = _ctrl.TryStartRun(provider); // validates + writes the sidecar (may do file I/O)
        }
        catch (Exception e)
        {
            // A sidecar write failure (locked/non-NTFS dest) must surface, not abort silently.
            _tile.ShowRunMessage("Could not save scenario: " + e.Message);
            Debug.LogError("[SCENARIO STARTUP HITL] commit failed: " + e);
            return;
        }
        if (!gate.IsReady)
        {
            _tile.ShowRunMessage(gate.Message);
            Debug.LogWarning("[SCENARIO STARTUP HITL] run blocked: " + gate.Message);
            return;
        }
        _tile.ShowRunMessage(null);

        // Snapshot run params on main (the worker thread must not touch the controller).
        _runInstruments = new List<string>(_ctrl.Universe.Ids).ToArray();
        _runStart = _ctrl.Params.Start;
        _runEnd = _ctrl.Params.End;
        _runGran = ScenarioStartupParams.GranularityToString(_ctrl.Params.Granularity);
        _runStrategy = gate.StrategyPath;

        _renderedCount = 0;
        _lastPayload = null;
        _startError = null;
        _errLogged = false;
        Volatile.Write(ref _runFinished, false);
        _finishedHandled = false;
        if (_statusText != null) _statusText.text = "Running…";

        // #30: a fresh run clears the terminal latch + resets the footer's 1x highlight (the
        // engine likewise resets speed on start_engine). Phase tracks the new run via the poll.
        _transport?.OnRunStarted();

        Volatile.Write(ref _running, true);
        _launcher = new Thread(Launcher) { IsBackground = true, Name = "ScenarioStartupLauncher" };
        _launcher.Start();

        if (_poll == null)
        {
            _poll = new Thread(PollLoop) { IsBackground = true, Name = "ScenarioStartupPoll" };
            _poll.Start();
        }
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
                // DuckDB root from env (BACKCAST_JQUANTS_DUCKDB_ROOT) and Replay streams via the
                // nautilus-free kernel — no nautilus_catalog_path ctor arg anymore (_catalogPath
                // is vestigial, kept only for the owner-facing log above).
                PyObject dataEngine = dataEngCls.Invoke(Array.Empty<PyObject>());
                server = inprocCls.Invoke(dataEngine);

                // load_replay_data(instrument_ids, start, end, granularity) → IDLE→LOADED.
                using (PyList insts = new PyList())
                {
                    foreach (string id in _runInstruments) insts.Append(new PyString(id));
                    using (PyObject res = dataEngine.InvokeMethod(
                        "load_replay_data", insts, new PyString(_runStart), new PyString(_runEnd), new PyString(_runGran)))
                    using (PyObject ok = res[0])
                    {
                        if (!ok.As<bool>())
                        {
                            using (PyObject msg = res[1]) Volatile.Write(ref _startError, "load_replay_data: " + msg.As<string>());
                        }
                    }
                }

                if (Volatile.Read(ref _startError) == null)
                {
                    _pollServer = server;
                    Volatile.Write(ref _pollServerReady, true);
                }
            } // GIL released → poll thread can run.

            if (Volatile.Read(ref _startError) != null) return;

            // start_engine RPC (synchronous). engine_run's per-bar sleep releases the GIL so
            // the poll thread reads the incrementally-streamed chart between bars.
            using (Py.GIL())
            using (PyDict cfg = new PyDict())
            {
                cfg.SetItem("strategy_file", new PyString(_runStrategy));
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
            // start_engine is synchronous, so reaching here means the run finished (or failed):
            // clear the re-entrancy guard so the owner can configure + Run again.
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
                    Debug.LogWarning("[SCENARIO STARTUP HITL] poll error (non-fatal): " + e.Message);
                }
            }
            Thread.Sleep(POLL_INTERVAL_MS);
        }
    }

    void Update()
    {
        string err = Volatile.Read(ref _startError);
        if (err != null && !_errLogged)
        {
            if (_statusText != null) _statusText.text = "FAILED: " + err;
            Debug.LogError("[SCENARIO STARTUP HITL] FAIL: " + err);
            _errLogged = true;
        }

        // A run that COMPLETES with zero streamed bars (e.g. the date range has no data for the
        // instrument — its DuckDB ends before the requested range) otherwise leaves the status on
        // "Running…" forever, because the "bars: N" transition below only fires when bars stream.
        // Surface a terminal status on completion so the owner gets feedback instead of an
        // apparent hang. N>0 runs already show "bars: N" via streaming, so only the 0-bar case
        // needs this terminal override.
        if (Volatile.Read(ref _runFinished) && !_finishedHandled)
        {
            _finishedHandled = true;
            // #30: terminal authority is the launcher result (poll goes IDLE after force_stop, so
            // it can't carry Done/Failed). Latch it on the lifecycle so the footer disables
            // transport + the ▶ re-arms on the next click.
            if (err != null) _lifecycle.MarkFailed(err); else _lifecycle.MarkDone();
            if (err == null && _renderedCount == 0)
            {
                if (_statusText != null) _statusText.text = "DONE: 0 bars — no data in the date range?";
                Debug.Log("[SCENARIO STARTUP HITL] run complete with 0 streamed bars (empty/future date range?)");
            }
        }

        string state = Volatile.Read(ref _latestStateJson);
        if (state != null && state != _lastPayload)
        {
            _lastPayload = state;
            // #30: feed the engine replay_state (IDLE/LOADED/RUNNING/PAUSED) to the lifecycle so
            // the footer ▶/⏸ + enablement track pause/resume. The terminal latch (above) wins.
            try { _lifecycle.ApplyPoll(JsonUtility.FromJson<_StateLite>(state).replay_state); }
            catch { /* malformed poll snapshot: keep the last phase */ }

            ReplayBarFrame frame = ReplayBarDecoder.Decode(state);
            if (frame.Ohlc != null && frame.Ohlc.Count != _renderedCount)
            {
                _chartView.Render(frame);
                _renderedCount = frame.Ohlc.Count;
                if (_statusText != null && err == null) _statusText.text = $"bars: {_renderedCount}";
            }
        }

        // Refresh the footer only when the phase actually changed (clicks Refresh themselves):
        // the durable view's setters early-out on unchanged values, but skipping the per-frame
        // call avoids needless GetComponent churn during a streaming run.
        if (_footer != null)
        {
            ReplayPhase phase = _lifecycle.Phase;
            if (_lastFooterPhase != phase) { _lastFooterPhase = phase; _footer.Refresh(); }
        }
    }

    // ---- #30 footer click handlers: decide the intent from the VM, marshal it over the GIL to
    // the InprocLiveServer transport forwarders (T1: a brief main-thread GIL acquire on click;
    // the per-bar sleep keeps the GIL available, and the calls are O(1)). ----
    void OnFooterPlayPause()
    {
        switch (_transport.PlayPauseIntent())
        {
            case ReplayTransportIntent.Run: OnRun(); break;          // re-arm / launch (OnRun resets)
            case ReplayTransportIntent.Pause: CallTransport("pause_replay"); break;
            case ReplayTransportIntent.Resume: CallTransport("resume_replay"); break;
        }
    }

    void OnFooterStep() { CallTransport("step_replay"); }
    void OnFooterStop() { CallTransport("force_stop_replay"); }
    void OnFooterSpeed(int mult) { if (_transport.SelectSpeed(mult)) CallTransportSpeed(mult); }

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
                {
                    using (PyObject em = res["error_message"])
                        Debug.LogWarning($"[SCENARIO STARTUP HITL] {method} rejected: {em.As<string>()}");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SCENARIO STARTUP HITL] {method} error (non-fatal): {e.Message}");
        }
    }

    void CallTransportSpeed(int mult)
    {
        if (!Volatile.Read(ref _pollServerReady)) return;
        try
        {
            using (Py.GIL())
            using (PyObject res = _pollServer.InvokeMethod("set_replay_speed", new PyInt(mult)))
            using (PyObject ok = res["success"])
            {
                if (!ok.As<bool>())
                    Debug.LogWarning($"[SCENARIO STARTUP HITL] set_replay_speed({mult}) rejected");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SCENARIO STARTUP HITL] set_replay_speed error (non-fatal): {e.Message}");
        }
    }

    // ---- UI: a 2-cell Hakoniwa grid [startup, chart] (PanelKind::Startup at slot 0) ----
    void BuildUi()
    {
        var canvasGo = new GameObject("ScenarioStartupCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // EventSystem for InputField interaction (HITL). The project uses the new Input System
        // (activeInputHandler=1), so a legacy StandaloneInputModule throws every frame and
        // input never reaches the tile — use InputSystemUIInputModule like the other harnesses
        // (Hakoniwa/FloatingWindow/StrategyEditor/InfiniteCanvas). The scene may carry MORE THAN
        // ONE EventSystem (scene-owned + harness-owned), so disable EVERY legacy module — a single
        // FindAnyObjectByType only fixes one of them and the others keep throwing.
        foreach (var legacy in FindObjectsByType<StandaloneInputModule>(FindObjectsSortMode.None))
            legacy.enabled = false;

        var existing = FindAnyObjectByType<EventSystem>();
        if (existing == null)
        {
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
        }
        else
        {
            var module = existing.GetComponent<InputSystemUIInputModule>();
            if (module == null) module = existing.gameObject.AddComponent<InputSystemUIInputModule>();
            module.enabled = true;
        }

        var root = new GameObject("HakoniwaRoot", typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(canvasGo.transform, false);
        Stretch(root);
        // #30: reserve a bottom strip for the screen-fixed transport footer so the grid doesn't
        // sit under it (the footer overlays the canvas, anchored to the bottom).
        const float footerPx = 40f;
        root.offsetMin = new Vector2(0f, footerPx);

        var startupTile = new GameObject("startup", typeof(RectTransform)).GetComponent<RectTransform>();
        startupTile.SetParent(root, false);
        var chartTile = new GameObject("chart", typeof(RectTransform)).GetComponent<RectTransform>();
        chartTile.SetParent(root, false);

        // Hakoniwa places the two tiles (slot 0 = startup, slot 1 = chart).
        new HakoniwaController(root,
            new Dictionary<string, RectTransform> { { "startup", startupTile }, { "chart", chartTile } },
            new[] { "startup", "chart" });

        _chartArea = chartTile;
        // production candlestick widget (#53) — owns bg + axes + candles (no title bar in the
        // scenario tile). The bars-count status text is added AFTER, so it overlays on top.
        _chartView = chartTile.gameObject.AddComponent<ChartView>();
        _chartView.Build(_chartArea, showTitleBar: false);

        _statusText = MakeText(_chartArea, "idle", 12, TextAnchor.UpperRight);

        _tile = new ScenarioStartupTile(_ctrl, OnRun, _font);
        _tile.Build(startupTile);

        // #30 transport footer: a screen-fixed bottom bar (40px) hosting play/pause/step/speed/stop.
        var footerBar = new GameObject("footer", typeof(RectTransform)).GetComponent<RectTransform>();
        footerBar.SetParent(canvasGo.transform, false);
        footerBar.anchorMin = new Vector2(0f, 0f);
        footerBar.anchorMax = new Vector2(1f, 0f);
        footerBar.pivot = new Vector2(0.5f, 0f);
        footerBar.anchoredPosition = Vector2.zero;
        footerBar.sizeDelta = new Vector2(0f, 40f);

        _transport = new ReplayTransportViewModel(_lifecycle);
        _footer = new ReplayFooterView(
            _transport, OnFooterPlayPause, OnFooterStep, OnFooterStop, OnFooterSpeed, _font);
        _footer.Build(footerBar);
    }

    Text MakeText(RectTransform parent, string text, int size, TextAnchor anchor)
    {
        var go = new GameObject("text", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(-6f, -6f); rt.sizeDelta = new Vector2(-12f, 20f);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = TEXT; t.text = text; t.fontSize = size; t.alignment = anchor;
        return t;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    void OnDestroy()
    {
        Volatile.Write(ref _pollStop, true);
        if (_poll != null && _poll.IsAlive) _poll.Join(500);
        // Interpreter stays alive for the process (S0-sanctioned); next Play reuses it.
        Debug.Log("[SCENARIO STARTUP HITL] OnDestroy: poll stopped; interpreter left alive.");
    }
}

// A minimal IStrategyFileProvider for the harness: supplyable when the bound .py exists
// (the strategy editor's richer provider plugs in here in the mainline app; #16 seam).
public sealed class BoundStrategyFileProvider : IStrategyFileProvider
{
    readonly string _path;
    public BoundStrategyFileProvider(string path) { _path = path; }

    public bool TryGetStrategyFile(out string path)
    {
        path = _path;
        return !string.IsNullOrEmpty(_path) && System.IO.File.Exists(_path);
    }
}
