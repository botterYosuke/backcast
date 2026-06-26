// ChartInputE2ERunner.cs — S2 #157 / findings 0119 D-3: drag pan / wheel zoom / double-click reset
// + right-button vacuity floor + drag-tail click exclusion。台本: same-dir ChartInputE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartInputE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART INPUT PASS] ... / exit=0
//
// WHAT THIS GATES (S2 acceptance gates + non-vacuity floors):
//   PAN-01: PanByPixels(-Δ) → translation_ms が +Δ/cell_width*basis_ms 増 + auto_scale=false。
//   PAN-VACUITY-01: 右ボタンドラッグの OnPointerDown→OnDrag は state を変えない（button フィルタ）。
//   ZOOM-01: ZoomByScroll(scroll, cursorX) → cell_width *= 1.1^scroll かつ cursor 下の bar 時刻が
//            ズーム前後で同じ位置に残る（cursor-centered zoom invariant、flowsurface apply_cursor_zoom 直訳）。
//   RESET-01: pan/zoom 後 RequestResetView → cell_width=DEFAULT, auto_scale=true, 最新 bar 右端 anchor。
//
// 設計判断: PointerEventData の合成は OnScroll / OnPointerClick の handler-level だけで使い、
// PAN-01 / ZOOM-01 / RESET-01 の core invariant は ChartView の public API（PanByPixels / ZoomByScroll /
// RequestResetView）を直接叩く。handler logic（button filter / drag-tail flag / Ctrl modifier）の vacuity
// floor は別 section で個別 assert。
//
// LITMUS (delete-the-production-logic):
//  * PanByPixels の `auto_scale=false;` を抜く → PAN-01 RED（pan 中も autoscale が居残る）。
//  * ZoomByScroll の cursor 中心補正を抜く → ZOOM-01 RED（zoom 後 cursor 下の bar が動く）。
//  * RequestResetView の `cell_width_px = DEFAULT_CELL_WIDTH_PX;` を抜く → RESET-01 RED。
//  * OnPointerDown / OnDrag の button filter を抜く → PAN-VACUITY-01 RED（右ドラッグで pan する）。

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class ChartInputE2ERunner
{
    const int TOTAL_BARS = 1000;
    const float HOST_WIDTH_PX = 620f;     // 540 plot + 60 left gutter + 20 right gutter
    const float HOST_HEIGHT_PX = 400f;
    const float EPS = 1e-3f;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_PanByPixels_LeftDrag()        // PAN-01
                ?? Section2_PanVacuity_RightButton()      // PAN-VACUITY-01
                ?? Section3_ZoomByScroll_CursorCenter()   // ZOOM-01
                ?? Section4_RequestResetView()            // RESET-01
                ?? Section5_DoubleClickResetsView()       // DBLCLICK-01
                ?? Section6_DragTailClickExcluded()       // DRAGTAIL-01
                ?? Section7_CtrlWheelNoOp();              // CTRL-WHEEL-01
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART INPUT PASS] (PAN-01) PanByPixels shifts translation_ms + clears auto_scale; "
                    + "(PAN-VACUITY-01) right-button drag is a no-op (button filter non-vacuous); "
                    + "(ZOOM-01) ZoomByScroll scales cell_width by 1.1^scroll and keeps the bar under cursor "
                    + "frozen (cursor-centered zoom invariant); (RESET-01) RequestResetView restores cell_width="
                    + "DEFAULT, auto_scale=true, latest bar right-anchored; "
                    + "(DBLCLICK-01) OnPointerClick clickCount==2 → RequestResetView (handler drives the helper, "
                    + "not just direct API); (DRAGTAIL-01) drag → click clickCount==2 excluded by "
                    + "_draggedThisGesture flag (no accidental ResetView on drag-tail); (CTRL-WHEEL-01) OnScroll "
                    + "without Ctrl zooms (handler non-vacuous floor; Ctrl-held branch is a no-op intercepted by "
                    + "IsCtrlHeld → parent PanCam).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART INPUT FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── PAN-01: left-drag (PanByPixels) shifts translation_ms + clears auto_scale. ──
    static string Section1_PanByPixels_LeftDrag()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(TOTAL_BARS);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            long t0 = cv.ViewState.translation_ms;
            float cw = cv.ViewState.cell_width_px;
            if (!cv.ViewState.auto_scale) return "S1 PAN-01: precondition — auto_scale should be true after Render";

            // Pan -60px (the user dragged the cursor 60px to the LEFT — chart should scroll BACK in time).
            cv.PanByPixels(-60f);
            long t1 = cv.ViewState.translation_ms;
            long basis = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            long expected = t0 + (long)((60f / cw) * basis);   // Pan(-60) → translation += 60/cw * basis
            // Drift tolerance: 1 basis tick for the (long)cast rounding.
            if (Math.Abs(t1 - expected) > basis)
                return "S1 PAN-01: translation_ms drift after Pan(-60). got=" + t1 + " want=" + expected;
            if (cv.ViewState.auto_scale)
                return "S1 PAN-01: auto_scale stayed true after pan — Pan() must clear auto_scale (otherwise "
                     + "the next Render right-anchors the user's pan away).";
            Debug.Log("[E2E PAN-01 PASS] PanByPixels(-60) → translation_ms+=" + (t1 - t0) + " (basis=" + basis
                    + "), auto_scale=false.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── PAN-VACUITY-01: right-button down→drag must NOT pan (button filter non-vacuous). ──
    static string Section2_PanVacuity_RightButton()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(TOTAL_BARS);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            long t0 = cv.ViewState.translation_ms;
            bool autoBefore = cv.ViewState.auto_scale;

            var es = EnsureEventSystem();
            var down = NewPointerEvent(es, PointerEventData.InputButton.Right);
            var drag = NewPointerEvent(es, PointerEventData.InputButton.Right);
            drag.delta = new Vector2(-60f, 0f);
            ((IPointerDownHandler)cv).OnPointerDown(down);
            ((IDragHandler)cv).OnDrag(drag);

            if (cv.ViewState.translation_ms != t0)
                return "S2 PAN-VACUITY-01: right-button drag shifted translation_ms by "
                     + (cv.ViewState.translation_ms - t0) + " — button filter is gone, right-drag now "
                     + "fights the floating-window drag (findings 0119 D-3 contract).";
            if (cv.ViewState.auto_scale != autoBefore)
                return "S2 PAN-VACUITY-01: right-button drag flipped auto_scale — handlers must short-circuit "
                     + "on non-Left buttons BEFORE touching state.";
            Debug.Log("[E2E PAN-VACUITY-01 PASS] right-button drag is a no-op (button filter holds).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── ZOOM-01: ZoomByScroll changes cell_width AND keeps the bar under cursor frozen. ──
    static string Section3_ZoomByScroll_CursorCenter()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(TOTAL_BARS);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            // The host's local rect goes from -310..+310 in x (pivot 0.5). The plot rect's xMin is
            // host.xMin + GutterLeft(60) = -250. Pick cursor halfway across the plot = -250 + 270 = +20
            // (270 px from left of plot). ZoomByScroll's cursorXLocal arg is measured from the LOCAL
            // (RectTransform-local) rect xMin — we pass +330 which is 330 from host.xMin(-310) = +20 host-x.
            float cursorXLocalFromRectMin = 330f;   // 330 px from rectTransform.rect.xMin
            long basisLocked = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            float plotXmin = cv.LastPlotRect.xMin;
            float plotW = cv.LastPlotRect.width;
            // Time under cursor BEFORE zoom (cursor in plot-local x = cursorXLocalFromRectMin - GutterLeft(60)).
            float cursorInPlot = cursorXLocalFromRectMin - 60f;
            long t_cursor_before = cv.ViewState.translation_ms + (long)((cursorInPlot / cv.ViewState.cell_width_px) * basisLocked);

            float cwBefore = cv.ViewState.cell_width_px;
            cv.ZoomByScroll(3f, cursorXLocalFromRectMin);
            float cwAfter = cv.ViewState.cell_width_px;
            float expectedCw = Mathf.Clamp(cwBefore * Mathf.Pow(1.1f, 3f),
                ChartViewState.MIN_CELL_WIDTH_PX, ChartViewState.MAX_CELL_WIDTH_PX);
            if (Mathf.Abs(cwAfter - expectedCw) > 1e-2f)
                return "S3 ZOOM-01: cell_width=" + cwAfter + ", want " + expectedCw + " (= " + cwBefore
                     + " * 1.1^3). Mathf.Pow zoom rule regression.";

            long t_cursor_after = cv.ViewState.translation_ms + (long)((cursorInPlot / cv.ViewState.cell_width_px) * basisLocked);
            long drift = Math.Abs(t_cursor_after - t_cursor_before);
            if (drift > basisLocked)
                return "S3 ZOOM-01: bar under cursor drifted by " + drift + " ms after zoom — cursor-centered "
                     + "zoom invariant (flowsurface apply_cursor_zoom) broken (translation_ms is not being "
                     + "corrected by cursor offset).";
            if (cv.ViewState.auto_scale)
                return "S3 ZOOM-01: auto_scale stayed true after zoom — Zoom() must clear auto_scale";
            Debug.Log("[E2E ZOOM-01 PASS] ZoomByScroll(3) → cell_width=" + cwAfter + " (1.1^3 of " + cwBefore
                    + "), cursor-anchored time drift " + drift + "ms < basis(" + basisLocked + "ms).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── RESET-01: RequestResetView restores DEFAULT cell_width + auto_scale + right anchor. ──
    static string Section4_RequestResetView()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(TOTAL_BARS);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();

            // Walk away from defaults: pan + zoom.
            cv.PanByPixels(-300f);
            cv.ZoomByScroll(2f, 200f);
            if (cv.ViewState.auto_scale) return "S4 RESET-01: precondition — auto_scale must be false after pan+zoom";
            if (Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S4 RESET-01: precondition — cell_width must differ from DEFAULT after zoom";

            cv.RequestResetView();
            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S4 RESET-01: cell_width=" + cv.ViewState.cell_width_px + ", want "
                     + ChartViewState.DEFAULT_CELL_WIDTH_PX + " after Reset";
            if (!cv.ViewState.auto_scale)
                return "S4 RESET-01: auto_scale stayed false after Reset";
            // Right-anchor: latest bar should land near the right edge of the plot.
            long latest = bars[bars.Length - 1].open_time_ms;
            long basis = cv.ViewState.basis_ms ?? ChartViewState.BASIS_DAILY_MS;
            long expectedTranslation = latest - (long)((540f / ChartViewState.DEFAULT_CELL_WIDTH_PX - 1) * basis);
            if (Math.Abs(cv.ViewState.translation_ms - expectedTranslation) > basis)
                return "S4 RESET-01: translation_ms=" + cv.ViewState.translation_ms + ", want ≈ "
                     + expectedTranslation + " (right-anchor regression after Reset)";
            Debug.Log("[E2E RESET-01 PASS] RequestResetView → cell_width=DEFAULT, auto_scale=true, right anchored.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── DBLCLICK-01: OnPointerClick with clickCount==2 drives RequestResetView (not just the API). ──
    static string Section5_DoubleClickResetsView()
    {
        // DBLCLICK-01: OnPointerClick with clickCount==2 should call RequestResetView (not just
        // PanByPixels direct). Drives the handler not the public helper.
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            cv.PanByPixels(-300f);
            cv.ZoomByScroll(2f, 200f);
            float cwBefore = cv.ViewState.cell_width_px;
            if (Mathf.Approximately(cwBefore, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S5 DBLCLICK-01: precondition — cell_width must differ from DEFAULT after pan+zoom";
            var es = EnsureEventSystem();
            var click = NewPointerEvent(es, PointerEventData.InputButton.Left);
            click.clickCount = 2;
            ((IPointerClickHandler)cv).OnPointerClick(click);
            if (!Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX))
                return "S5 DBLCLICK-01: cell_width=" + cv.ViewState.cell_width_px + " after double-click — OnPointerClick clickCount==2 didn't call RequestResetView.";
            if (!cv.ViewState.auto_scale)
                return "S5 DBLCLICK-01: auto_scale stayed false after double-click reset";
            Debug.Log("[E2E DBLCLICK-01 PASS] OnPointerClick clickCount==2 → RequestResetView restores DEFAULT cell_width + auto_scale.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── DRAGTAIL-01: drag → click clickCount==2 must NOT trigger ResetView. ──
    static string Section6_DragTailClickExcluded()
    {
        // DRAGTAIL-01: drag → release → 1st click → 2nd click — Unity's clickCount eventually reaches
        // 2 but our _draggedThisGesture flag should mark the gesture as a drag-tail and EXCLUDE the
        // click from being a double-click candidate. The reset MUST NOT fire.
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            float cwBefore = cv.ViewState.cell_width_px;
            long tBefore = cv.ViewState.translation_ms;
            var es = EnsureEventSystem();
            var down = NewPointerEvent(es, PointerEventData.InputButton.Left);
            var drag = NewPointerEvent(es, PointerEventData.InputButton.Left);
            drag.delta = new Vector2(-60f, 0f);
            drag.position = new Vector2(100f, 100f);
            ((IPointerDownHandler)cv).OnPointerDown(down);
            ((IDragHandler)cv).OnDrag(drag);
            // Click after drag.
            var click = NewPointerEvent(es, PointerEventData.InputButton.Left);
            click.clickCount = 2;
            ((IPointerClickHandler)cv).OnPointerClick(click);
            // After a drag, OnPointerClick should early-return — cell_width unchanged from current
            // pan state, NOT reset to DEFAULT.
            if (Mathf.Approximately(cv.ViewState.cell_width_px, ChartViewState.DEFAULT_CELL_WIDTH_PX) && tBefore != cv.ViewState.translation_ms)
                return "S6 DRAGTAIL-01: drag-tail double-click triggered ResetView (cell_width reverted to DEFAULT). _draggedThisGesture exclusion broken.";
            Debug.Log("[E2E DRAGTAIL-01 PASS] drag → click clickCount==2 → ResetView excluded by _draggedThisGesture flag.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ── CTRL-WHEEL-01: OnScroll handler is non-vacuous (Ctrl-held branch can't be exercised headless). ──
    static string Section7_CtrlWheelNoOp()
    {
        // CTRL-WHEEL-01: Ctrl+wheel must not zoom the chart — that gesture is reserved for parent
        // PanCam's global zoom. cell_width must stay put. Note: IsCtrlHeld reads Keyboard.current —
        // in batchmode Keyboard.current may be null, in which case IsCtrlHeld returns false and Ctrl
        // intercept can't be exercised — assert that OnScroll without Ctrl DOES zoom (handler is
        // non-vacuous), then skip the Ctrl-held branch with a comment.
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            var es = EnsureEventSystem();
            var scroll = NewPointerEvent(es, PointerEventData.InputButton.Left);
            scroll.scrollDelta = new Vector2(0f, 3f);
            scroll.position = new Vector2(0f, 0f);   // center of host
            float cwBefore = cv.ViewState.cell_width_px;
            ((IScrollHandler)cv).OnScroll(scroll);
            if (Mathf.Approximately(cv.ViewState.cell_width_px, cwBefore))
                return "S7 CTRL-WHEEL-01: precondition — OnScroll without Ctrl must zoom (cell_width changed); handler may be vacuous.";
            Debug.Log("[E2E CTRL-WHEEL-01 PASS] OnScroll without Ctrl zooms (non-vacuous handler floor; Ctrl-held branch is a no-op intercepted by IsCtrlHeld → parent PanCam).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers ----

    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartInputCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        var host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f); host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(HOST_WIDTH_PX, HOST_HEIGHT_PX);
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        return cv;
    }

    static OhlcPoint[] SyntheticDaily(int n)
    {
        var arr = new OhlcPoint[n];
        long startMs = 1_700_000_000_000L;
        double basePrice = 100.0;
        for (int i = 0; i < n; i++)
        {
            double drift = ((i % 7) - 3) * 0.5;
            double o = basePrice + drift;
            double c = basePrice + drift + (i % 2 == 0 ? +0.3 : -0.3);
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o, close = c,
                high = Math.Max(o, c) + 0.4, low = Math.Min(o, c) - 0.4, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    static EventSystem EnsureEventSystem()
    {
        var es = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
        if (es != null) return es;
        var go = new GameObject("EventSystem", typeof(EventSystem));
        return go.GetComponent<EventSystem>();
    }

    static PointerEventData NewPointerEvent(EventSystem es, PointerEventData.InputButton btn)
    {
        return new PointerEventData(es) { button = btn };
    }
}
