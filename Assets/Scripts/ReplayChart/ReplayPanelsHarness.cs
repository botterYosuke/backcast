// ReplayPanelsHarness.cs — issue #11 "Replay panels" (M3, THROWAWAY HITL visual gate)
//
// THROWAWAY turnkey playmode widget the OWNER runs once to SEE, in ONE Play, the replay
// candlestick chart advance NEXT TO four live side panels — status / positions / orders
// / run_result — all fed by the SAME production replay seam. It builds its OWN runtime
// Canvas + uGUI, depends on NOTHING in SampleScene, and will be deleted when the mainline
// scene/DI lands (#5/#7). Keep it minimal.
//
// Owner flow: open the project, press Play -> an auto-bootstrap spawns this MonoBehaviour
// -> it starts the production Nautilus replay (TRADING fixture spike_buy_sell.py, so the
// run submits a BUY at bar 3 and a SELL at bar 40) -> candles advance bar-by-bar while
// the four panels fill in.
//
// PANEL DATA SOURCES (docs/findings/0003-replay-panels.md §2; NOT get_state_json):
//   * status      = ReplayRunLifecycle (Idle->Running->Done/Failed): MarkRunning when we
//                   launch the run, Observe(sink) drives Done/Failed off the terminal sink
//                   callbacks. PLUS execution_mode / venue_state from the AC4 poll (§5).
//   * positions   = ReplayEventSink.TryDequeuePortfolio -> ReplayPanelDecoder.DecodePortfolio
//   * orders      = ReplayEventSink.TryDequeueOrder     -> ReplayPanelDecoder.DecodeOrder
//   * run_result  = push_run_complete summary           -> ReplayPanelDecoder.DecodeRunResult
//   * chart       = ReplayEventSink.TryDequeueBar       -> ReplayBarDecoder.Decode (reuse #10)
//
// AC4 — NON-STALL GIL-MARSHALED POLL (findings 0003 §5): a DEDICATED poll thread takes
// Py.GIL() and calls server.get_state_json() at ~6.7Hz (within the 4-10Hz budget),
// writing the result into a latest-wins `volatile string _latestStateJson`. MAIN never
// takes the GIL — it only reads that snapshot GIL-free and decodes execution_mode /
// venue_state. While BOTH the Python daemon backtest thread AND this poll thread contend
// for the GIL, main must keep an in-budget frame cadence. We assert that STRUCTURALLY
// (the AC4 false-green kill): after a warmup, count frames whose dt exceeds HITCH_DT_S;
// PASS requires hitches <= MAX_HITCHES across >= TARGET_FRAMES frames. A systematic
// per-bar GIL stall on main would show ~1 hitch per bar; an occasional GC hitch stays
// under the budget. poll's replay_state is IGNORED (IDLE-pinned misinfo, §1); only
// execution_mode/venue_state are consumed (both static config here: Replay / DISCONNECTED).
//
// SINGLE PLAY-OWNER / DOUBLE-INIT STATIC GUARD (findings 0003 §4): exactly ONE
// auto-bootstrap may own a Play. ReplayChartHarness(#10) is flag-OFF
// (AutoBootstrapEnabled=false). As a safety net, Start() FAILS LOUDLY if Python is
// already initialized by a foreign bootstrap (PythonEngine.IsInitialized true while we
// have not bootstrapped) — turning the known double-init/GIL-contention failure mode into
// an explicit FAIL instead of a silently non-green gate.
//
// THREADING (proven S1/#10 pattern):
//   * main: ConfigureBeforeInitialize -> Initialize -> BeginAllowThreads (release the main
//     GIL, NEVER reacquire) -> start a LAUNCHER thread + a POLL thread. Main then drains
//     the sink GIL-free every Update() and renders pure C# uGUI.
//   * launcher: takes Py.GIL(), imports engine, builds the 68-bar cfg with rust_sink = a
//     C# ReplayEventSink, calls start_nautilus_replay (spawns its OWN daemon backtest
//     thread and RETURNS), publishes the server PyObject for the poll thread, then
//     RELEASES the GIL by leaving the using-block. The launcher dies immediately.
//   * poll: loops at ~6.7Hz; each tick takes Py.GIL(), calls server.get_state_json(),
//     stores the JSON, releases. A flag-honoring C# loop (UNLIKE the daemon), so OnDestroy
//     can stop+Join it safely — preventing poll-thread accumulation across repeat editor
//     plays (background threads survive playmode-stop inside the editor process).
//   * daemon (Python backtest thread): acquires the GIL itself, streams 68 bars,
//     duck-calls push_bar/push_order/push_portfolio per event, then push_run_complete.
//
// TEARDOWN (DECIDED, mirrors #10): OnDestroy does NOT call PythonEngine.Shutdown() and
// does NOT EndAllowThreads() — the daemon backtest thread is nondeterministic and
// reacquiring the GIL on main to Shutdown could DEADLOCK. The interpreter persists for the
// process (s_pythonBootstrapped makes the next Play reuse it without re-Init). The POLL
// thread, being our own flag-honoring C# loop, IS stopped+joined.
//
// HEADLESS COMPILE GATE: the auto-bootstrap is guarded by Application.isBatchMode, so
// `Unity -batchmode -nographics -quit` only compiles this script and quits — it never inits
// Python or renders. No .meta authored here — Unity generates it on next import.

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Python.Runtime;

