// ChartFitAllE2ERunner.cs — #156 follow-up gate: the OPT-IN fit-to-all auto_scale view that makes a
// Replay full-period cold load (S6 #156) actually VISIBLE on init, instead of the DEFAULT 6px
// right-anchored ~90-bar window (findings 0119 D-4). ADR-0034 D-4 amended (owner 2026-06-27).
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartFitAllE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART FITALL PASS] ... / exit=0
//   # compile-only: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the load-bearing invariants of SetFitAllOnAutoScale + ResetView's fit branch:
//
//   FITALL-OFF-01 (opt-in / Live non-regression floor): fit_all_on_autoscale defaults FALSE →
//     Render(1000 bars) keeps cell_width=DEFAULT(6) and RenderedBarCount ≈ 90. Proves Live charts are
//     untouched and fit-all is strictly opt-in (mirrors VIRTUALIZE-01's premise). delete-the-logic
//     litmus: if ResetView ALWAYS fit (drop the `fit_all_on_autoscale ?` guard) this drops to ~540 → RED.
//
//   FITALL-FITS-02 (the actual feature, GAPPED series): fit_all=true → Render(200 weekday-only Daily
//     bars spanning ~280 calendar days on a 540px plot). The chart positions bars by TIME, so the fit
//     must size by the calendar SPAN (~280 basis-slots), not the 200-bar count: cell_width =
//     540/spanSlots, RenderedBarCount == 200 (the WHOLE series renders, oldest bar at/right of the left
//     edge). delete-the-logic litmus #1: replace the fit branch with DEFAULT_CELL_WIDTH_PX →
//     RenderedBarCount collapses to ~90 → RED. litmus #2: size the fit by bar COUNT (540/200) instead of
//     time span → the window covers only 200 of ~280 days → the oldest ~57 bars clip off the left,
//     RenderedBarCount ≈ 143 → RED. THIS is the "init shows 全期間 even with weekend gaps" assertion.
//     litmus #3 (float round-trip): the fit branch pins translation = first exactly. If translation is
//     re-derived from a rounded visibleBars (latest - (visibleBars-1)*basis), float32 rounding pushes it
//     a few hundred ms past `first` on ~25% of spans → strict `<` virtualization drops the oldest bar →
//     RenderedBarCount 199, translation != first → RED. FITS-02 + RESET-04 assert the EXACT count + pin.
//
//   FITALL-OVERFLOW-03 (owner's MIN-clamp + right-anchor policy): fit_all=true → Render(1000 bars) →
//     fit = 540/1000 = 0.54 < MIN, so cell_width clamps to MIN(1.0) and the right-anchor shows the most
//     recent ~540 bars (NOT all 1000, NOT just 90). Virtualization still skips the off-screen left bars.
//     latest bar sits at the right edge.
//
//   FITALL-RESET-04: fit_all=true → Render(300) → zoom in (auto_scale=false, cell_width≫fit) →
//     RequestResetView → returns to the fit view (cell_width≈1.8, all 300 rendered, auto_scale=true).
//     "Reset = home = see the whole period" when fit-all is engaged. CONTIGUOUS 300 is a round-trip
//     dropping span, so RESET-04 also pins translation == first (the litmus #3 regression gate).
//
//   FITALL-WIRING-05 (mode→fit host policy): DockShape.ShouldFitChartToAll — the predicate the poll
//     loop (BackcastWorkspaceRoot) wires into every chart — pins Replay/unknown → fit-all, LiveManual/
//     LiveAuto → DEFAULT. Inverting it (Live fits / Replay 6px) flips the section → RED. Closes the
//     "flipping !IsLiveShape turns no gate red" litmus that the ChartView-direct sections can't reach.
//
// Python-FREE: pure C# / synthetic OhlcPoint arrays into ChartView.Render(), Canvas.ForceUpdateCanvases()
// fires OnPopulateMesh, RenderedBarCount read post-emit (same harness shape as ChartVirtualizationE2ERunner).

using System;
using UnityEditor;
using UnityEngine;

