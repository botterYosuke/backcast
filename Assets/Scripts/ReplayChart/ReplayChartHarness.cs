// ReplayChartHarness.cs — issue #10 "Replay chart" (M3, THROWAWAY HITL visual gate)
//
// THROWAWAY: this is a turnkey playmode widget the OWNER runs once to SEE replay
// candles advance. It is NOT integrated into the mainline scene/prefab/DI (that is
// #5/#7) — it builds its OWN runtime Canvas + uGUI candles, depending on NOTHING in
// SampleScene. It will be deleted/replaced. Keep it minimal.
//
// Owner flow: open the project, press Play -> an auto-bootstrap spawns this MonoBehaviour,
// it starts the production Nautilus replay seam, and candlesticks advance bar-by-bar.
//
// Threading reuses the M2 probe (ReplayChartDecodeProbe) PROVEN pattern exactly:
//   * main (Unity thread): PythonRuntimeLocator.ConfigureBeforeInitialize() ->
//     PythonEngine.Initialize() -> BeginAllowThreads() (release the main GIL and
//     NEVER reacquire it) -> start a LAUNCHER thread. Main then drains the sink
//     GIL-FREE every Update() and renders pure C# uGUI.
//   * launcher thread: takes Py.GIL(), imports engine.core / engine.inproc_server,
//     builds the SAME 68-bar cfg as M2 with rust_sink = a C# ReplayEventSink, calls
//     start_nautilus_replay (which spawns its OWN daemon backtest thread and RETURNS),
//     then RELEASES the GIL by leaving the using-block. The launcher dies immediately.
//   * daemon (Python backtest thread): acquires the GIL itself, streams 68 bars,
//     duck-calls sink.push_bar(str) per bar (cumulative ohlc_points), then push_run_complete.
//
// Rendering: ohlc_points is CUMULATIVE (M2-confirmed: the final frame carries all 68
// bars), so we keep only the LATEST drained payload, ReplayBarDecoder.Decode it, and
// rebuild the candle rects when the bar count grows. x = open_time_ms mapped to chart
// width; body = open/close rect; wick = high/low line; price autoscaled to chart height.
//
// TEARDOWN trade-off (DECIDED): OnDestroy does NOT call PythonEngine.Shutdown() and
// does NOT EndAllowThreads(). Unlike S0SpikeHarness (whose C# worker LOOP honors a
// stop flag, so its Join->Shutdown is safe), here the live Python thread is the daemon
// backtest spawned by start_nautilus_replay, which C# cannot deterministically stop. If
// the owner presses Stop mid-run, that daemon may still hold/contend the GIL; reacquiring
// it on main to Shutdown could DEADLOCK / hang Unity. So we leave the embedded interpreter
// alive for the whole process (S0-sanctioned: "interpreter persists across plays"), and a
// process-lifetime static guard (s_pythonBootstrapped) makes the NEXT Play reuse it
// WITHOUT re-Initialize (avoiding a double-init crash) and WITHOUT a second BeginAllowThreads
// (the GIL was released on the first Play and never reacquired). Accepted edge: a Stop
// mid-run then immediate Play can briefly overlap two daemon backtests on one interpreter —
// cosmetic for this throwaway gate.
//
// HEADLESS COMPILE GATE: the auto-bootstrap is guarded by Application.isBatchMode, so the
// owner's `Unity -batchmode -nographics -quit` compile check NEVER initializes Python or
// renders — it only compiles this script and quits. Only real playmode bootstraps.
//
// No .meta is authored here — Unity generates ReplayChartHarness.cs.meta on next import.

using System;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Python.Runtime;

public class ReplayChartHarness : MonoBehaviour
{
    // cfg fixture — SAME 68-bar fixture/cfg as the M2 probe (ReplayChartDecodeProbe).
    const string STRATEGY_FILE = "/Users/sasac/backcast/python/spike/fixtures/strategies/spike_bar_consumer.py";
    const string CATALOG_PATH  = "/Users/sasac/backcast/python/spike/fixtures/jquants-catalog";
    const string INSTRUMENT    = "8918.TSE";
    const string START_DATE    = "2024-10-01";
    const string END_DATE      = "2025-01-10";
    const string GRANULARITY   = "Daily";
    const long   INITIAL_CASH  = 10_000_000;

    const int    TARGET_FRAMES = 300; // >=300 smooth frames = the no-deadlock/no-stall evidence

    // Process-lifetime guard: Python is Initialized + main GIL released exactly ONCE per
    // Unity process (see teardown trade-off in the header). A repeat Play reuses it.
    static bool s_pythonBootstrapped;

    // launcher -> main: non-null => the launcher failed (only a C# string crosses the boundary).
    string _startError;

    // The C# sink: constructed on main, drained on main (GIL-free), pushed-to by the
    // Python daemon backtest thread under the GIL.
    ReplayEventSink _sink;
    Thread _launcher;

