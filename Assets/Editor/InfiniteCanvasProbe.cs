// InfiniteCanvasProbe.cs — issue #13 "infinite canvas" (THROWAWAY AFK regression gate)
//
// The headless, Python-FREE, render-FREE regression gate for the infinite-canvas pan/zoom
// seam. Run:
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod InfiniteCanvasProbe.Run -logFile <log>
//   # expect: [INFINITE CANVAS PASS] ... / exit=0
//
// #13's AFK gate is AUTHORITATIVE for the arithmetic (findings 0006 §5); the actual
// drag/wheel feel is the owner-launched HITL harness (Tools > Backcast > Infinite Canvas
// HITL). Like #12 this probe spawns NO auto-bootstrap, so it never re-triggers the single-
// Play-owner collision (findings 0003 §8).
//
// THREE-WAY INDEPENDENCE — the whole point (findings 0006 §5): the gate cross-checks PURE
// MATH (CanvasViewMath) against the UNITY TRANSFORM ENGINE (RectTransform.TransformPoint)
// against SERIALIZATION (LayoutStore round-trip). The child-follow section is deliberately
// NON-TAUTOLOGICAL: it asserts Unity's own transform composition equals the math's
// prediction, not the math against itself.
//
// SIX SECTIONS (findings 0006 §5), each returns null on pass or a reason string:
//   1. pan arithmetic (logical move = -dScreen/zoom, resolution-independent)
//   2. zoom clamp [0.2, 5.0] (saturates, no overshoot)
//   3. cursor-centred invariant — normal step + clamped step, both non-no-op
//   4. real RectTransform child-follow (engine == math) + controller Apply/Capture
//   5. non-identity CanvasView disk round-trip (vacuous-green kill, on-disk TEXT proof)
//   6. back-compat (old v1, no canvasView) + sanitize (zoom 0->1, 99->5, non-finite) +
//      malformed-document fallback

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class InfiniteCanvasProbe
{
    const float EPS = 1e-3f;

    // A deterministic, writable temp path — NOT the production sidecar, so the gate never
    // clobbers a real layout (mirrors ReplayLayoutProbe).
    static string TempDir => Path.Combine(Application.temporaryCachePath, "infinite_canvas_probe");
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
                ?? Section6_BackCompatAndSanitize();
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
            Debug.Log("[INFINITE CANVAS PASS] pan arithmetic + zoom clamp[0.2,5.0] + cursor-centred invariant " +
                      "(normal+clamped, non-no-op) + real-RectTransform child-follow (engine==math) + " +
                      "Apply/Capture boundary + non-vacuous CanvasView disk round-trip + back-compat/sanitize " +
                      "(Unity-owned versioned schema, additive capability surface, ADR-0003 capability parity, under Unity Mono)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[INFINITE CANVAS FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. pan arithmetic: logical move = -dScreen/zoom, resolution-independent ----
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

    // ---- helpers ----
    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;
}