public static class ChartFitAllE2ERunner
{
    const float PLOT_WIDTH_PX = 540f;     // mirror of #155/#156 acceptance criteria
    const float CHART_HEIGHT_PX = 400f;
    const float GUTTER_HORIZ = 80f;       // GutterLeft(60) + GutterRight(20)
    const float HOST_WIDTH_PX = PLOT_WIDTH_PX + GUTTER_HORIZ;   // = 620
    const long START_MS = 1_700_000_000_000L;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_FitAllOffFloor()        // FITALL-OFF-01
                ?? Section2_FitAllFitsWholeSeries()  // FITALL-FITS-02
                ?? Section3_FitAllOverflowClampMin() // FITALL-OVERFLOW-03
                ?? Section4_FitAllResetReturnsFit()  // FITALL-RESET-04
                ?? Section5_ModeToFitPolicy();       // FITALL-WIRING-05
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART FITALL PASS] (FITALL-OFF-01) default fit_all=false keeps DEFAULT 6px / "
                    + "~90 rendered (Live non-regression); (FITALL-FITS-02) fit_all=true fits 200 GAPPED bars "
                    + "(~278 day span) into 540px by TIME → all 200 render, oldest at left (init shows 全期間 "
                    + "despite weekend gaps); (FITALL-OVERFLOW-03) 1000 bars clamp "
                    + "cell_width to MIN(1.0) + right-anchor → ~540 rendered, latest at right edge (owner "
                    + "MIN-clamp policy); (FITALL-RESET-04) RequestResetView returns to the fit view; "
                    + "(FITALL-WIRING-05) DockShape.ShouldFitChartToAll pins Replay→fit / Live→DEFAULT so the "
                    + "poll's mode→fit wiring can't silently invert.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART FITALL FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── FITALL-OFF-01: opt-in floor — default flag leaves DEFAULT 6px right-anchor (Live unchanged). ──
    static string Section1_FitAllOffFloor()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            if (cv.ViewState.fit_all_on_autoscale)
                return "S1 FITALL-OFF-01: fit_all_on_autoscale defaulted TRUE — must be opt-in (FALSE).";
            var bars = SyntheticDailyBars(1000);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S1 FITALL-OFF-01: cell_width=" + cv.ViewState.cell_width_px + ", want DEFAULT "
                     + ChartViewState.DEFAULT_CELL_WIDTH_PX + " when fit_all is off.";
            int rendered = cv.RenderedBarCount;
            if (Math.Abs(rendered - 90) > 2)
                return "S1 FITALL-OFF-01: RenderedBarCount=" + rendered + ", want ~90 (DEFAULT window). "
                     + "If this is ~540 the fit branch fires unconditionally (opt-in guard gone).";
            Debug.Log("[E2E FITALL-OFF-01 PASS] default fit_all=false → cell_width=6 / RenderedBarCount="
                    + rendered + " (~90): Live charts untouched, fit-all is strictly opt-in.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── FITALL-FITS-02: fit_all=true, 200 GAPPED (weekday-only) bars → ALL 200 render. ──
    static string Section2_FitAllFitsWholeSeries()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetFitAllOnAutoScale(true);
            var bars = GappedDailyBars(200);   // ~280 calendar days; span_slots > bar count
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            if (cv.TotalBarCount != 200)
                return "S2 FITALL-FITS-02: TotalBarCount=" + cv.TotalBarCount + ", want 200.";
            if (!cv.ViewState.auto_scale)
                return "S2 FITALL-FITS-02: auto_scale flipped false (fit ResetView regression).";
            // The fit sizes by TIME span, not bar count. span_slots = (last-first)/basis + 1 (≈280).
            long basis = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            double spanSlots = (double)(bars[bars.Length - 1].open_time_ms - bars[0].open_time_ms) / basis + 1.0;
            if (spanSlots <= 200)
                return "S2 FITALL-FITS-02: harness bug — span_slots=" + spanSlots + " must exceed 200 to "
                     + "distinguish time-fit from count-fit (the bars aren't gapped).";
            float expectedCw = Mathf.Clamp(PLOT_WIDTH_PX / (float)spanSlots,
                ChartViewState.MIN_CELL_WIDTH_PX, ChartViewState.MAX_CELL_WIDTH_PX);
            if (!Approx(cv.ViewState.cell_width_px, expectedCw, 0.05f))
                return "S2 FITALL-FITS-02: cell_width=" + cv.ViewState.cell_width_px + ", want " + expectedCw
                     + " (plot_width / span_slots). If it's ~" + (PLOT_WIDTH_PX / 200f) + " the fit is sized by "
                     + "bar COUNT not TIME — gapped bars will clip off the left.";
            int rendered = cv.RenderedBarCount;
            if (rendered != 200)
                return "S2 FITALL-FITS-02: RenderedBarCount=" + rendered + ", want EXACTLY 200 (the WHOLE "
                     + "series, no tolerance). ~90 → fit branch dead (cell_width pinned DEFAULT 6); ~143 → fit "
                     + "sized by bar count so the oldest bars clipped off the left edge (count-vs-time bug); "
                     + "199 → the oldest bar dropped by the float round-trip right-anchor (#156 follow-up).";
            // Oldest bar must land EXACTLY on the left edge: the fit branch pins translation = first. A
            // re-derived right-anchor round-trips through float32 and can push translation a few hundred ms
            // past `first`, which the strict `open_time_ms < winStart` virtualization then drops (~25% of
            // spans). Equality (not <=) is the regression gate for that fix.
            if (cv.ViewState.translation_ms != bars[0].open_time_ms)
                return "S2 FITALL-FITS-02: translation_ms=" + cv.ViewState.translation_ms + " != first bar open="
                     + bars[0].open_time_ms + " — the oldest bar is not pinned to the left edge (float "
                     + "round-trip right-anchor regression, #156 follow-up).";
            Debug.Log("[E2E FITALL-FITS-02 PASS] fit_all=true → 200 gapped bars / span_slots=" + (int)spanSlots
                    + " @ cell_width=" + cv.ViewState.cell_width_px + " → RenderedBarCount=" + rendered
                    + ", first bar at/right of left edge (entire period visible despite weekend gaps).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── FITALL-OVERFLOW-03: 1000 bars > 540px → clamp MIN + right-anchor latest ~540. ──
    static string Section3_FitAllOverflowClampMin()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetFitAllOnAutoScale(true);
            var bars = SyntheticDailyBars(1000);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            // fit = 540/1000 = 0.54 < MIN → clamp to MIN(1.0).
            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.MIN_CELL_WIDTH_PX))
                return "S3 FITALL-OVERFLOW-03: cell_width=" + cv.ViewState.cell_width_px + ", want MIN "
                     + ChartViewState.MIN_CELL_WIDTH_PX + " (overflow clamp).";
            int rendered = cv.RenderedBarCount;
            // plot_width / MIN = 540 visible.
            if (Math.Abs(rendered - 540) > 2)
                return "S3 FITALL-OVERFLOW-03: RenderedBarCount=" + rendered + ", want ~540 (MIN-clamp window). "
                     + "1000 would mean no virtualization; 90 would mean clamp failed to widen the window.";
            // Right-anchor: latest bar near the right edge → translation = latest - (540-1)*basis.
            long basis = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            long latest = bars[bars.Length - 1].open_time_ms;
            long expected = latest - (long)((PLOT_WIDTH_PX / ChartViewState.MIN_CELL_WIDTH_PX - 1) * basis);
            if (Math.Abs(cv.ViewState.translation_ms - expected) > basis)
                return "S3 FITALL-OVERFLOW-03: translation_ms=" + cv.ViewState.translation_ms + ", want "
                     + expected + " (latest bar not right-anchored).";
            Debug.Log("[E2E FITALL-OVERFLOW-03 PASS] 1000 bars → cell_width=MIN(1.0) / RenderedBarCount="
                    + rendered + " right-anchored on the latest bar (owner MIN-clamp + 右端寄せ policy).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── FITALL-RESET-04: zoom in then RequestResetView returns to the fit view. ──
    static string Section4_FitAllResetReturnsFit()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetFitAllOnAutoScale(true);
            var bars = SyntheticDailyBars(300);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            float fitCw = cv.ViewState.cell_width_px;   // ~1.8

            // Zoom in hard (auto_scale → false, cell_width grows well past the fit width).
            cv.ZoomByScroll(20f, PLOT_WIDTH_PX * 0.5f);
            Canvas.ForceUpdateCanvases();
            if (cv.ViewState.auto_scale)
                return "S4 FITALL-RESET-04: precondition — auto_scale must be false after zoom.";
            if (Approx(cv.ViewState.cell_width_px, fitCw, 0.1f))
                return "S4 FITALL-RESET-04: precondition — cell_width must differ from the fit width after zoom.";

            cv.RequestResetView();
            Canvas.ForceUpdateCanvases();

            if (!cv.ViewState.auto_scale)
                return "S4 FITALL-RESET-04: auto_scale stayed false after reset.";
            if (!Approx(cv.ViewState.cell_width_px, fitCw, 0.05f))
                return "S4 FITALL-RESET-04: cell_width=" + cv.ViewState.cell_width_px + " after reset, want fit "
                     + fitCw + " — RequestResetView didn't return to the fit view.";
            int rendered = cv.RenderedBarCount;
            // EXACT 300 (no ±2 tolerance): a CONTIGUOUS 300-bar span is one of the ~25% of spans whose
            // float round-trip right-anchor dropped the oldest bar (rendered 299). The fit branch now pins
            // translation = first, so all 300 render and translation == first exactly. This is the
            // deterministic RED→GREEN gate for the #156 follow-up float round-trip fix.
            if (rendered != 300)
                return "S4 FITALL-RESET-04: RenderedBarCount=" + rendered + " after reset, want EXACTLY 300 "
                     + "(全期間 again). 299 → oldest bar dropped by the float round-trip right-anchor.";
            if (cv.ViewState.translation_ms != bars[0].open_time_ms)
                return "S4 FITALL-RESET-04: translation_ms=" + cv.ViewState.translation_ms + " != first bar open="
                     + bars[0].open_time_ms + " — fit reset must pin the oldest bar to the left edge.";
            Debug.Log("[E2E FITALL-RESET-04 PASS] zoom → RequestResetView → cell_width back to fit (" + fitCw
                    + ") / RenderedBarCount=" + rendered + ": reset returns to the whole-period view.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── FITALL-WIRING-05: the mode→fit POLICY (DockShape.ShouldFitChartToAll) the poll loop wires into
    //    every chart. Replay → fit-all, Live(Manual/Auto) → DEFAULT. Inverting the predicate (the litmus
    //    `!IsLiveShape` couldn't catch) flips every assertion → RED. Pins the host wiring decision that the
    //    ChartView-direct sections above don't exercise. ──
    static string Section5_ModeToFitPolicy()
    {
        // Replay charts fit the whole cold-loaded series.
        if (!DockShape.ShouldFitChartToAll(FooterModeViewModel.Replay))
            return "S5 FITALL-WIRING-05: ShouldFitChartToAll(Replay)=false, want TRUE (Replay fits 全期間). "
                 + "If inverted, Replay charts would render the DEFAULT ~90-bar window — the #156 symptom.";
        // Live charts (both shapes) keep the DEFAULT 6px right-anchor.
        if (DockShape.ShouldFitChartToAll(FooterModeViewModel.LiveManual))
            return "S5 FITALL-WIRING-05: ShouldFitChartToAll(LiveManual)=true, want FALSE (Live keeps DEFAULT).";
        if (DockShape.ShouldFitChartToAll(FooterModeViewModel.LiveAuto))
            return "S5 FITALL-WIRING-05: ShouldFitChartToAll(LiveAuto)=true, want FALSE (Live keeps DEFAULT).";
        // An unknown/garbage mode is NOT live → treated as Replay-like (fit). Mirrors IsLiveShape's
        // default-false contract, so a never-set mode doesn't silently disable fit-all.
        if (!DockShape.ShouldFitChartToAll("garbage-mode"))
            return "S5 FITALL-WIRING-05: ShouldFitChartToAll(unknown)=false, want TRUE (non-live ⇒ fit).";
        Debug.Log("[E2E FITALL-WIRING-05 PASS] DockShape.ShouldFitChartToAll: Replay/unknown → fit-all, "
                + "LiveManual/LiveAuto → DEFAULT 6px. The poll's mode→fit wiring can't silently invert.");
        return null;
    }

    // ---- helpers ----

    static bool Approx(float a, float b, float tol) => Mathf.Abs(a - b) <= tol;

    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartFitAllCanvas", typeof(Canvas));
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

    // Deterministic Daily OhlcPoint array (no RNG → reproducible across CI runs).
    static OhlcPoint[] SyntheticDailyBars(int n)
    {
        var arr = new OhlcPoint[n];
        for (int i = 0; i < n; i++)
        {
            double drift = ((i % 7) - 3) * 0.5;
            double o = 100.0 + drift;
            double c = 100.0 + drift + (i % 2 == 0 ? +0.3 : -0.3);
            double h = Math.Max(o, c) + 0.4;
            double l = Math.Min(o, c) - 0.4;
            arr[i] = new OhlcPoint
            {
                open_time_ms = START_MS + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o, high = h, low = l, close = c, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    // Weekday-only Daily bars (5/7 density): n trading bars span ~n*7/5 calendar days, so the calendar
    // SPAN exceeds the bar COUNT — the case that distinguishes time-fit from count-fit. Deterministic.
    static OhlcPoint[] GappedDailyBars(int n)
    {
        var arr = new OhlcPoint[n];
        int produced = 0;
        long day = 0;
        while (produced < n)
        {
            if (day % 7 < 5)   // 0..4 = weekday; 5,6 = weekend (skipped → a gap in open_time_ms)
            {
                double drift = ((produced % 7) - 3) * 0.5;
                double o = 100.0 + drift;
                double c = 100.0 + drift + (produced % 2 == 0 ? +0.3 : -0.3);
                double h = Math.Max(o, c) + 0.4;
                double l = Math.Min(o, c) - 0.4;
                arr[produced] = new OhlcPoint
                {
                    open_time_ms = START_MS + day * ChartViewState.BASIS_DAILY_MS,
                    open = o, high = h, low = l, close = c, volume = 1000.0 + produced,
                };
                produced++;
            }
            day++;
        }
        return arr;
    }
}