    // main-thread render/progress state
    int    _frameCount;
    string _lastPayload;     // latest cumulative push_bar JSON (= full series)
    int    _renderedCount;   // bars currently drawn; re-render when the drained count grows
    bool   _errLogged;
    bool   _passLogged;

    // runtime uGUI
    RectTransform _chartArea;   // the plot rect (inside the axis margins)
    RectTransform _candleRoot;  // candle rects parent (full-stretch over _chartArea)

    static readonly Color BG_COLOR    = new Color(0.08f, 0.08f, 0.10f, 1f);
    static readonly Color AXIS_COLOR  = new Color(0.55f, 0.55f, 0.60f, 1f);
    static readonly Color UP_COLOR    = new Color(0.20f, 0.80f, 0.35f, 1f);
    static readonly Color DOWN_COLOR  = new Color(0.85f, 0.28f, 0.28f, 1f);

    // SUPERSEDED by #11 ReplayPanelsHarness: that harness now OWNS Play (chart + 4
    // panels in one Play). This #10 auto-bootstrap is PRESERVED but flag-OFF so it
    // never collides with #11's PythonEngine.Initialize() (one Play-owner rule;
    // findings 0003 §4). Flip AutoBootstrapEnabled back to true to resurrect the #10
    // chart-only playmode gate (e.g. the Windows leg re-run). Mirrors S0SpikeHarness.
    const bool AutoBootstrapEnabled = false;

