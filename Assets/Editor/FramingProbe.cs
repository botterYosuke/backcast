// FramingProbe.cs — issues #166-168 / findings 0123 (AFK regression gate for ウィンドウフレーミング)
//
// Headless gate for the title-bar double-click / sidebar-row-click framing seam. Run:
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod FramingProbe.Run -logFile <abs log>
//
// Emits one `[E2E FRAMING-<ID> PASS|FAIL]` tag per Action-ID (findings 0123 §7) so the
// rollup parser in scripts/E2ERollup.ps1 can pin verdicts independently. The probe ALWAYS
// exits 0 on overall-pass (every Action-ID PASS) and 1 if any Action-ID is FAIL.
//
// Sections (one Action-ID each):
//   FRAMING-S1-MATH-CENTRE   dock/floating 両 plane で centre が viewport 原点 (issue #166 AC#1)
//   FRAMING-S1-MATH-FIT      contain-fit zoom が両軸の小さい方に一致 (issue #166 AC#1)
//   FRAMING-S1-MATH-MARGIN   m=0.06 で screen 上の窓幅が viewport の 94% 内 (issue #166 AC#1)
//   FRAMING-S1-MATH-CLAMP    極小→MAX_ZOOM, 巨大→MIN_ZOOM (centre は維持) (issue #166 AC#1)
//   FRAMING-S1-DBL-CLICK     clickCount==2 で applyView 1 回・==1 で 0 回 (issue #166 AC#2)
//   FRAMING-S2-GLIDE         200ms 後 to に収束・途中 ease-out lerp・overshoot 無し (issue #167 AC#1/AC#3/AC#4)
//   FRAMING-S2-INTERRUPT     外部書き戻しで kill・kill 後値はその外部値 (issue #167 AC#2)
//   FRAMING-S3-SIDEBAR       SelectRow 毎に FocusChartHook 発火 (issue #168 AC#1/AC#2)
//   FRAMING-S3-NOOP          chart 窓 null / inactive で no-op (issue #168 AC#3)

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class FramingProbe
{
    const float EPS = 1e-3f;

    public static void Run()
    {
        int fails = 0;
        var spawned = new List<GameObject>();
        try
        {
            fails += S1MathCentre()      ? 0 : 1;
            fails += S1MathFit()         ? 0 : 1;
            fails += S1MathMargin()      ? 0 : 1;
            fails += S1MathClamp()       ? 0 : 1;
            fails += S1DoubleClick(spawned) ? 0 : 1;
            fails += S2Glide(spawned)    ? 0 : 1;
            fails += S2Interrupt(spawned)? 0 : 1;
            fails += S3Sidebar()         ? 0 : 1;
            fails += S3Noop(spawned)     ? 0 : 1;
        }
        catch (System.Exception e)
        {
            Debug.LogError("[FRAMING PROBE FAIL] driver: " + e);
            fails++;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) Object.DestroyImmediate(go);
        }

        if (fails == 0) Debug.Log("[FRAMING PROBE PASS] all 9 framing Action-IDs PASS");
        else            Debug.LogError("[FRAMING PROBE FAIL] " + fails + " section(s) FAIL — see [E2E FRAMING-* FAIL] lines above");
        EditorApplication.Exit(fails == 0 ? 0 : 1);
    }

    // ---- S1: math seam — centre, fit, margin, clamp ----

    // dock (factor=1) + floating (factor=1.2) で centre が viewport(0,0) に乗る。
    // factor を 1 にした「parallax 無視」版が floating plane で viewport(centre) を原点から外す
    // ことも示し parallax 補正が load-bearing なことを pin。
    static bool S1MathCentre()
    {
        Vector2 viewport = new Vector2(1600f, 900f);

        // (a) dock plane (factor=1.0)
        {
            Vector2 tl = new Vector2(300f, 200f);
            Vector2 sz = new Vector2(520f, 360f);
            var v = CanvasViewMath.FrameWindow(tl, sz, viewport, 1f, 0.06f);
            Vector2 centre = new Vector2(tl.x + sz.x / 2f, tl.y - sz.y / 2f);
            Vector2 vp = CanvasViewMath.LogicalToViewport(centre, v);
            if (vp.magnitude > EPS)
                return TagFail("FRAMING-S1-MATH-CENTRE", "dock plane: viewport(centre)=" + vp + " not at origin (v=" + Format(v) + ")");
        }
        // (b) floating plane (factor=1.2)
        {
            Vector2 tl = new Vector2(-450f, 320f);
            Vector2 sz = new Vector2(440f, 280f);
            const float factor = 1.2f;
            var v = CanvasViewMath.FrameWindow(tl, sz, viewport, factor, 0.06f);
            Vector2 centre = new Vector2(tl.x + sz.x / 2f, tl.y - sz.y / 2f);
            // Foreground render: viewport(centre) = zoom * (centre - factor*pan)
            Vector2 vp = v.zoom * (centre - factor * new Vector2(v.panX, v.panY));
            if (vp.magnitude > EPS)
                return TagFail("FRAMING-S1-MATH-CENTRE", "floating plane: viewport(centre)=" + vp + " not at origin (v=" + Format(v) + ")");

            // sanity: if we IGNORED the parallax correction (pan = centre) the floating plane would NOT be centred.
            var bad = new CanvasView(centre.x, centre.y, v.zoom);
            Vector2 vpBad = v.zoom * (centre - factor * new Vector2(bad.panX, bad.panY));
            if (vpBad.magnitude <= EPS)
                return TagFail("FRAMING-S1-MATH-CENTRE", "parallax non-load-bearing? pan=centre also centred floating plane — math degenerate");
        }
        return TagPass("FRAMING-S1-MATH-CENTRE", "dock(1.0) + floating(1.2) centre at viewport origin; pan=centre/factor load-bearing");
    }

    // contain-fit zoom = min((1-m)*vw/w, (1-m)*vh/h). 縦長窓と横長窓の双方で軸選択を確認。
    static bool S1MathFit()
    {
        Vector2 viewport = new Vector2(1600f, 900f);
        const float m = 0.06f;
        // (a) 横長窓 (w 主導): zx < zy.
        {
            var v = CanvasViewMath.FrameWindow(new Vector2(0f, 0f), new Vector2(800f, 200f), viewport, 1f, m);
            float zx = (1f - m) * viewport.x / 800f;
            float zy = (1f - m) * viewport.y / 200f;
            float expected = Mathf.Min(zx, zy);
            if (Mathf.Abs(v.zoom - expected) > EPS)
                return TagFail("FRAMING-S1-MATH-FIT", "w-driven: zoom=" + v.zoom + " expected=" + expected);
        }
        // (b) 縦長窓 (h 主導): zy < zx.
        {
            var v = CanvasViewMath.FrameWindow(new Vector2(0f, 0f), new Vector2(200f, 800f), viewport, 1f, m);
            float zx = (1f - m) * viewport.x / 200f;
            float zy = (1f - m) * viewport.y / 800f;
            float expected = Mathf.Min(zx, zy);
            if (Mathf.Abs(v.zoom - expected) > EPS)
                return TagFail("FRAMING-S1-MATH-FIT", "h-driven: zoom=" + v.zoom + " expected=" + expected);
        }
        // (c) m=0: 枠ぴったり。screen 上の窓幅 = vw か vh のうち先に当たる方。
        {
            var v = CanvasViewMath.FrameWindow(new Vector2(0f, 0f), new Vector2(500f, 500f), viewport, 1f, 0f);
            float expected = Mathf.Min(viewport.x / 500f, viewport.y / 500f);
            if (Mathf.Abs(v.zoom - expected) > EPS)
                return TagFail("FRAMING-S1-MATH-FIT", "m=0: zoom=" + v.zoom + " expected=" + expected);
        }
        return TagPass("FRAMING-S1-MATH-FIT", "contain-fit zoom = min((1-m)*vw/w, (1-m)*vh/h) on 3 cases");
    }

    // m=0.06 のとき、screen 上の窓幅 (zoom * w) が viewport の 94% 以内。
    static bool S1MathMargin()
    {
        Vector2 viewport = new Vector2(1600f, 900f);
        Vector2 sz = new Vector2(700f, 400f);
        var v = CanvasViewMath.FrameWindow(new Vector2(120f, 80f), sz, viewport, 1f, 0.06f);
        float screenW = v.zoom * sz.x;
        float screenH = v.zoom * sz.y;
        if (screenW > 0.94f * viewport.x + EPS)
            return TagFail("FRAMING-S1-MATH-MARGIN", "screen window width " + screenW + " > 94% viewport " + (0.94f * viewport.x));
        if (screenH > 0.94f * viewport.y + EPS)
            return TagFail("FRAMING-S1-MATH-MARGIN", "screen window height " + screenH + " > 94% viewport " + (0.94f * viewport.y));
        return TagPass("FRAMING-S1-MATH-MARGIN", "screen size <= (1-m)*viewport on both axes (m=0.06)");
    }

    // 極小窓 → MAX_ZOOM 飽和、巨大窓 → MIN_ZOOM 飽和、ともに centre は viewport 原点を維持。
    static bool S1MathClamp()
    {
        Vector2 viewport = new Vector2(1600f, 900f);
        // (a) 極小窓 — zoom_fit > MAX_ZOOM (5.0). 結果は MAX_ZOOM。
        {
            Vector2 tl = new Vector2(50f, 60f);
            Vector2 sz = new Vector2(1f, 1f);
            var v = CanvasViewMath.FrameWindow(tl, sz, viewport, 1f, 0.06f);
            if (Mathf.Abs(v.zoom - CanvasView.MAX_ZOOM) > EPS)
                return TagFail("FRAMING-S1-MATH-CLAMP", "tiny window: zoom=" + v.zoom + " expected MAX_ZOOM=" + CanvasView.MAX_ZOOM);
            Vector2 centre = new Vector2(tl.x + sz.x / 2f, tl.y - sz.y / 2f);
            Vector2 vp = CanvasViewMath.LogicalToViewport(centre, v);
            if (vp.magnitude > EPS)
                return TagFail("FRAMING-S1-MATH-CLAMP", "tiny window clamp lost centre: vp=" + vp);
        }
        // (b) 巨大窓 — zoom_fit < MIN_ZOOM (0.2). 結果は MIN_ZOOM。
        {
            Vector2 tl = new Vector2(0f, 0f);
            Vector2 sz = new Vector2(100000f, 100000f);
            var v = CanvasViewMath.FrameWindow(tl, sz, viewport, 1f, 0.06f);
            if (Mathf.Abs(v.zoom - CanvasView.MIN_ZOOM) > EPS)
                return TagFail("FRAMING-S1-MATH-CLAMP", "huge window: zoom=" + v.zoom + " expected MIN_ZOOM=" + CanvasView.MIN_ZOOM);
            Vector2 centre = new Vector2(tl.x + sz.x / 2f, tl.y - sz.y / 2f);
            Vector2 vp = CanvasViewMath.LogicalToViewport(centre, v);
            if (vp.magnitude > EPS)
                return TagFail("FRAMING-S1-MATH-CLAMP", "huge window clamp lost centre: vp=" + vp);
        }
        return TagPass("FRAMING-S1-MATH-CLAMP", "tiny->MAX_ZOOM, huge->MIN_ZOOM; centre preserved on both");
    }

    // ---- S1: detection seam — IPointerClickHandler / clickCount ----

    // clickCount==2 で applyView が 1 回呼ばれその引数が FrameWindow の結果と一致、
    // clickCount==1 では呼ばれない。
    static bool S1DoubleClick(List<GameObject> spawned)
    {
        var parent = NewRoot("S1DBL_Parent", spawned);
        var content = NewRect("Content", parent);
        var viewport = NewRect("Viewport", parent, size: new Vector2(1600f, 900f));
        var canvas = new InfiniteCanvasController(content, null, 1f);
        var windowsCtrl = new FloatingWindowController(content, FloatingWindowCatalog.Default(),
            (spec, id) => null);    // factory not used (we Adopt below)

        // Build a window RectTransform with pivot=(0,1), put it at a known top-left+size, Adopt it
        // under a kind so RectOf(id) returns it.
        var winRoot = NewRect("dblclickWin", content,
            pivot: new Vector2(0f, 1f),
            anchoredPosition: new Vector2(100f, 200f),
            size: new Vector2(520f, 360f));
        windowsCtrl.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "wid", winRoot);

        // The title input MonoBehaviour, parented under the window root (Initialize attaches a resize
        // grip to transform.parent — the window root — so a parent is required for compile parity).
        var titleGo = new GameObject("Title", typeof(RectTransform));
        titleGo.transform.SetParent(winRoot, false);
        var title = titleGo.AddComponent<FloatingWindowTitleInput>();

        int applied = 0;
        CanvasView captured = null;
        title.Initialize(windowsCtrl, canvas, viewport, "wid", 1f, v => { applied++; captured = v; });

        // clickCount=1 → no fire.
        var ev1 = new PointerEventData(EventSystem.current) { clickCount = 1, button = PointerEventData.InputButton.Left };
        title.OnPointerClick(ev1);
        if (applied != 0)
            return TagFail("FRAMING-S1-DBL-CLICK", "single click fired applyView (clickCount=1 → " + applied + " calls)");

        // **Right-button double → no fire** (review finding): IPointerClickHandler fires for any button;
        // the title bar only honors Left double-clicks.
        var evRight = new PointerEventData(EventSystem.current) { clickCount = 2, button = PointerEventData.InputButton.Right };
        title.OnPointerClick(evRight);
        if (applied != 0)
            return TagFail("FRAMING-S1-DBL-CLICK", "right-button double fired applyView (button filter missing)");
        var evMiddle = new PointerEventData(EventSystem.current) { clickCount = 2, button = PointerEventData.InputButton.Middle };
        title.OnPointerClick(evMiddle);
        if (applied != 0)
            return TagFail("FRAMING-S1-DBL-CLICK", "middle-button double fired applyView (button filter missing)");

        // clickCount=2 Left → fire once; captured view equals FrameWindow result.
        var ev2 = new PointerEventData(EventSystem.current) { clickCount = 2, button = PointerEventData.InputButton.Left };
        title.OnPointerClick(ev2);
        if (applied != 1)
            return TagFail("FRAMING-S1-DBL-CLICK", "Left double click fired applyView " + applied + " times (expected 1)");
        var expected = CanvasViewMath.FrameWindow(
            winRoot.anchoredPosition, winRoot.sizeDelta, viewport.rect.size,
            1f, CanvasViewMath.FRAME_MARGIN_DEFAULT, canvas.CaptureView());
        if (!Approx(captured, expected, EPS))
            return TagFail("FRAMING-S1-DBL-CLICK", "captured view " + Format(captured) + " != FrameWindow " + Format(expected));
        return TagPass("FRAMING-S1-DBL-CLICK", "Left double → 1 applyView; Right/Middle double / single Left → 0; view matches FrameWindow");
    }

    // ---- S2: glide convergence + interrupt kill ----

    static bool S2Glide(List<GameObject> spawned)
    {
        var parent = NewRoot("S2Glide_Parent", spawned);
        var content = NewRect("Content", parent,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            pivot: new Vector2(0.5f, 0.5f));
        var canvas = new InfiniteCanvasController(content, null, 1f);
        canvas.ApplyView(new CanvasView(0f, 0f, 1f));

        var glideGo = new GameObject("GlideDriver");
        glideGo.transform.SetParent(parent, false);
        spawned.Add(glideGo);
        var glide = glideGo.AddComponent<CameraGlideDriver>();
        glide.Bind(canvas);

        var target = new CanvasView(300f, -150f, 2.5f);
        glide.BeginGlide(target);

        if (!glide.IsAnimating)
            return TagFail("FRAMING-S2-GLIDE", "BeginGlide did not set IsAnimating");

        // Step in small ticks (each ≤ MAX_DT_MS = DURATION/4) so the driver's per-tick dt-cap (review
        // finding: protects against huge first-frame dt) does not collapse a single 100ms advance into 50ms.
        // 10 ticks of DURATION/20 = total DURATION/2 → t=0.5.
        float small = CameraGlideDriver.DURATION_MS / 20f;
        for (int i = 0; i < 10; i++) glide.Advance(small);
        var mid = canvas.CaptureView();
        if (mid.zoom <= 1f + EPS || mid.zoom >= 2.5f - EPS)
            return TagFail("FRAMING-S2-GLIDE", "mid-tween zoom not strictly inside (1, 2.5): " + mid.zoom);
        if (mid.zoom < CanvasView.MIN_ZOOM || mid.zoom > CanvasView.MAX_ZOOM)
            return TagFail("FRAMING-S2-GLIDE", "mid-tween zoom out of bounds: " + mid.zoom);

        // Cubic ease-out at t=0.5: eased = 1 - 0.5^3 = 0.875. zoom expected = lerp(1, 2.5, 0.875) = 2.3125.
        float easedHalf = 1f - 0.125f;
        float expectedZoom = Mathf.LerpUnclamped(1f, 2.5f, easedHalf);
        if (Mathf.Abs(mid.zoom - expectedZoom) > 0.02f)
            return TagFail("FRAMING-S2-GLIDE", "mid-tween zoom " + mid.zoom + " not on ease-out cubic (expected ~" + expectedZoom + ")");

        // Advance to completion in small ticks too (DURATION/2 remaining → 10 more ticks).
        for (int i = 0; i < 10; i++) glide.Advance(small);
        if (glide.IsAnimating)
            return TagFail("FRAMING-S2-GLIDE", "still animating after duration");
        var end = canvas.CaptureView();
        if (Mathf.Abs(end.panX - target.panX) > EPS || Mathf.Abs(end.panY - target.panY) > EPS || Mathf.Abs(end.zoom - target.zoom) > EPS)
            return TagFail("FRAMING-S2-GLIDE", "end view " + Format(end) + " != target " + Format(target));

        // **Cap proof** (review finding): a single oversized dt is clamped — driving Advance with a huge dt
        // must NOT collapse the tween to instant. Restart and verify the first tick only advances ≤25%.
        canvas.ApplyView(new CanvasView(0f, 0f, 1f));
        glide.BeginGlide(target);
        glide.Advance(10_000f);   // huge dt — should be clamped to MAX_DT_MS=50ms ⇒ t≈0.25
        if (!glide.IsAnimating)
            return TagFail("FRAMING-S2-GLIDE", "huge-dt single Advance collapsed tween (cap not in effect)");
        var afterClamped = canvas.CaptureView();
        // eased at t=0.25 = 1 - 0.75^3 = 0.578125. zoom expected ≈ lerp(1, 2.5, 0.578) ≈ 1.867.
        if (afterClamped.zoom > 2.0f)
            return TagFail("FRAMING-S2-GLIDE", "huge-dt advanced past 25%: zoom=" + afterClamped.zoom);

        return TagPass("FRAMING-S2-GLIDE", "ease-out cubic converged; mid-tween on curve; no overshoot; per-tick dt clamped");
    }

    static bool S2Interrupt(List<GameObject> spawned)
    {
        var parent = NewRoot("S2Int_Parent", spawned);
        var content = NewRect("Content", parent,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            pivot: new Vector2(0.5f, 0.5f));
        var canvas = new InfiniteCanvasController(content, null, 1f);
        canvas.ApplyView(new CanvasView(0f, 0f, 1f));

        var glideGo = new GameObject("GlideDriverInt");
        glideGo.transform.SetParent(parent, false);
        spawned.Add(glideGo);
        var glide = glideGo.AddComponent<CameraGlideDriver>();
        glide.Bind(canvas);

        glide.BeginGlide(new CanvasView(500f, -200f, 3f));
        glide.Advance(CameraGlideDriver.DURATION_MS * 0.3f);
        var midBeforeInterrupt = canvas.CaptureView();

        // External write — simulate a manual pan/zoom landing on the controller mid-glide.
        var externalView = new CanvasView(-100f, 50f, 1.3f);
        canvas.ApplyView(externalView);

        // Next Advance should detect divergence > EPS and KILL the tween. Content stays at externalView.
        glide.Advance(CameraGlideDriver.DURATION_MS * 0.1f);
        if (glide.IsAnimating)
            return TagFail("FRAMING-S2-INTERRUPT", "still animating after external ApplyView (kill missed)");
        var after = canvas.CaptureView();
        if (!Approx(after, externalView, EPS))
            return TagFail("FRAMING-S2-INTERRUPT", "kill overwrote external view: after=" + Format(after) + " external=" + Format(externalView));

        // Idempotent: further Advance is a no-op.
        glide.Advance(1000f);
        var stillAfter = canvas.CaptureView();
        if (!Approx(stillAfter, externalView, EPS))
            return TagFail("FRAMING-S2-INTERRUPT", "post-kill Advance disturbed view");

        return TagPass("FRAMING-S2-INTERRUPT", "external ApplyView mid-glide kills tween; view = external; further Advance no-op");
    }

    // ---- S3: sidebar hook + no-op ----

    // FocusChartHook が SelectRow 毎に発火（同じ id 再クリックでも）。
    static bool S3Sidebar()
    {
        var registry = new InstrumentRegistry();
        registry.ReplaceAll(new List<string> { "AAA", "BBB" });
        var selected = new SelectedSymbol();
        var writeback = new UniverseWriteback();
        var ctrl = new UniverseSidebarController(registry, selected, writeback, null);

        int fires = 0;
        string lastId = null;
        ctrl.FocusChartHook = id => { fires++; lastId = id; };

        // First click on AAA — moves focus + fires.
        bool moved1 = ctrl.SelectRow("AAA", UniverseSourceMode.Replay);
        if (!moved1) return TagFail("FRAMING-S3-SIDEBAR", "first SelectRow did not move focus");
        if (fires != 1 || lastId != "AAA")
            return TagFail("FRAMING-S3-SIDEBAR", "first click did not fire hook with AAA (fires=" + fires + ", id=" + lastId + ")");

        // Re-click on AAA — DOES NOT move focus (SelectedSymbol.Set returns false) but MUST still fire.
        bool moved2 = ctrl.SelectRow("AAA", UniverseSourceMode.Replay);
        if (moved2) return TagFail("FRAMING-S3-SIDEBAR", "re-click on same id reported moved=true");
        if (fires != 2 || lastId != "AAA")
            return TagFail("FRAMING-S3-SIDEBAR", "re-click did not fire hook (fires=" + fires + " — relied on SelectedSymbol.Changed?)");

        // Click BBB — moves and fires.
        ctrl.SelectRow("BBB", UniverseSourceMode.Replay);
        if (fires != 3 || lastId != "BBB")
            return TagFail("FRAMING-S3-SIDEBAR", "BBB click did not fire (fires=" + fires + ", id=" + lastId + ")");

        // Live mode also fires (mode-independent).
        ctrl.SelectRow("AAA", UniverseSourceMode.Live);
        if (fires != 4) return TagFail("FRAMING-S3-SIDEBAR", "Live mode click did not fire (fires=" + fires + ")");

        return TagPass("FRAMING-S3-SIDEBAR", "FocusChartHook fires per click incl. re-click + both modes (4 fires across 4 clicks)");
    }

    // 窓未生成 / inactive のとき glide が始まらない (no-op)。
    static bool S3Noop(List<GameObject> spawned)
    {
        var parent = NewRoot("S3Noop_Parent", spawned);
        var content = NewRect("Content", parent,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            pivot: new Vector2(0.5f, 0.5f));
        var viewport = NewRect("Viewport", parent, size: new Vector2(1600f, 900f));
        var canvas = new InfiniteCanvasController(content, null, 1f);
        canvas.ApplyView(new CanvasView(0f, 0f, 1f));

        var dockCtrl = new FloatingWindowController(content, FloatingWindowCatalog.Default(),
            (spec, id) => null);

        var glideGo = new GameObject("GlideDriverNoop");
        glideGo.transform.SetParent(parent, false);
        spawned.Add(glideGo);
        var glide = glideGo.AddComponent<CameraGlideDriver>();
        glide.Bind(canvas);

        // Helper that mirrors BackcastWorkspaceRoot.FrameChartWindowForInstrument:
        //   chart 窓が無い / inactive (本人 or 祖先) → BeginGlide を呼ばない。
        System.Action<string> frameChart = iid =>
        {
            string chartId = DockShape.ChartId(iid);
            if (string.IsNullOrEmpty(chartId)) return;
            var rt = dockCtrl.RectOf(chartId);
            if (rt == null || !rt.gameObject.activeInHierarchy) return;
            var view = CanvasViewMath.FrameWindow(
                rt.anchoredPosition, rt.sizeDelta, viewport.rect.size,
                CanvasViewMath.DOCK_PLANE_PARALLAX, CanvasViewMath.FRAME_MARGIN_DEFAULT,
                canvas.CaptureView());
            glide.BeginGlide(view);
        };

        // (a) 窓未生成 → no-op (glide does not start, view unchanged).
        var before = canvas.CaptureView();
        frameChart("UNREGISTERED");
        if (glide.IsAnimating)
            return TagFail("FRAMING-S3-NOOP", "BeginGlide called for missing chart window");
        var after = canvas.CaptureView();
        if (!Approx(before, after, EPS))
            return TagFail("FRAMING-S3-NOOP", "view changed for missing chart window");

        // (b) 窓は登録するが inactive → no-op.
        var inactiveWin = NewRect("chart:HIDDEN", content,
            pivot: new Vector2(0f, 1f),
            anchoredPosition: new Vector2(80f, 60f),
            size: new Vector2(520f, 360f));
        inactiveWin.gameObject.SetActive(false);
        dockCtrl.Adopt(FloatingWindowCatalog.KIND_CHART, "chart:HIDDEN", inactiveWin);
        frameChart("HIDDEN");
        if (glide.IsAnimating)
            return TagFail("FRAMING-S3-NOOP", "BeginGlide called for inactive chart window");

        // (b') ancestor inactive — window's own activeSelf==true but activeInHierarchy==false (review
        // finding: activeSelf would let this slip through). Build a chart window under an extra inactive
        // wrapper transform, register it, attempt to frame — must be no-op.
        var hiddenGroup = NewRect("HiddenGroup", content);
        hiddenGroup.gameObject.SetActive(false);
        var ancHiddenWin = NewRect("chart:ANCHIDDEN", hiddenGroup,
            pivot: new Vector2(0f, 1f),
            anchoredPosition: new Vector2(0f, 0f),
            size: new Vector2(300f, 200f));
        // Adopt re-parents under content's layer — circumvent by Adopting before moving back under the
        // hidden group, mirroring the real-world failure (controller register first, ancestor hidden later).
        dockCtrl.Adopt(FloatingWindowCatalog.KIND_CHART, "chart:ANCHIDDEN", ancHiddenWin);
        ancHiddenWin.SetParent(hiddenGroup, false);   // ancestor now inactive
        if (ancHiddenWin.gameObject.activeSelf == false)
            return TagFail("FRAMING-S3-NOOP", "test setup: ancHiddenWin's own activeSelf should be true (we hide via ancestor)");
        if (ancHiddenWin.gameObject.activeInHierarchy)
            return TagFail("FRAMING-S3-NOOP", "test setup: ancHiddenWin should be activeInHierarchy==false (ancestor inactive)");
        frameChart("ANCHIDDEN");
        if (glide.IsAnimating)
            return TagFail("FRAMING-S3-NOOP", "BeginGlide called for ancestor-hidden chart window (activeSelf check would miss this)");

        // (c) 窓が active なら glide 開始 — sanity check that the noop wasn't accidental.
        var activeWin = NewRect("chart:VISIBLE", content,
            pivot: new Vector2(0f, 1f),
            anchoredPosition: new Vector2(-80f, 20f),
            size: new Vector2(420f, 280f));
        dockCtrl.Adopt(FloatingWindowCatalog.KIND_CHART, "chart:VISIBLE", activeWin);
        frameChart("VISIBLE");
        if (!glide.IsAnimating)
            return TagFail("FRAMING-S3-NOOP", "active chart window did NOT start a glide (sanity)");

        return TagPass("FRAMING-S3-NOOP", "missing & inactive chart windows do not start a glide; active starts");
    }

    // ---- helpers ----

    static bool TagPass(string id, string msg) { Debug.Log("[E2E " + id + " PASS] " + msg); return true; }
    static bool TagFail(string id, string msg) { Debug.LogError("[E2E " + id + " FAIL] " + msg); return false; }

    static string Format(CanvasView v) =>
        v == null ? "null" : ("pan=(" + v.panX + "," + v.panY + ") zoom=" + v.zoom);

    static bool Approx(CanvasView a, CanvasView b, float eps)
    {
        if (a == null || b == null) return a == b;
        return Mathf.Abs(a.panX - b.panX) <= eps
            && Mathf.Abs(a.panY - b.panY) <= eps
            && Mathf.Abs(a.zoom - b.zoom) <= eps;
    }

    static RectTransform NewRoot(string name, List<GameObject> spawned)
    {
        var go = new GameObject(name, typeof(RectTransform));
        spawned.Add(go);
        return go.GetComponent<RectTransform>();
    }

    static RectTransform NewRect(
        string name, RectTransform parent,
        Vector2? anchorMin = null, Vector2? anchorMax = null,
        Vector2? pivot = null, Vector2? anchoredPosition = null, Vector2? size = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        if (parent != null) rt.SetParent(parent, false);
        rt.anchorMin = anchorMin ?? new Vector2(0.5f, 0.5f);
        rt.anchorMax = anchorMax ?? new Vector2(0.5f, 0.5f);
        rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPosition ?? Vector2.zero;
        rt.sizeDelta = size ?? new Vector2(100f, 100f);
        return rt;
    }
}
