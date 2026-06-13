// FloatingWindowProbe.cs — issue #15 "floating windows" (THROWAWAY AFK regression gate)
//
// The headless, Python-FREE, render-FREE regression gate for the floating-window seam. Run:
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod FloatingWindowProbe.Run -logFile <log>
//   # expect: [FLOATING WINDOW PASS] ... / exit=0
//
// #15's AFK gate is AUTHORITATIVE for the drag->logical arithmetic, the z-order normalization,
// the canvas-logical placement/follow, and the rect/z/visible persistence round-trip (findings
// 0008 §8); the actual title-drag / click-to-front FEEL is the owner-launched HITL harness
// (Tools > Backcast > Floating Window HITL). Like #12/#13/#14 this probe spawns NO auto-
// bootstrap, so it never re-triggers the single-Play-owner collision (findings 0003 §8).
//
// INDEPENDENCE (three-way cross-check, kills false-green): the rigour lives in PURE arithmetic
// (drag/zoom — S1; z normalization — S2), the REAL-RectTransform composition through the
// IDENTITY FloatingWindowLayer (S3, engine==math, non-tautological), the z-order live wiring
// (S4, engine==math), and the NON-VACUOUS disk round-trip (S5, on-disk TEXT proof, fresh load).
//
// SIX SECTIONS (findings 0008 §8); the 7th gate item — #12/#13/#14 regression — is run by
// EXECUTING those probes individually (recorded in findings §11), not from inside this one.
//   1. drag -> canvas-logical arithmetic (viewportDelta / zoom, resolution-independent, guard)
//   2. z-order normalize (non-contiguous/duplicate/negative -> stable contiguous 0..n-1)
//   3. real-RectTransform placement + child-follow through the identity layer (engine==math) + move
//   4. z-order live application + BringToFront (engine==math, SetAsLastSibling)
//   5. non-vacuous disk round-trip: 2 strategy_editor + order, non-default size/pos, non-contiguous
//      z, one visible=false (vacuous-green kill, on-disk TEXT proof, fresh load + restore)
//   6. back-compat (old sidecar -> empty) + sanitize (dup id, non-finite/<=0 size drop, x/y->0,
//      unknown-kind preserved-but-spawn-skipped, spec-min clamp) + malformed -> default

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class FloatingWindowProbe
{
    const float EPS = 1e-4f;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "floating_window_probe");
    static string TempPath => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);

            fail = Section1_DragArithmetic()
                ?? Section2_ZOrderNormalize()
                ?? Section3_PlacementAndChildFollow(spawned)
                ?? Section4_ZOrderLiveApply(spawned)
                ?? Section5_DiskRoundTripNonVacuous(spawned)
                ?? Section6_BackCompatSanitizeFallback(spawned);
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
            Debug.Log("[FLOATING WINDOW PASS] drag->logical arithmetic (viewportDelta/zoom, resolution-independent, " +
                      "zoom<=0 guard) + z-order normalize (non-contiguous/duplicate/negative -> stable contiguous 0..n-1) + " +
                      "real-RectTransform placement + child-follow through IDENTITY FloatingWindowLayer (engine==math) + " +
                      "MoveByLogical + z-order live application + BringToFront (SetAsLastSibling, engine==math) + " +
                      "non-vacuous disk round-trip (2 strategy_editor + order, non-default size/pos, non-contiguous z, " +
                      "visible=false; on-disk text proof, fresh load + restore) + back-compat (old sidecar -> empty) + " +
                      "sanitize (dup-id first-wins, non-finite/<=0 size drop, x/y->0, unknown-kind preserved+spawn-skipped, " +
                      "spec-min clamp) + malformed -> default (Unity-owned versioned schema, additive capability surface, " +
                      "ADR-0003 capability parity, under Unity Mono)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[FLOATING WINDOW FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. drag -> canvas-logical arithmetic ----
    static string Section1_DragArithmetic()
    {
        // A viewport-local delta divided by zoom is the canvas-logical delta. At zoom 2 the same
        // screen drag should move HALF as far in logical units (the window is drawn 2x).
        Vector2 d = new Vector2(40f, -24f);
        Vector2 atOne = FloatingWindowMath.ViewportDeltaToLogical(d, 1f);
        if (!Approx2(atOne, d)) return $"S1: zoom=1 should be identity, got {atOne}";
        Vector2 atTwo = FloatingWindowMath.ViewportDeltaToLogical(d, 2f);
        if (!Approx2(atTwo, new Vector2(20f, -12f))) return $"S1: zoom=2 should halve, got {atTwo}";
        Vector2 atHalf = FloatingWindowMath.ViewportDeltaToLogical(d, 0.5f);
        if (!Approx2(atHalf, new Vector2(80f, -48f))) return $"S1: zoom=0.5 should double, got {atHalf}";

        // degenerate zoom -> zero delta (no divide-by-zero / NaN propagation into the transform).
        if (FloatingWindowMath.ViewportDeltaToLogical(d, 0f) != Vector2.zero) return "S1: zoom=0 not guarded";
        if (FloatingWindowMath.ViewportDeltaToLogical(d, float.NaN) != Vector2.zero) return "S1: zoom=NaN not guarded";
        if (FloatingWindowMath.ViewportDeltaToLogical(d, -3f) != Vector2.zero) return "S1: zoom<0 not guarded";
        return null;
    }

    // ---- 2. z-order normalize ----
    static string Section2_ZOrderNormalize()
    {
        // Non-contiguous, with a duplicate and a negative. Stable: ascending zOrder, ties keep
        // input order. Input zOrders by index: [5, 2, 99, 2, -1].
        //   sorted (z, idx): (-1,4) (2,1) (2,3) (5,0) (99,2)
        //   so sibling slot 0..4 -> input indices [4, 1, 3, 0, 2].
        int[] order = FloatingWindowMath.SiblingOrder(new[] { 5, 2, 99, 2, -1 });
        int[] expected = { 4, 1, 3, 0, 2 };
        if (order.Length != expected.Length) return $"S2: length {order.Length} != {expected.Length}";
        for (int i = 0; i < expected.Length; i++)
            if (order[i] != expected[i]) return $"S2: slot {i} -> input {order[i]}, expected {expected[i]} (z tie-break by input order broke)";

        // empty / single.
        if (FloatingWindowMath.SiblingOrder(new int[0]).Length != 0) return "S2: empty not empty";
        var one = FloatingWindowMath.SiblingOrder(new[] { 7 });
        if (one.Length != 1 || one[0] != 0) return "S2: single not identity";
        return null;
    }

    // ---- 3. real-RectTransform placement + child-follow through the identity layer ----
    static string Section3_PlacementAndChildFollow(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out RectTransform viewport, out RectTransform content,
                         out RectTransform layer, out InfiniteCanvasController canvas);

        // The FloatingWindowLayer MUST be identity under Content (else the persisted canvas-
        // logical coords don't mean what the schema says, and child-follow can't reuse #13's math).
        if (layer.localPosition != Vector3.zero || layer.localScale != Vector3.one)
            return $"S3: FloatingWindowLayer not identity (pos {layer.localPosition}, scale {layer.localScale})";

        var controller = MakeController(spawned, layer);

        // Spawn a window at a known canvas-LOGICAL top-left with a non-default size (above min).
        Vector2 L = new Vector2(80f, -50f);
        RectTransform win = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR,
                                             "strategy_editor:s3", L.x, L.y, 520f, 360f, true);
        if (win == null) return "S3: Spawn returned null for a known kind";
        if (win.parent != layer) return "S3: window not parented under the FloatingWindowLayer";
        if (!Approx2(win.anchoredPosition, L)) return $"S3: anchoredPosition {win.anchoredPosition} != top-left logical {L}";
        if (!Approx2(win.sizeDelta, new Vector2(520f, 360f))) return $"S3: sizeDelta {win.sizeDelta} != (520,360)";
        if (win.pivot != new Vector2(0f, 1f)) return $"S3: pivot {win.pivot} != top-left (0,1)";

        // Apply a non-identity view (pan != 0, zoom != 1): the window must follow pan/zoom because
        // it rides Content via the identity layer. Cross-check Unity's transform composition vs
        // the pure math, in viewport-centre coords (window.position composes Content AND Layer).
        var view = new CanvasView(15f, -10f, 1.8f);
        canvas.ApplyView(view);
        Vector2 measured = viewport.InverseTransformPoint(win.position);
        Vector2 predicted = CanvasViewMath.LogicalToViewport(L, view);
        if (!Approx2(measured, predicted))
            return $"S3: child-follow engine!=math (engine {measured}, math {predicted}) — window does not track pan/zoom through the identity layer";

        // MoveByLogical translates the logical top-left; follow still holds afterward.
        Vector2 delta = new Vector2(30f, 12f);
        controller.MoveByLogical("strategy_editor:s3", delta);
        Vector2 L2 = L + delta;
        if (!Approx2(win.anchoredPosition, L2)) return $"S3: after MoveByLogical anchoredPosition {win.anchoredPosition} != {L2}";
        Vector2 measured2 = viewport.InverseTransformPoint(win.position);
        Vector2 predicted2 = CanvasViewMath.LogicalToViewport(L2, view);
        if (!Approx2(measured2, predicted2)) return $"S3: child-follow broke after move (engine {measured2}, math {predicted2})";
        return null;
    }

    // ---- 4. z-order live application + BringToFront ----
    static string Section4_ZOrderLiveApply(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        // Apply a doc whose three windows carry NON-CONTIGUOUS z (5, 2, 99). The live sibling
        // order must be the stable normalization: z2 -> back (slot 0), z5 -> 1, z99 -> front (2).
        var doc = DocOf(
            W("strategy_editor:a", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 0, 0, 300, 200, 5, true),
            W("strategy_editor:b", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 0, 0, 300, 200, 2, true),
            W("order",             FloatingWindowCatalog.KIND_ORDER,           0, 0, 300, 200, 99, true));
        controller.Apply(doc);
        if (controller.Count != 3) return $"S4: expected 3 live windows, got {controller.Count}";
        if (controller.RectOf("strategy_editor:b").GetSiblingIndex() != 0) return "S4: z=2 not backmost (slot 0)";
        if (controller.RectOf("strategy_editor:a").GetSiblingIndex() != 1) return "S4: z=5 not slot 1";
        if (controller.RectOf("order").GetSiblingIndex() != 2) return "S4: z=99 not frontmost (slot 2)";

        // Capture re-ranks live sibling indices to CONTIGUOUS 0..n-1 (0=backmost).
        LayoutDocument cap = controller.Capture();
        if (cap.FindWindow("strategy_editor:b").zOrder != 0) return "S4: Capture z(b) != 0";
        if (cap.FindWindow("strategy_editor:a").zOrder != 1) return "S4: Capture z(a) != 1";
        if (cap.FindWindow("order").zOrder != 2) return "S4: Capture z(order) != 2";

        // BringToFront raises to last sibling; Capture reflects it.
        controller.BringToFront("strategy_editor:b");
        if (controller.RectOf("strategy_editor:b").GetSiblingIndex() != 2) return "S4: BringToFront did not raise to last sibling";
        if (controller.Capture().FindWindow("strategy_editor:b").zOrder != 2) return "S4: Capture did not reflect BringToFront";
        return null;
    }

    // ---- 5. non-vacuous disk round-trip (vacuous-green kill) ----
    static string Section5_DiskRoundTripNonVacuous(List<GameObject> spawned)
    {
        // Non-default sizes/positions, NON-CONTIGUOUS z {5,2,99}, one window HIDDEN. The .5
        // values serialize unambiguously so the on-disk TEXT proof is robust to float formatting.
        var mutated = DocOf(
            W("strategy_editor:region_001", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 123.5f, -45.5f, 520.5f, 380.5f, 5, true),
            W("strategy_editor:region_002", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, -200.25f, 60.5f, 300.5f, 200.5f, 2, false),
            W("order",                      FloatingWindowCatalog.KIND_ORDER,           15.5f, 300.5f, 360.5f, 300.5f, 99, true));

        if (LayoutDocument.StructurallyEqual(mutated, LayoutDocument.Default(), EPS))
            return "S5: mutated doc equals Default (mutation no-op)";

        LayoutStore.Save(mutated, TempPath);
        if (!File.Exists(TempPath)) return "S5: Save did not create the sidecar";

        // STRUCTURAL on-disk TEXT proof (catches an in-memory-only round-trip or a serializer
        // that drops a field). Whitespace-insensitive.
        string compact = File.ReadAllText(TempPath).Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        string[] needles =
        {
            "\"id\":\"strategy_editor:region_001\"", "\"kind\":\"strategy_editor\"",
            "\"id\":\"strategy_editor:region_002\"", "\"id\":\"order\"", "\"kind\":\"order\"",
            "\"w\":520.5", "\"x\":123.5", "\"zOrder\":99", "\"zOrder\":5", "\"zOrder\":2", "\"visible\":false",
        };
        foreach (var n in needles) if (!compact.Contains(n)) return $"S5: on-disk JSON missing {n}";

        // Fresh load: every field survives VERBATIM (zOrder NOT normalized at the persistence layer).
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (!LayoutDocument.StructurallyEqual(loaded, mutated, EPS)) return "S5: loaded != mutated (round-trip lost a field)";
        if (LayoutDocument.StructurallyEqual(loaded, LayoutDocument.Default(), EPS)) return "S5: loaded == Default (vacuous round-trip)";
        if (loaded.FindWindow("order").zOrder != 99) return "S5: non-contiguous z not preserved verbatim on load";

        // Restore to a FRESH controller: spawn count, normalized sibling order, restored geometry/visibility.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);
        controller.Apply(loaded);
        if (controller.Count != 3) return $"S5: restored {controller.Count} windows, expected 3";
        // z {5,2,99} -> region_002 back, region_001 mid, order front.
        if (controller.RectOf("strategy_editor:region_002").GetSiblingIndex() != 0) return "S5: restored back window wrong";
        if (controller.RectOf("strategy_editor:region_001").GetSiblingIndex() != 1) return "S5: restored mid window wrong";
        if (controller.RectOf("order").GetSiblingIndex() != 2) return "S5: restored front window wrong";
        RectTransform r1 = controller.RectOf("strategy_editor:region_001");
        if (!Approx2(r1.anchoredPosition, new Vector2(123.5f, -45.5f))) return $"S5: restored position {r1.anchoredPosition}";
        if (!Approx2(r1.sizeDelta, new Vector2(520.5f, 380.5f))) return $"S5: restored size {r1.sizeDelta}";
        if (controller.RectOf("strategy_editor:region_002").gameObject.activeSelf)
            return "S5: hidden window (visible=false) restored as active";
        return null;
    }

    // ---- 6. back-compat + sanitize + fallback ----
    static string Section6_BackCompatSanitizeFallback(List<GameObject> spawned)
    {
        // (a) An OLD #13-era sidecar (panels + canvasView, NO floatingWindows field) loads with
        // floatingWindows normalized to an EMPTY list and panels/canvasView intact.
        string oldJson =
            "{\"version\":1,\"panels\":[{\"id\":\"chart\",\"slot\":0,\"visible\":true," +
            "\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":0.62,\"maxY\":1}}]," +
            "\"canvasView\":{\"panX\":12,\"panY\":-8,\"zoom\":1.75}}";
        LayoutDocument old = LayoutStore.LoadFromJson(oldJson);
        if (old.floatingWindows == null || old.floatingWindows.Count != 0) return "S6a: old sidecar did not yield empty floatingWindows";
        if (old.Find("chart") == null) return "S6a: old sidecar lost its panels";
        if (!Approx(old.canvasView.zoom, 1.75f)) return "S6a: old sidecar lost its canvasView";

        // (b) DUPLICATE id -> keep first, drop rest (document-unique).
        var dup = DocOf(
            W("order", FloatingWindowCatalog.KIND_ORDER, 1, 2, 300, 200, 0, true),
            W("order", FloatingWindowCatalog.KIND_ORDER, 9, 9, 300, 200, 1, true));
        LayoutStore.NormalizeFloatingWindows(dup);
        if (dup.floatingWindows.Count != 1) return $"S6b: duplicate id not collapsed (count {dup.floatingWindows.Count})";
        if (!Approx(dup.floatingWindows[0].x, 1f)) return "S6b: duplicate id did not keep the FIRST";

        // (c) Degenerate size -> DROP (NOT a generic fallback). Non-finite set DIRECTLY on the POCO
        // (NaN/Inf aren't valid JSON, so this is separated from the malformed-JSON path).
        var sizes = DocOf(
            W("a", FloatingWindowCatalog.KIND_ORDER, 0, 0, 0f, 200, 0, true),                       // w<=0 -> drop
            W("b", FloatingWindowCatalog.KIND_ORDER, 0, 0, 300, float.NaN, 0, true),                 // h NaN -> drop
            W("c", FloatingWindowCatalog.KIND_ORDER, 0, 0, float.PositiveInfinity, 200, 0, true),    // w Inf -> drop
            W("d", FloatingWindowCatalog.KIND_ORDER, float.NaN, float.NegativeInfinity, 300, 200, 0, true)); // good size, bad x/y -> keep, x/y->0
        LayoutStore.NormalizeFloatingWindows(sizes);
        if (sizes.floatingWindows.Count != 1) return $"S6c: degenerate-size drop wrong (count {sizes.floatingWindows.Count})";
        var kept = sizes.floatingWindows[0];
        if (kept.id != "d") return "S6c: kept the wrong entry";
        if (kept.x != 0f || kept.y != 0f) return $"S6c: non-finite x/y not zeroed (got {kept.x},{kept.y})";

        // (d) UNKNOWN kind -> PRESERVED by the store, but the restore controller SKIPS its spawn.
        var unknown = DocOf(
            W("ghost", "ghost_kind", 0, 0, 300, 200, 0, true),
            W("order", FloatingWindowCatalog.KIND_ORDER, 0, 0, 300, 200, 1, true));
        LayoutStore.NormalizeFloatingWindows(unknown);
        if (unknown.FindWindow("ghost") == null) return "S6d: store dropped an unknown kind (must preserve)";
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);
        controller.Apply(unknown);
        if (controller.Has("ghost")) return "S6d: controller spawned an unknown kind";
        if (!controller.Has("order")) return "S6d: controller skipped a KNOWN kind alongside the unknown one";
        if (controller.Count != 1) return $"S6d: expected only the known window live (got {controller.Count})";

        // (e) spec-min clamp at the SPAWN boundary: a small-but-positive size clamps UP to minSize.
        RectTransform small = controller.Spawn(FloatingWindowCatalog.KIND_ORDER, "tiny", 0, 0, 10f, 10f, true);
        if (small == null) return "S6e: clamp spawn returned null";
        if (!Approx2(small.sizeDelta, new Vector2(280f, 180f))) return $"S6e: size not clamped to min (got {small.sizeDelta})";

        // (f) malformed JSON -> whole-document default (-> empty floatingWindows).
        LayoutDocument def = LayoutStore.LoadFromJson("{not valid json");
        if (!LayoutDocument.StructurallyEqual(def, LayoutDocument.Default(), 1e-3f)) return "S6f: malformed JSON did not fall back to default";
        return null;
    }

    // ---- helpers ----

    // Build Viewport(identity) -> Content(centre anchor/pivot, controller-driven) ->
    // FloatingWindowLayer(identity), returning the canvas controller. Mirrors #13's Section 4
    // stack, with the extra layer #15 introduces.
    static void BuildCanvasStack(List<GameObject> spawned, out RectTransform viewport, out RectTransform content,
                                 out RectTransform layer, out InfiniteCanvasController canvas)
    {
        var viewportGo = new GameObject("ProbeViewport", typeof(RectTransform));
        spawned.Add(viewportGo);
        viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = viewport.anchorMax = viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.sizeDelta = new Vector2(1000f, 800f);

        var contentGo = new GameObject("ProbeContent", typeof(RectTransform));
        content = contentGo.GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        var layerGo = new GameObject("FloatingWindowLayer", typeof(RectTransform));
        layer = layerGo.GetComponent<RectTransform>();
        layer.SetParent(content, false);
        layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(0.5f, 0.5f);
        layer.anchoredPosition = Vector2.zero;
        layer.sizeDelta = Vector2.zero;   // identity under Content

        canvas = new InfiniteCanvasController(content);
    }

    // A controller whose factory mints BARE RectTransforms (no title bar / body — the AFK gate
    // proves placement/z/persist, not visuals) and whose remover uses DestroyImmediate (Destroy
    // is a no-op outside playmode).
    static FloatingWindowController MakeController(List<GameObject> spawned, RectTransform layer)
    {
        return new FloatingWindowController(
            layer, FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var go = new GameObject("W_" + id, typeof(RectTransform));
                spawned.Add(go);
                return go.GetComponent<RectTransform>();
            },
            go => UnityEngine.Object.DestroyImmediate(go));
    }

    static FloatingWindowLayout W(string id, string kind, float x, float y, float w, float h, int z, bool visible) =>
        new FloatingWindowLayout(id, kind, x, y, w, h, z, visible);

    static LayoutDocument DocOf(params FloatingWindowLayout[] windows)
    {
        return new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            canvasView = CanvasView.Identity(),
            floatingWindows = new List<FloatingWindowLayout>(windows),
        };
    }

    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;
    static bool Approx2(Vector2 a, Vector2 b) => Approx(a.x, b.x) && Approx(a.y, b.y);
}