public class ReplayPanelsHarness : MonoBehaviour
{
    // cfg fixture — the TRADING fixture (#11): same 68-bar 8918.TSE Daily window as
    // #9/#10, but it submits a BUY at bar 3 and a SELL at bar 40 so the panels fill.
    const string STRATEGY_FILE = "/Users/sasac/backcast/python/spike/fixtures/strategies/spike_buy_sell.py";
    const string CATALOG_PATH  = "/Users/sasac/backcast/python/spike/fixtures/jquants-catalog";
    const string INSTRUMENT    = "8918.TSE";
    const string START_DATE    = "2024-10-01";
    const string END_DATE      = "2025-01-10";
    const string GRANULARITY   = "Daily";
    const long   INITIAL_CASH  = 10_000_000;

    // AC4 frame-cadence budget (the false-green kill).
    const int   TARGET_FRAMES   = 300;    // >= 300 frames = main kept ticking (S0 parity)
    const int   WARMUP_FRAMES   = 60;     // ignore editor JIT/asset-load hitches at startup
    const float HITCH_DT_S      = 0.20f;  // a post-warmup frame slower than 200ms = a hitch
    const int   MAX_HITCHES     = 5;      // a few GC hitches OK; ~1/bar = a real GIL stall -> FAIL

    // Poll cadence: ~6.7Hz, within findings §5's 4-10Hz (kind to the daemon's GIL).
    const int   POLL_INTERVAL_MS = 150;

    // Process-lifetime guard: Python Initialized + main GIL released exactly ONCE per Unity
    // process. A repeat Play reuses it (no re-Init, no second BeginAllowThreads).
    static bool s_pythonBootstrapped;

    // launcher -> main: non-null => the launcher failed (only a C# string crosses).
    string _startError;

    // The C# sink: constructed on main, drained on main (GIL-free), pushed-to by the
    // Python daemon backtest thread under the GIL.
    ReplayEventSink _sink;
    Thread _launcher;

    // --- AC4 poll plumbing ---
    Thread          _poll;
    volatile bool   _pollStop;
    volatile bool   _pollServerReady; // launcher sets true once the server is live
    volatile PyObject _pollServer;    // the live InprocLiveServer; published launcher->poll (volatile: matches ReplayEventSink cross-thread discipline on Mono)
    volatile string _latestStateJson; // latest-wins poll snapshot (single slot, not a queue)
    long            _pollSamples;     // Interlocked: # successful get_state_json reads (AC4 proof)

    // status panel source of truth (main-thread owned: MarkRunning in Start, Observe/MarkFailed in Update).
    readonly ReplayRunLifecycle _lifecycle = new ReplayRunLifecycle();

    // main-thread render/progress state
    int   _frameCount;
    int   _hitchFrames;
    float _maxDtAfterWarmup;
    bool  _errLogged;
    bool  _passLogged;
    bool  _markFailedDone;