    // TURNKEY auto-bootstrap: owner just presses Play. Guarded OUT of batchmode so the
    // headless compile gate (`-batchmode -nographics -quit`) never inits Python / renders.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;   // #11 ReplayPanelsHarness owns Play (findings 0003 §4)
        if (Application.isBatchMode) return;
        var go = new GameObject("ReplayChartHarness");
        DontDestroyOnLoad(go);
        go.AddComponent<ReplayChartHarness>();
    }

    void Start()
    {
        try
        {
            BuildChartUi();

            if (!s_pythonBootstrapped)
            {
                PythonRuntimeLocator.ConfigureBeforeInitialize();
                PythonEngine.Initialize();
                // Release the GIL Initialize() holds on main and NEVER reacquire it: the
                // launcher takes it briefly, then the daemon backtest thread needs it free.
                PythonEngine.BeginAllowThreads();
                s_pythonBootstrapped = true;
                Debug.Log("[REPLAY CHART] PythonEngine.Initialize OK; main is GIL-free; launcher starting.");
            }
            else
            {
                Debug.Log("[REPLAY CHART] reusing the already-initialized interpreter (repeat Play; no re-Init).");
            }

            _sink = new ReplayEventSink();
            _launcher = new Thread(Launcher) { IsBackground = true, Name = "ReplayChartLauncher" };
            _launcher.Start();
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "init: " + e);
            Debug.LogError("[REPLAY CHART] FAIL (init): " + e);
        }
    }

    // Launcher thread: drives the production replay seam exactly like the M2 probe.
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

                using (PyObject coreMod    = Py.Import("engine.core"))
                using (PyObject inprocMod   = Py.Import("engine.inproc_server"))
                using (PyObject dataEngCls  = coreMod.GetAttr("DataEngine"))
                using (PyObject inprocCls   = inprocMod.GetAttr("InprocLiveServer"))
                using (PyObject dataEngine  = dataEngCls.Invoke())
                using (PyObject server      = inprocCls.Invoke(dataEngine))
                using (PyObject sinkPy      = PyObject.FromManagedObject(_sink))
                using (PyList instruments   = new PyList())
                using (PyDict cfg           = new PyDict())
                {
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
            } // GIL released here -> the Python daemon backtest thread can now run and push bars.
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "launcher: " + e);
        }
    }

    // Main thread: count frames (the >=300 smooth-frames evidence), drain GIL-free, render.
    void Update()
    {
        _frameCount++;

        if (_frameCount % 50 == 0)
        {
            Debug.Log($"[REPLAY CHART] frame={_frameCount} bars={_renderedCount}");
        }

        string err = Volatile.Read(ref _startError);
        if (err != null && !_errLogged)
        {
            Debug.LogError("[REPLAY CHART] FAIL: " + err);
            _errLogged = true;
        }

        if (_sink != null)
        {
            if (_sink.Failed && !_errLogged)
            {
                Debug.LogError("[REPLAY CHART] FAIL: push_run_failed: " + _sink.Error);
                _errLogged = true;
            }

            // Drain GIL-free; keep ONLY the latest payload (cumulative -> latest = full series).
            bool newPayload = false;
            while (_sink.TryDequeueBar(out string payload))
            {
                _lastPayload = payload;
                newPayload = true;
            }

            // Decode + render ONLY when a new bar was drained this frame. Without this
            // guard, the run streams 68 bars in ~7s but Update() keeps running for
            // thousands more frames, re-parsing the unchanged final ~KB payload every
            // frame (JsonUtility.FromJson allocates) for zero render work.
            if (newPayload && _lastPayload != null)
            {
                ReplayBarFrame frame = ReplayBarDecoder.Decode(_lastPayload);
                if (frame.Ohlc != null && frame.Ohlc.Count != _renderedCount)
                {
                    RenderCandles(frame);
                    _renderedCount = frame.Ohlc.Count;
                }
            }

            if (!_passLogged && _frameCount >= TARGET_FRAMES && _sink.Completed && _renderedCount > 0)
            {
                Debug.Log($"REPLAY CHART PASS: frames={_frameCount} bars={_renderedCount}");
                _passLogged = true;
            }
        }
    }

    // ---- runtime uGUI construction (own Canvas; no scene dependency) ----

    void BuildChartUi()
    {
        var canvasGo = new GameObject("ReplayChartCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        // Plot area: full screen inset by axis margins (left 60 / bottom 40 / top 20 / right 20).
        var areaGo = new GameObject("ChartArea", typeof(RectTransform), typeof(Image));
        areaGo.transform.SetParent(canvasGo.transform, false);
        _chartArea = areaGo.GetComponent<RectTransform>();
        _chartArea.anchorMin = new Vector2(0f, 0f);
        _chartArea.anchorMax = new Vector2(1f, 1f);
        _chartArea.offsetMin = new Vector2(60f, 40f);
        _chartArea.offsetMax = new Vector2(-20f, -20f);
        areaGo.GetComponent<Image>().color = BG_COLOR;

        // Price (left, vertical) axis line.
        var yAxis = new GameObject("YAxis", typeof(RectTransform), typeof(Image));
        yAxis.transform.SetParent(_chartArea, false);
        var yrt = yAxis.GetComponent<RectTransform>();
        yrt.anchorMin = new Vector2(0f, 0f);
        yrt.anchorMax = new Vector2(0f, 1f);
        yrt.pivot = new Vector2(0f, 0.5f);
        yrt.sizeDelta = new Vector2(2f, 0f);
        yrt.anchoredPosition = Vector2.zero;
        yAxis.GetComponent<Image>().color = AXIS_COLOR;

        // Time (bottom, horizontal) axis line.
        var xAxis = new GameObject("XAxis", typeof(RectTransform), typeof(Image));
        xAxis.transform.SetParent(_chartArea, false);
        var xrt = xAxis.GetComponent<RectTransform>();
        xrt.anchorMin = new Vector2(0f, 0f);
        xrt.anchorMax = new Vector2(1f, 0f);
        xrt.pivot = new Vector2(0.5f, 0f);
        xrt.sizeDelta = new Vector2(0f, 2f);
        xrt.anchoredPosition = Vector2.zero;
        xAxis.GetComponent<Image>().color = AXIS_COLOR;

        // Candle parent: full-stretch over the plot area; candle children are positioned in
        // pixels from its bottom-left, so its rect == the plot rect.
        var rootGo = new GameObject("Candles", typeof(RectTransform));
        rootGo.transform.SetParent(_chartArea, false);
        _candleRoot = rootGo.GetComponent<RectTransform>();
        _candleRoot.anchorMin = new Vector2(0f, 0f);
        _candleRoot.anchorMax = new Vector2(1f, 1f);
        _candleRoot.offsetMin = Vector2.zero;
        _candleRoot.offsetMax = Vector2.zero;
        _candleRoot.pivot = new Vector2(0f, 0f);
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
        long   minT   = long.MaxValue,   maxT    = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            OhlcPoint p = pts[i];
            if (p.low  < minLow)  minLow  = p.low;
            if (p.high > maxHigh) maxHigh = p.high;
            if (p.open_time_ms < minT) minT = p.open_time_ms;
            if (p.open_time_ms > maxT) maxT = p.open_time_ms;
        }

        double priceRange = maxHigh - minLow;
        if (priceRange <= 0) priceRange = 1.0;   // flat series guard
        long timeRange = maxT - minT;

        float w = _chartArea.rect.width;
        float h = _chartArea.rect.height;
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

            // wick (high-low), 1px centered on x
            AddRect(x - 0.5f, yLow, 1f, Mathf.Max(1f, yHigh - yLow), c);

            // body (open-close)
            float bottom = Mathf.Min(yOpen, yClose);
            float bodyH  = Mathf.Max(1f, Mathf.Abs(yClose - yOpen));
            AddRect(x - bodyW * 0.5f, bottom, bodyW, bodyH, c);
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

    void OnDestroy()
    {
        // DELIBERATELY no PythonEngine.Shutdown() / EndAllowThreads() — see the header
        // teardown trade-off. The daemon backtest thread's lifecycle is nondeterministic;
        // reacquiring the GIL on main to Shutdown could deadlock against it. We keep the
        // interpreter alive for the process; s_pythonBootstrapped makes the next Play reuse it.
        Debug.Log("[REPLAY CHART] OnDestroy: leaving interpreter alive (no GIL reacquire); Canvas torn down with the GameObject.");
    }
}
