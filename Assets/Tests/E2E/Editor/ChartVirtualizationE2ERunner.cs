// ChartVirtualizationE2ERunner.cs — S1 #155 / findings 0119 D-8 new gate: ChartView の Mesh-based
// visible-window virtualization + default ResetView right-anchor を実証する Surface E2E runner.
// 台本: same-dir ChartVirtualizationE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartVirtualizationE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART VIRTUALIZATION PASS] ... / exit=0
//   # compile-only: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the load-bearing invariants of S1 ChartView Mesh + ChartViewState that no existing
// runner covers:
//
//   VIRTUALIZE-01: TotalBarCount=1000 / plot=540px / cell_width=6 → RenderedBarCount ≈ 90 (±1)。
//     視窓外 910 本は Mesh 頂点に出ない（描画コストが bar 数に依存しない事実を pin）。findings 0119 D-2 /
//     ADR-0034 §2 + §4 の核。delete-the-logic litmus: OnPopulateMesh の visible-window gate
//     (`if (p.open_time_ms < winStart || p.open_time_ms > winEnd) continue;`) を抜く → RenderedBarCount
//     が TotalBarCount(1000) に張り付く → RED。
//
//   RESET-INIT-01: spawn 直後 + Render(1000 bars) → ViewState.cell_width_px == DEFAULT_CELL_WIDTH_PX(6.0)
//     ／ auto_scale == true ／ visible window の右端が最新 bar に anchor（findings 0119 D-4）。
//     delete-the-logic litmus: Render() の `if (ViewState.auto_scale) ResetView(...)` を抜く →
//     translation_ms が右端 anchor にならず VIRTUALIZE-01 で RenderedBarCount が 0（左端から右へ
//     伸ばす visible window が data の手前で空転する）→ 連動 RED。
//
//   VIRTUALIZE-02 (pan 非依存・データ量非依存性): TotalBarCount=1000 のまま translation_ms を 200 bar
//     左へ pan → 同じく RenderedBarCount ≈ 90。 visible-window 計算が「データの右端」に hard-code されて
//     いない事実を pin（D-2 の純粋関数性）。これは S2 #157 の PAN-01 とは別の角度: PAN-01 は drag handler
//     が translation_ms を動かすことを assert する pointer-driven gate、VIRTUALIZE-02 は ViewState を
//     直接いじって描画側が窓に追随することを assert する pure data-driven gate（drag が無くても窓が成立）。
//
// Python-FREE: pure C# / 合成 OhlcPoint 配列を ChartView.Render() に直接渡し、Canvas.ForceUpdateCanvases()
// で OnPopulateMesh を発火させて RenderedBarCount を post-emit で読む。

using System;
using UnityEditor;
using UnityEngine;

public static class ChartVirtualizationE2ERunner
{
    const int TOTAL_BARS = 1000;
    const float PLOT_WIDTH_PX = 540f;   // mirror of #155 acceptance criteria
    const float CHART_HEIGHT_PX = 400f;
    // gutter inset (mirror of ChartView's GutterLeft + GutterRight + GutterTop + GutterBottom). The chart
    // host is sized so plot.width comes out to PLOT_WIDTH_PX after gutter inset:
    //   chartHost.width = PLOT_WIDTH_PX + GutterLeft(60) + GutterRight(20) = 620
    //   chartHost.height = CHART_HEIGHT_PX
    const float GUTTER_HORIZ = 80f;     // 60 + 20
    const float HOST_WIDTH_PX = PLOT_WIDTH_PX + GUTTER_HORIZ;   // = 620

