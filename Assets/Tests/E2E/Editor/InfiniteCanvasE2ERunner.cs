// InfiniteCanvasE2ERunner.cs — infinite-canvas（pan/zoom）サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// InfiniteCanvasE2ERunner.md）。第二波。`InfiniteCanvasProbe`（throwaway AFK gate, Assets/Editor）から昇格・改名
// （ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=findings 0054 / FooterMode=findings 0055）。実証済み
// Probe の Section1〜7 を assert 1 行も削らず移送し各 section に `Covers:` を付与、台本で唯一 `要新規自動化` だった
// CANVAS-05（input 境界の per-event scroll-tick clamp）を Section8 として追加した。Python-FREE・render-FREE。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod InfiniteCanvasE2ERunner.Run -logFile <log>
//   # expect: [E2E INFINITE CANVAS PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。共有 pure 算術
// （CanvasViewMath）は Action ID ごとに人工分割せず一つの自然な検証単位で assert する（E2E-CONVENTIONS.md
// 「runner section ↔ Action ID 対応方針」）。gate 形は probe の `Execute()`-形（各 section が null=PASS、最初の
// 失敗文字列を返す）をそのまま温存。`EditorApplication.Exit` は self-failing gate として無条件化。
//
// THREE-WAY INDEPENDENCE — the whole point (findings 0006 §5): the gate cross-checks PURE
// MATH (CanvasViewMath) against the UNITY TRANSFORM ENGINE (RectTransform.TransformPoint)
// against SERIALIZATION (LayoutStore round-trip). The child-follow section is deliberately
// NON-TAUTOLOGICAL: it asserts Unity's own transform composition equals the math's
// prediction, not the math against itself.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class InfiniteCanvasE2ERunner
{
    const float EPS = 1e-3f;

    // A deterministic, writable temp path — NOT the production sidecar, so the gate never
    // clobbers a real layout (mirrors ReplayLayoutProbe).
    static string TempDir => Path.Combine(Application.temporaryCachePath, "infinite_canvas_e2e");
    static string TempPath => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);

            fail = Section1_PanArithmetic()
                ?? Section2_ZoomClamp()
                ?? Section3_CursorInvariant()
                ?? Section4_ChildFollowAndController(spawned)
                ?? Section5_DiskRoundTripNonVacuous()
                ?? Section6_BackCompatAndSanitize()
                ?? Section7_ParallaxForegroundLayer(spawned)
                ?? Section8_ScrollTickClamp(spawned);
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[E2E INFINITE CANVAS PASS] pan arithmetic + zoom clamp[0.2,5.0] + cursor-centred invariant " +
                      "(normal+clamped, non-no-op) + real-RectTransform child-follow (engine==math) + " +
                      "Apply/Capture boundary + non-vacuous CanvasView disk round-trip + back-compat/sanitize " +
                      "+ parallax foreground layer (offset=(1-F)*pan, zoom-independent, engine travel == F*base, strictly-more) " +
                      "+ input-boundary scroll-tick clamp (raw wheel ~120 capped to 4 notches, no zoom saturation) " +
                      "(Unity-owned versioned schema, additive capability surface, ADR-0003 capability parity, under Unity Mono)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E INFINITE CANVAS FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. pan arithmetic: logical move = -dScreen/zoom, resolution-independent ----
    // Covers: CANVAS-01
    static string Section1_PanArithmetic()
    {
        var v = new CanvasView(10f, 20f, 2f);
        var moved = CanvasViewMath.PanByScreenDelta(v, new Vector2(4f, -6f));
        // pan -= d/zoom -> (10 - 4/2, 20 - (-6)/2) = (8, 23); zoom unchanged.
        if (!Approx(moved.panX, 8f) || !Approx(moved.panY, 23f))
            return $"S1: pan delta wrong (got {moved.panX},{moved.panY}, expected 8,23)";
        if (!Approx(moved.zoom, 2f))
            return "S1: pan changed zoom (must not)";

        // resolution-independence: the SAME screen delta at a DIFFERENT zoom yields a
        // different LOGICAL move (dScreen/zoom), proving pan is in logical units not px.
        var atUnit = CanvasViewMath.PanByScreenDelta(new CanvasView(0f, 0f, 1f), new Vector2(4f, 0f));
        var atQuad = CanvasViewMath.PanByScreenDelta(new CanvasView(0f, 0f, 4f), new Vector2(4f, 0f));
        if (Approx(atUnit.panX, atQuad.panX))
            return "S1: pan ignored zoom (logical move must scale by 1/zoom)";
        if (!Approx(atUnit.panX, -4f) || !Approx(atQuad.panX, -1f))
            return $"S1: logical move wrong (zoom1={atUnit.panX} expected -4, zoom4={atQuad.panX} expected -1)";
        return null;
    }

    // ---- 2. zoom clamp: saturates at MIN/MAX, no overshoot ----
    // Covers: CANVAS-03
    static string Section2_ZoomClamp()
    {
        Vector2 c = new Vector2(17f, -9f);

        // overshoot up: 4 * 4 = 16 -> clamp 5.0
        var up = CanvasViewMath.ZoomAtCursor(new CanvasView(1f, 1f, 4f), c, 4f);
        if (!Approx(up.zoom, CanvasView.MAX_ZOOM)) return $"S2: zoom-in not clamped to {CanvasView.MAX_ZOOM} (got {up.zoom})";

        // overshoot down: 0.3 * 0.1 = 0.03 -> clamp 0.2
        var dn = CanvasViewMath.ZoomAtCursor(new CanvasView(1f, 1f, 0.3f), c, 0.1f);
        if (!Approx(dn.zoom, CanvasView.MIN_ZOOM)) return $"S2: zoom-out not clamped to {CanvasView.MIN_ZOOM} (got {dn.zoom})";

        // a normal in-range step is NOT clamped.
        var mid = CanvasViewMath.ZoomAtCursor(new CanvasView(1f, 1f, 1.5f), c, 1.4f);
        if (!Approx(mid.zoom, 2.1f)) return $"S2: in-range zoom altered (got {mid.zoom}, expected 2.1)";
        return null;
    }

    // ---- 3. cursor-centred invariant: normal + clamped step, both non-no-op ----
    // Covers: CANVAS-02, CANVAS-04
    static string Section3_CursorInvariant()
    {
        Vector2 c = new Vector2(30f, -15f);

        // normal step (in range): logical point under cursor must be invariant.
        var v1 = new CanvasView(5f, 7f, 1.5f);
        Vector2 before1 = CanvasViewMath.LogicalUnderCursor(v1, c);
        var z1 = CanvasViewMath.ZoomAtCursor(v1, c, 1.4f);              // 1.5 -> 2.1
        if (Approx(z1.zoom, v1.zoom)) return "S3: normal step was a no-op (zoom unchanged)";
        Vector2 after1 = CanvasViewMath.LogicalUnderCursor(z1, c);
        if (!Approx(after1.x, before1.x) || !Approx(after1.y, before1.y))
            return $"S3: cursor point moved on a normal zoom (before {before1}, after {after1})";

        // clamped step: invariant must STILL hold, computed with the CLAMPED newZoom.
        var v2 = new CanvasView(-12f, 3f, 4.5f);
        Vector2 before2 = CanvasViewMath.LogicalUnderCursor(v2, c);
        var z2 = CanvasViewMath.ZoomAtCursor(v2, c, 3f);               // 13.5 -> clamp 5.0
        if (!Approx(z2.zoom, CanvasView.MAX_ZOOM)) return $"S3: clamped step did not saturate (got {z2.zoom})";
        if (Approx(z2.zoom, v2.zoom)) return "S3: clamped step was a no-op (zoom unchanged)";
        Vector2 after2 = CanvasViewMath.LogicalUnderCursor(z2, c);
        if (!Approx(after2.x, before2.x) || !Approx(after2.y, before2.y))
            return $"S3: cursor point moved on a CLAMPED zoom (before {before2}, after {after2})";
        return null;
    }

    // ---- 4. real RectTransform child-follow (engine == math) + controller boundary ----
    // Covers: CANVAS-01, CANVAS-02, CANVAS-06
    static string Section4_ChildFollowAndController(List<GameObject> spawned)
    {
        // Viewport: a root RectTransform at identity transform (pos 0, scale 1, no rot),
        // pivot centre -> its rect centre sits at the world origin.
        var viewportGo = new GameObject("ProbeViewport", typeof(RectTransform));
        spawned.Add(viewportGo);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = viewport.anchorMax = viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.sizeDelta = new Vector2(1000f, 800f);

        // Content: child of viewport, anchored AND pivoted at the viewport centre (the
        // controller's contract) -> anchoredPosition == localPosition.xy, position == anchoredPosition.
        var contentGo = new GameObject("ProbeContent", typeof(RectTransform));
        var content = contentGo.GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        // A REAL RectTransform child placed at a known Content-LOGICAL point L.
        Vector2 L = new Vector2(40f, 30f);
        var childGo = new GameObject("ProbeChild", typeof(RectTransform));
        var child = childGo.GetComponent<RectTransform>();
        child.SetParent(content, false);
        child.anchorMin = child.anchorMax = child.pivot = new Vector2(0.5f, 0.5f);
        child.anchoredPosition = L;   // centred anchors -> localPosition.xy == L

        var controller = new InfiniteCanvasController(content);

        // Apply a non-identity view (pan != 0, zoom != 1).
        var viewA = new CanvasView(12f, -8f, 1.75f);
        controller.ApplyView(viewA);

        // CROSS-CHECK: Unity's transform engine vs the pure math, in VIEWPORT-CENTRE coords.
        Vector3 world = content.TransformPoint(child.localPosition);
        Vector2 actual = viewport.InverseTransformPoint(world);
        Vector2 expected = CanvasViewMath.LogicalToViewport(L, viewA);
        if (!Approx(actual.x, expected.x) || !Approx(actual.y, expected.y))
            return $"S4: child-follow engine!=math (engine {actual}, math {expected}) — " +
                   "the placed widget does not track pan/zoom as predicted";

        // Apply/Capture boundary: CaptureView must round-trip the applied view.
        var captured = controller.CaptureView();
        if (!CanvasView.Approx(captured, viewA, EPS))
            return $"S4: CaptureView != ApplyView'd view (got {captured.panX},{captured.panY},{captured.zoom})";

        // Controller delegates to the math: a cursor zoom through the controller must match
        // CanvasViewMath exactly (and the child must still follow afterward).
        Vector2 c = new Vector2(120f, 55f);
        controller.ZoomAtCursor(c, 1.3f);
        var expView = CanvasViewMath.ZoomAtCursor(viewA, c, 1.3f);
        if (!CanvasView.Approx(controller.CaptureView(), expView, EPS))
            return "S4: controller.ZoomAtCursor diverged from CanvasViewMath.ZoomAtCursor";
        Vector3 world2 = content.TransformPoint(child.localPosition);
        Vector2 actual2 = viewport.InverseTransformPoint(world2);
        Vector2 expected2 = CanvasViewMath.LogicalToViewport(L, expView);
        if (!Approx(actual2.x, expected2.x) || !Approx(actual2.y, expected2.y))
            return $"S4: child-follow broke after a controller zoom (engine {actual2}, math {expected2})";
        return null;
    }

    // ---- 5. non-identity CanvasView disk round-trip (vacuous-green kill) ----
    // Covers: CANVAS-07
    static string Section5_DiskRoundTripNonVacuous()
    {
        LayoutDocument def = LayoutDocument.Default();

        // Stable float values (exactly representable -> deterministic JSON text).
        LayoutDocument mutated = def.Clone();
        mutated.canvasView = new CanvasView(123.25f, -45.5f, 2.5f);
        if (LayoutDocument.StructurallyEqual(mutated, def, EPS))
            return "S5: mutated view still equals default (mutation no-op)";

        LayoutStore.Save(mutated, TempPath);
        if (!File.Exists(TempPath)) return "S5: Save did not create the sidecar";

        // STRUCTURAL: the values must reach the on-disk TEXT (catches an in-memory-only
        // round-trip or a serializer that drops canvasView). Whitespace-insensitive.
        string raw = File.ReadAllText(TempPath);
        string compact = raw.Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        if (!compact.Contains("\"panX\":123.25")) return "S5: panX value not in on-disk JSON text";
        if (!compact.Contains("\"panY\":-45.5"))  return "S5: panY value not in on-disk JSON text";
        if (!compact.Contains("\"zoom\":2.5"))    return "S5: zoom value not in on-disk JSON text";

        // Fresh load: the mutated view survived and is distinct from default.
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (!CanvasView.Approx(loaded.canvasView, mutated.canvasView, EPS))
            return $"S5: loaded view != mutated (got {loaded.canvasView?.panX},{loaded.canvasView?.panY},{loaded.canvasView?.zoom})";
        if (!LayoutDocument.StructurallyEqual(loaded, mutated, EPS))
            return "S5: loaded != mutated structurally";
        if (LayoutDocument.StructurallyEqual(loaded, def, EPS))
            return "S5: loaded == default (vacuous round-trip — mutation did not persist)";
        return null;
    }

    // ---- 6. back-compat + sanitize + malformed-document fallback ----
    // Covers: CANVAS-08
    static string Section6_BackCompatAndSanitize()
    {
        // (a) old v1 sidecar with NO canvasView -> panels kept, view normalized to identity.
        string oldV1 = "{\"version\":1,\"panels\":[{\"id\":\"chart\",\"slot\":0,\"visible\":true," +
                       "\"rect\":{\"minX\":0.0,\"minY\":0.0,\"maxX\":0.62,\"maxY\":1.0}}]}";
        LayoutDocument a = LayoutStore.LoadFromJson(oldV1);
        if (a.Find("chart") == null) return "S6a: old-v1 load dropped the panels";
        if (!CanvasView.Approx(a.canvasView, CanvasView.Identity(), EPS))
            return $"S6a: missing canvasView not normalized to identity (got {a.canvasView?.panX},{a.canvasView?.panY},{a.canvasView?.zoom})";

        // (b) JSON zoom:0 -> 1 (pan preserved).
        LayoutDocument b = LayoutStore.LoadFromJson(
            "{\"version\":1,\"panels\":[],\"canvasView\":{\"panX\":3.0,\"panY\":4.0,\"zoom\":0.0}}");
        if (!Approx(b.canvasView.zoom, 1f)) return $"S6b: zoom 0 not normalized to 1 (got {b.canvasView.zoom})";
        if (!Approx(b.canvasView.panX, 3f) || !Approx(b.canvasView.panY, 4f))
            return "S6b: finite pan was not preserved";

        // (c) JSON zoom:99 -> clamp 5.0.
        LayoutDocument cc = LayoutStore.LoadFromJson(
            "{\"version\":1,\"panels\":[],\"canvasView\":{\"panX\":0.0,\"panY\":0.0,\"zoom\":99.0}}");
        if (!Approx(cc.canvasView.zoom, CanvasView.MAX_ZOOM)) return $"S6c: zoom 99 not clamped to {CanvasView.MAX_ZOOM} (got {cc.canvasView.zoom})";

        // (d) non-finite values DIRECTLY on a CanvasView (NaN/Inf aren't valid JSON, so they
        // can't be reached through LoadFromJson without tripping the document fallback) ->
        // normalized via the public normalizer.
        var d = new LayoutDocument { version = 1, panels = new List<PanelLayout>(),
                                     canvasView = new CanvasView(float.NaN, float.PositiveInfinity, float.NaN) };
        LayoutStore.NormalizeCanvasView(d);
        if (!CanvasView.Approx(d.canvasView, CanvasView.Identity(), EPS))
            return $"S6d: non-finite view not normalized to identity (got {d.canvasView.panX},{d.canvasView.panY},{d.canvasView.zoom})";

        // (e) malformed JSON -> whole-document default fallback (existing LayoutStore discipline),
        // and default carries an identity view.
        LayoutDocument e = LayoutStore.LoadFromJson("{not valid json");
        if (!LayoutDocument.StructurallyEqual(e, LayoutDocument.Default(), EPS))
            return "S6e: malformed JSON did not fall back to default";
        if (!CanvasView.Approx(e.canvasView, CanvasView.Identity(), EPS))
            return "S6e: default fallback view is not identity";
        return null;
    }

    // ---- 7. parallax foreground layer: travels F× the base plane per unit pan (depth cue) ----
    // Covers: (depth-cue 拡張 — 操作一覧表に対応 Action 行は無いが、実証済み assert なので回帰網保全のため温存。
    //          canvas 追従 CANVAS-06 の延長で「foreground は base より MORE 移動」を engine==math で固定する)
    static string Section7_ParallaxForegroundLayer(List<GameObject> spawned)
    {
        const float F = 1.2f;

        // (a) PURE MATH. factor=1 -> always zero (coplanar, today's behaviour), at any pan/zoom.
        var coplanar = CanvasViewMath.ParallaxLayerOffset(new CanvasView(50f, -30f, 2f), 1f);
        if (!Approx(coplanar.x, 0f) || !Approx(coplanar.y, 0f))
            return $"S7: factor=1 must give zero offset (got {coplanar})";
        // pan=0 -> zero even for a foreground factor (centred layout & persistence stay unchanged).
        var centred = CanvasViewMath.ParallaxLayerOffset(new CanvasView(0f, 0f, 3f), F);
        if (!Approx(centred.x, 0f) || !Approx(centred.y, 0f))
            return $"S7: pan=0 must give zero offset (got {centred})";
        // non-zero pan -> O = (1-F)*pan, and zoom-INDEPENDENT (Content-local units).
        var off = CanvasViewMath.ParallaxLayerOffset(new CanvasView(40f, -25f, 1f), F);
        if (!Approx(off.x, (1f - F) * 40f) || !Approx(off.y, (1f - F) * -25f))
            return $"S7: offset != (1-F)*pan (got {off}, expected {(1f - F) * 40f},{(1f - F) * -25f})";
        var offZoomed = CanvasViewMath.ParallaxLayerOffset(new CanvasView(40f, -25f, 4f), F);
        if (!Approx(offZoomed.x, off.x) || !Approx(offZoomed.y, off.y))
            return $"S7: offset must be zoom-independent (got {offZoomed} vs {off})";

        // (b) ENGINE CROSS-CHECK (non-tautological): a real base-plane child (direct Content child)
        // vs a real foreground child (inside a parallax layer), driven by the controller. The
        // foreground must travel F× the base plane's screen distance for the SAME pan.
        var viewportGo = new GameObject("S7Viewport", typeof(RectTransform));
        spawned.Add(viewportGo);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = viewport.anchorMax = viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.sizeDelta = new Vector2(1000f, 800f);

        var contentGo = new GameObject("S7Content", typeof(RectTransform));
        var content = contentGo.GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        var baseGo = new GameObject("S7Base", typeof(RectTransform));   // Hakoniwa-plane child
        var baseChild = baseGo.GetComponent<RectTransform>();
        baseChild.SetParent(content, false);
        baseChild.anchorMin = baseChild.anchorMax = baseChild.pivot = new Vector2(0.5f, 0.5f);
        baseChild.anchoredPosition = Vector2.zero;

        var layerGo = new GameObject("S7Layer", typeof(RectTransform));   // FloatingWindowLayer
        var layer = layerGo.GetComponent<RectTransform>();
        layer.SetParent(content, false);
        layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(0.5f, 0.5f);
        layer.anchoredPosition = Vector2.zero;

        var fgGo = new GameObject("S7Fg", typeof(RectTransform));   // a window inside the layer
        var fgChild = fgGo.GetComponent<RectTransform>();
        fgChild.SetParent(layer, false);
        fgChild.anchorMin = fgChild.anchorMax = fgChild.pivot = new Vector2(0.5f, 0.5f);
        fgChild.anchoredPosition = Vector2.zero;

        var controller = new InfiniteCanvasController(content, layer, F);

        var vA = new CanvasView(0f, 0f, 1f);
        var vB = new CanvasView(15f, -10f, 1f);   // a pure pan step (zoom fixed)

        controller.ApplyView(vA);
        if (!Approx(layer.anchoredPosition.x, 0f) || !Approx(layer.anchoredPosition.y, 0f))
            return $"S7: layer offset at pan=0 not zero (got {layer.anchoredPosition})";
        Vector2 baseA = viewport.InverseTransformPoint(content.TransformPoint(baseChild.localPosition));
        Vector2 fgA = viewport.InverseTransformPoint(layer.TransformPoint(fgChild.localPosition));

        controller.ApplyView(vB);
        Vector2 expectOff = CanvasViewMath.ParallaxLayerOffset(vB, F);
        if (!Approx(layer.anchoredPosition.x, expectOff.x) || !Approx(layer.anchoredPosition.y, expectOff.y))
            return $"S7: live layer offset != math (engine {layer.anchoredPosition}, math {expectOff})";
        Vector2 baseB = viewport.InverseTransformPoint(content.TransformPoint(baseChild.localPosition));
        Vector2 fgB = viewport.InverseTransformPoint(layer.TransformPoint(fgChild.localPosition));

        Vector2 baseTravel = baseB - baseA;
        Vector2 fgTravel = fgB - fgA;
        if (Approx(baseTravel.x, 0f) && Approx(baseTravel.y, 0f))
            return "S7: base plane did not move (vacuous)";
        if (!Approx(fgTravel.x, F * baseTravel.x) || !Approx(fgTravel.y, F * baseTravel.y))
            return $"S7: foreground travel != F*base (fg {fgTravel}, base {baseTravel}, F {F})";
        if (Mathf.Abs(fgTravel.x) <= Mathf.Abs(baseTravel.x) + EPS)
            return $"S7: foreground did not move MORE than base (no depth cue: fg {fgTravel}, base {baseTravel})";
        return null;
    }

    // ---- 8. per-event scroll-tick clamp at the INPUT boundary (NEW; S1-S7 bypass the surface) ----
    // Covers: CANVAS-05
    // The controller-direct sections above never exercise InfiniteCanvasInputSurface.OnScroll, where
    // the per-event magnitude cap lives (Mathf.Clamp(scrollDelta.y, ±MAX_SCROLL_TICKS=4)). This section
    // drives the REAL MonoBehaviour surface with an injected PointerEventData so a raw platform wheel
    // notch (~120) can't jump straight to a zoom bound.
    static string Section8_ScrollTickClamp(List<GameObject> spawned)
    {
        // The input surface is a MonoBehaviour attached to the VIEWPORT (production wiring),
        // driving a real Viewport->Content tree through the controller.
        var viewportGo = new GameObject("S8Viewport", typeof(RectTransform), typeof(InfiniteCanvasInputSurface));
        spawned.Add(viewportGo);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = viewport.anchorMax = viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.sizeDelta = new Vector2(1000f, 800f);

        var contentGo = new GameObject("S8Content", typeof(RectTransform));
        var content = contentGo.GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        var controller = new InfiniteCanvasController(content);
        var surface = viewportGo.GetComponent<InfiniteCanvasInputSurface>();
        surface.Initialize(controller, viewport);

        // PRESENCE / vacuous-green kill: an IN-RANGE scroll must actually reach the controller
        // through the surface. From identity, 2 notches -> factor 1.1^2 -> zoom 1.21 (in range).
        // If the surface were unwired (Initialize a no-op, or OnScroll early-returning) the zoom
        // would stay 1 and this FAILs — so the clamp assert below can't false-green on a dead path.
        controller.ApplyView(new CanvasView(0f, 0f, 1f));
        surface.OnScroll(Scroll(new Vector2(0f, 2f)));
        float inRange = controller.CaptureView().zoom;
        if (!Approx(inRange, Mathf.Pow(1.1f, 2f)))
            return $"S8: in-range scroll did not reach controller via surface (zoom {inRange}, expected {Mathf.Pow(1.1f, 2f)})";

        // THE CLAMP (up): a raw wheel notch (~120) is capped to MAX_SCROLL_TICKS=4 -> factor 1.1^4,
        // and must NOT saturate the zoom to MAX_ZOOM. delete-the-logic litmus: drop the Mathf.Clamp
        // in OnScroll and ticks=120 -> 1.1^120 -> zoom clamps to 5.0 -> both asserts below FAIL.
        controller.ApplyView(new CanvasView(0f, 0f, 1f));
        surface.OnScroll(Scroll(new Vector2(0f, 120f)));
        float zUp = controller.CaptureView().zoom;
        if (!Approx(zUp, Mathf.Pow(1.1f, 4f)))
            return $"S8: large up-scroll not capped to 4 notches (got {zUp}, expected {Mathf.Pow(1.1f, 4f)})";
        if (zUp >= CanvasView.MAX_ZOOM - EPS)
            return $"S8: large up-scroll saturated zoom to MAX (tick clamp removed? got {zUp})";

        // THE CLAMP (down): symmetric — a large NEGATIVE wheel caps to -4 notches -> 1.1^-4, not MIN.
        controller.ApplyView(new CanvasView(0f, 0f, 1f));
        surface.OnScroll(Scroll(new Vector2(0f, -120f)));
        float zDn = controller.CaptureView().zoom;
        if (!Approx(zDn, Mathf.Pow(1.1f, -4f)))
            return $"S8: large down-scroll not capped to -4 notches (got {zDn}, expected {Mathf.Pow(1.1f, -4f)})";
        if (zDn <= CanvasView.MIN_ZOOM + EPS)
            return $"S8: large down-scroll saturated zoom to MIN (got {zDn})";
        return null;
    }

    // ---- helpers ----
    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;

    // A PointerEventData carrying a wheel scrollDelta at the viewport centre. The cursor POSITION
    // only steers the cursor-centred pan; the zoom MAGNITUDE asserted in S8 is position-independent.
    // EventSystem.current may be null in batchmode — the ctor merely stores it (we never raycast).
    static PointerEventData Scroll(Vector2 scrollDelta) =>
        new PointerEventData(EventSystem.current) { position = Vector2.zero, scrollDelta = scrollDelta };
}