    // chart
    string _lastBarPayload;
    int    _renderedCount;

    // panels decoded state
    readonly List<OrderRow> _orderRows = new List<OrderRow>();
    PortfolioSnapshot _portfolio;
    int    _portfoliosDecoded;
    bool   _haveRunResult;
    RunResult _runResult;

    // poll-decoded status (GIL-free decode on main from _latestStateJson)
    string _executionMode = "—";
    string _venueState    = "—";

    // change-driven status re-render (init to sentinels so the first frame renders)
    RunStatus _renderedStatus      = (RunStatus)(-1);
    int       _renderedPollSamples = -1;

    // runtime uGUI
    RectTransform _chartArea;   // persisted-layout PANEL (offset-zero; box == Default chart rect)
    RectTransform _plotArea;    // CHILD inset = axis-label gutter (widget chrome; NOT persisted)
    RectTransform _candleRoot;
    Text _statusText;
    Text _positionsText;
    Text _ordersText;
    Text _runResultText;
    Font _font;

    static readonly Color BG_COLOR   = new Color(0.08f, 0.08f, 0.10f, 1f);
    static readonly Color PANEL_BG   = new Color(0.13f, 0.13f, 0.16f, 1f);
    static readonly Color AXIS_COLOR = new Color(0.55f, 0.55f, 0.60f, 1f);
    static readonly Color UP_COLOR   = new Color(0.20f, 0.80f, 0.35f, 1f);
    static readonly Color DOWN_COLOR = new Color(0.85f, 0.28f, 0.28f, 1f);
    static readonly Color TEXT_COLOR = new Color(0.90f, 0.90f, 0.92f, 1f);

    // SINGLE PLAY-OWNER toggle (findings 0005 §6 / 0011 exercise discipline). This harness is the
    // DEFAULT Play owner (ReplayChartHarness / S2SpikeLiveLoopHarness yield with their own flag=false).
    // Flip this to false to free the interpreter for ANOTHER menu-driven leg that owns Play —
    // e.g. the #20 "Live Adapter Tracer HITL" (Tools > Backcast). Restore to true afterwards.
    const bool AutoBootstrapEnabled = true;

