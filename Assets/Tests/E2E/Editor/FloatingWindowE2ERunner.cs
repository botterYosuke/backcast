// FloatingWindowE2ERunner.cs — floating window サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// FloatingWindowE2ERunner.md）。第二波で `FloatingWindowProbe`（throwaway AFK gate, Assets/Editor）から
// 昇格・改名（ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=findings 0054 / FooterMode=findings 0055）。
// 実証済み Probe の S1〜S6 を assert 1 行も削らず移送し（各 section の `Covers:` 参照）、台本の `要新規自動化`
// 行（WINDOW-04 cascade / WINDOW-05 single Close / WINDOW-08 reveal cycle）を S7〜S9 として追加した。
// Python-FREE・render-FREE・実 root 不要（headless な Viewport→Content→FloatingWindowLayer の RectTransform
// ツリーを反射合成し `FloatingWindowController` を pure に駆動）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FloatingWindowE2ERunner.Run -logFile <log>
//   # expect: [E2E FLOATING WINDOW PASS] ... / exit=0  （確認は Bash `grep -a "E2E FLOATING WINDOW"`）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// #15 の gate は drag->logical arithmetic / z-order normalization / canvas-logical placement+follow /
// rect・z・visible 永続化 round-trip の正本（findings 0008 §8）。実 title-drag / click-to-front の FEEL は
// owner-launched HITL harness（Tools > Backcast > Floating Window HITL = WINDOW-11）。
//
// INDEPENDENCE (three-way cross-check, kills false-green): the rigour lives in PURE arithmetic
// (drag/zoom — S1; z normalization — S2), the REAL-RectTransform composition through the
// IDENTITY FloatingWindowLayer (S3, engine==math, non-tautological), the z-order live wiring
// (S4, engine==math), and the NON-VACUOUS disk round-trip (S5, on-disk TEXT proof, fresh load).
//
// SECTIONS — S1-S6 promoted from FloatingWindowProbe (findings 0008 §8); S7-S9 are the 要新規 rows:
//   1. drag -> canvas-logical arithmetic (viewportDelta / zoom, resolution-independent, guard)  [WINDOW-01]
//   2. z-order normalize (non-contiguous/duplicate/negative -> stable contiguous 0..n-1)         [WINDOW-06]
//   3. real-RectTransform placement + child-follow through the identity layer (engine==math) + move [WINDOW-01/03/09]
//   4. z-order live application + BringToFront (engine==math, SetAsLastSibling)                   [WINDOW-02/06]
//   5. non-vacuous disk round-trip: 2 strategy_editor + order, non-default size/pos, non-contiguous
//      z, one visible=false (vacuous-green kill, on-disk TEXT proof, fresh load + restore)        [WINDOW-07/08]
//   6. back-compat (old sidecar -> empty) + sanitize (dup id, non-finite/<=0 size drop, x/y->0,
//      unknown-kind preserved-but-spawn-skipped, spec-min clamp) + malformed -> default           [WINDOW-03/10]
//   7. auto-placement cascade: SpawnAuto -> SpawnPlacement.Next diagonal cascade off ALL live tops [WINDOW-04]
//   8. close(X): single Close despawns ONLY the target, leaves the sibling untouched              [WINDOW-05]
//   9. dormant hide + reveal-on-insert: Hide(SetActive false, still registered) -> Show(SetActive
//      true + BringToFront)                                                                       [WINDOW-08]

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class FloatingWindowE2ERunner
{
    const float EPS = 1e-4f;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "floating_window_e2e");
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
                ?? Section6_BackCompatSanitizeFallback(spawned)
                ?? Section7_SpawnAutoCascade(spawned)
                ?? Section8_SingleClose(spawned)
                ?? Section9_DormantHideReveal(spawned);
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
            Debug.Log("[E2E FLOATING WINDOW PASS] drag->logical arithmetic (viewportDelta/zoom, resolution-independent, " +
                      "zoom<=0 guard) + z-order normalize (non-contiguous/duplicate/negative -> stable contiguous 0..n-1) + " +
                      "real-RectTransform placement + child-follow through IDENTITY FloatingWindowLayer (engine==math) + " +
                      "MoveByLogical + z-order live application + BringToFront (SetAsLastSibling, engine==math) + " +
                      "non-vacuous disk round-trip (2 strategy_editor + order, non-default size/pos, non-contiguous z, " +
                      "visible=false; on-disk text proof, fresh load + restore) + back-compat (old sidecar -> empty) + " +
                      "sanitize (dup-id first-wins, non-finite/<=0 size drop, x/y->0, unknown-kind preserved+spawn-skipped, " +
                      "spec-min clamp) + malformed -> default + auto-placement cascade (SpawnAuto diagonal off ALL live " +
                      "tops incl. non-cell) + single Close (target despawned, sibling untouched, unknown->false) + dormant " +
                      "Hide/reveal Show (SetActive + BringToFront) (Unity-owned versioned schema, additive capability " +
                      "surface, ADR-0003 capability parity, under Unity Mono) [WINDOW-01..10]");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E FLOATING WINDOW FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. drag -> canvas-logical arithmetic ----
    // Covers: WINDOW-01 (title-bar drag の screen delta -> viewport-local -> /zoom = canvas 論理 delta の算術。
    //         MoveByLogical 後の anchoredPosition は S3 で assert)
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
    // Covers: WINDOW-06 (SiblingOrder 算術 = 非連続/重複/負の zOrder -> stable contiguous 0..n-1。live 適用は S4)
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
    // Covers: WINDOW-03 (Spawn の placement/pivot=top-left/size), WINDOW-09 (identity layer 経由の pan/zoom 追従
    //         engine==math), WINDOW-01 (MoveByLogical 後の anchoredPosition + follow 維持)
    static string Section3_PlacementAndChildFollow(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out RectTransform viewport, out RectTransform content,
                         out RectTransform layer, out InfiniteCanvasController canvas);

        // This probe drives a NON-parallax controller (MakeController below), so the layer stays
        // identity under Content here — the baseline for child-follow reusing #13's math. (Production
        // adds a parallax offset to the layer under pan; that offset is compensated at spawn and is
        // exercised by InfiniteCanvasE2ERunner S7, not here.)
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
    // Covers: WINDOW-06 (Apply/Capture の live sibling index 再ランク), WINDOW-02 (BringToFront = SetAsLastSibling
    //         で最前面 + Capture().zOrder 反映 = TTWR WindowManager.max_z parity)
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
    // Covers: WINDOW-07 (rect/zOrder/visible の Save->disk->Load->Apply round-trip + fresh controller 復元),
    //         WINDOW-08 (永続 visible=false leg = restore 後も hidden = registered-but-inactive。Hide->Show の
    //         reveal cycle は S9)
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
    // Covers: WINDOW-10 (旧 sidecar->empty, dup id first-wins, 非有限/<=0 size drop, x/y->0, 未知 kind 保持+spawn
    //         skip, malformed->default), WINDOW-03 (spawn 境界の spec-min clamp UP + 未知 kind skip + dup first-wins)
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

    // ---- 7. auto-placement cascade (SpawnAuto -> SpawnPlacement.Next diagonal cascade) ----
    // Covers: WINDOW-04 (新 window が既存窓を避ける = anchor を全 live window の top-left から対角 cascade。
    //         FloatingWindowProbe は明示座標の Spawn のみで cascade 未カバー = この section が新規昇格)
    static string Section7_SpawnAutoCascade(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        // The caller (NotebookCellCoordinator) hands SpawnAuto a canvas-logical anchor (the viewport
        // centre top-left). Use a fixed anchor and assert the diagonal cascade off the LIVE tops.
        Vector2 anchor = new Vector2(100f, -60f);
        float off = SpawnPlacement.DefaultOffset;

        // (a) first cell into an EMPTY canvas: anchor used VERBATIM (no collision -> no cascade).
        RectTransform w1 = controller.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "cell:1", 400f, 300f, anchor, true);
        if (w1 == null) return "S7: SpawnAuto returned null for a known kind";
        if (!Approx2(w1.anchoredPosition, anchor)) return $"S7: first auto window not at anchor verbatim (got {w1.anchoredPosition}, anchor {anchor})";

        // (b) second cell at the SAME anchor collides -> ONE diagonal step (off,off).
        RectTransform w2 = controller.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "cell:2", 400f, 300f, anchor, true);
        Vector2 step1 = anchor + new Vector2(off, off);
        if (w2 == null) return "S7: 2nd SpawnAuto returned null";
        if (!Approx2(w2.anchoredPosition, step1)) return $"S7: cascade step1 {w2.anchoredPosition} != {step1} (diagonal offset not applied)";

        // (c) third cell at the same anchor cascades PAST both blockers -> two diagonal steps.
        RectTransform w3 = controller.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "cell:3", 400f, 300f, anchor, true);
        Vector2 step2 = anchor + new Vector2(2f * off, 2f * off);
        if (w3 == null) return "S7: 3rd SpawnAuto returned null";
        if (!Approx2(w3.anchoredPosition, step2)) return $"S7: cascade step2 {w3.anchoredPosition} != {step2}";

        // (d) collision母集合 is ALL live windows (cell AND non-cell): a NON-cell Order window sitting
        // at the anchor must push the next auto-placed cell off it — proving CaptureTopLefts feeds
        // SpawnPlacement the whole _windows set, not just cell windows. Fresh stack for an unambiguous count.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        Vector2 anchor2 = new Vector2(-25f, 40f);
        RectTransform order = c2.Spawn(FloatingWindowCatalog.KIND_ORDER, "order", anchor2.x, anchor2.y, 360f, 300f, true);
        if (order == null) return "S7: Order spawn returned null (precondition)";
        if (!Approx2(order.anchoredPosition, anchor2)) return $"S7: Order not at the collision anchor (precondition, got {order.anchoredPosition})";
        RectTransform cell = c2.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "cell:x", 400f, 300f, anchor2, true);
        if (cell == null) return "S7: SpawnAuto over an Order window returned null";
        if (Approx2(cell.anchoredPosition, anchor2))
            return "S7: auto-placed cell landed ON a non-cell Order window (collision母集合 excludes non-cell windows)";
        if (!Approx2(cell.anchoredPosition, anchor2 + new Vector2(off, off)))
            return $"S7: cell did not cascade exactly one step off the Order window (got {cell.anchoredPosition})";
        return null;
    }

    // ---- 8. close(X): single despawn leaves the other window untouched ----
    // Covers: WINDOW-05 (Close(id) は対象のみ destroy+deregister、adopted/sibling 窓は残す = File->New cutover。
    //         未知 id は false。FloatingWindowProbe は Apply 全置換 remove のみで単体 Close 未カバー = 新規昇格)
    static string Section8_SingleClose(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        RectTransform keep = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "keep", 0, 0, 300, 200, true);
        RectTransform drop = controller.Spawn(FloatingWindowCatalog.KIND_ORDER, "drop", 50, 50, 300, 200, true);
        // presence guard (vacuous-green kill): assert BOTH exist BEFORE the close, so the post-close
        // absence assert cannot false-green on a window that never spawned.
        if (keep == null || drop == null) return "S8: precondition spawn returned null";
        if (!controller.Has("keep") || !controller.Has("drop")) return "S8: precondition both windows not registered";
        if (controller.Count != 2) return $"S8: precondition expected 2 live windows, got {controller.Count}";
        GameObject dropGo = drop.gameObject;

        // Close the target id: returns true, deregisters + destroys ONLY it.
        if (!controller.Close("drop")) return "S8: Close(existing id) returned false";
        if (controller.Has("drop")) return "S8: closed window still registered";
        if (dropGo != null) return "S8: closed window GameObject not destroyed (DestroyImmediate)";
        // the OTHER window survives untouched.
        if (!controller.Has("keep")) return "S8: Close despawned the wrong window (sibling not preserved)";
        if (controller.RectOf("keep") == null) return "S8: surviving window lost its RectTransform";
        if (controller.Count != 1) return $"S8: expected 1 live window after close, got {controller.Count}";

        // unknown id -> false, no-op (never disturbs the live set).
        if (controller.Close("ghost")) return "S8: Close(unknown id) returned true";
        if (controller.Count != 1) return "S8: Close(unknown id) mutated the live set";
        return null;
    }

    // ---- 9. dormant hide + reveal-on-insert (Hide -> Show) ----
    // Covers: WINDOW-08 (adopt 窓 delete = Hide で SetActive(false) の dormant・never-Destroy、次の AddCell で
    //         Show = SetActive(true) + BringToFront = #81 reveal-on-insert。S5 は永続 visible=false leg のみで
    //         reveal cycle 未カバー = この section が新規昇格)
    static string Section9_DormantHideReveal(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        // Two windows: 'shell' is the adopted region_001 analogue we hide+reveal; 'other' rides in
        // FRONT so the reveal's BringToFront has a sibling to leapfrog (a vacuous raise passes if
        // shell were already last sibling).
        RectTransform shell = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "shell", 0, 0, 300, 200, true);
        RectTransform other = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "other", 20, 20, 300, 200, true);
        if (shell == null || other == null) return "S9: precondition spawn returned null";
        if (other.GetSiblingIndex() <= shell.GetSiblingIndex()) return "S9: precondition 'other' not in front of 'shell'";
        if (!shell.gameObject.activeSelf) return "S9: precondition shell not active";

        // Hide: dormant via SetActive(false), but STILL registered (never-Destroy adopted shell).
        if (!controller.Hide("shell")) return "S9: Hide(existing) returned false";
        if (shell.gameObject.activeSelf) return "S9: Hide did not SetActive(false)";
        if (!controller.Has("shell")) return "S9: Hide deregistered the window (must stay registered, dormant)";
        if (controller.Count != 2) return $"S9: Hide changed the live count (got {controller.Count}, must only deactivate)";
        if (controller.Hide("ghost")) return "S9: Hide(unknown id) returned true";

        // Show: reveal (SetActive(true)) AND raise to FRONT (BringToFront = last sibling) — a cell
        // added into a hidden editor becomes visible AND frontmost (#81 reveal-on-insert).
        controller.Show("shell");
        if (!shell.gameObject.activeSelf) return "S9: Show did not re-activate the window";
        if (shell.GetSiblingIndex() != shell.parent.childCount - 1) return "S9: Show did not raise to last sibling (front)";
        if (shell.GetSiblingIndex() <= other.GetSiblingIndex()) return "S9: Show did not bring shell in front of other";
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