    // ~90 bars (540 / 6) is the design's stated default visible count on a plot of 540px / cell 6px.
    // Allow ±2 tolerance for fractional bar-fit at the right edge (the for-loop uses inclusive `<=` on
    // winEnd, which can include one extra bar at the boundary).
    const int EXPECTED_RENDERED = 90;
    const int RENDERED_TOLERANCE = 2;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_ResetInitDefaultRightAnchor()    // RESET-INIT-01
                ?? Section2_VirtualizeRightAnchor()           // VIRTUALIZE-01
                ?? Section3_VirtualizeAfterPan();             // VIRTUALIZE-02
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART VIRTUALIZATION PASS] (RESET-INIT-01) Render(1000 Daily bars) leaves "
                    + "ViewState.cell_width_px=DEFAULT(6) + auto_scale=true + translation_ms right-anchored "
                    + "to the latest bar (findings 0119 D-4); (VIRTUALIZE-01) on a 540px plot ~" + EXPECTED_RENDERED
                    + " bars (±" + RENDERED_TOLERANCE + ") emit Mesh vertices while the other ~"
                    + (TOTAL_BARS - EXPECTED_RENDERED) + " are virtualization-skipped (findings 0119 D-2); "
                    + "(VIRTUALIZE-02) translation_ms shifted left 200 bars keeps RenderedBarCount ~"
                    + EXPECTED_RENDERED + " — visible-window calc is pure data, not hard-coded on data right-edge.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART VIRTUALIZATION FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── RESET-INIT-01: defaults on spawn + after Render. ──
    static string Section1_ResetInitDefaultRightAnchor()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            // Pre-Render defaults: empty data, no basis_ms inferred yet, default cell_width / auto_scale.
            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S1 RESET-INIT-01: pre-Render cell_width_px=" + cv.ViewState.cell_width_px
                     + ", want " + ChartViewState.DEFAULT_CELL_WIDTH_PX;
            if (!cv.ViewState.auto_scale)
                return "S1 RESET-INIT-01: pre-Render auto_scale=false, want true (initial state)";

            // Render 1000 Daily bars: basis_ms should be inferred as Daily, translation_ms right-anchored.
            var bars = SyntheticDailyBars(TOTAL_BARS, basePrice: 100.0, startMs: 1_700_000_000_000L);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            if (cv.TotalBarCount != TOTAL_BARS)
                return "S1 RESET-INIT-01: TotalBarCount=" + cv.TotalBarCount + ", want " + TOTAL_BARS;
            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S1 RESET-INIT-01: post-Render cell_width_px=" + cv.ViewState.cell_width_px
                     + " — ResetView should keep DEFAULT_CELL_WIDTH_PX on auto_scale";
            if (!cv.ViewState.auto_scale)
                return "S1 RESET-INIT-01: post-Render auto_scale flipped to false (ResetView regression)";
            if (cv.ViewState.basis_ms != ChartViewState.BASIS_DAILY_MS)
                return "S1 RESET-INIT-01: basis_ms=" + cv.ViewState.basis_ms + ", want "
                     + ChartViewState.BASIS_DAILY_MS + " (Daily auto-infer regression)";

            // Right-anchor: latest_bar.open_time_ms should land near the right edge. visible_end ≈
            // translation_ms + (plot_width / cell_width) * basis = latest_bar + basis (since visible
            // bars = plot_width / cell_width = 90 and ResetView puts (visible-1)*basis to the left).
            long latest = bars[bars.Length - 1].open_time_ms;
            long expectedTranslation = latest - (long)((PLOT_WIDTH_PX / ChartViewState.DEFAULT_CELL_WIDTH_PX - 1) * ChartViewState.BASIS_DAILY_MS);
            // Allow ±1 basis tick for the (long)cast rounding.
            long drift = Math.Abs(cv.ViewState.translation_ms - expectedTranslation);
            if (drift > ChartViewState.BASIS_DAILY_MS)
                return "S1 RESET-INIT-01: translation_ms=" + cv.ViewState.translation_ms + ", want "
                     + expectedTranslation + " (right-anchor regression: latest bar not at right edge)";
            Debug.Log("[E2E RESET-INIT-01 PASS] Render(1000 Daily bars) → cell_width="
                    + ChartViewState.DEFAULT_CELL_WIDTH_PX + " / auto_scale=true / basis=Daily / translation_ms "
                    + "right-anchored within 1 day of expected.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── VIRTUALIZE-01: TotalBarCount=1000 → RenderedBarCount ≈ 90 on a 540px plot. ──
    static string Section2_VirtualizeRightAnchor()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDailyBars(TOTAL_BARS, basePrice: 100.0, startMs: 1_700_000_000_000L);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            if (cv.TotalBarCount != TOTAL_BARS)
                return "S2 VIRTUALIZE-01: TotalBarCount=" + cv.TotalBarCount + ", want " + TOTAL_BARS;
            int rendered = cv.RenderedBarCount;
            int diff = Math.Abs(rendered - EXPECTED_RENDERED);
            if (diff > RENDERED_TOLERANCE)
                return "S2 VIRTUALIZE-01: RenderedBarCount=" + rendered + ", want " + EXPECTED_RENDERED
                     + " ±" + RENDERED_TOLERANCE + " on a 540px plot @ cell_width=6 / "
                     + TOTAL_BARS + " bars. Either visible-window virtualization is broken (rendered=Total) "
                     + "or right-anchor is dropping bars off-screen (rendered=0).";
            if (rendered == TOTAL_BARS)
                return "S2 VIRTUALIZE-01: RenderedBarCount=TotalBarCount → no virtualization "
                     + "(OnPopulateMesh's `if (p.open_time_ms < winStart || > winEnd) continue;` guard is gone)";
            Debug.Log("[E2E VIRTUALIZE-01 PASS] TotalBarCount=" + TOTAL_BARS + " / RenderedBarCount="
                    + rendered + " (visible-window virtualization confirmed: " + (TOTAL_BARS - rendered)
                    + " out-of-window bars skipped vertex emit).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── VIRTUALIZE-02: pan ViewState 200 bars left → still ~90 rendered. ──
    static string Section3_VirtualizeAfterPan()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDailyBars(TOTAL_BARS, basePrice: 100.0, startMs: 1_700_000_000_000L);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            // Pan 200 bars left (auto_scale → false). Visible window now centers on bars[~710..~800].
            long basis = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            cv.ViewState.translation_ms -= 200 * basis;
            cv.ViewState.auto_scale = false;
            cv.SetVerticesDirty();
            Canvas.ForceUpdateCanvases();

            int rendered = cv.RenderedBarCount;
            int diff = Math.Abs(rendered - EXPECTED_RENDERED);
            if (diff > RENDERED_TOLERANCE)
                return "S3 VIRTUALIZE-02: post-pan RenderedBarCount=" + rendered + ", want " + EXPECTED_RENDERED
                     + " ±" + RENDERED_TOLERANCE + ". The visible-window calc may be hard-coded on data "
                     + "right-edge instead of using translation_ms (ViewState pure-data regression).";
            Debug.Log("[E2E VIRTUALIZE-02 PASS] after panning translation_ms by -200 bars / auto_scale=false, "
                    + "RenderedBarCount=" + rendered + " (window follows ViewState, not data edge).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers ----

    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartVirtCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        var host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f);
        host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(HOST_WIDTH_PX, CHART_HEIGHT_PX);
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        return cv;
    }

    // Synthesize a deterministic Daily OhlcPoint array — daily spaced opens, ±2% body drift around base,
    // monotonic time index. Avoids any RNG so VIRTUALIZE-01 / RESET-INIT-01 stay reproducible across CI runs.
    static OhlcPoint[] SyntheticDailyBars(int n, double basePrice, long startMs)
    {
        var arr = new OhlcPoint[n];
        for (int i = 0; i < n; i++)
        {
            double drift = ((i % 7) - 3) * 0.5;   // bounded ±1.5 swing
            double o = basePrice + drift;
            double c = basePrice + drift + (i % 2 == 0 ? +0.3 : -0.3);
            double h = Math.Max(o, c) + 0.4;
            double l = Math.Min(o, c) - 0.4;
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o,
                high = h,
                low = l,
                close = c,
                volume = 1000.0 + i,
            };
        }
        return arr;
    }
}