    // TURNKEY auto-bootstrap: owner just presses Play. Guarded OUT of batchmode so the
    // headless compile gate (`-batchmode -nographics -quit`) never inits Python / renders.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;   // yield Play to another menu-driven owner (e.g. #20 Live tracer HITL)
        if (Application.isBatchMode) return;
        var go = new GameObject("ReplayPanelsHarness");
        DontDestroyOnLoad(go);
        go.AddComponent<ReplayPanelsHarness>();
    }

    void Start()
    {
        try
        {
            BuildUi();

            if (!s_pythonBootstrapped)
            {
                // DOUBLE-INIT STATIC GUARD (findings 0003 §4): if Python is already up but
                // WE didn't bootstrap it, a foreign auto-bootstrap (e.g. a re-enabled
                // ReplayChartHarness) owns the interpreter -> explicit FAIL.
                if (PythonEngine.IsInitialized)
                {
                    Volatile.Write(ref _startError,
                        "double-init: PythonEngine already initialized by another bootstrap " +
                        "(only one Play-owner allowed; is ReplayChartHarness AutoBootstrapEnabled?)");
                    Debug.LogError("[REPLAY PANELS] FAIL: " + _startError);
                    return;
                }

                PythonRuntimeLocator.ConfigureBeforeInitialize();
                PythonEngine.Initialize();
                // Release the GIL Initialize() holds on main and NEVER reacquire it.
                PythonEngine.BeginAllowThreads();
                s_pythonBootstrapped = true;
                Debug.Log("[REPLAY PANELS] PythonEngine.Initialize OK; main is GIL-free; launcher + poll starting.");
            }
            else
            {
                Debug.Log("[REPLAY PANELS] reusing the already-initialized interpreter (repeat Play; no re-Init).");
            }

            _sink = new ReplayEventSink();

            _launcher = new Thread(Launcher) { IsBackground = true, Name = "ReplayPanelsLauncher" };
            _launcher.Start();

            _poll = new Thread(PollLoop) { IsBackground = true, Name = "ReplayPanelsPoll" };
            _poll.Start();

            // We are committing to drive the run -> status = Running. Observe(sink) carries
            // it to Done/Failed; a launcher start-error flips it to Failed (in Update).
            _lifecycle.MarkRunning();
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "init: " + e);
            Debug.LogError("[REPLAY PANELS] FAIL (init): " + e);
        }
    }

    // Launcher thread: drives the production replay seam, then PUBLISHES the live server for
    // the poll thread (kept alive past this block — NOT disposed; throwaway/process-lifetime,
    // mirrors the interpreter-stays-alive teardown).
    void Launcher()
    {
        try
        {
            using (Py.GIL())
            {
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }

                PyObject server = null;
                using (PyObject coreMod   = Py.Import("engine.core"))
                using (PyObject inprocMod  = Py.Import("engine.inproc_server"))
                using (PyObject dataEngCls = coreMod.GetAttr("DataEngine"))
                using (PyObject inprocCls  = inprocMod.GetAttr("InprocLiveServer"))
                using (PyObject dataEngine = dataEngCls.Invoke())
                using (PyObject sinkPy     = PyObject.FromManagedObject(_sink))
                using (PyList instruments  = new PyList())
                using (PyDict cfg          = new PyDict())
                {
                    // server is NOT in the using-chain: the poll thread keeps calling it
                    // after this block. It leaks for the process (throwaway), like the
                    // interpreter. dataEngine CAN be disposed here — the Python server holds
                    // its own reference, keeping the Python object alive.
                    server = inprocCls.Invoke(dataEngine);

                    instruments.Append(new PyString(INSTRUMENT));

                    cfg.SetItem("strategy_file", new PyString(STRATEGY_FILE));
                    cfg.SetItem("instruments",   instruments);
                    cfg.SetItem("start_date",    new PyString(START_DATE));
                    cfg.SetItem("end_date",      new PyString(END_DATE));
                    cfg.SetItem("granularity",   new PyString(GRANULARITY));
                    cfg.SetItem("initial_cash",  new PyInt(INITIAL_CASH));
                    cfg.SetItem("catalog_path",  new PyString(CATALOG_PATH));
                    cfg.SetItem("rust_sink",     sinkPy);

                    using (PyObject result  = server.InvokeMethod("start_nautilus_replay", cfg))
                    using (PyObject success = result["success"])
                    {
                        if (!success.As<bool>())
                        {
                            using (PyObject ec = result["error_code"])
                            using (PyObject em = result["error_message"])
                            {
                                Volatile.Write(ref _startError,
                                    $"start_nautilus_replay rejected: error_code={ec.As<string>()} " +
                                    $"error_message={em.As<string>()}");
                            }
                        }
                    }
                }

                if (Volatile.Read(ref _startError) == null)
                {
                    // Publish the live server for the poll thread (still under the GIL).
                    _pollServer = server;
                    Volatile.Write(ref _pollServerReady, true);
                }
            } // GIL released here -> daemon backtest + poll thread can now run.
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "launcher: " + e);
        }
    }

    // AC4 poll thread: a flag-honoring C# loop. Each tick takes the GIL, reads
    // get_state_json into a latest-wins slot, releases. Stoppable+joinable (UNLIKE the
    // daemon) so OnDestroy can retire it across repeat editor plays.
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
                    {
                        Volatile.Write(ref _latestStateJson, js.As<string>());
                    }
                    Interlocked.Increment(ref _pollSamples);
                }
                catch (Exception e)
                {
                    // best-effort: a poll error must NOT crash the gate. The GIL was still
                    // contended (AC4 mechanism intact); we just skip this sample.
                    Debug.LogWarning("[REPLAY PANELS] poll get_state_json error (non-fatal): " + e.Message);
                }
            }
            Thread.Sleep(POLL_INTERVAL_MS);
        }
    }

    // Main thread: count frames (AC4 cadence), drain GIL-free, render.
    void Update()
    {
        _frameCount++;

        // AC4 cadence: count post-warmup hitches (frames slower than the budget).
        if (_frameCount > WARMUP_FRAMES)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > _maxDtAfterWarmup) _maxDtAfterWarmup = dt;
            if (dt > HITCH_DT_S) _hitchFrames++;
        }

        // start-error channel -> lifecycle Failed
        string err = Volatile.Read(ref _startError);
        if (err != null)
        {
            if (!_markFailedDone) { _lifecycle.MarkFailed(err); _markFailedDone = true; }
            if (!_errLogged) { Debug.LogError("[REPLAY PANELS] FAIL: " + err); _errLogged = true; }
        }

        if (_sink != null)
        {
            if (_sink.Failed && !_errLogged)
            {
                Debug.LogError("[REPLAY PANELS] FAIL: push_run_failed: " + _sink.Error);
                _errLogged = true;
            }

            _lifecycle.Observe(_sink); // GIL-free observe of the terminal flags
            DrainAndRender();
        }

        // status panel (poll fields + lifecycle), change-driven to avoid per-frame rebuilds.
        long samples = Interlocked.Read(ref _pollSamples);
        if (_lifecycle.Status != _renderedStatus || (int)samples != _renderedPollSamples)
        {
            _renderedStatus = _lifecycle.Status;
            _renderedPollSamples = (int)samples;
            string js = Volatile.Read(ref _latestStateJson);
            if (!string.IsNullOrEmpty(js))
            {
                PollStateDto ps = DecodePollState(js);
                if (!string.IsNullOrEmpty(ps.execution_mode)) _executionMode = ps.execution_mode;
                if (!string.IsNullOrEmpty(ps.venue_state))    _venueState    = ps.venue_state;
            }
            RenderStatusPanel();
        }

        if (_frameCount % 50 == 0)
            Debug.Log($"[REPLAY PANELS] frame={_frameCount} bars={_renderedCount} orders={_orderRows.Count} pollSamples={samples} hitches={_hitchFrames}");

        TryLogPass();
    }

    void DrainAndRender()
    {
        // chart bars (latest cumulative payload = full series).
        bool newBar = false;
        while (_sink.TryDequeueBar(out string payload)) { _lastBarPayload = payload; newBar = true; }
        if (newBar && _lastBarPayload != null)
        {
            ReplayBarFrame frame = ReplayBarDecoder.Decode(_lastBarPayload);
            if (frame.Ohlc != null && frame.Ohlc.Count != _renderedCount)
            {
                RenderCandles(frame);
                _renderedCount = frame.Ohlc.Count;
            }
        }

        // orders: accumulate every decoded row.
        bool newOrder = false;
        while (_sink.TryDequeueOrder(out string payload))
        {
            _orderRows.Add(ReplayPanelDecoder.DecodeOrder(payload));
            newOrder = true;
        }
        if (newOrder) RenderOrdersPanel();

        // portfolios: keep the LATEST snapshot for the positions panel.
        bool newPortfolio = false;
        while (_sink.TryDequeuePortfolio(out string payload))
        {
            _portfolio = ReplayPanelDecoder.DecodePortfolio(payload);
            _portfoliosDecoded++;
            newPortfolio = true;
        }
        if (newPortfolio) RenderPositionsPanel();

        // run_result: decode once the terminal summary is in.
        if (!_haveRunResult && _sink.Completed && !string.IsNullOrEmpty(_sink.Summary))
        {
            _runResult = ReplayPanelDecoder.DecodeRunResult(_sink.Summary);
            _haveRunResult = true;
            RenderRunResultPanel();
        }
    }

    void TryLogPass()
    {
        if (_passLogged) return;
        if (_frameCount < TARGET_FRAMES) return;
        if (_sink == null || !_sink.Completed) return;
        if (_renderedCount <= 0) return;                      // chart drew
        if (_orderRows.Count < 2) return;                     // BUY + SELL fills decoded
        if (_portfoliosDecoded < 1) return;                   // a position snapshot decoded
        if (!_haveRunResult) return;                          // run_result decoded
        if (Interlocked.Read(ref _pollSamples) <= 0) return;  // AC4 poll really ran

        // Medium-1 regression gate (#9-12 review): the spike SELL at bar 40 closes
        // 8918.TSE, so the LATEST positions snapshot must be FLAT. A leftover row
        // here means the actor regressed to cache.positions() (closed qty=0 phantom).
        int openPositions = _portfolio.Positions?.Count ?? 0;
        if (openPositions > 0)
        {
            if (!_errLogged)
            {
                Debug.LogError($"REPLAY PANELS FAIL: positions panel not flat at completion — " +
                               $"{openPositions} position(s) remain ({_portfolio.Positions[0].symbol} " +
                               $"qty={_portfolio.Positions[0].qty:0.##}); a closed position leaked into the snapshot");
                _errLogged = true;
            }
            return;
        }

        _passLogged = true;
        long samples = Interlocked.Read(ref _pollSamples);
        if (_hitchFrames <= MAX_HITCHES)
        {
            Debug.Log($"REPLAY PANELS PASS: frames={_frameCount} bars={_renderedCount} " +
                      $"orders={_orderRows.Count} portfolios={_portfoliosDecoded} positions=flat fills={_runResult.FillsCount} " +
                      $"pollSamples={samples} hitches={_hitchFrames} maxDt={_maxDtAfterWarmup:0.000}s " +
                      $"mode={_executionMode} venue={_venueState} " +
                      "(chart + 4 panels + AC4 non-stall GIL-marshaled poll all GREEN under Unity Mono)");
        }
        else
        {
            Debug.LogError($"REPLAY PANELS FAIL: AC4 stall — hitches={_hitchFrames} > {MAX_HITCHES} " +
                           $"maxDt={_maxDtAfterWarmup:0.000}s over {_frameCount} frames " +
                           "(poll/worker GIL contention blocked main frame cadence)");
        }
    }

    // ---- runtime uGUI construction (own Canvas; no scene dependency) ----

    void BuildUi()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        var canvasGo = new GameObject("ReplayPanelsCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        BuildChartArea(canvasGo.transform); // left region  [0 .. 0.62]
        BuildPanels(canvasGo.transform);    // right column [0.63 .. 1.0]
    }

    void BuildChartArea(Transform parent)
    {
        // Chart PANEL = the persisted-layout target: offset-ZERO so its box equals the
        // NORMALIZED rect LayoutDocument.Default() carries (chart [0..0.62]). The axis-
        // label gutter is NOT on this panel — it moved to the PlotArea CHILD below, i.e.
        // widget-internal chrome the layout seam never persists (Medium-2 review: this is
        // what makes the missing/corrupt fallback reproduce the live default at ALL
        // resolutions instead of dropping a folded-in pixel gutter).
        var areaGo = new GameObject("ChartArea", typeof(RectTransform), typeof(Image));
        areaGo.transform.SetParent(parent, false);
        _chartArea = areaGo.GetComponent<RectTransform>();
        _chartArea.anchorMin = new Vector2(0f, 0f);
        _chartArea.anchorMax = new Vector2(0.62f, 1f);
        _chartArea.offsetMin = Vector2.zero;
        _chartArea.offsetMax = Vector2.zero;
        areaGo.GetComponent<Image>().color = BG_COLOR;

        // PlotArea: the axis-label gutter, now a CHILD inset of the chart panel (the old
        // (60,40)/(-10,-20) ChartArea offsets). Axes + candles live here, so candle sizing
        // uses _plotArea.rect (not the full panel).
        var plotGo = new GameObject("PlotArea", typeof(RectTransform));
        plotGo.transform.SetParent(_chartArea, false);
        _plotArea = plotGo.GetComponent<RectTransform>();
        _plotArea.anchorMin = new Vector2(0f, 0f);
        _plotArea.anchorMax = new Vector2(1f, 1f);
        _plotArea.offsetMin = new Vector2(60f, 40f);
        _plotArea.offsetMax = new Vector2(-10f, -20f);

        AddAxis(_plotArea, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(2f, 0f)); // y
        AddAxis(_plotArea, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f)); // x

        var rootGo = new GameObject("Candles", typeof(RectTransform));
        rootGo.transform.SetParent(_plotArea, false);
        _candleRoot = rootGo.GetComponent<RectTransform>();
        _candleRoot.anchorMin = new Vector2(0f, 0f);
        _candleRoot.anchorMax = new Vector2(1f, 1f);
        _candleRoot.offsetMin = Vector2.zero;
        _candleRoot.offsetMax = Vector2.zero;
        _candleRoot.pivot = new Vector2(0f, 0f);
    }

    void AddAxis(RectTransform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 size)
    {
        var go = new GameObject("Axis", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.pivot = pivot;
        rt.sizeDelta = size;
        rt.anchoredPosition = Vector2.zero;
        go.GetComponent<Image>().color = AXIS_COLOR;
    }

    void BuildPanels(Transform parent)
    {
        // four stacked panels in the right column, full height split into quarters.
        _statusText    = BuildPanel(parent, "StatusPanel",    0.75f, 1.00f, "STATUS");
        _positionsText = BuildPanel(parent, "PositionsPanel", 0.50f, 0.75f, "POSITIONS");
        _ordersText    = BuildPanel(parent, "OrdersPanel",    0.25f, 0.50f, "ORDERS");
        _runResultText = BuildPanel(parent, "RunResultPanel", 0.00f, 0.25f, "RUN RESULT");
    }

    Text BuildPanel(Transform parent, string name, float yMin, float yMax, string initial)
    {
        var panelGo = new GameObject(name, typeof(RectTransform), typeof(Image));
        panelGo.transform.SetParent(parent, false);
        var prt = panelGo.GetComponent<RectTransform>();
        // This PANEL is the persisted-layout target: offset-ZERO so its box equals the
        // normalized rect LayoutDocument.Default() carries. The visual padding lives in
        // the Text CHILD inset below (widget chrome, not persisted) (Medium-2 review).
        prt.anchorMin = new Vector2(0.63f, yMin);
        prt.anchorMax = new Vector2(1.00f, yMax);
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;
        panelGo.GetComponent<Image>().color = PANEL_BG;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(panelGo.transform, false);
        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(8f, 8f);
        trt.offsetMax = new Vector2(-8f, -8f);

        var t = textGo.GetComponent<Text>();
        t.font = _font;
        t.fontSize = 14;
        t.color = TEXT_COLOR;
        t.alignment = TextAnchor.UpperLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.text = initial;
        return t;
    }

    void RenderStatusPanel()
    {
        if (_statusText == null) return;
        string line = $"STATUS: {_lifecycle.Status}";
        if (!string.IsNullOrEmpty(_lifecycle.FailureReason))
            line += $"\n  error: {_lifecycle.FailureReason}";
        line += $"\nmode: {_executionMode}   venue: {_venueState}";
        line += $"\nframe: {_frameCount}   pollSamples: {Interlocked.Read(ref _pollSamples)}";
        _statusText.text = line;
    }

    void RenderPositionsPanel()
    {
        if (_positionsText == null) return;
        var sb = new System.Text.StringBuilder();
        sb.Append($"POSITIONS  equity={_portfolio.Equity:0.##}  bp={_portfolio.BuyingPower:0.##}\n");
        if (_portfolio.Positions != null && _portfolio.Positions.Count > 0)
        {
            for (int i = 0; i < _portfolio.Positions.Count; i++)
            {
                PositionRow p = _portfolio.Positions[i];
                sb.Append($"  {p.symbol}  qty={p.qty:0.##}  @ {p.avg_price:0.##}\n");
            }
        }
        else
        {
            sb.Append("  (flat)\n");
        }
        _positionsText.text = sb.ToString();
    }

    void RenderOrdersPanel()
    {
        if (_ordersText == null) return;
        var sb = new System.Text.StringBuilder();
        sb.Append($"ORDERS ({_orderRows.Count})\n");
        for (int i = 0; i < _orderRows.Count; i++)
        {
            OrderRow o = _orderRows[i];
            sb.Append($"  {o.Side} {o.Qty:0.##} @ {o.Price:0.##} {o.Status}\n");
        }
        _ordersText.text = sb.ToString();
    }

    void RenderRunResultPanel()
    {
        if (_runResultText == null) return;
        _runResultText.text =
            "RUN RESULT\n" +
            $"  fills={_runResult.FillsCount}\n" +
            $"  equityPts={_runResult.EquityPoints}\n" +
            $"  maxDD={_runResult.MaxDrawdown:0.####}\n" +
            $"  sharpe={_runResult.Sharpe:0.###}  sortino={_runResult.Sortino:0.###}";
    }

    void RenderCandles(ReplayBarFrame frame)
    {
        var pts = frame.Ohlc;
        int n = pts.Count;

        // Rebuild from scratch (<=68 candles, only on a new bar -> cheap).
        for (int i = _candleRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(_candleRoot.GetChild(i).gameObject);
        }
        if (n == 0) return;

        double minLow = double.MaxValue, maxHigh = double.MinValue;
        long minT = long.MaxValue, maxT = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            OhlcPoint p = pts[i];
            if (p.low  < minLow)  minLow  = p.low;
            if (p.high > maxHigh) maxHigh = p.high;
            if (p.open_time_ms < minT) minT = p.open_time_ms;
            if (p.open_time_ms > maxT) maxT = p.open_time_ms;
        }

        double priceRange = maxHigh - minLow;
        if (priceRange <= 0) priceRange = 1.0; // flat series guard
        long timeRange = maxT - minT;

        float w = _plotArea.rect.width;   // candles fill the PlotArea (gutter-inset child), not the full panel
        float h = _plotArea.rect.height;
        float bodyW = Mathf.Max(1f, (w / n) * 0.6f);

        for (int i = 0; i < n; i++)
        {
            OhlcPoint p = pts[i];

            float x = timeRange > 0
                ? (float)((p.open_time_ms - minT) / (double)timeRange) * w
                : (n > 1 ? (float)i / (n - 1) * w : w * 0.5f);

            float yOpen  = (float)((p.open  - minLow) / priceRange) * h;
            float yClose = (float)((p.close - minLow) / priceRange) * h;
            float yHigh  = (float)((p.high  - minLow) / priceRange) * h;
            float yLow   = (float)((p.low   - minLow) / priceRange) * h;

            Color c = p.close >= p.open ? UP_COLOR : DOWN_COLOR;

            AddRect(x - 0.5f, yLow, 1f, Mathf.Max(1f, yHigh - yLow), c); // wick

            float bottom = Mathf.Min(yOpen, yClose);
            float bodyH  = Mathf.Max(1f, Mathf.Abs(yClose - yOpen));
            AddRect(x - bodyW * 0.5f, bottom, bodyW, bodyH, c);          // body
        }
    }

    void AddRect(float x, float y, float w, float h, Color c)
    {
        var go = new GameObject("c", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(_candleRoot, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        go.GetComponent<Image>().color = c;
    }

    // ---- AC4 poll-state decode (throwaway, inline; the durable decoder is untouched) ----
    // get_state_json() returns the full TradingState JSON; JsonUtility binds only the two
    // declared fields. Wrapped in try/catch because a throwaway status nicety must never
    // break the HITL gate (unlike the durable decoder's surface-malformed discipline).

    [System.Serializable]
    class PollStateDto
    {
        public string execution_mode;
        public string venue_state;
    }

    static PollStateDto DecodePollState(string json)
    {
        try { return JsonUtility.FromJson<PollStateDto>(json) ?? new PollStateDto(); }
        catch { return new PollStateDto(); }
    }

    void OnDestroy()
    {
        // Retire OUR poll thread (a flag-honoring C# loop -> safe to stop+join, UNLIKE the
        // Python daemon). Prevents poll-thread accumulation across repeat editor plays
        // (background threads survive playmode-stop inside the editor process).
        Volatile.Write(ref _pollStop, true);
        try { _poll?.Join(2000); } catch { }

        // DELIBERATELY no PythonEngine.Shutdown() / EndAllowThreads() — the daemon backtest
        // thread is nondeterministic; reacquiring the GIL on main to Shutdown could deadlock.
        // Interpreter persists for the process; s_pythonBootstrapped makes the next Play reuse it.
        Debug.Log("[REPLAY PANELS] OnDestroy: poll thread retired; interpreter left alive (no GIL reacquire).");
    }
}
