// FloatingWindowE2ERunner.cs — floating window サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// FloatingWindowE2ERunner.md）。第二波で `FloatingWindowProbe`（throwaway AFK gate, Assets/Editor）から
// 昇格・改名（ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=findings 0054 / FooterMode=findings 0055）。
// 実証済み Probe の S1〜S6 を assert 1 行も削らず移送し（各 section の `Covers:` 参照）、台本の `要新規自動化`
// 行（WINDOW-04 cascade / WINDOW-05 single Close / WINDOW-08 reveal cycle）を S7〜S9 として追加した。
// Python-FREE・render-FREE・実 root 不要（headless な Viewport→Content→FloatingWindowLayer の RectTransform
// ツリーを反射合成し `FloatingWindowController` を pure に駆動）。**例外は S19 のみ**＝実シーン
// `BackcastWorkspace.unity` を editmode で開き #103 の DockLayer 背面 sibling／serialized 参照を構造検証する
// （PlayMode 不要・Python 不要・他 section の合成スタックには触れない＝最後に走る）。
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
//  10. #99 magnet snap pure arithmetic (flush/edge-align, x/y independent, threshold guards)       [SNAP-01]
//  11. #99 snap controller wiring (excludes self/hidden, dragged-only, no group propagation)       [SNAP-02]
//  12. #99 dock catalog kinds (chart multi-instance + 5 base singletons, unknown tolerance)        [DOCK-01]
//  13. #99 DockDefaultPlacement grid arithmetic (base-5 first-launch placement)                    [DOCK-02]
//  14. #101 DockSnapPlacement flush adjacency (right→down→left→up, overflow cascade, size verbatim) [DOCK-03]
//  15. #101 focus-adjacent dock spawn (spec-fixed count-independent size, focus/nearest target)    [DOCK-04]
//  16. #103 two depth planes: back DockLayer 1.0× vs front FloatingWindowLayer 1.2× (parallax speed
//      gap = restored #99 depth, back sibling, engine==math composition)                            [PLANE-01]
//  17. #103 cross-plane snap BAN (per-controller母集合 — dock never snaps to front; same-plane snap
//      unchanged; dock focus within plane; DockShape.IsDockKind routing parity)                     [PLANE-02]
//  18. #103 two-controller persist round-trip (capture UNION → disk → restore routes by kind to the
//      correct plane/layer, hidden startup preserved, no cross-plane leak, schema-add 0)            [PLANE-03]
//  19. #103 REAL scene wiring (loads BackcastWorkspace.unity: DockLayer is the backmost Content sibling
//      of FloatingWindowLayer + _dockLayer/_floatingLayer serialized refs — pins the scene-builder output) [PLANE-01]
//  20. #104 Slice A: groupId additive schema (Capture/Apply pass-through + on-disk round-trip + back-compat null +
//      Spawn=null invariant + existing-entry Apply update)                                                  [GROUP-01]
//  21. #104 Slice B: IsFlushAdjacent pure (flush yes / same-edge align no / corner overlap=0 no / eps=1px) [GROUP-02]
//  22. ADR-0024 §5: merge cascade pure (size max > StringCompareOrdinal min > new GUID — NO Hakoniwa-prio)  [DRAG-12]
//  23. #104 Slice B: flush-attach release commit + Spawn=null invariant + dict-min merge end-to-end         [GROUP-04]
//  24. ADR-0024 §2: ResolveDragMode pure 3-mode dispatcher (Swap dist-indep / Detach ≥256 incl / Translate) [DRAG-01..04]
//  25. ADR-0024 §7: Translate/Detach real-render (whole island vs dragged-only) + groupId unchanged         [DRAG-02/03]
//  26. ADR-0024 §4: detach release commit + DissolveIfShrunkTo(2) + Close cascade + Hide preserve           [DRAG-08]
//  27. ADR-0024 §1: Hakoniwa special RETIRED (core-bearing island translates / core member detaches)        [DRAG-11]
//  28. ADR-0024 §2: ResolveDropTarget pure (cursor under = swap target, top sibling, dragged/hidden excl)   [DRAG-01/04]
//  29. ADR-0024 §4: Swap commit ((x,y,w,h) exchange, any island, kind/id/groupId untouched)                 [DRAG-01]
//  30. #104 Slice F: cross-plane group restore split (majority / tie → dock / loser=null) + shared dissolve [GROUP-11]
//  31. ADR-0024 §7: drag-ghost is SWAP-ONLY (2 ghosts swap / 0 translate / 0 detach / commit-on-release)    [DRAG-14]
//  32. #105/ADR-0020/ADR-0024: factory first-launch grouping forms ONE PLAIN island (cores drag freely)     [DRAG-14]
//  33. ADR-0024 §3: ComputeMagneticSnap pure (flush within R_SNAP, orthogonal overlap, x/y independent)     [DRAG-05/06]
//  34. ADR-0024 §4: ResolveNearestFlush pure (4 candidates, min move, tie-break x, orthogonal overlap ≥1)   [DRAG-07]
//  35. ADR-0024 §3: SpringEase / SpringRectAt pure (ease-out-back, peak overshoot exactly 8% at t=0.6)      [DRAG-10]
//  36. ADR-0024 §3/§7: in-drag magnetic snap real-render (Translate island snap + stickiness / Detach snap) [DRAG-05/06]
//  37. ADR-0024 §4: release overlap → nearest-flush + merge (Translate & Detach) + ADR-0019 D4 incidental   [DRAG-07]
//  38. ADR-0024 §8: ESC cancel (live render reverts to rest, state untouched, MouseUp commits nothing)      [DRAG-09]
//  39. ADR-0024 §3: spring fire-point wiring (swap / detach commit + ESC fire the injected animator)        [DRAG-10]
//  40. ADR-0024 §8: chart:<id> drags like any window (flush-attach island, translate, detach)               [DRAG-13]

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
                ?? Section9_DormantHideReveal(spawned)
                ?? Section10_SnapPureArithmetic()
                ?? Section11_SnapOnReleaseControllerWiring(spawned)
                ?? Section12_DockCatalogKinds()
                ?? Section13_DockDefaultPlacementArithmetic()
                ?? Section14_DockSnapPlacementArithmetic()
                ?? Section15_FocusAdjacentDockSpawn(spawned)
                ?? Section16_TwoPlaneParallaxDepth(spawned)
                ?? Section17_CrossPlaneSnapBan(spawned)
                ?? Section18_TwoControllerPersistRoundTrip(spawned)
                ?? Section20_GroupIdSchemaRoundTrip(spawned)
                ?? Section21_IsFlushAdjacentPure()
                ?? Section22_MergeCascadePure()
                ?? Section23_FlushAttachWiring(spawned)
                ?? Section24_ResolveDragModePure()
                ?? Section25_TranslateDetachRender(spawned)
                ?? Section26_DetachDissolveCloseHide(spawned)
                ?? Section27_HakoniwaRetired(spawned)
                ?? Section28_ResolveDropTargetPure()
                ?? Section29_SwapWiring(spawned)
                ?? Section30_CrossPlaneGroupRestoreSplit(spawned)
                ?? Section31_GhostSwapOnly(spawned)
                ?? Section32_FactoryBaseGroup(spawned)
                ?? Section33_ComputeMagneticSnapPure()
                ?? Section34_ResolveNearestFlushPure()
                ?? Section35_SpringEasePure()
                ?? Section36_MagneticSnapRenderWiring(spawned)
                ?? Section37_ReleaseOverlapMerge(spawned)
                ?? Section38_EscCancel(spawned)
                ?? Section39_SpringFireWiring(spawned)
                ?? Section40_ChartEquivalence(spawned)
                ?? Section19_SceneWiringBackPlane();   // LAST: loads the real scene (root-free sections run first)
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
                      "Hide/reveal Show (SetActive + BringToFront) + #99 magnet snap (pure flush/edge-align x-y INDEPENDENT, " +
                      "beyond-threshold->0, threshold<=0 guard) + controller SnapOnRelease (excludes self, ignores hidden, " +
                      "applies via anchoredPosition, dragged-only — no group propagation) + #99 dock catalog kinds (chart " +
                      "multi-instance + 5 base singletons, accents from PlayerColors, unknown-kind tolerance preserved) + " +
                      "DockDefaultPlacement (ceil(√n) grid in absolute canvas-logical coords, row-major slot 0=top-left, " +
                      "y up-positive rows, no overlap, n=0 empty) + #101 DockSnapPlacement (flush adjacency right→down→" +
                      "left→up, perpendicular-edge align, strict no-overlap selection, gap=0 flush, size verbatim, " +
                      "overflow diagonal cascade) + #101 focus-adjacent dock spawn (spec-fixed size count-INDEPENDENT, " +
                      "snap to USER-focused window, programmatic BringToFront does NOT record focus, no-focus/closed→" +
                      "nearest-visible fallback, dup/unknown guards) + #103 two depth planes (back DockLayer 1.0× vs " +
                      "front FloatingWindowLayer 1.2× — parallax speed gap = restored depth, back sibling, engine==math " +
                      "composition) + cross-plane snap BAN (per-controller snap母集合 — dock window never snaps to a " +
                      "front window; same-plane snap unchanged; dock focus resolves WITHIN the back plane; " +
                      "DockShape.IsDockKind routing parity) + two-controller persist round-trip (capture UNION → disk → " +
                      "restore routes by kind to the correct plane/layer, hidden startup preserved, no cross-plane leak, " +
                      "schema-add 0) + #103 REAL scene wiring (authored DockLayer is the backmost Content sibling of " +
                      "FloatingWindowLayer + _dockLayer/_floatingLayer serialized refs point to them — binds depth " +
                      "ordering to the scene-builder output, not the test's setup) + " +
                      "#104 groupId additive schema (round-trip on-disk, back-compat null, Spawn=null invariant, " +
                      "Capture/Apply pass-through) + IsFlushAdjacent (flush yes / same-edge align no / corner " +
                      "overlap=0 no / eps=1px) + ADR-0024 merge cascade (size max > StringCompareOrdinal min > " +
                      "new GUID — Hakoniwa-priority RETIRED) + flush-attach release commit (controller wiring) + " +
                      "SpawnDockedToFocus groupId=null invariant + ResolveDragMode 3-mode dispatcher (Swap " +
                      "distance-INDEPENDENT over a sibling / Detach |cursor-dragStart|≥256 INCLUSIVE / Translate " +
                      "otherwise / center-in-rect on-off / singleton) + Translate/Detach real-render (whole island " +
                      "vs dragged-only at rest+offset, groupId unchanged mid-drag) + detach release commit + " +
                      "DissolveIfShrunkTo shared helper (≥2 keeps, <2 chain dissolves) + Close cascade + Hide " +
                      "groupId preserve + HAKONIWA SPECIAL RETIRED (core-bearing island translates, core member " +
                      "detaches — no translate-ban / no core-lock) + ResolveDropTarget pure (cursor under = swap " +
                      "target, top-sibling, dragged/hidden excl) + Swap commit (x,y,w,h 4-value exchange, any " +
                      "island, kind/id/groupId untouched) + cross-plane group restore split (majority / tie → dock " +
                      "/ loser null / shared dissolve) + drag-ghost SWAP-ONLY (2 ghosts swap solid@target+dashed@" +
                      "rest / 0 translate / 0 detach / alpha=0.45 / last-sibling / commit-on-release clear) + " +
                      "factory first-launch grouping (FormGroup mints ONE shared groupId, plain island — cores " +
                      "drag freely, FLUSH/docked placement) + ComputeMagneticSnap pure (flush within R_SNAP=96, " +
                      "orthogonal overlap, x/y independent, beyond→0) + ResolveNearestFlush pure (4 candidates, " +
                      "min move, tie-break x, orthogonal overlap≥1) + SpringEase/SpringRectAt (ease-out-back, peak " +
                      "overshoot EXACTLY 8% at t=0.6, 200ms) + in-drag magnetic snap real-render (Translate island " +
                      "snap + stickiness / Detach snap to flush) + release overlap → nearest-flush + merge " +
                      "(Translate & Detach singleton-join) + ADR-0019 D4 incidental flush attach retained + ESC " +
                      "cancel (live render reverts to rest, state untouched, MouseUp commits nothing) + spring " +
                      "fire-points (swap / detach commit + ESC fire the injected animator) + chart:<id> drags like " +
                      "any window (flush-attach island, translate, detach) " +
                      "(Unity-owned versioned schema, additive capability surface, ADR-0003 capability parity, " +
                      "under Unity Mono) " +
                      "[WINDOW-01..10,SNAP-01,02,DOCK-01,02,03,04,PLANE-01,02,03,GROUP-01,02,04,11,DRAG-01..14]");
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

    // ---- 10. magnet snap pure arithmetic (FloatingWindowMath.SnapOffset) ----
    // Covers: SNAP-01 — #99 Slice 1 / findings 0075 §1 (flush + edge-align, x/y INDEPENDENT, beyond-threshold→0,
    // threshold≤0/NaN guard, deterministic tie-break, no group / no resize).
    static string Section10_SnapPureArithmetic()
    {
        const float TH = 12f;

        // (a) empty `others` → zero on both axes (no neighbour to snap to).
        var solo = new FloatingWindowMath.DockRect(0, 0, 200, 100);
        var noneOffset = FloatingWindowMath.SnapOffset(solo, new List<FloatingWindowMath.DockRect>(), TH);
        if (noneOffset != Vector2.zero) return $"S10a: empty others should yield zero (got {noneOffset})";

        // (b) FLUSH RIGHT: dragged at (x=100,y=0,w=200,h=100) — right edge at x=300; neighbour at
        // x=305 — flush right candidate Δx=+5. Same y → 0. Closest x candidate is the flush one (5px),
        // not the 200-wide same-edge or 405 flush-left (both far beyond threshold).
        var dragA = new FloatingWindowMath.DockRect(100, 0, 200, 100);
        var nbrR = new FloatingWindowMath.DockRect(305, 0, 200, 100);
        Vector2 oFlush = FloatingWindowMath.SnapOffset(dragA, new[] { nbrR }, TH);
        if (!Approx(oFlush.x, 5f)) return $"S10b: flush-right Δx expected 5, got {oFlush.x}";
        if (!Approx(oFlush.y, 0f)) return $"S10b: y unexpectedly nudged (got {oFlush.y})";

        // (c) SAME-EDGE LEFT align: dragged at x=100; neighbour at x=104 (same w/h). All flush
        // candidates are 200+ away; same-edge left↔left Δx = 104-100 = +4 wins.
        var nbrSame = new FloatingWindowMath.DockRect(104, 0, 200, 100);
        Vector2 oAlign = FloatingWindowMath.SnapOffset(dragA, new[] { nbrSame }, TH);
        if (!Approx(oAlign.x, 4f)) return $"S10c: edge-align Δx expected 4, got {oAlign.x}";
        if (!Approx(oAlign.y, 0f)) return $"S10c: y unexpectedly nudged (got {oAlign.y})";

        // (d) BEYOND THRESHOLD on x → 0 on x. Neighbour 50px away — no candidate ≤ 12px.
        var nbrFar = new FloatingWindowMath.DockRect(350, 0, 200, 100);
        Vector2 oFar = FloatingWindowMath.SnapOffset(dragA, new[] { nbrFar }, TH);
        if (oFar != Vector2.zero) return $"S10d: beyond-threshold should yield zero (got {oFar})";

        // (e) X-Y INDEPENDENT: dragged at (100,0,200,100); neighbour-A nudges x by flush-right Δ=+5
        // (offset by 5 on x, far on y). Neighbour-B nudges y by edge-align top↔top Δ=+3 (close on y,
        // far on x). The pure math must take x from A and y from B SIMULTANEOUSLY.
        var nbrXOnly = new FloatingWindowMath.DockRect(305, 999f, 200, 100);   // x=+5; y far away
        var nbrYOnly = new FloatingWindowMath.DockRect(999f, 3f,   200, 100);   // y=+3 (top↔top); x far
        Vector2 oXY = FloatingWindowMath.SnapOffset(dragA, new[] { nbrXOnly, nbrYOnly }, TH);
        if (!Approx(oXY.x, 5f)) return $"S10e: x INDEPENDENTLY snapped expected 5, got {oXY.x}";
        if (!Approx(oXY.y, 3f)) return $"S10e: y INDEPENDENTLY snapped expected 3, got {oXY.y}";

        // (f) FLUSH-BOTTOM on Y (dragged's bottom ↔ neighbour's top). Top-left-pivot, y up-positive:
        // dragged at y_top=0,h=100 → bottom=-100. Neighbour at y_top=-105 (just below). Δy that brings
        // dragged.bottom to nbr.top = -105 - (-100) = -5. Pure number; no x interaction.
        var dragB = new FloatingWindowMath.DockRect(0, 0, 200, 100);
        var nbrBelow = new FloatingWindowMath.DockRect(0, -105, 200, 100);
        Vector2 oFlushB = FloatingWindowMath.SnapOffset(dragB, new[] { nbrBelow }, TH);
        if (!Approx(oFlushB.x, 0f)) return $"S10f: x unexpectedly nudged on a y-flush case (got {oFlushB.x})";
        if (!Approx(oFlushB.y, -5f)) return $"S10f: flush-bottom Δy expected -5, got {oFlushB.y}";

        // (g) threshold guards: 0 / negative / NaN → Vector2.zero (degenerate; controller no-op release).
        if (FloatingWindowMath.SnapOffset(dragA, new[] { nbrSame }, 0f) != Vector2.zero) return "S10g: threshold=0 not guarded";
        if (FloatingWindowMath.SnapOffset(dragA, new[] { nbrSame }, -3f) != Vector2.zero) return "S10g: threshold<0 not guarded";
        if (FloatingWindowMath.SnapOffset(dragA, new[] { nbrSame }, float.NaN) != Vector2.zero) return "S10g: threshold=NaN not guarded";

        // (h) NEAREST WINS within threshold: two neighbours, one at +8 and one at +3 (same-edge align).
        // The +3 candidate has smaller |Δ| and must win. Deterministic regardless of input list order.
        var nbrFurther = new FloatingWindowMath.DockRect(108, 0, 200, 100);   // +8
        var nbrCloser  = new FloatingWindowMath.DockRect(103, 0, 200, 100);   // +3
        Vector2 oNear1 = FloatingWindowMath.SnapOffset(dragA, new[] { nbrFurther, nbrCloser }, TH);
        Vector2 oNear2 = FloatingWindowMath.SnapOffset(dragA, new[] { nbrCloser, nbrFurther }, TH);
        if (!Approx(oNear1.x, 3f) || !Approx(oNear2.x, 3f))
            return $"S10h: closer candidate must win regardless of input order (got {oNear1.x}, {oNear2.x})";

        return null;
    }

    // ---- 11. controller wiring: SnapOnRelease applies the math to the live rectTransform ----
    // Covers: SNAP-02 — #99 Slice 1 (controller side: excludes self, ignores hidden neighbours, applies
    // the offset to ONLY the dragged window via anchoredPosition, returns the applied Δ. No group
    // propagation — the neighbour does NOT move).
    static string Section11_SnapOnReleaseControllerWiring(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        // Two equal windows; dragged 5px shy of flush-right against an anchored neighbour. The size is
        // EXACTLY strategy_editor minSize (280×180) so Spawn's spec-min clamp leaves it unchanged — a
        // sub-min size (e.g. 200×100) would be clamped UP and silently shift the right edge, breaking the
        // intended 5px gap (the cause of S11's pre-existing false RED, fixed with #101). dragged right
        // edge = 100+280 = 380; nbr left = 385 → a 5px flush-right gap.
        RectTransform dragged = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR,
                                                 "drag", 100, 0, 280, 180, true);
        RectTransform nbr = controller.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR,
                                              "nbr", 385, 0, 280, 180, true);
        if (dragged == null || nbr == null) return "S11: precondition spawn returned null";
        // Guard the clamp assumption directly (vacuous-green kill): if a spec change ever pushes minSize
        // past these dims, the 5px geometry below is meaningless — fail loudly instead of false-greening.
        if (!Approx2(dragged.sizeDelta, new Vector2(280f, 180f)) || !Approx2(nbr.sizeDelta, new Vector2(280f, 180f)))
            return $"S11: precondition windows clamped off the intended size (drag {dragged.sizeDelta}, nbr {nbr.sizeDelta})";

        Vector2 draggedBefore = dragged.anchoredPosition;
        Vector2 nbrBefore = nbr.anchoredPosition;

        // Default-threshold convenience overload: should snap the 5px gap closed.
        Vector2 applied = controller.SnapOnRelease("drag");
        if (!Approx(applied.x, 5f) || !Approx(applied.y, 0f)) return $"S11: applied offset {applied} expected (5,0)";
        if (!Approx2(dragged.anchoredPosition, draggedBefore + new Vector2(5f, 0f)))
            return $"S11: dragged anchoredPosition {dragged.anchoredPosition} != before+(5,0)";
        if (!Approx2(nbr.anchoredPosition, nbrBefore)) return "S11: NEIGHBOUR moved — group propagation must NOT exist";

        // unknown id → Vector2.zero, no side effects.
        Vector2 ghost = controller.SnapOnRelease("ghost");
        if (ghost != Vector2.zero) return "S11: SnapOnRelease(unknown) did not return zero";

        // Hide the neighbour → no more pull (hidden windows are excluded from `others`).
        // Re-position dragged within threshold of the hidden neighbour and call snap; nothing should happen.
        controller.Hide("nbr");
        dragged.anchoredPosition = new Vector2(100, 0);   // back to 5px shy
        Vector2 hiddenPull = controller.SnapOnRelease("drag");
        if (hiddenPull != Vector2.zero) return $"S11: hidden neighbour exerted pull {hiddenPull}";
        if (!Approx2(dragged.anchoredPosition, new Vector2(100, 0))) return "S11: dragged moved despite hidden neighbour";

        // Custom-threshold overload: a tighter threshold (3px) must REJECT a 5px gap snap.
        controller.Show("nbr");
        dragged.anchoredPosition = new Vector2(100, 0);   // back to 5px shy
        Vector2 tight = controller.SnapOnRelease("drag", 3f);
        if (tight != Vector2.zero) return $"S11: tight threshold should reject (got {tight})";
        if (!Approx2(dragged.anchoredPosition, new Vector2(100, 0))) return "S11: dragged moved under tight threshold";

        return null;
    }

    // ---- 12. dock catalog kinds ----
    // Covers: DOCK-01 — #99 Slice 2 / findings 0075 §2 (chart multi-instance + 5 base singleton kinds
    // present in Default(), each with a distinct accent and a usable spec; unknown-kind tolerance
    // unchanged so a forward-evolved doc still survives).
    static string Section12_DockCatalogKinds()
    {
        var catalog = FloatingWindowCatalog.Default();

        // (a) every #99 kind resolves.
        string[] kinds = {
            FloatingWindowCatalog.KIND_CHART,
            FloatingWindowCatalog.KIND_BUYING_POWER,
            FloatingWindowCatalog.KIND_ORDERS,
            FloatingWindowCatalog.KIND_POSITIONS,
            FloatingWindowCatalog.KIND_RUN_RESULT,
            FloatingWindowCatalog.KIND_STARTUP,
        };
        foreach (var k in kinds)
        {
            if (!catalog.TryGet(k, out var spec)) return $"S12a: catalog missing dock kind '{k}'";
            if (spec == null || spec.kind != k) return $"S12a: spec kind mismatch for '{k}'";
            if (spec.defaultSize.x <= 0f || spec.defaultSize.y <= 0f) return $"S12a: defaultSize non-positive for '{k}'";
            if (spec.minSize.x <= 0f || spec.minSize.y <= 0f) return $"S12a: minSize non-positive for '{k}'";
            if (spec.defaultSize.x < spec.minSize.x || spec.defaultSize.y < spec.minSize.y)
                return $"S12a: defaultSize < minSize for '{k}'";
        }

        // (b) the pre-existing editor/order kinds STILL resolve (additive, never regressed).
        if (!catalog.TryGet(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, out _)) return "S12b: strategy_editor regressed";
        if (!catalog.TryGet(FloatingWindowCatalog.KIND_ORDER, out _)) return "S12b: order regressed";

        // (c) unknown-kind tolerance is unchanged (forward-evolution discipline, findings 0008 §3).
        if (catalog.TryGet("future_unknown", out _)) return "S12c: unknown-kind tolerance broke";
        if (catalog.Contains("future_unknown")) return "S12c: unknown-kind tolerance broke (Contains)";
        return null;
    }

    // ---- 13. DockDefaultPlacement pure arithmetic ----
    // Covers: DOCK-02 — #99 Slice 2 / findings 0075 §4 (ceil(√n) grid in absolute canvas-logical coords,
    // row-major slot 0=top-left, y up-positive rows, no overlap, n=0 empty).
    static string Section13_DockDefaultPlacementArithmetic()
    {
        // (a) n=0 / negative -> empty list, no allocations beyond the list itself.
        var none = DockDefaultPlacement.ComputeRects(0);
        if (none.Count != 0) return $"S13a: n=0 yielded {none.Count} rects";
        var neg = DockDefaultPlacement.ComputeRects(-3);
        if (neg.Count != 0) return $"S13a: negative n yielded {neg.Count} rects";

        // (b) n=5 in a 1200×640 box (cols=3, rows=2) with 12px gap: cell = ((1200-24)/3, (640-12)/2) =
        // (392, 314). slot 0 at top-left of the centred box: (-600, +320). row 0 = top.
        Vector2 box = DockDefaultPlacement.DefaultBoxSize;
        Vector2 anchor = DockDefaultPlacement.CentredAnchorTopLeft(box);
        Vector2 gap = DockDefaultPlacement.DefaultGap;
        if (!Approx2(anchor, new Vector2(-600f, 320f))) return $"S13b: anchor {anchor} != (-600, 320)";
        var five = DockDefaultPlacement.ComputeRects(5, anchor, box, gap);
        if (five.Count != 5) return $"S13b: expected 5 rects, got {five.Count}";

        // slot 0 = top-left = the anchor verbatim.
        if (!Approx2(five[0].topLeft, anchor)) return $"S13b: slot 0 topLeft {five[0].topLeft} != anchor {anchor}";
        // all rects share the same cell size.
        float cellW = (box.x - 2 * gap.x) / 3f;
        float cellH = (box.y - 1 * gap.y) / 2f;
        foreach (var r in five)
        {
            if (!Approx2(r.size, new Vector2(cellW, cellH))) return $"S13b: cell size {r.size} != ({cellW},{cellH})";
        }
        // slot 2 = top-right; slot 3 = bottom-left (col 0, row 1); slot 4 below slot 1.
        if (!Approx(five[2].topLeft.x, anchor.x + 2 * (cellW + gap.x))) return $"S13b: slot 2 x wrong (got {five[2].topLeft.x})";
        if (!Approx(five[2].topLeft.y, anchor.y)) return $"S13b: slot 2 y not on row 0 (got {five[2].topLeft.y})";
        if (!Approx(five[3].topLeft.x, anchor.x)) return $"S13b: slot 3 not in column 0 (got {five[3].topLeft.x})";
        if (!Approx(five[3].topLeft.y, anchor.y - (cellH + gap.y))) return $"S13b: slot 3 y not below row 0 (got {five[3].topLeft.y})";
        if (!Approx(five[4].topLeft.x, five[1].topLeft.x)) return "S13b: slot 4 not under slot 1 (row-major broke)";

        // (c) no overlap between any pair of rects (gaps strictly separate the columns and rows).
        for (int i = 0; i < five.Count; i++)
            for (int j = i + 1; j < five.Count; j++)
            {
                var a = five[i]; var b = five[j];
                bool xOverlap = a.Left < b.Right && b.Left < a.Right;
                bool yOverlap = a.Bottom < b.Top && b.Bottom < a.Top;
                if (xOverlap && yOverlap) return $"S13c: rects {i} and {j} overlap";
            }

        // (d) n=4 deals 2×2; n=1 deals 1×1 (the whole box).
        var four = DockDefaultPlacement.ComputeRects(4, anchor, box, gap);
        if (four.Count != 4) return $"S13d: n=4 yielded {four.Count}";
        if (!Approx(four[3].topLeft.x, anchor.x + (four[0].size.x + gap.x))) return "S13d: n=4 slot 3 not bottom-right column";
        var one = DockDefaultPlacement.ComputeRects(1, anchor, box, gap);
        if (one.Count != 1) return $"S13d: n=1 yielded {one.Count}";
        if (!Approx2(one[0].size, box)) return $"S13d: n=1 cell did not span the whole box (got {one[0].size})";
        return null;
    }

    // ---- 14. DockSnapPlacement pure arithmetic (flush adjacency + overflow cascade) ----
    // Covers: DOCK-03 — #101 Slice 1 / findings 0078 §1 (flush-adjacent placement: search order
    // right→down→left→up, perpendicular-edge alignment, STRICT non-overlap selection, size verbatim,
    // gap=0 flush, all-edges-blocked → deterministic diagonal cascade). PURE — no controller / root.
    static string Section14_DockSnapPlacementArithmetic()
    {
        // A 200×100 target at the origin (top-left pivot, y up-positive). Edges: Left=0, Right=200,
        // Top=0, Bottom=-100. A new window is 50×40 unless noted.
        var target = new FloatingWindowMath.DockRect(0, 0, 200, 100);
        Vector2 sz = new Vector2(50, 40);
        var empty = new List<FloatingWindowMath.DockRect>();

        // (a) FLUSH RIGHT into empty space: first edge in the search order (right) is free → land flush
        // right (left edge = target.Right = 200) and ALIGNED on the top edge (y = target.Top = 0).
        Vector2 pRight = DockSnapPlacement.PlaceAdjacent(target, sz, empty, 0f);
        if (!Approx2(pRight, new Vector2(200f, 0f))) return $"S14a: flush-right expected (200,0), got {pRight}";

        // (b) LARGE-WINDOW CASCADE escapes (guards the cascade BOUND, not just its direction): four
        // chart-sized (520×360) blockers flush on all edges of a 520×360 target. The overflow cascade must
        // step until genuinely clear — a guard of others.Count(=4) × 30px would stop at (640,-120), still
        // deep inside the 520-wide right blocker, so assert the returned rect overlaps NONE of them.
        var bigTarget = new FloatingWindowMath.DockRect(0, 0, 520, 360);
        Vector2 bigSize = new Vector2(520, 360);
        var bigBlockers = new[]
        {
            new FloatingWindowMath.DockRect(520, 0, 520, 360),     // right
            new FloatingWindowMath.DockRect(0, -360, 520, 360),    // down
            new FloatingWindowMath.DockRect(-520, 0, 520, 360),    // left
            new FloatingWindowMath.DockRect(0, 360, 520, 360),     // up
        };
        Vector2 pBig = DockSnapPlacement.PlaceAdjacent(bigTarget, bigSize, bigBlockers, 0f);
        foreach (var b in bigBlockers)
        {
            var r = new FloatingWindowMath.DockRect(pBig, bigSize);
            if (r.Left < b.Right && b.Left < r.Right && r.Bottom < b.Top && b.Bottom < r.Top)
                return $"S14b: large-window cascade did not escape — overlaps a 520-wide blocker (got {pBig})";
        }

        // (c) RIGHT BLOCKED → DOWN: a blocker covering the right slot forces the 2nd edge (down). Down =
        // flush below (top = target.Bottom = -100) ALIGNED on the left edge (x = target.Left = 0).
        var blockRight = new FloatingWindowMath.DockRect(200, 0, 50, 40);   // exactly the right candidate
        Vector2 pDown = DockSnapPlacement.PlaceAdjacent(target, sz, new[] { blockRight }, 0f);
        if (!Approx2(pDown, new Vector2(0f, -100f))) return $"S14c: right-blocked should fall to DOWN (0,-100), got {pDown}";

        // (d) RIGHT + DOWN BLOCKED → LEFT: right candidate occupied AND down candidate occupied. Left =
        // right edge flush to target.Left (top-left.x = target.Left - newSize.x = -50), aligned top (y=0).
        var blockDown = new FloatingWindowMath.DockRect(0, -100, 50, 40);   // exactly the down candidate
        Vector2 pLeft = DockSnapPlacement.PlaceAdjacent(target, sz, new[] { blockRight, blockDown }, 0f);
        if (!Approx2(pLeft, new Vector2(-50f, 0f))) return $"S14d: right+down blocked should fall to LEFT (-50,0), got {pLeft}";

        // (e) RIGHT + DOWN + LEFT BLOCKED → UP: only the up edge remains. Up = bottom edge flush to
        // target.Top (top-left.y = target.Top + newSize.y = 40), aligned left (x = target.Left = 0).
        var blockLeft = new FloatingWindowMath.DockRect(-50, 0, 50, 40);   // exactly the left candidate
        Vector2 pUp = DockSnapPlacement.PlaceAdjacent(target, sz, new[] { blockRight, blockDown, blockLeft }, 0f);
        if (!Approx2(pUp, new Vector2(0f, 40f))) return $"S14e: right+down+left blocked should fall to UP (0,40), got {pUp}";

        // (f) ALL FOUR EDGES BLOCKED → diagonal CASCADE off the right candidate. With all four flush
        // slots filled, step (+CascadeStep, -CascadeStep) until clear. The right blocker spans x∈[200,250],
        // y∈[0,-40], so the 1st step (230,-30) still clips it (a 50×40 window only half-clears in one 30px
        // diagonal) — two steps to (260,-60) are needed. The exact count is incidental; the contract is
        // "deterministic and non-overlapping", asserted both by the value and the overlap sweep below.
        var blockUp = new FloatingWindowMath.DockRect(0, 40, 50, 40);   // exactly the up candidate
        Vector2 pCascade = DockSnapPlacement.PlaceAdjacent(
            target, sz, new[] { blockRight, blockDown, blockLeft, blockUp }, 0f);
        float step = DockSnapPlacement.CascadeStep;
        if (!Approx2(pCascade, new Vector2(200f + 2f * step, -2f * step)))
            return $"S14f: all-blocked cascade expected ({200f + 2f * step},{-2f * step}), got {pCascade}";
        // …and the cascaded result must itself be non-overlapping against every blocker (determinism +
        // the cascade actually escapes, not just "returns something").
        foreach (var b in new[] { blockRight, blockDown, blockLeft, blockUp })
        {
            var r = new FloatingWindowMath.DockRect(pCascade, sz);
            bool xo = r.Left < b.Right && b.Left < r.Right;
            bool yo = r.Bottom < b.Top && b.Bottom < r.Top;
            if (xo && yo) return "S14f: cascaded placement still overlaps a blocker";
        }

        // (g) STRICT non-overlap (touching is NOT overlap): a blocker whose LEFT edge sits exactly at the
        // right candidate's RIGHT edge merely KISSES it → the right edge is still considered FREE, so the
        // right candidate wins (no spurious fall-through to DOWN). target.Right=200, newSize.x=50 → right
        // candidate spans x∈[200,250]; a blocker at x=250 touches but does not overlap.
        var kissRight = new FloatingWindowMath.DockRect(250, 0, 50, 40);
        Vector2 pKiss = DockSnapPlacement.PlaceAdjacent(target, sz, new[] { kissRight }, 0f);
        if (!Approx2(pKiss, new Vector2(200f, 0f))) return $"S14g: touching edge wrongly treated as overlap (got {pKiss})";

        // (h) GAP applied at the seam: a non-zero gap separates the flush edge by exactly `gap` (the
        // production caller passes 0, but the helper must honour a positive gap for reuse/HITL tuning).
        Vector2 pGap = DockSnapPlacement.PlaceAdjacent(target, sz, empty, 12f);
        if (!Approx2(pGap, new Vector2(212f, 0f))) return $"S14h: gap=12 flush-right expected (212,0), got {pGap}";

        return null;
    }

    // ---- 15. focus-adjacent fixed-size dock spawn (SpawnDockedToFocus + NoteUserFocus wiring) ----
    // Covers: DOCK-04 — #101 Slice 2 / findings 0078 §2/§3 (a new dock window spawns at the SPEC-FIXED
    // size — INDEPENDENT of the live count, the #99 bug — and snaps flush to the USER-focused window;
    // programmatic BringToFront does NOT record focus; no focus / closed focus → nearest-visible
    // fallback; self/dup/unknown guards). Drives the controller root-free (no Python, no BackcastRoot).
    static string Section15_FocusAdjacentDockSpawn(List<GameObject> spawned)
    {
        // The chart spec size is the single source of truth (catalog default) — assert against it rather
        // than a literal so a spec tuning doesn't silently make this section lie.
        var catalog = FloatingWindowCatalog.Default();
        if (!catalog.TryGet(FloatingWindowCatalog.KIND_CHART, out var chartSpec)) return "S15: catalog missing chart kind";
        Vector2 chartSize = chartSpec.defaultSize;
        if (chartSize.x <= 0f || chartSize.y <= 0f) return $"S15: chart defaultSize non-positive ({chartSize})";

        // (a) FOCUS WINS over NEAREST: 'A' is focused; 'B' is placed so its centre is NEARER the anchor
        // than A's (B centre (1140,910) dist² 31.6M < A centre (140,-90) dist² 49.5M to anchor (5000,5000)).
        // So if the focus branch were removed, the nearest-fallback would pick B — the chart snapping FLUSH
        // to A instead proves focus beats nearest. A.Right=280, A.Top=0 → chart top-left (280,0), size fixed.
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            RectTransform a = c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0, 0, 280, 180, true);
            RectTransform b = c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "B", 1000, 1000, 280, 180, true);
            if (a == null || b == null) return "S15a: precondition spawn returned null";
            c.NoteUserFocus("A");
            RectTransform chart = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:1", new Vector2(5000, 5000), true);
            if (chart == null) return "S15a: SpawnDockedToFocus returned null for a known kind";
            if (!Approx2(chart.sizeDelta, chartSize)) return $"S15a: chart size {chart.sizeDelta} != spec-fixed {chartSize}";
            if (!Approx2(chart.anchoredPosition, new Vector2(280f, 0f)))
                return $"S15a: chart not flush-right of the FOCUSED window (got {chart.anchoredPosition}, expected (280,0))";
        }

        // (b) SIZE IS COUNT-INDEPENDENT (the #99 regression): spawn THREE charts against the same focused
        // window; every one is the spec-fixed size regardless of how many already exist. (The #99 bug
        // sized each to ComputeRects(N) so chart 2/3 would shrink — here all three stay chartSize.)
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0, 0, 280, 180, true);
            c.NoteUserFocus("A");
            for (int i = 1; i <= 3; i++)
            {
                RectTransform chart = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, $"chart:{i}", new Vector2(0, 0), true);
                if (chart == null) return $"S15b: chart {i} spawn returned null";
                if (!Approx2(chart.sizeDelta, chartSize))
                    return $"S15b: chart {i} size {chart.sizeDelta} != {chartSize} — size DEPENDS on count (the #99 bug)";
            }
        }

        // (c) PROGRAMMATIC BringToFront does NOT record focus: focus 'A', then BringToFront('B')
        // programmatically (a non-user raise, e.g. a layout restore). The chart must STILL snap to A —
        // if BringToFront had forged focus, the chart would snap to B at (2280,0) instead.
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0, 0, 280, 180, true);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "B", 2000, 0, 280, 180, true);
            c.NoteUserFocus("A");
            c.BringToFront("B");   // programmatic raise — must NOT change the focus target
            RectTransform chart = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:c", new Vector2(9000, 9000), true);
            if (chart == null) return "S15c: spawn returned null";
            if (!Approx2(chart.anchoredPosition, new Vector2(280f, 0f)))
                return $"S15c: programmatic BringToFront forged focus (chart at {chart.anchoredPosition}, expected flush to A (280,0))";
        }

        // (d) NO FOCUS → NEAREST-VISIBLE fallback: no NoteUserFocus at all. 'A' is far, 'B' is centred on
        // the anchor → the chart snaps to B. B top-left (-140,90), size 280×180 → centre (0,0) = anchor;
        // B.Right=140, B.Top=90 → flush-right slot (140,90).
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 5000, 5000, 280, 180, true);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "B", -140, 90, 280, 180, true);
            c.NoteUserFocus("ghost");   // unknown id → MUST NOT poison the focus target (no-op)
            RectTransform chart = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:d", new Vector2(0, 0), true);
            if (chart == null) return "S15d: spawn returned null";
            if (!Approx2(chart.anchoredPosition, new Vector2(140f, 90f)))
                return $"S15d: no-focus fallback did not pick the nearest window B (chart at {chart.anchoredPosition}, expected (140,90))";
        }

        // (e) CLOSED focus target → fallback: focus 'A', then Close('A'). The next chart must fall back to
        // the nearest visible window (B), proving a stale focus target is dropped, not snapped to a ghost.
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0, 0, 280, 180, true);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "B", -140, 90, 280, 180, true);
            c.NoteUserFocus("A");
            if (!c.Close("A")) return "S15e: precondition Close(A) returned false";
            RectTransform chart = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:e", new Vector2(0, 0), true);
            if (chart == null) return "S15e: spawn returned null";
            if (!Approx2(chart.anchoredPosition, new Vector2(140f, 90f)))
                return $"S15e: closed focus target not dropped (chart at {chart.anchoredPosition}, expected fallback to B (140,90))";
        }

        // (f) GUARDS: duplicate id → returns the existing window (no second spawn); unknown kind → null.
        {
            BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
            var c = MakeController(spawned, layer);
            c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0, 0, 280, 180, true);
            RectTransform first = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:f", new Vector2(0, 0), true);
            if (first == null) return "S15f: first chart spawn returned null";
            int before = c.Count;
            RectTransform dup = c.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:f", new Vector2(0, 0), true);
            if (dup != first) return "S15f: duplicate id did not return the first window";
            if (c.Count != before) return $"S15f: duplicate id spawned a second window (count {before}→{c.Count})";
            if (c.SpawnDockedToFocus("ghost_kind", "ghostwin", new Vector2(0, 0), true) != null)
                return "S15f: unknown kind did not return null";
            if (c.Has("ghostwin")) return "S15f: unknown kind leaked a registered window";
        }

        return null;
    }

    // ---- 16. two depth planes: parallax speed difference (1.0× back vs 1.2× front) + back sibling ----
    // Covers: PLANE-01 — #103 / ADR-0018 / findings 0075 §10 (the dock plane rides Content at 1.0× while the
    // floating plane rides it at the 1.2× parallax factor, so a pan opens a SPEED gap = the restored #99
    // depth; DockLayer is the earlier/backmost sibling so it always draws behind). Mirrors S3's child-follow
    // engine==math cross-check, now across BOTH planes.
    static string Section16_TwoPlaneParallaxDepth(List<GameObject> spawned)
    {
        const float FRONT = 1.2f;
        BuildTwoPlaneStack(spawned, FRONT, out RectTransform viewport, out _,
                           out RectTransform dockLayer, out RectTransform floatingLayer, out InfiniteCanvasController canvas);

        // (a) back sibling: DockLayer draws BEHIND FloatingWindowLayer (earlier sibling index under Content).
        if (dockLayer.GetSiblingIndex() >= floatingLayer.GetSiblingIndex())
            return $"S16a: DockLayer sibling {dockLayer.GetSiblingIndex()} not behind FloatingWindowLayer {floatingLayer.GetSiblingIndex()}";

        // (b) unit parallax math: the back plane (factor 1.0) gets ZERO offset (rides Content 1×); the front
        // plane (1.2) gets a NON-zero offset = (1-1.2)·pan. Equal factors would collapse the gap (the #99 bug).
        var view = new CanvasView(60f, -40f, 1.5f);
        if (CanvasViewMath.ParallaxLayerOffset(view, 1.0f) != Vector2.zero)
            return "S16b: back plane (factor 1.0) must have ZERO parallax offset";
        Vector2 frontOffset = CanvasViewMath.ParallaxLayerOffset(view, FRONT);
        Vector2 expectedFront = new Vector2((1f - FRONT) * view.panX, (1f - FRONT) * view.panY);
        if (!Approx2(frontOffset, expectedFront)) return $"S16b: front offset {frontOffset} != {expectedFront}";
        if (frontOffset == Vector2.zero) return "S16b: front plane offset is zero — planes coplanar, no depth";

        // (c) rendered composition: a window at the SAME logical top-left on each plane renders at DIFFERENT
        // viewport positions once panned — the dock window tracks pan 1×, the floating window 1.2×.
        var dockCtrl = MakeController(spawned, dockLayer);
        var floatCtrl = MakeController(spawned, floatingLayer);
        Vector2 L = new Vector2(50f, -30f);
        RectTransform dockWin = dockCtrl.Spawn(FloatingWindowCatalog.KIND_CHART, "chart:x", L.x, L.y, 520, 360, true);
        RectTransform floatWin = floatCtrl.Spawn(FloatingWindowCatalog.KIND_ORDER, "order", L.x, L.y, 360, 300, true);
        if (dockWin == null || floatWin == null) return "S16c: precondition spawn returned null";

        canvas.ApplyView(view);

        // dock window: rides Content at 1× (no layer offset) → viewport == pure logical-to-viewport of L.
        Vector2 dockMeasured = viewport.InverseTransformPoint(dockWin.position);
        Vector2 dockPredicted = CanvasViewMath.LogicalToViewport(L, view);
        if (!Approx2(dockMeasured, dockPredicted))
            return $"S16c: dock window not 1× (engine {dockMeasured}, math {dockPredicted})";

        // floating window: rides Content PLUS the 1.2× parallax offset → shifted by zoom·offset from the dock.
        Vector2 floatMeasured = viewport.InverseTransformPoint(floatWin.position);
        Vector2 floatPredicted = dockPredicted + view.zoom * frontOffset;
        if (!Approx2(floatMeasured, floatPredicted))
            return $"S16c: floating window not 1.2× (engine {floatMeasured}, math {floatPredicted})";

        // the depth gap is REAL and non-zero (delete-the-production-logic litmus: equal factors → gap 0).
        if (Approx2(dockMeasured, floatMeasured))
            return "S16c: dock and floating windows render at the SAME position — depth gap vanished";
        return null;
    }

    // ---- 17. cross-plane snap ban + same-plane snap + dock focus stays in plane ----
    // Covers: PLANE-02 — #103 / ADR-0018 / findings 0075 §10 (snap母集合 is per-controller, so a back-plane
    // window never snaps to a front-plane window; same-plane snap is unchanged; a dock spawn's focus target
    // resolves WITHIN the back plane). The kind→plane routing predicate (DockShape.IsDockKind) is the same one
    // RestoreFloating uses, so the two test controllers mirror production's plane assignment.
    static string Section17_CrossPlaneSnapBan(List<GameObject> spawned)
    {
        // routing-predicate parity (mirrors RestoreFloating: IsDockKind ? _dockWindows : _windows).
        if (!DockShape.IsDockKind(FloatingWindowCatalog.KIND_CHART)) return "S17: chart must route to the dock plane";
        if (!DockShape.IsDockKind(FloatingWindowCatalog.KIND_STARTUP)) return "S17: startup must route to the dock plane";
        if (DockShape.IsDockKind(FloatingWindowCatalog.KIND_ORDER)) return "S17: order must route to the front plane";
        if (DockShape.IsDockKind(FloatingWindowCatalog.KIND_STRATEGY_EDITOR)) return "S17: strategy_editor must route to the front plane";

        BuildTwoPlaneStack(spawned, 1.2f, out _, out _, out RectTransform dockLayer, out RectTransform floatLayer, out _);
        var dockCtrl = MakeController(spawned, dockLayer);
        var floatCtrl = MakeController(spawned, floatLayer);

        // (a) CROSS-PLANE BAN: a dock window 5px shy of flush-right of a FRONT-plane window. Snapping the dock
        // window must NOT move it — the front window is in the OTHER controller, invisible to the dock snap母集合.
        // (If the two planes shared one controller — the #99 collapse — the dock window would snap 5px here.)
        RectTransform dockWin = dockCtrl.Spawn(FloatingWindowCatalog.KIND_CHART, "chart:1", 100, 0, 280, 180, true);
        RectTransform frontWin = floatCtrl.Spawn(FloatingWindowCatalog.KIND_ORDER, "order", 385, 0, 280, 180, true);
        if (dockWin == null || frontWin == null) return "S17a: precondition spawn returned null";
        Vector2 applied = dockCtrl.SnapOnRelease("chart:1");
        if (applied != Vector2.zero) return $"S17a: dock window snapped across planes (offset {applied}) — cross-plane snap must be banned";
        if (!Approx2(dockWin.anchoredPosition, new Vector2(100f, 0f))) return "S17a: dock window moved despite the neighbour being on another plane";

        // (b) SAME-PLANE SNAP still works: add a SECOND dock window within threshold on the SAME controller.
        RectTransform dockNbr = dockCtrl.Spawn(FloatingWindowCatalog.KIND_BUYING_POWER, "buying_power", 385, 0, 280, 180, true);
        if (dockNbr == null) return "S17b: precondition spawn returned null";
        Vector2 sameApplied = dockCtrl.SnapOnRelease("chart:1");
        if (!Approx(sameApplied.x, 5f) || !Approx(sameApplied.y, 0f)) return $"S17b: same-plane snap offset {sameApplied} expected (5,0)";
        if (!Approx2(dockWin.anchoredPosition, new Vector2(105f, 0f))) return "S17b: same-plane snap did not apply";

        // (c) DOCK FOCUS STAYS IN PLANE: focus a front-plane window AND a back-plane window. A dock
        // SpawnDockedToFocus must snap to the BACK-plane focus target, never the front one (separate focus
        // registries). buying_power top-left (385,0) size 280×180 → flush-right slot top-left (665,0).
        floatCtrl.NoteUserFocus("order");          // front-plane focus — must be invisible to the dock spawn
        dockCtrl.NoteUserFocus("buying_power");     // back-plane focus target
        RectTransform chart2 = dockCtrl.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:2", new Vector2(99999, 99999), true);
        if (chart2 == null) return "S17c: dock spawn returned null";
        if (!Approx2(chart2.anchoredPosition, new Vector2(665f, 0f)))
            return $"S17c: dock spawn did not snap WITHIN the back plane (got {chart2.anchoredPosition}, expected (665,0))";
        return null;
    }

    // ---- 18. two-controller persist round-trip with kind→plane routing ----
    // Covers: PLANE-03 — #103 / ADR-0018 / findings 0075 §10 (CaptureLayout unions BOTH controllers into one
    // floatingWindows list; RestoreFloating routes each window back to its plane by DockShape.IsDockKind, so a
    // chart restores onto the back plane and the order onto the front plane — the depth survives a disk
    // round-trip with NO schema field added). Non-vacuous: a broken predicate parents a window on the wrong layer.
    static string Section18_TwoControllerPersistRoundTrip(List<GameObject> spawned)
    {
        // --- live: dock kinds on the back controller + order on the front controller ---
        BuildTwoPlaneStack(spawned, 1.2f, out _, out _, out RectTransform dockLayer, out RectTransform floatLayer, out _);
        var dockCtrl = MakeController(spawned, dockLayer);
        var floatCtrl = MakeController(spawned, floatLayer);
        dockCtrl.Spawn(FloatingWindowCatalog.KIND_CHART, "chart:7203", 10.5f, -20.5f, 520.5f, 360.5f, true);
        dockCtrl.Spawn(FloatingWindowCatalog.KIND_BUYING_POWER, "buying_power", -100.5f, 50.5f, 340.5f, 140.5f, true);
        dockCtrl.Spawn(FloatingWindowCatalog.KIND_STARTUP, "startup", -300.5f, 200.5f, 380.5f, 260.5f, false);   // hidden (Live shape)
        floatCtrl.Spawn(FloatingWindowCatalog.KIND_ORDER, "order", 40.5f, -40.5f, 360.5f, 300.5f, true);

        // --- capture: UNION both controllers into one list (mirrors CaptureLayout) ---
        var union = new List<FloatingWindowLayout>();
        foreach (var w in floatCtrl.Capture().floatingWindows)
            if (w != null && w.kind != FloatingWindowCatalog.KIND_STRATEGY_EDITOR) union.Add(w);
        foreach (var w in dockCtrl.Capture().floatingWindows)
            if (w != null) union.Add(w);
        if (union.Count != 4) return $"S18: captured {union.Count} windows, expected 4 (3 dock + 1 front)";

        // --- disk round-trip (prove it survives serialize/deserialize verbatim, single floatingWindows list) ---
        LayoutStore.Save(DocOf(union.ToArray()), TempPath);
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (loaded?.floatingWindows == null || loaded.floatingWindows.Count != 4) return "S18: disk round-trip lost windows";

        // --- restore: route each window to its plane by DockShape.IsDockKind (mirrors RestoreFloating) ---
        BuildTwoPlaneStack(spawned, 1.2f, out _, out _, out RectTransform dockLayer2, out RectTransform floatLayer2, out _);
        var dockCtrl2 = MakeController(spawned, dockLayer2);
        var floatCtrl2 = MakeController(spawned, floatLayer2);
        foreach (var w in loaded.floatingWindows)
        {
            if (w == null || string.IsNullOrEmpty(w.id)) continue;
            if (w.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR) continue;
            var ctrl = DockShape.IsDockKind(w.kind) ? dockCtrl2 : floatCtrl2;
            if (ctrl.Has(w.id)) ctrl.ApplyGeometry(w);
            else ctrl.Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible);
            ctrl.BringToFront(w.id);
        }

        // --- assert each window landed on the CORRECT plane (parented under the right layer) ---
        if (dockCtrl2.Count != 3) return $"S18: back plane restored {dockCtrl2.Count} windows, expected 3";
        if (floatCtrl2.Count != 1) return $"S18: front plane restored {floatCtrl2.Count} windows, expected 1";

        RectTransform chart = dockCtrl2.RectOf("chart:7203");
        if (chart == null || chart.parent != dockLayer2) return "S18: chart did not restore onto the BACK plane";
        if (!Approx2(chart.anchoredPosition, new Vector2(10.5f, -20.5f))) return $"S18: chart geometry lost ({chart.anchoredPosition})";
        if (dockCtrl2.RectOf("buying_power")?.parent != dockLayer2) return "S18: buying_power did not restore onto the BACK plane";
        RectTransform startup = dockCtrl2.RectOf("startup");
        if (startup == null || startup.parent != dockLayer2) return "S18: startup did not restore onto the BACK plane";
        if (startup.gameObject.activeSelf) return "S18: hidden startup (visible=false) restored as active";

        RectTransform order = floatCtrl2.RectOf("order");
        if (order == null || order.parent != floatLayer2) return "S18: order did not restore onto the FRONT plane";
        // non-vacuous: routing must NOT cross — a broken IsDockKind would leak a window onto the wrong plane.
        if (dockCtrl2.Has("order")) return "S18: order leaked onto the BACK plane (routing broke)";
        if (floatCtrl2.Has("chart:7203")) return "S18: chart leaked onto the FRONT plane (routing broke)";
        return null;
    }

    // ---- 19. PRODUCTION scene wiring: DockLayer is the back sibling + serialized refs point to the planes ----
    // Covers: PLANE-01 (production half) — #103 / ADR-0018 / findings 0075 §10. S16a's back-sibling check is a
    // tautology over the TEST's own layer-creation order; this section loads the REAL authored scene and pins
    // the scene-builder output: DockLayer and FloatingWindowLayer are both Content children, DockLayer is the
    // EARLIER (backmost) sibling, and BackcastWorkspaceRoot's _dockLayer/_floatingLayer serialized refs point to
    // them. This is the ONE section that loads the real scene (the rest are root-free) — it runs LAST so its
    // OpenScene(Single) does not disturb the synthetic-stack sections (their GameObjects fake-null on unload and
    // the finally's null-guard skips them). No PlayMode / no Python: editmode scene load reads authored structure.
    // ---- 20. #104 Slice A: groupId schema additive (Capture/Apply pass-through + round-trip + back-compat) ----
    // Covers: GROUP-01 (FloatingWindowLayout.groupId additive・Spawn=null 不変・Capture pass-through・on-disk text 証明・
    //         fresh Load+Apply 再現・back-compat null・Apply 更新側 pass-through)
    //
    // Slice A is the schema foundation: groupId is persisted on every FloatingWindowLayout, round-trips
    // through disk, and is restored verbatim onto a FRESH controller. The attach commit (groupId becomes
    // non-null) is Slice B's job and lives in Section23 — here we drive groupId entirely from the
    // persistence path and via SetGroupId (the controller's group-lifecycle write seam). This section also
    // pins the "programmatic Spawn yields groupId=null" invariant findings 0082 §10 fixes — the only way
    // a non-null groupId enters the system is the user's drag-release (Slice B) or a persisted-doc restore.
    static string Section20_GroupIdSchemaRoundTrip(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var controller = MakeController(spawned, layer);

        // (a) Programmatic Spawn never mints a group: ADR-0019 D8 / findings 0082 §10 invariant.
        var solo = controller.Spawn(FloatingWindowCatalog.KIND_ORDER, "order:solo", 0, 0, 360, 300, true);
        if (solo == null) return "S20a: precondition Spawn returned null";
        if (controller.GroupIdOf("order:solo") != null)
            return $"S20a: Spawn minted a non-null groupId ({controller.GroupIdOf("order:solo")}) — attach must come from a user drag-release";

        // (b) SetGroupId (the lifecycle write seam) takes; GroupIdOf reflects it.
        const string GRP = "grp_a1b2c3d4e5f6470b9c2d1a3f4b5c6d7e";   // findings 0082 §1 GUID shape (hex32, hyphenless)
        controller.SetGroupId("order:solo", GRP);
        if (controller.GroupIdOf("order:solo") != GRP)
            return $"S20b: SetGroupId did not stick (got {controller.GroupIdOf("order:solo")})";

        // (c) Capture carries the groupId verbatim onto the FloatingWindowLayout.
        var cap = controller.Capture();
        var captured = cap.FindWindow("order:solo");
        if (captured == null) return "S20c: Capture missing the entry";
        if (captured.groupId != GRP) return $"S20c: Capture lost groupId (got {captured.groupId})";

        // (d) Mutated doc with TWO members sharing a group + one ungrouped + an Order with a distinct
        // group: a non-vacuous schema round-trip. Mutated != Default; on-disk JSON literally carries the
        // groupId field; fresh Load preserves it byte-equivalent; fresh controller Apply restores it.
        const string GRP2 = "grp_11112222333344445555666677778888";
        var mutated = DocOf(
            WG("strategy_editor:region_001", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 10, -10, 520, 380, 0, true,  GRP),
            WG("strategy_editor:region_002", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 30, -20, 520, 380, 1, true,  GRP),
            W ("strategy_editor:lone",       FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 50, -30, 520, 380, 2, true),       // groupId=null
            WG("order",                      FloatingWindowCatalog.KIND_ORDER,           70, -40, 360, 300, 3, true,  GRP2));
        if (LayoutDocument.StructurallyEqual(mutated, LayoutDocument.Default(), EPS))
            return "S20d: mutated doc equals Default (mutation no-op)";

        LayoutStore.Save(mutated, TempPath);
        if (!File.Exists(TempPath)) return "S20d: Save did not create the sidecar";
        string compact = File.ReadAllText(TempPath).Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        if (!compact.Contains($"\"groupId\":\"{GRP}\""))
            return "S20d: on-disk JSON missing groupId (the additive schema field did not serialize)";
        if (!compact.Contains($"\"groupId\":\"{GRP2}\""))
            return "S20d: on-disk JSON missing the second groupId";

        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (!LayoutDocument.StructurallyEqual(loaded, mutated, EPS))
            return "S20d: loaded != mutated (groupId or another field lost on round-trip)";
        if (loaded.FindWindow("strategy_editor:region_001").groupId != GRP) return "S20d: loaded.region_001 groupId drift";
        if (loaded.FindWindow("strategy_editor:region_002").groupId != GRP) return "S20d: loaded.region_002 groupId drift";
        if (loaded.FindWindow("strategy_editor:lone").groupId != null)      return "S20d: loaded.lone groupId not null";
        if (loaded.FindWindow("order").groupId != GRP2)                     return "S20d: loaded.order groupId drift";

        // Fresh controller restore: groupId reaches each live entry via Apply()'s pass-through (the
        // spawn branch for region_002/lone/order, the update branch for region_001 — see (g)).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var fresh = MakeController(spawned, layer2);
        fresh.Apply(loaded);
        if (fresh.Count != 4) return $"S20d: restore expected 4 windows, got {fresh.Count}";
        if (fresh.GroupIdOf("strategy_editor:region_001") != GRP) return "S20d: fresh region_001 groupId not restored";
        if (fresh.GroupIdOf("strategy_editor:region_002") != GRP) return "S20d: fresh region_002 groupId not restored";
        if (fresh.GroupIdOf("strategy_editor:lone") != null)      return "S20d: fresh lone groupId not null on restore";
        if (fresh.GroupIdOf("order") != GRP2)                     return "S20d: fresh order groupId not restored";

        // (e) Back-compat: an old sidecar with no groupId field on any window loads with groupId=null
        // for every entry (forward-evolution tolerance — findings 0008 §3, ADR-0019 §1 D8 / findings 0082 §11).
        string oldJson = "{\"version\":1,\"floatingWindows\":[{\"id\":\"x\",\"kind\":\"order\",\"x\":0,\"y\":0," +
                         "\"w\":300,\"h\":200,\"zOrder\":0,\"visible\":true}]}";
        LayoutDocument old = LayoutStore.LoadFromJson(oldJson);
        var oldWin = old.FindWindow("x");
        if (oldWin == null) return "S20e: legacy sidecar dropped the entry";
        if (oldWin.groupId != null)
            return $"S20e: legacy sidecar entry did not coalesce groupId to null (got {oldWin.groupId})";

        // (f) Apply onto a controller that ALREADY has the entry (adopted scene window scenario) must
        // re-apply the persisted groupId onto the existing live entry — not just the new-spawn branch.
        // Pre-Apply: controller has order:solo as ungrouped. Doc names it under a third group; Apply
        // must update the live groupId verbatim (the adopted-window round-trip).
        const string GRP3 = "grp_99998888777766665555444433332222";
        var update = DocOf(
            WG("order:solo", FloatingWindowCatalog.KIND_ORDER, 0, 0, 360, 300, 0, true, GRP3));
        // Use the SAME controller as (a)/(b)/(c) — it still has order:solo registered (with GRP). The
        // update path of Apply() must overwrite the live groupId with the doc's GRP3.
        controller.Apply(update);
        if (!controller.Has("order:solo")) return "S20f: Apply destroyed the adopted entry it should update in place";
        if (controller.GroupIdOf("order:solo") != GRP3)
            return $"S20f: Apply did not pass groupId onto the EXISTING entry (got {controller.GroupIdOf("order:solo")})";
        return null;
    }

    // ---- 21. #104 Slice B: IsFlushAdjacent pure arithmetic ----
    // Covers: GROUP-02 (flush=辺一致 ε 以内 ∧ 直交軸 overlap > 0 / same-edge align 离散 / corner 接触 overlap=0
    //         / eps 境界・eps<=0 ガード)
    static string Section21_IsFlushAdjacentPure()
    {
        // 100×80 box at (0, 0) (top-left = 0,0; y up-positive ⇒ Top=0, Bottom=-80, Left=0, Right=100).
        var a = new FloatingWindowMath.DockRect(0f, 0f, 100f, 80f);

        // (a) FLUSH RIGHT: b.left ≈ a.right (=100). y-overlap is strict (both span y in [-80,0] roughly).
        var bRight = new FloatingWindowMath.DockRect(100f, 0f, 100f, 80f);
        if (!FloatingWindowMath.IsFlushAdjacent(a, bRight, 1f)) return "S21a: flush-right (a.right==b.left, y-overlap>0) NOT flush";

        // (b) FLUSH LEFT: b.right ≈ a.left.
        var bLeft = new FloatingWindowMath.DockRect(-100f, 0f, 100f, 80f);
        if (!FloatingWindowMath.IsFlushAdjacent(a, bLeft, 1f)) return "S21b: flush-left NOT flush";

        // (c) FLUSH TOP (a above b): a.bottom == b.top. a sits at y=0..-80; b at y=-80..-160 ⇒ b.top=-80 == a.bottom.
        var bBelow = new FloatingWindowMath.DockRect(0f, -80f, 100f, 80f);
        if (!FloatingWindowMath.IsFlushAdjacent(a, bBelow, 1f)) return "S21c: flush-top (a.bottom==b.top, x-overlap>0) NOT flush";

        // (d) FLUSH BOTTOM (a below b): a.top == b.bottom. Put b ABOVE a (top-left y=80 ⇒ Top=80, Bottom=0).
        var bAbove = new FloatingWindowMath.DockRect(0f, 80f, 100f, 80f);
        if (!FloatingWindowMath.IsFlushAdjacent(a, bAbove, 1f)) return "S21d: flush-bottom NOT flush";

        // (e) SAME-EDGE ALIGN ONLY (left↔left), partners side-by-side with a 20px gap: edges meet eps
        // for x but no horizontal flush (a.right != b.left, etc.), and there's a y-overlap. MUST be false.
        var bGap = new FloatingWindowMath.DockRect(120f, 0f, 100f, 80f);   // a.right=100, b.left=120 ⇒ gap 20 > eps
        if (FloatingWindowMath.IsFlushAdjacent(a, bGap, 1f)) return "S21e: same-edge align with 20px gap reported flush (must be NOT flush)";

        // (f) CORNER-ONLY contact: a.right == b.left ∧ b sits directly below a (a.bottom == b.top), so
        // ONLY the SE corner of a touches the NW corner of b — y-overlap segment for the right edge = 0
        // (single point), x-overlap segment for the bottom edge = 0 (single point). Must NOT be flush.
        var bCorner = new FloatingWindowMath.DockRect(100f, -80f, 100f, 80f);
        if (FloatingWindowMath.IsFlushAdjacent(a, bCorner, 1f)) return "S21f: corner-only contact reported flush (overlap must be >0, not >=0)";

        // (g) eps boundary: 0.5px gap with eps=1 ⇒ flush; 2px gap with eps=1 ⇒ not flush.
        var bNear = new FloatingWindowMath.DockRect(100.5f, 0f, 100f, 80f);
        if (!FloatingWindowMath.IsFlushAdjacent(a, bNear, 1f)) return "S21g: 0.5px gap with eps=1 NOT flush";
        var bFar = new FloatingWindowMath.DockRect(102f, 0f, 100f, 80f);
        if (FloatingWindowMath.IsFlushAdjacent(a, bFar, 1f)) return "S21g: 2px gap with eps=1 reported flush (must be NOT flush)";

        // (h) degenerate eps ⇒ false.
        if (FloatingWindowMath.IsFlushAdjacent(a, bRight, 0f)) return "S21h: eps=0 must be NOT flush (no attach)";
        if (FloatingWindowMath.IsFlushAdjacent(a, bRight, -1f)) return "S21h: eps<0 not guarded";
        if (FloatingWindowMath.IsFlushAdjacent(a, bRight, float.NaN)) return "S21h: eps=NaN not guarded";
        return null;
    }

    // ---- 22. ADR-0024 §5: merge-cascade pure arithmetic (Hakoniwa-priority RETIRED) ----
    // Covers: DRAG-12 (ResolveMergeWinner cascade = size 最大 > 辞書順最小 > 新規 GUID. NO Hakoniwa-priority.)
    static string Section22_MergeCascadePure()
    {
        // (a) NO Hakoniwa-priority: a small (would-be-"core") group does NOT beat a bigger group. With the
        // hasCore field gone, the cascade is pure size-then-dict. A 2-member group lex-late vs a 5-member
        // group lex-early ⇒ the BIGGER (size 5) wins, regardless of any former "core" status.
        var noPrio = new List<FloatingWindowMath.MergeCandidate>
        {
            new FloatingWindowMath.MergeCandidate("grp_zzz", 2),   // smaller, lex-late (was Hakoniwa)
            new FloatingWindowMath.MergeCandidate("grp_aaa", 5),   // bigger, lex-early
        };
        if (FloatingWindowMath.ResolveMergeWinner(noPrio) != "grp_aaa")
            return $"S22a: size-max did not win (Hakoniwa-priority must be retired) — got {FloatingWindowMath.ResolveMergeWinner(noPrio)}";

        // (b) Size-max among 3 groups; ties broken by StringCompareOrdinal min.
        var sizeMax = new List<FloatingWindowMath.MergeCandidate>
        {
            new FloatingWindowMath.MergeCandidate("grp_b", 2),
            new FloatingWindowMath.MergeCandidate("grp_a", 5),    // bigger
            new FloatingWindowMath.MergeCandidate("grp_c", 3),
        };
        if (FloatingWindowMath.ResolveMergeWinner(sizeMax) != "grp_a")
            return "S22b: size-max cascade wrong";

        // (c) Dict tie-break: equal count ⇒ lexicographically-smallest id wins.
        var tie = new List<FloatingWindowMath.MergeCandidate>
        {
            new FloatingWindowMath.MergeCandidate("grp_b", 3),
            new FloatingWindowMath.MergeCandidate("grp_a", 3),    // lex-min, must win
            new FloatingWindowMath.MergeCandidate("grp_c", 3),
        };
        if (FloatingWindowMath.ResolveMergeWinner(tie) != "grp_a")
            return "S22c: dict-min tie-break wrong";

        // (d) All singletons (id=null): cascade returns null ⇒ caller mints a new GUID.
        var singletons = new List<FloatingWindowMath.MergeCandidate>
        {
            new FloatingWindowMath.MergeCandidate(null, 1),
            new FloatingWindowMath.MergeCandidate(null, 1),
        };
        if (FloatingWindowMath.ResolveMergeWinner(singletons) != null)
            return "S22d: all-singleton cascade did not return null (caller mint expected)";

        // (e) Single non-null entry survives over a singleton partner.
        var oneSurvivor = new List<FloatingWindowMath.MergeCandidate>
        {
            new FloatingWindowMath.MergeCandidate(null, 1),
            new FloatingWindowMath.MergeCandidate("grp_only", 2),
        };
        if (FloatingWindowMath.ResolveMergeWinner(oneSurvivor) != "grp_only")
            return "S22e: lone non-null candidate did not survive";

        // (f) null array / empty array → null (caller chooses).
        if (FloatingWindowMath.ResolveMergeWinner(null) != null) return "S22f: null array not null result";
        if (FloatingWindowMath.ResolveMergeWinner(new List<FloatingWindowMath.MergeCandidate>()) != null)
            return "S22f: empty array not null result";
        return null;
    }

    // ---- 23. #104 Slice B: SnapOnRelease flush-attach + Spawn=null invariant (controller wiring) ----
    // Covers: GROUP-04 (SnapOnRelease 後の flush 隣接判定 → groupId 付与・merge cascade 経由 / same-edge align
    //         単独では非 attach / programmatic Spawn は groupId=null / Hakoniwa-priority 統合)
    static string Section23_FlushAttachWiring(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);

        // (a) Spawn=null invariant: programmatic spawn never mints a group. SnapOnRelease alone (no
        // flush partner) does NOT mint either — the dragged stays a singleton.
        c.Spawn(FloatingWindowCatalog.KIND_ORDER, "lone", -500, 0, 300, 200, true);
        c.SnapOnRelease("lone");
        if (c.GroupIdOf("lone") != null) return "S23a: SnapOnRelease minted a group with no flush partner";

        // (b) Flush attach: place 2 windows so b.left == a.right (flush) and run SnapOnRelease on the
        // dragged. Both end up sharing a brand-new grp_<hex32>. Cascade returned null (all-null
        // contributors) ⇒ MintGroupId fired ⇒ id starts with "grp_" and is non-null.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        // 280×180 = the catalog's strategy_editor minSize ⇒ no clamp surprise, geometry is verbatim.
        c2.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "A", 0,   0, 280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "B", 280, 0, 280, 180, true);
        if (c2.GroupIdOf("A") != null || c2.GroupIdOf("B") != null) return "S23b: pre-release groupId not null";
        c2.SnapOnRelease("B");
        string g = c2.GroupIdOf("B");
        if (string.IsNullOrEmpty(g)) return "S23b: flush release did not mint a groupId";
        if (!g.StartsWith("grp_") || g.Length != 4 + 32) return $"S23b: groupId shape (grp_<hex32>) drift: {g}";
        if (c2.GroupIdOf("A") != g) return "S23b: flush partner did not adopt the same groupId";

        // (c) Same-edge align only (left↔left side-by-side with 20px gap) ⇒ NOT flush ⇒ no attach.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer3, out _);
        var c3 = MakeController(spawned, layer3);
        c3.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "P", 0,   0, 280, 180, true);
        c3.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "Q", 300, 0, 280, 180, true);   // 20px gap; left↔left aligned in y
        c3.SnapOnRelease("Q");
        if (c3.GroupIdOf("Q") != null || c3.GroupIdOf("P") != null)
            return "S23c: same-edge alignment with gap minted a group (must not attach)";

        // (d) NO Hakoniwa-priority (ADR-0024 §1): two equal-size groups merge by DICT-MIN, NOT by which
        // one contains a core. A core-bearing group (startup, O) at grp_zzz… and a plain group (M, N) at
        // grp_aaa… both size 2; the dragged core S flush-attaches onto M. The winner is the
        // lexicographically-smallest id (grp_aaa…), proving the former core protection is gone.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer4, out _);
        var c4 = MakeController(spawned, layer4);
        const string GRP_A = "grp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";   // plain group, lex-EARLY
        const string GRP_Z = "grp_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";   // core-bearing group, lex-LATE
        c4.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "M", 280, 0, 280, 180, true);     // dragged's flush-right partner
        c4.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "N", 280, -180, 280, 180, true);   // M's group sibling
        c4.SetGroupId("M", GRP_A);
        c4.SetGroupId("N", GRP_A);
        c4.Spawn(FloatingWindowCatalog.KIND_STARTUP,   "S", 0, 0, 280, 180, true);        // dragged (former "core")
        c4.Spawn(FloatingWindowCatalog.KIND_RUN_RESULT,"O", -280, 0, 280, 180, true);     // S's group sibling
        c4.SetGroupId("S", GRP_Z);
        c4.SetGroupId("O", GRP_Z);
        // S.right=280 == M.left=280 ⇒ flush. SnapOnRelease(S) merges both size-2 groups; the dict-min id
        // (GRP_A) survives even though S's group contains startup + run_result "cores".
        c4.SnapOnRelease("S");
        if (c4.GroupIdOf("S") != GRP_A) return $"S23d: dragged did not absorb to dict-min id (got {c4.GroupIdOf("S")})";
        if (c4.GroupIdOf("O") != GRP_A) return $"S23d: core sibling O did not absorb (got {c4.GroupIdOf("O")})";
        if (c4.GroupIdOf("M") != GRP_A) return $"S23d: flush partner M lost dict-min id (got {c4.GroupIdOf("M")})";
        if (c4.GroupIdOf("N") != GRP_A) return $"S23d: sibling N did not keep dict-min id (got {c4.GroupIdOf("N")})";

        // (e) Size-max cascade when no Hakoniwa is involved: 2 disjoint non-Hakoniwa groups, dragged
        // attaches to one. Bigger group's id survives, smaller absorbs.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer5, out _);
        var c5 = MakeController(spawned, layer5);
        const string GBIG = "grp_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";   // size 2 (32 b's, lex-EARLIER than c's)
        const string GSMALL = "grp_cccccccccccccccccccccccccccccccc"; // size 1 (32 c's)
        c5.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "X", 280, 0,    280, 180, true);   // flush-right partner (GBIG)
        c5.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Y", 280, -180, 280, 180, true);   // GBIG sibling
        c5.SetGroupId("X", GBIG); c5.SetGroupId("Y", GBIG);
        c5.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "D", 0, 0, 280, 180, true);        // dragged (in GSMALL alone)
        c5.SetGroupId("D", GSMALL);
        c5.SnapOnRelease("D");
        if (c5.GroupIdOf("D") != GBIG) return $"S23e: size-max did not promote bigger group id (got {c5.GroupIdOf("D")})";
        if (c5.GroupIdOf("X") != GBIG || c5.GroupIdOf("Y") != GBIG) return "S23e: bigger group lost members on merge";

        // (f) SpawnDockedToFocus flush placement DOES NOT attach: a dock spawn that lands flush against
        // a focused window leaves groupId=null because attach is the user drag-release's exclusive
        // trigger (findings 0082 §10). SnapOnRelease is NEVER called on the spawn path.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer6, out _);
        var c6 = MakeController(spawned, layer6);
        c6.Spawn(FloatingWindowCatalog.KIND_STARTUP, "anchor", 0, 0, 280, 180, true);
        c6.NoteUserFocus("anchor");
        c6.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, "chart:abc", new Vector2(0, 0), true);
        // Even though the chart is dropped flush-adjacent to "anchor" (DockSnapPlacement), no group forms.
        if (c6.GroupIdOf("chart:abc") != null || c6.GroupIdOf("anchor") != null)
            return "S23f: SpawnDockedToFocus auto-attached (must remain groupId=null until user drag-release)";

        return null;
    }

    // ---- 24. ADR-0024 §2: ResolveDragMode pure 3-mode dispatcher ----
    // Covers: DRAG-01..04 (cursor-position dispatch: Swap when cursor over a sibling — distance-INDEPENDENT;
    //         Detach when outside island ∧ |cursor-dragStart| ≥ D_DETACH_PX=256 INCLUSIVE; Translate otherwise;
    //         center-in-rect swap trigger on/off; singleton)
    static string Section24_ResolveDragModePure()
    {
        float dDetach = FloatingWindowMath.D_DETACH_PX;   // non-const local: pins the value without a folded-constant unreachable warning
        if (dDetach != 256f) return $"S24: D_DETACH_PX expected 256 (got {dDetach})";

        var dragStart = new Vector2(0f, 0f);
        // island: A (dragged) at (0,0,280,180); sibling B at (300,0,280,180) → B rect = x[300,580], y[-180,0].
        var members = new List<FloatingWindowMath.GroupMember>
        {
            new FloatingWindowMath.GroupMember { id = "A", rect = new FloatingWindowMath.DockRect(0f,   0f, 280f, 180f), siblingIndex = 0 },
            new FloatingWindowMath.GroupMember { id = "B", rect = new FloatingWindowMath.DockRect(300f, 0f, 280f, 180f), siblingIndex = 1 },
        };

        // (a) DRAG-01 Swap: cursor inside sibling B's rect ⇒ Swap(B), even though |cursor-dragStart|≈353 ≥ 256
        //     (distance-INDEPENDENT — swap wins over the detach threshold).
        var swap = FloatingWindowMath.ResolveDragMode(new Vector2(350f, -50f), dragStart, members, "A", 256f);
        if (swap.mode != FloatingWindowMath.DragMode.Swap) return $"S24a: cursor over sibling ≠ Swap (got {swap.mode})";
        if (swap.swapTargetId != "B") return $"S24a: swap target ≠ B (got {swap.swapTargetId})";

        // (b) DRAG-03 Detach: cursor outside island ∧ |cursor-dragStart|=300 ≥ 256.
        var det = FloatingWindowMath.ResolveDragMode(new Vector2(0f, -300f), dragStart, members, "A", 256f);
        if (det.mode != FloatingWindowMath.DragMode.Detach) return $"S24b: far-outside ≠ Detach (got {det.mode})";
        if (det.swapTargetId != null) return "S24b: Detach carried a swap target";

        // (c) DRAG-02 Translate: cursor outside island ∧ |cursor-dragStart|=100 < 256.
        var tr = FloatingWindowMath.ResolveDragMode(new Vector2(0f, -100f), dragStart, members, "A", 256f);
        if (tr.mode != FloatingWindowMath.DragMode.Translate) return $"S24c: near-outside ≠ Translate (got {tr.mode})";

        // (d) Boundary: |cursor-dragStart| EXACTLY 256 ⇒ Detach (inclusive ≥).
        var atExact = FloatingWindowMath.ResolveDragMode(new Vector2(0f, -256f), dragStart, members, "A", 256f);
        if (atExact.mode != FloatingWindowMath.DragMode.Detach) return "S24d: |cursor-dragStart|=256 should be Detach (inclusive)";

        // (e) DRAG-04 center-in-rect on/off: cursor just inside B ⇒ Swap; nudge it just past B.Right (580) ⇒
        //     no longer Swap (falls to Detach at this distance). The swap trigger toggles on rect entry/exit.
        var inB  = FloatingWindowMath.ResolveDragMode(new Vector2(580f, -50f), dragStart, members, "A", 256f);  // x=580 == B.Right (inside, inclusive)
        if (inB.mode != FloatingWindowMath.DragMode.Swap) return "S24e: cursor at B.Right edge not Swap (edge inclusive)";
        var outB = FloatingWindowMath.ResolveDragMode(new Vector2(581f, -50f), dragStart, members, "A", 256f);  // x=581 just past B.Right
        if (outB.mode == FloatingWindowMath.DragMode.Swap) return "S24e: cursor past B.Right still Swap (center-in-rect exit failed)";

        // (f) Singleton: members = [A] only (swap scan excludes the dragged) ⇒ Translate/Detach by distance.
        var solo = new List<FloatingWindowMath.GroupMember>
        {
            new FloatingWindowMath.GroupMember { id = "A", rect = new FloatingWindowMath.DockRect(0f, 0f, 280f, 180f), siblingIndex = 0 },
        };
        if (FloatingWindowMath.ResolveDragMode(new Vector2(0f, -50f), dragStart, solo, "A", 256f).mode != FloatingWindowMath.DragMode.Translate)
            return "S24f: singleton near ≠ Translate";
        if (FloatingWindowMath.ResolveDragMode(new Vector2(0f, -400f), dragStart, solo, "A", 256f).mode != FloatingWindowMath.DragMode.Detach)
            return "S24f: singleton far ≠ Detach";
        return null;
    }

    // ---- 25. ADR-0024 §7: Translate / Detach real-render wiring (DragApplyDelta) ----
    // Covers: DRAG-02 (Translate ⇒ whole island real-renders at rest+offset) / DRAG-03 (Detach ⇒ ONLY the
    //         dragged real-renders, siblings stay at rest) / groupId unchanged mid-drag / singleton translate.
    static string Section25_TranslateDetachRender(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);

        // 2-member island stacked VERTICALLY (so a horizontal drag never enters the sibling's rect ⇒ no
        // accidental Swap). A=(0,0,280,180), B=(0,-200,280,180). Bootstrap the group via SetGroupId
        // (attach itself is pinned by Section23). No external windows ⇒ no magnetic snap interference.
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);
        const string G = "grp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        c.SetGroupId("A", G); c.SetGroupId("B", G);
        Vector2 a0 = c.RectOf("A").anchoredPosition;
        Vector2 b0 = c.RectOf("B").anchoredPosition;
        Vector2 dragStart = a0;

        // (a) DRAG-02 Translate: cursor outside the island, |cursor-dragStart|=60 < 256 ⇒ BOTH members
        //     real-render shifted by the offset (the whole island moves — startup/run_result have no
        //     special status, this is a plain island).
        Vector2 cursor = a0 + new Vector2(60f, 0f);
        var mode = c.DragApplyDelta("A", dragStart, cursor, new Vector2(60f, 0f));
        if (mode != FloatingWindowMath.DragMode.Translate) return $"S25a: expected Translate, got {mode}";
        if (!Approx2(c.RectOf("A").anchoredPosition, a0 + new Vector2(60f, 0f))) return "S25a: dragged did not translate";
        if (!Approx2(c.RectOf("B").anchoredPosition, b0 + new Vector2(60f, 0f))) return "S25a: island sibling did not translate";
        if (c.GroupIdOf("A") != G || c.GroupIdOf("B") != G) return "S25a: groupId mutated during translate";

        // (b) DRAG-03 Detach: cursor far outside (|cursor-dragStart|=400 ≥ 256), away from B's rect ⇒ ONLY
        //     the dragged real-renders at offset; the sibling snaps back to rest.
        Vector2 cursor2 = a0 + new Vector2(400f, 0f);
        mode = c.DragApplyDelta("A", dragStart, cursor2, new Vector2(340f, 0f));
        if (mode != FloatingWindowMath.DragMode.Detach) return $"S25b: expected Detach, got {mode}";
        if (!Approx2(c.RectOf("A").anchoredPosition, a0 + new Vector2(400f, 0f))) return "S25b: detached dragged did not follow cursor";
        if (!Approx2(c.RectOf("B").anchoredPosition, b0)) return "S25b: sibling moved during Detach (only the dragged should)";
        if (c.GroupIdOf("A") != G || c.GroupIdOf("B") != G) return "S25b: groupId mutated mid-drag (commit is release-only)";

        // (c) Singleton translate: an ungrouped window moves alone via DragApplyDelta.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDER, "lone", 0, 0, 300, 200, true);
        Vector2 lone0 = c2.RectOf("lone").anchoredPosition;
        var mode2 = c2.DragApplyDelta("lone", lone0, lone0 + new Vector2(10f, 3f), new Vector2(10f, 3f));
        if (mode2 != FloatingWindowMath.DragMode.Translate) return $"S25c: ungrouped near ≠ Translate (got {mode2})";
        if (!Approx2(c2.RectOf("lone").anchoredPosition, lone0 + new Vector2(10f, 3f))) return "S25c: solo move dropped";

        // (d) Singleton-in-name (groupId set but only 1 visible member) is NOT a group ⇒ translates alone
        //     (findings 0082 §2 "group = ≥2 visible/live").
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer3, out _);
        var c3 = MakeController(spawned, layer3);
        c3.Spawn(FloatingWindowCatalog.KIND_ORDER, "stale", 0, 0, 300, 200, true);
        c3.SetGroupId("stale", "grp_stalestalestalestalestalestalest");
        Vector2 stale0 = c3.RectOf("stale").anchoredPosition;
        var mode3 = c3.DragApplyDelta("stale", stale0, stale0 + new Vector2(1f, 0f), new Vector2(1f, 0f));
        if (mode3 != FloatingWindowMath.DragMode.Translate) return $"S25d: singleton (≥2 rule) ≠ Translate (got {mode3})";
        if (!Approx2(c3.RectOf("stale").anchoredPosition, stale0 + new Vector2(1f, 0f))) return "S25d: singleton drag did not move";
        return null;
    }

    // ---- 26. ADR-0024 §4: Detach release commit + dissolve helper + Close cascade + Hide preserve ----
    // Covers: DRAG-08 (Detach release on empty space ⇒ groupId=null + DissolveIfShrunkTo(2): 残 ≥2 維持 /
    //         残 1 連鎖 dissolve) / Close も同 helper / Hide は groupId 温存. Cursors are placed straight up
    //         from rest so they never enter a sibling rect (no accidental Swap).
    static string Section26_DetachDissolveCloseHide(List<GameObject> spawned)
    {
        // (a) 3-member group: detach 1 → remainder still ≥2 → group keeps its id.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0,   0, 280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 280, 0, 280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,     "C", 560, 0, 280, 180, true);
        c.SnapOnRelease("B");
        c.SnapOnRelease("C");
        string g = c.GroupIdOf("A");
        if (string.IsNullOrEmpty(g) || c.GroupIdOf("B") != g || c.GroupIdOf("C") != g)
            return "S26a: precondition 3-member attach failed";
        Vector2 restA = c.RectOf("A").anchoredPosition;
        Vector2 cursor = restA + new Vector2(0f, 300f);   // straight UP, |Δ|=300 ≥ 256, off all sibling rects
        var mode = c.ReleaseDrag("A", restA, cursor);
        if (mode != FloatingWindowMath.DragMode.Detach) return $"S26a: expected Detach (got {mode})";
        if (c.GroupIdOf("A") != null) return "S26a: dragged groupId not cleared after detach";
        if (c.GroupIdOf("B") != g || c.GroupIdOf("C") != g) return "S26a: 3→2 should NOT dissolve (≥2 remains)";

        // (b) 2-member group: detach 1 → remainder = 1 → chain dissolve clears both.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "P", 0,   0, 280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Q", 280, 0, 280, 180, true);
        c2.SnapOnRelease("Q");
        string g2 = c2.GroupIdOf("P");
        if (string.IsNullOrEmpty(g2)) return "S26b: precondition 2-member attach failed";
        Vector2 restP = c2.RectOf("P").anchoredPosition;
        var mode2 = c2.ReleaseDrag("P", restP, restP + new Vector2(0f, 300f));
        if (mode2 != FloatingWindowMath.DragMode.Detach) return "S26b: not Detach";
        if (c2.GroupIdOf("P") != null) return "S26b: detached dragged still grouped";
        if (c2.GroupIdOf("Q") != null) return "S26b: chain dissolve did not clear the remnant (≥2 rule)";

        // (c) Close cascade: 2-member group → Close one → chain dissolve via the shared helper.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer3, out _);
        var c3 = MakeController(spawned, layer3);
        c3.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "X", 0,   0, 280, 180, true);
        c3.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Y", 280, 0, 280, 180, true);
        c3.SnapOnRelease("Y");
        string g3 = c3.GroupIdOf("X");
        if (string.IsNullOrEmpty(g3)) return "S26c: precondition attach failed";
        if (!c3.Close("Y")) return "S26c: Close returned false";
        if (c3.GroupIdOf("X") != null) return "S26c: Close did not chain-dissolve the remnant";

        // (d) Hide preserves groupId (mode-switch / Replay↔Live invariant — findings 0082 §10).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer4, out _);
        var c4 = MakeController(spawned, layer4);
        c4.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "L", 0,   0, 280, 180, true);
        c4.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "M", 280, 0, 280, 180, true);
        c4.SnapOnRelease("M");
        string g4 = c4.GroupIdOf("L");
        if (string.IsNullOrEmpty(g4)) return "S26d: precondition attach failed";
        if (!c4.Hide("L")) return "S26d: Hide returned false";
        if (c4.GroupIdOf("L") != g4) return "S26d: Hide cleared groupId — must preserve for Replay↔Live revival";
        if (c4.GroupIdOf("M") != g4) return "S26d: Hide leaked to the other member";

        // (e) Core member detach (ADR-0024 §1 — no core-lock): startup + orders island, detach orders →
        //     group is now (startup) alone → chain dissolve → startup.groupId cleared. (Pre-ADR-0024 a
        //     core group banned this; now a core drags exactly like any window.)
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer5, out _);
        var c5 = MakeController(spawned, layer5);
        c5.Spawn(FloatingWindowCatalog.KIND_STARTUP, "startup", 0,   0, 280, 180, true);
        c5.Spawn(FloatingWindowCatalog.KIND_ORDERS,  "orders",  280, 0, 280, 180, true);
        c5.SnapOnRelease("orders");
        string g5 = c5.GroupIdOf("startup");
        if (string.IsNullOrEmpty(g5)) return "S26e: precondition attach failed";
        Vector2 restO = c5.RectOf("orders").anchoredPosition;
        var mode5 = c5.ReleaseDrag("orders", restO, restO + new Vector2(0f, 300f));
        if (mode5 != FloatingWindowMath.DragMode.Detach) return $"S26e: expected Detach (got {mode5})";
        if (c5.GroupIdOf("orders") != null) return "S26e: Detach dragged still grouped";
        if (c5.GroupIdOf("startup") != null) return "S26e: chain dissolve did not clear remnant (startup alone)";

        // (f) Close on a non-grouped window: just returns true and removes (no dissolve attempt).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer6, out _);
        var c6 = MakeController(spawned, layer6);
        c6.Spawn(FloatingWindowCatalog.KIND_ORDER, "lone", 0, 0, 300, 200, true);
        if (!c6.Close("lone")) return "S26f: Close on ungrouped lone returned false";
        if (c6.Has("lone")) return "S26f: lone not removed";

        // (g) DissolveIfShrunkTo direct call: a group with 1 visible member (other Hidden) dissolves to
        //     null on EVERY member (live or hidden). Pins the helper independent of Hide.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer7, out _);
        var c7 = MakeController(spawned, layer7);
        c7.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "U", 0,   0, 280, 180, true);
        c7.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "V", 280, 0, 280, 180, true);
        c7.SnapOnRelease("V");
        string g7 = c7.GroupIdOf("U");
        if (string.IsNullOrEmpty(g7)) return "S26g: precondition attach failed";
        c7.Hide("V");   // V hidden; visible count for g7 = 1 (only U)
        if (c7.GroupIdOf("V") != g7) return "S26g: Hide cleared groupId (must preserve)";
        c7.DissolveIfShrunkTo(g7, 2);
        if (c7.GroupIdOf("U") != null) return "S26g: helper did not dissolve U at threshold";
        if (c7.GroupIdOf("V") != null) return "S26g: helper did not also clear the Hidden member's groupId";
        return null;
    }

    // ---- 27. ADR-0024 §1: Hakoniwa special RETIRED — startup / run_result drag like any window ----
    // Covers: DRAG-11 (a core-bearing island TRANSLATES (no translate-ban) and a core member DETACHES
    //         (no core-lock). The old GROUP-08 translate-ban / HakoniwaCoreLock / dynamic re-promote are gone.)
    static string Section27_HakoniwaRetired(List<GameObject> spawned)
    {
        // Island of the two former "cores": startup + run_result, stacked vertically (so a horizontal
        // drag never enters the sibling rect). Bootstrap via SetGroupId.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_STARTUP,    "startup",    0, 0,    280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_RUN_RESULT, "run_result", 0, -200, 280, 180, true);
        const string G = "grp_cccccccccccccccccccccccccccccccc";
        c.SetGroupId("startup", G); c.SetGroupId("run_result", G);
        Vector2 s0 = c.RectOf("startup").anchoredPosition;
        Vector2 r0 = c.RectOf("run_result").anchoredPosition;

        // (a) TRANSLATE a core-bearing island (was BANNED): drag startup (a core) with cursor outside the
        //     island ∧ <256 ⇒ the WHOLE island shifts. Both members move.
        Vector2 cursor = s0 + new Vector2(60f, 0f);
        var mode = c.DragApplyDelta("startup", s0, cursor, new Vector2(60f, 0f));
        if (mode != FloatingWindowMath.DragMode.Translate) return $"S27a: core-bearing island not Translate (got {mode}) — translate-ban must be retired";
        if (!Approx2(c.RectOf("startup").anchoredPosition, s0 + new Vector2(60f, 0f))) return "S27a: core dragged did not translate";
        if (!Approx2(c.RectOf("run_result").anchoredPosition, r0 + new Vector2(60f, 0f))) return "S27a: core sibling did not translate (whole-island move)";

        // (b) DETACH a CORE member (was core-LOCKED): drag startup beyond 256, off the sibling rect ⇒
        //     Detach, and ReleaseDrag commits it (groupId=null, remnant dissolves). Cores are no longer
        //     detach-immune.
        var mode2 = c.DragApplyDelta("startup", s0, s0 + new Vector2(0f, 400f), new Vector2(0f, 400f));
        if (mode2 != FloatingWindowMath.DragMode.Detach) return $"S27b: core beyond 256 not Detach (got {mode2}) — core-lock must be retired";
        var rel = c.ReleaseDrag("startup", s0, s0 + new Vector2(0f, 400f));
        if (rel != FloatingWindowMath.DragMode.Detach) return "S27b: core release mode drifted";
        if (c.GroupIdOf("startup") != null) return "S27b: core detach did not clear groupId (core-lock retired)";
        if (c.GroupIdOf("run_result") != null) return "S27b: remnant did not dissolve after core detach";
        return null;
    }

    // ---- 28. ADR-0024 §2: ResolveDropTarget pure (swap-target resolver, used by ResolveDragMode) ----
    // Covers: DRAG-01/04 (cursor 下メンバー = swap target / 重なり最前面 sibling 優先 / dragged 自身は除外 /
    //         cursor が空 ⇒ null / null/empty inputs ⇒ null)
    static string Section28_ResolveDropTargetPure()
    {
        // 3 members of one island laid out side-by-side; cursor positions probe the resolver.
        // Member rects: A=(0..100, 0..-80), B=(100..200, 0..-80), C=(50..150, -40..-120) — A and C
        // overlap; C overlaps B. siblingIndex: A=0, B=1, C=2 (C front-most).
        var members = new List<FloatingWindowMath.GroupMember>
        {
            new FloatingWindowMath.GroupMember{ id="A", rect=new FloatingWindowMath.DockRect(0f,    0f,   100f, 80f), siblingIndex=0 },
            new FloatingWindowMath.GroupMember{ id="B", rect=new FloatingWindowMath.DockRect(100f,  0f,   100f, 80f), siblingIndex=1 },
            new FloatingWindowMath.GroupMember{ id="C", rect=new FloatingWindowMath.DockRect(50f, -40f,   100f, 80f), siblingIndex=2 },
        };

        // (a) Cursor inside A only (and NOT inside C): A wins.
        if (FloatingWindowMath.ResolveDropTarget(new Vector2(20f, -10f), members, "dragged") != "A")
            return "S28a: cursor in A only did not resolve to A";

        // (b) Cursor inside B only (x=180 right of C's right=150): B wins.
        if (FloatingWindowMath.ResolveDropTarget(new Vector2(180f, -10f), members, "dragged") != "B")
            return "S28b: cursor in B only did not resolve to B";

        // (c) Cursor at (75, -60) sits inside A (75 in [0,100], -60 in [-80,0]) AND inside C (75 in
        // [50,150], -60 in [-120,-40]). Top sibling wins ⇒ C.
        if (FloatingWindowMath.ResolveDropTarget(new Vector2(75f, -60f), members, "dragged") != "C")
            return "S28c: overlapping A+C did not pick front-most sibling (C)";

        // (d) dragged exclusion: cursor inside A but draggedId=A ⇒ skip A. Result null (cursor not in
        // B or C at this point).
        if (FloatingWindowMath.ResolveDropTarget(new Vector2(20f, -10f), members, "A") != null)
            return "S28d: dragged self-exclusion failed";

        // (e) No member under cursor: empty result.
        if (FloatingWindowMath.ResolveDropTarget(new Vector2(500f, 500f), members, "dragged") != null)
            return "S28e: cursor off all members did not return null";

        // (f) null/empty inputs ⇒ null.
        if (FloatingWindowMath.ResolveDropTarget(Vector2.zero, null, "x") != null) return "S28f: null members not null";
        if (FloatingWindowMath.ResolveDropTarget(Vector2.zero, new List<FloatingWindowMath.GroupMember>(), "x") != null)
            return "S28f: empty members not null";
        return null;
    }

    // ---- 29. ADR-0024 §4: Swap commit ((x,y,w,h) exchange) — ANY island (no Hakoniwa special) ----
    // Covers: DRAG-01 (Swap commit が dragged↔target で (x,y,w,h) 4 値交換 / kind/id/groupId 不変 / island
    //         footprint 不変 / live geometry は drag 中凍結 / cursor が空 ⇒ Translate (swap でない))
    static string Section29_SwapWiring(List<GameObject> spawned)
    {
        // (a) Swap end-to-end on a PLAIN island (orders + positions, NO core) with DIFFERENT-sized tiles so
        // the (x, y, w, h) exchange is verifiable on all 4 components. The tiles overlap so the cursor can
        // sit inside the target. A third member keeps the island ≥2 even mid-thought (not needed here —
        // 2 members suffice). Bootstrap via SetGroupId.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "orders",    0,  0, 280, 180, true);    // dragged: (0,0), 280×180
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "positions", 30, 0, 300, 200, true);    // target: (30,0), 300×200 (overlaps)
        const string G = "grp_hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";
        c.SetGroupId("orders", G); c.SetGroupId("positions", G);

        Vector2 oRest = c.RectOf("orders").anchoredPosition;     // (0, 0)
        Vector2 oSize = c.RectOf("orders").sizeDelta;            // (280, 180)
        Vector2 pPos  = c.RectOf("positions").anchoredPosition;  // (30, 0)
        Vector2 pSize = c.RectOf("positions").sizeDelta;         // (300, 200)

        // Cursor (35, -30) ⇒ inside positions (x∈[30,330], y∈[-200,0]) ⇒ Swap(target=positions).
        Vector2 cursor = new Vector2(35f, -30f);
        var mode = c.DragApplyDelta("orders", oRest, cursor, cursor - oRest);
        if (mode != FloatingWindowMath.DragMode.Swap) return $"S29a: mid-drag mode {mode} ≠ Swap";
        // Live geometry frozen during a swap drag (ghosts only).
        if (!Approx2(c.RectOf("orders").anchoredPosition, oRest)) return "S29a: orders moved during Swap drag (freeze broken)";
        if (!Approx2(c.RectOf("positions").anchoredPosition, pPos)) return "S29a: positions moved during Swap drag";

        var relMode = c.ReleaseDrag("orders", oRest, cursor);
        if (relMode != FloatingWindowMath.DragMode.Swap) return $"S29a: release mode {relMode} ≠ Swap";
        if (!Approx2(c.RectOf("orders").anchoredPosition, pPos)) return $"S29a: orders did not jump to target pos (got {c.RectOf("orders").anchoredPosition})";
        if (!Approx2(c.RectOf("orders").sizeDelta, pSize)) return $"S29a: orders did not take target size (got {c.RectOf("orders").sizeDelta})";
        if (!Approx2(c.RectOf("positions").anchoredPosition, oRest)) return "S29a: positions did not jump to dragged rest pos";
        if (!Approx2(c.RectOf("positions").sizeDelta, oSize)) return "S29a: positions did not take dragged size";
        if (c.GroupIdOf("orders") != G || c.GroupIdOf("positions") != G) return "S29a: swap mutated groupId";

        // (b) Cursor inside the island but NOT over any sibling ⇒ NOT swap. With the tiles non-overlapping
        // and the cursor in empty space within 256, the mode is Translate (the whole island would move) —
        // proving swap requires the cursor to actually be over a sibling rect.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);   // stacked, gap
        const string G2 = "grp_iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii";
        c2.SetGroupId("A", G2); c2.SetGroupId("B", G2);
        Vector2 aRest = c2.RectOf("A").anchoredPosition;
        Vector2 cursor2 = aRest + new Vector2(20f, 5f);   // empty space, not over B, <256
        var midMode2 = c2.DragApplyDelta("A", aRest, cursor2, cursor2 - aRest);
        if (midMode2 != FloatingWindowMath.DragMode.Translate) return $"S29b: cursor in empty space ≠ Translate (got {midMode2})";
        if (c2.GroupIdOf("A") != G2) return "S29b: Translate disturbed groupId mid-drag";
        return null;
    }

    // ---- 30. #104 Slice F: cross-plane group restore split + shared dissolve ----
    // Covers: GROUP-11 (BackcastWorkspaceRoot.SplitCrossPlaneGroups: 同一 groupId が両 plane に跨ったら
    //         多数派 plane 残し / 同数 ⇒ dock 優先 / 負け側 groupId=null / 単一 plane group は不変。
    //         共有 DissolveIfShrunkTo は Slice D で固定済み — ここでは split のみを pin)
    static string Section30_CrossPlaneGroupRestoreSplit(List<GameObject> spawned)
    {
        // (a) Single-plane group (no cross-plane): no mutation.
        const string G_DOCK = "grp_dddddddddddddddddddddddddddddddd";
        const string G_FRONT = "grp_ffffffffffffffffffffffffffffffff";
        var single = new List<FloatingWindowLayout>
        {
            WG("startup",      FloatingWindowCatalog.KIND_STARTUP,   0, 0, 280, 180, 0, true, G_DOCK),
            WG("orders",       FloatingWindowCatalog.KIND_ORDERS,    0, 0, 280, 180, 1, true, G_DOCK),
            WG("order:singlet", FloatingWindowCatalog.KIND_ORDER,    0, 0, 280, 180, 2, true, null),
        };
        BackcastWorkspaceRoot.SplitCrossPlaneGroups(single);
        if (single[0].groupId != G_DOCK) return "S30a: single-plane dock group lost groupId";
        if (single[1].groupId != G_DOCK) return "S30a: single-plane dock group lost groupId on member 2";
        if (single[2].groupId != null) return "S30a: ungrouped order mutated";

        // (b) Cross-plane majority dock: 3 dock kinds + 1 front kind share a groupId. Dock wins; the
        // front member's groupId becomes null.
        const string G_MAJ = "grp_mmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmm";
        var majorityDock = new List<FloatingWindowLayout>
        {
            WG("startup",   FloatingWindowCatalog.KIND_STARTUP,   0, 0, 280, 180, 0, true, G_MAJ),
            WG("orders",    FloatingWindowCatalog.KIND_ORDERS,    0, 0, 280, 180, 1, true, G_MAJ),
            WG("positions", FloatingWindowCatalog.KIND_POSITIONS, 0, 0, 280, 180, 2, true, G_MAJ),
            WG("order",     FloatingWindowCatalog.KIND_ORDER,     0, 0, 280, 180, 3, true, G_MAJ),
        };
        BackcastWorkspaceRoot.SplitCrossPlaneGroups(majorityDock);
        if (majorityDock[0].groupId != G_MAJ) return "S30b: dock majority winner #1 lost id";
        if (majorityDock[1].groupId != G_MAJ) return "S30b: dock majority winner #2 lost id";
        if (majorityDock[2].groupId != G_MAJ) return "S30b: dock majority winner #3 lost id";
        if (majorityDock[3].groupId != null) return "S30b: loser-plane front member retained group";

        // (c) Cross-plane majority FRONT: 2 front members + 1 dock member share a groupId. Front wins.
        const string G_FMAJ = "grp_nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn";
        var majorityFront = new List<FloatingWindowLayout>
        {
            WG("order",                       FloatingWindowCatalog.KIND_ORDER,           0, 0, 280, 180, 0, true, G_FMAJ),
            WG("strategy_editor:region_001",  FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 0, 0, 280, 180, 1, true, G_FMAJ),
            WG("orders",                      FloatingWindowCatalog.KIND_ORDERS,          0, 0, 280, 180, 2, true, G_FMAJ),
        };
        BackcastWorkspaceRoot.SplitCrossPlaneGroups(majorityFront);
        if (majorityFront[0].groupId != G_FMAJ) return "S30c: front majority winner #1 lost id";
        if (majorityFront[1].groupId != G_FMAJ) return "S30c: front majority winner #2 lost id";
        if (majorityFront[2].groupId != null) return "S30c: loser-plane dock member retained group";

        // (d) Tie ⇒ DOCK wins (dock-plane bias per ADR-0019 D9 / findings 0082 §9 — retained by ADR-0024).
        const string G_TIE = "grp_tttttttttttttttttttttttttttttttt";
        var tie = new List<FloatingWindowLayout>
        {
            WG("orders", FloatingWindowCatalog.KIND_ORDERS, 0, 0, 280, 180, 0, true, G_TIE),
            WG("order",  FloatingWindowCatalog.KIND_ORDER,  0, 0, 280, 180, 1, true, G_TIE),
        };
        BackcastWorkspaceRoot.SplitCrossPlaneGroups(tie);
        if (tie[0].groupId != G_TIE) return "S30d: tie ⇒ dock side did not win";
        if (tie[1].groupId != null) return "S30d: tie loser (front) retained group";

        // (e) Two independent groups, only one is cross-plane: untouched + split.
        const string G_CLEAN = "grp_cleancleancleancleancleancleanc";
        const string G_SPLIT = "grp_splitsplitsplitsplitsplitsplits";
        var mixed = new List<FloatingWindowLayout>
        {
            WG("startup",   FloatingWindowCatalog.KIND_STARTUP,   0, 0, 280, 180, 0, true, G_CLEAN),
            WG("orders",    FloatingWindowCatalog.KIND_ORDERS,    0, 0, 280, 180, 1, true, G_CLEAN),
            WG("positions", FloatingWindowCatalog.KIND_POSITIONS, 0, 0, 280, 180, 2, true, G_SPLIT),
            WG("order",     FloatingWindowCatalog.KIND_ORDER,     0, 0, 280, 180, 3, true, G_SPLIT),
        };
        BackcastWorkspaceRoot.SplitCrossPlaneGroups(mixed);
        if (mixed[0].groupId != G_CLEAN || mixed[1].groupId != G_CLEAN) return "S30e: clean group mutated";
        if (mixed[2].groupId != G_SPLIT) return "S30e: split-winner (back) lost id";
        if (mixed[3].groupId != null) return "S30e: split-loser (front) retained id";

        // (f) Shared dissolve helper: after split leaves the winner with only 1 visible member, the
        // chain dissolve clears that member's groupId too. This pins that Slice F shares the SAME
        // helper as Slice D — there is no Slice-F-specific dissolve logic.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        const string GR = "grp_rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr";
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "U", 0, 0, 280, 180, true);
        c.SetGroupId("U", GR);   // singleton remnant on this controller after a hypothetical split
        c.DissolveIfShrunkTo(GR, 2);
        if (c.GroupIdOf("U") != null) return "S30f: shared dissolve helper did not clear singleton remnant";
        return null;
    }

    // ---- 31. ADR-0024 §7: drag-ghost preview is SWAP-ONLY (translate / detach real-render) ----
    // Covers: DRAG-14 (Swap ⇒ 2 ghosts: solid at target rect / dashed at dragged rest. Translate ⇒ 0 ghosts.
    //         Detach ⇒ 0 ghosts (CHANGED from #104). container 最前面 sibling / Clear で 0 / commit-on-release.)
    static string Section31_GhostSwapOnly(List<GameObject> spawned)
    {
        float alpha = DragGhostLayer.ALPHA;   // non-const local (avoid folded-constant unreachable warning)
        if (alpha != 0.45f) return $"S31: ALPHA != 0.45 (got {alpha})";

        // A plain 2-member island (orders + positions, overlapping) + bare-stack ghost layer.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        var containerGo = new GameObject("GhostContainer", typeof(RectTransform));
        spawned.Add(containerGo);
        var container = (RectTransform)containerGo.transform; container.SetParent(layer, false);
        var ghostLayer = new DragGhostLayer(container, FloatingWindowCatalog.Default(),
            () => { var go = new GameObject("GhostWindow_unset", typeof(RectTransform)); spawned.Add(go); return (RectTransform)go.transform; },
            go => UnityEngine.Object.DestroyImmediate(go));
        c.AttachGhostLayer(ghostLayer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "orders",    0,  0, 280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "positions", 30, 0, 300, 200, true);
        const string G = "grp_gggggggggggggggggggggggggggggggg";
        c.SetGroupId("orders", G); c.SetGroupId("positions", G);

        Vector2 orest = c.RectOf("orders").anchoredPosition;
        Vector2 osize = c.RectOf("orders").sizeDelta;
        Vector2 ppos  = c.RectOf("positions").anchoredPosition;
        Vector2 psize = c.RectOf("positions").sizeDelta;

        // (a) Swap: cursor inside positions ⇒ 2 ghosts (solid at target rect, dashed at dragged rest).
        Vector2 swapCursor = new Vector2(35f, -30f);
        var mode = c.DragApplyDelta("orders", orest, swapCursor, swapCursor - orest);
        if (mode != FloatingWindowMath.DragMode.Swap) return $"S31a: precondition mode={mode}";
        if (ghostLayer.ActiveCount != 2) return $"S31a: Swap should produce 2 ghosts (got {ghostLayer.ActiveCount})";
        var g0 = ghostLayer.GhostAt(0);
        var g1 = ghostLayer.GhostAt(1);
        if (g0 == null || g1 == null) return "S31a: swap ghosts null";
        if (g0.gameObject.name != "GhostWindow_Solid") return $"S31a: dragged ghost not Solid (got {g0.gameObject.name})";
        if (g1.gameObject.name != "GhostWindow_Dashed") return $"S31a: target ghost not Dashed (got {g1.gameObject.name})";
        if (!Approx2(g0.anchoredPosition, ppos)) return $"S31a: dragged ghost not at target pos (got {g0.anchoredPosition} vs {ppos})";
        if (!Approx2(g0.sizeDelta, psize)) return "S31a: dragged ghost not at target size";
        if (!Approx2(g1.anchoredPosition, orest)) return "S31a: target ghost not at dragged rest";
        if (!Approx2(g1.sizeDelta, osize)) return "S31a: target ghost not at dragged size";

        // (b) Container is the LAST sibling (ghosts draw in front).
        if (container.parent != null)
        {
            int last = container.parent.childCount - 1;
            if (container.GetSiblingIndex() != last)
                return $"S31b: ghost container not last sibling (idx={container.GetSiblingIndex()}, expected {last})";
        }

        // (c) Translate ⇒ NO ghost (real-render). Use the vertically-stacked island so a drag is Translate.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layerT, out _);
        var ct = MakeController(spawned, layerT);
        var contTGo = new GameObject("GhostContainerT", typeof(RectTransform)); spawned.Add(contTGo);
        var contT = (RectTransform)contTGo.transform; contT.SetParent(layerT, false);
        var gt = new DragGhostLayer(contT, FloatingWindowCatalog.Default(),
            () => { var go = new GameObject("g", typeof(RectTransform)); spawned.Add(go); return (RectTransform)go.transform; }, null);
        ct.AttachGhostLayer(gt);
        ct.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);
        ct.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);
        const string GT = "grp_tttttttttttttttttttttttttttttttt";
        ct.SetGroupId("A", GT); ct.SetGroupId("B", GT);
        Vector2 aRest = ct.RectOf("A").anchoredPosition;
        var modeT = ct.DragApplyDelta("A", aRest, aRest + new Vector2(60f, 0f), new Vector2(60f, 0f));
        if (modeT != FloatingWindowMath.DragMode.Translate) return $"S31c: precondition mode={modeT}";
        if (gt.ActiveCount != 0) return $"S31c: Translate should produce 0 ghosts (got {gt.ActiveCount}) — real-render, no ghost";

        // (d) Detach ⇒ NO ghost (CHANGED from #104's 1-ghost detach). Drag A beyond 256 off the sibling.
        var modeD = ct.DragApplyDelta("A", aRest, aRest + new Vector2(400f, 0f), new Vector2(340f, 0f));
        if (modeD != FloatingWindowMath.DragMode.Detach) return $"S31d: precondition mode={modeD}";
        if (gt.ActiveCount != 0) return $"S31d: Detach should produce 0 ghosts (got {gt.ActiveCount}) — real-render, no ghost";

        // (e) Clear() collapses ActiveCount to 0.
        ghostLayer.Clear();
        if (ghostLayer.ActiveCount != 0) return $"S31e: Clear left ActiveCount={ghostLayer.ActiveCount}";

        // (f) commit-on-release ⇒ ReleaseDrag clears the swap ghosts.
        c.DragApplyDelta("orders", orest, swapCursor, swapCursor - orest);   // paint 2 ghosts
        if (ghostLayer.ActiveCount != 2) return "S31f: precondition swap ghosts not painted";
        c.ReleaseDrag("orders", orest, swapCursor);
        if (ghostLayer.ActiveCount != 0) return "S31f: ReleaseDrag did not clear ghosts (commit-on-release rule)";
        return null;
    }

    // ---- 32. #105 / ADR-0020 / ADR-0024 §1: factory first-launch grouping — FormFactoryBaseGroup bundles
    //      the base dock cluster into ONE PLAIN island (ADR-0024 retires the Hakoniwa special). Covers
    //      DRAG-14: FormGroup mints ONE shared non-null groupId across every named member; a programmatic
    //      Spawn still mints nothing (S20 invariant); the cluster is a NORMAL island (cores drag freely —
    //      no core-lock); <2 live members ⇒ no group; first-launch placement is FLUSH (docked), not gapped.
    //      RED→GREEN litmus: no-op FormGroup's body ⇒ (b)/(c) FAIL; restore a non-zero placement gap ⇒ (e) FAIL.
    static string Section32_FactoryBaseGroup(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);

        // Spawn the 5 base dock windows ungrouped — mirrors BuildWorkspace.SpawnBaseDockWindows (the
        // factory grouping runs AFTER spawn, stamping the shared groupId onto already-live windows).
        c.Spawn(FloatingWindowCatalog.KIND_STARTUP,      "startup",      0,    0,    280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_BUYING_POWER, "buying_power", 280,  0,    280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,       "orders",       0,   -180,  280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS,    "positions",    280, -180,  280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_RUN_RESULT,   "run_result",   0,   -360,  280, 180, true);

        var ids = new[] { "startup", "buying_power", "orders", "positions", "run_result" };

        // (a) Pre: every base window is ungrouped — Spawn never mints a group (ADR-0019 D8, S20 invariant).
        foreach (var id in ids)
            if (c.GroupIdOf(id) != null)
                return $"S32a: {id} spawned with a groupId ({c.GroupIdOf(id)}) — Spawn must not mint";

        // (b) FormGroup mints ONE shared non-null groupId across every member.
        string g = c.FormGroup(ids);
        if (string.IsNullOrEmpty(g)) return "S32b: FormGroup returned null for 5 live members";
        foreach (var id in ids)
            if (c.GroupIdOf(id) != g)
                return $"S32b: {id} not in the factory group (got {c.GroupIdOf(id)}, expected {g})";

        // (c) The factory cluster is a PLAIN island (ADR-0024 §1): dragging a core member (startup)
        //     beyond D_DETACH_PX, off any sibling rect, DETACHES — there is no core-lock. The grid puts
        //     startup at (0,0) with buying_power flush to its right and orders flush below; dragging
        //     straight UP keeps the cursor off both, so the mode is Detach (not Swap).
        Vector2 s0 = c.RectOf("startup").anchoredPosition;
        var mode = c.DragApplyDelta("startup", s0, s0 + new Vector2(0f, 400f), new Vector2(0f, 400f));
        if (mode != FloatingWindowMath.DragMode.Detach)
            return $"S32c: factory core not free to detach (mode {mode}, expected Detach) — Hakoniwa core-lock must be retired";
        if (!Approx2(c.RectOf("startup").anchoredPosition, s0 + new Vector2(0f, 400f)))
            return "S32c: factory core did not follow cursor on detach (real-render)";

        // (d) <2 live members ⇒ FormGroup forms NO group (a 1-member group is meaningless). Unknown ids
        //     are ignored, not errors.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_STARTUP, "only", 0, 0, 280, 180, true);
        if (!c2.Has("only")) return "S32d: precondition — lone member did not spawn (the <2 path would be vacuous)";
        if (c2.FormGroup(new[] { "only", "missing" }) != null)
            return "S32d: FormGroup grouped fewer than 2 live members (1 live + 1 unknown id)";
        if (c2.GroupIdOf("only") != null)
            return $"S32d: lone member got a groupId ({c2.GroupIdOf("only")})";
        if (c2.FormGroup(null) != null) return "S32d: FormGroup(null) did not return null";

        // (e) The factory cluster must be DOCKED (flush, touching) — not just grouped. SpawnBaseDockWindows
        //     uses DockDefaultPlacement.ComputeFlushRects (the SAME call), so assert its 3×2 grid for n=5
        //     has flush-adjacent neighbours. Grid (row-major, y up-positive): [0][1][2] / [3][4]. Flush
        //     pairs: 0-1, 1-2 (horizontal) and 0-3, 1-4 (vertical). RED→GREEN: if the placement gap is
        //     restored to non-zero (DefaultGap), these go NOT-flush — proven by the negative check below.
        var flush = DockDefaultPlacement.ComputeFlushRects(5);
        if (flush.Count != 5) return $"S32e: ComputeFlushRects(5) returned {flush.Count} rects";
        const float eps = FloatingWindowController.DEFAULT_FLUSH_EPS;
        (int, int)[] adj = { (0, 1), (1, 2), (0, 3), (1, 4) };
        foreach (var (a, b) in adj)
            if (!FloatingWindowMath.IsFlushAdjacent(flush[a], flush[b], eps))
                return $"S32e: factory cluster tiles {a},{b} are NOT flush (windows look ungrouped / 隙間あり)";
        // Negative control: the gapped placement (DefaultGap) is NOT flush — proves flushness comes from
        // the gap=0 choice, not from any pair always testing flush (non-vacuous).
        var gapped = DockDefaultPlacement.ComputeRects(5);
        if (FloatingWindowMath.IsFlushAdjacent(gapped[0], gapped[1], eps))
            return "S32e: gapped placement reported flush — the flush assertion is vacuous (gap not honored)";
        return null;
    }

    // ---- 33. ADR-0024 §3: ComputeMagneticSnap pure arithmetic ----
    // Covers: DRAG-05/06 pure (flush within R_SNAP, orthogonal-overlap required, x/y independent, beyond→0).
    static string Section33_ComputeMagneticSnapPure()
    {
        float R = FloatingWindowMath.R_SNAP_PX;   // non-const local: also pins the value (no folded-constant unreachable warning)
        if (R != 96f) return $"S33: R_SNAP_PX expected 96 (got {R})";
        var moving = new FloatingWindowMath.DockRect(0f, 0f, 100f, 80f);   // L0 R100 T0 B-80

        // (a) flush-right within R: other.Left=130 (gap 30 ≤ 96), y-overlap=80 ⇒ snap +30 on x.
        var snapR = FloatingWindowMath.ComputeMagneticSnap(moving,
            new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(130f, 0f, 100f, 80f) }, R);
        if (!Approx2(snapR, new Vector2(30f, 0f))) return $"S33a: flush-right snap wrong (got {snapR})";

        // (b) flush-left: other.Right=-30 ⇒ snap -30 on x.
        var snapL = FloatingWindowMath.ComputeMagneticSnap(moving,
            new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(-130f, 0f, 100f, 80f) }, R);
        if (!Approx2(snapL, new Vector2(-30f, 0f))) return $"S33b: flush-left snap wrong (got {snapL})";

        // (c) flush below (moving.bottom ↔ other.top): other at top-left (0,-110) ⇒ other.Top=-110 ⇒ snap -30 on y.
        var snapD = FloatingWindowMath.ComputeMagneticSnap(moving,
            new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(0f, -110f, 100f, 80f) }, R);
        if (!Approx2(snapD, new Vector2(0f, -30f))) return $"S33c: flush-below snap wrong (got {snapD})";

        // (d) beyond R: other.Left=250 (gap 150 > 96) ⇒ no snap.
        var none = FloatingWindowMath.ComputeMagneticSnap(moving,
            new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(250f, 0f, 100f, 80f) }, R);
        if (!Approx2(none, Vector2.zero)) return $"S33d: beyond-R should not snap (got {none})";

        // (e) no orthogonal overlap ⇒ no snap (a corner near-miss does not pull).
        var noOverlap = FloatingWindowMath.ComputeMagneticSnap(moving,
            new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(130f, -200f, 100f, 80f) }, R);
        if (!Approx2(noOverlap, Vector2.zero)) return $"S33e: no-overlap should not snap (got {noOverlap})";

        // (f) x and y independent: one neighbour pulls x, another pulls y ⇒ combined offset.
        var both = FloatingWindowMath.ComputeMagneticSnap(moving, new List<FloatingWindowMath.DockRect>
        {
            new FloatingWindowMath.DockRect(130f, 0f, 100f, 80f),    // x +30
            new FloatingWindowMath.DockRect(0f, -110f, 100f, 80f),   // y -30
        }, R);
        if (!Approx2(both, new Vector2(30f, -30f))) return $"S33f: independent x/y snap wrong (got {both})";

        // (g) guards: R ≤ 0 / null / empty ⇒ zero.
        if (!Approx2(FloatingWindowMath.ComputeMagneticSnap(moving, new List<FloatingWindowMath.DockRect> { new FloatingWindowMath.DockRect(130f, 0f, 100f, 80f) }, 0f), Vector2.zero))
            return "S33g: R=0 not guarded";
        if (!Approx2(FloatingWindowMath.ComputeMagneticSnap(moving, null, R), Vector2.zero)) return "S33g: null others not guarded";
        if (!Approx2(FloatingWindowMath.ComputeMagneticSnap(moving, new List<FloatingWindowMath.DockRect>(), R), Vector2.zero)) return "S33g: empty others not guarded";
        return null;
    }

    // ---- 34. ADR-0024 §4: ResolveNearestFlush pure arithmetic ----
    // Covers: DRAG-07 pure (4 候補から最小移動の flush offset / 直交軸 overlap ≥ 1px / tie-break x 優先 / 無効→0).
    static string Section34_ResolveNearestFlushPure()
    {
        var moving = new FloatingWindowMath.DockRect(0f, 0f, 100f, 80f);   // L0 R100 T0 B-80

        // (a) overlapping on the right: nearest flush is right→left (move -20 on x).
        var t1 = new FloatingWindowMath.DockRect(80f, 0f, 100f, 80f);      // L80 R180
        var f1 = FloatingWindowMath.ResolveNearestFlush(moving, t1);
        if (!Approx2(f1, new Vector2(-20f, 0f))) return $"S34a: nearest flush right wrong (got {f1})";

        // (b) tie-break x over y: moving 100×100, target offset so |x-flush| == |y-flush| (both 40).
        var m2 = new FloatingWindowMath.DockRect(0f, 0f, 100f, 100f);      // L0 R100 T0 B-100
        var t2 = new FloatingWindowMath.DockRect(60f, -60f, 100f, 100f);   // L60 R160 T-60 B-160
        var f2 = FloatingWindowMath.ResolveNearestFlush(m2, t2);
        if (!Approx2(f2, new Vector2(-40f, 0f))) return $"S34b: tie should resolve to x (got {f2})";

        // (c) overlapping on the left: nearest flush is left→right (move +20 on x).
        var t3 = new FloatingWindowMath.DockRect(-80f, 0f, 100f, 80f);     // L-80 R20
        var f3 = FloatingWindowMath.ResolveNearestFlush(moving, t3);
        if (!Approx2(f3, new Vector2(20f, 0f))) return $"S34c: nearest flush left wrong (got {f3})";

        // (d) no orthogonal overlap on either axis ⇒ zero (degenerate; caller falls back to plain translate).
        var t4 = new FloatingWindowMath.DockRect(300f, -300f, 50f, 50f);
        var f4 = FloatingWindowMath.ResolveNearestFlush(moving, t4);
        if (!Approx2(f4, Vector2.zero)) return $"S34d: disjoint target should give zero (got {f4})";
        return null;
    }

    // ---- 35. ADR-0024 §3: SpringEase / SpringRectAt pure curve ----
    // Covers: DRAG-10 (ease-out-back: e(0)=0, e(1)=1, single overshoot peak EXACTLY 8% at t=0.6; rect interp).
    static string Section35_SpringEasePure()
    {
        int springMs = FloatingWindowMath.SPRING_DURATION_MS;   // non-const locals (avoid folded-constant unreachable warning)
        float overshoot = FloatingWindowMath.SPRING_OVERSHOOT_RATIO;
        if (springMs != 200) return $"S35: SPRING_DURATION_MS expected 200 (got {springMs})";
        if (!Approx(overshoot, 0.08f)) return $"S35: SPRING_OVERSHOOT_RATIO expected 0.08 (got {overshoot})";

        if (!Approx(FloatingWindowMath.SpringEase(0f), 0f)) return "S35a: e(0) != 0";
        if (!Approx(FloatingWindowMath.SpringEase(1f), 1f)) return "S35a: e(1) != 1";
        if (!Approx(FloatingWindowMath.SpringEase(-1f), 0f)) return "S35a: e(<0) not clamped to 0";
        if (!Approx(FloatingWindowMath.SpringEase(2f), 1f)) return "S35a: e(>1) not clamped to 1";

        // (b) Peak overshoot EXACTLY 8% at t=0.6 (s=1.5 tuned so 4s³/(27(s+1)²)=0.08).
        if (!Approx(FloatingWindowMath.SpringEase(0.6f), 1.08f)) return $"S35b: e(0.6) != 1.08 (got {FloatingWindowMath.SpringEase(0.6f)})";

        // (c) 0.6 is the global max over [0,1]; no sample exceeds 1.08 (single overshoot, then settle).
        float maxE = 0f;
        for (int i = 0; i <= 100; i++) maxE = Mathf.Max(maxE, FloatingWindowMath.SpringEase(i / 100f));
        if (maxE > 1.08f + EPS) return $"S35c: peak overshoot exceeds 8% (max {maxE})";
        if (maxE < 1.08f - 1e-3f) return $"S35c: peak overshoot below 8% (max {maxE})";

        // (d) SpringRectAt interpolates pos + size; t=0 ⇒ from, t=1 ⇒ to, t=0.6 ⇒ 8% past target.
        var from = new FloatingWindowMath.DockRect(0f, 0f, 100f, 100f);
        var to   = new FloatingWindowMath.DockRect(100f, 0f, 200f, 100f);
        var r0 = FloatingWindowMath.SpringRectAt(from, to, 0f);
        if (!Approx2(r0.topLeft, from.topLeft) || !Approx2(r0.size, from.size)) return "S35d: t=0 != from";
        var r1 = FloatingWindowMath.SpringRectAt(from, to, 1f);
        if (!Approx2(r1.topLeft, to.topLeft) || !Approx2(r1.size, to.size)) return "S35d: t=1 != to";
        var rPeak = FloatingWindowMath.SpringRectAt(from, to, 0.6f);
        if (!Approx(rPeak.topLeft.x, 108f)) return $"S35d: overshoot x != 108 (got {rPeak.topLeft.x})";   // 0 + 1.08*100
        if (!Approx(rPeak.size.x, 208f)) return $"S35d: overshoot w != 208 (got {rPeak.size.x})";          // 100 + 1.08*100
        return null;
    }

    // ---- 36. ADR-0024 §3/§7: in-drag magnetic snap real-render wiring ----
    // Covers: DRAG-05 (TRANSLATE: island outer edge within R_SNAP of an external window ⇒ flush snap + stickiness)
    //         DRAG-06 (DETACH: dragged edge within R_SNAP ⇒ flush snap).
    static string Section36_MagneticSnapRenderWiring(List<GameObject> spawned)
    {
        // (a) DRAG-05 Translate snap: singleton A dragged so its right edge comes within R_SNAP of E.left.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "A", 0,   0, 280, 180, true);   // dragged singleton
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,  "E", 400, 0, 280, 180, true);   // external, ungrouped
        Vector2 aStart = c.RectOf("A").anchoredPosition;   // (0,0); A.right=280, E.left=400
        // offset (90,0) ⇒ A.right=370, gap to E.left=400 is 30 ≤ 96 ⇒ snap +30 ⇒ A lands at (120,0) flush.
        var mode = c.DragApplyDelta("A", aStart, aStart + new Vector2(90f, 0f), new Vector2(90f, 0f));
        if (mode != FloatingWindowMath.DragMode.Translate) return $"S36a: expected Translate (got {mode})";
        if (!Approx2(c.RectOf("A").anchoredPosition, new Vector2(120f, 0f))) return $"S36a: magnetic snap did not flush A to E (got {c.RectOf("A").anchoredPosition})";

        // stickiness: drag a bit further (offset 100) — A.right=380, gap 20 ≤ 96 ⇒ still snapped to (120,0).
        c.DragApplyDelta("A", aStart, aStart + new Vector2(100f, 0f), new Vector2(10f, 0f));
        if (!Approx2(c.RectOf("A").anchoredPosition, new Vector2(120f, 0f))) return $"S36a: stickiness broke (got {c.RectOf("A").anchoredPosition})";

        // off the magnet: offset 10 ⇒ A.right=290, gap 110 > 96 ⇒ free at (10,0).
        c.DragApplyDelta("A", aStart, aStart + new Vector2(10f, 0f), new Vector2(-90f, 0f));
        if (!Approx2(c.RectOf("A").anchoredPosition, new Vector2(10f, 0f))) return $"S36a: did not release from magnet beyond R_SNAP (got {c.RectOf("A").anchoredPosition})";

        // (b) DRAG-06 Detach snap: drag A beyond D_DETACH toward a far E, landing within R_SNAP of E.left.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS, "A", 0,   0, 280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDER,  "E", 600, 0, 280, 180, true);   // E.left=600
        Vector2 a2 = c2.RectOf("A").anchoredPosition;
        // offset (290,0): dist 290 ≥ 256 ⇒ Detach; A.right=570, gap to 600 = 30 ≤ 96 ⇒ snap +30 ⇒ (320,0) flush.
        var mode2 = c2.DragApplyDelta("A", a2, a2 + new Vector2(290f, 0f), new Vector2(290f, 0f));
        if (mode2 != FloatingWindowMath.DragMode.Detach) return $"S36b: expected Detach (got {mode2})";
        if (!Approx2(c2.RectOf("A").anchoredPosition, new Vector2(320f, 0f))) return $"S36b: detach magnetic snap did not flush A to E (got {c2.RectOf("A").anchoredPosition})";
        return null;
    }

    // ---- 37. ADR-0024 §4: release-position overlap → nearest-flush + merge ----
    // Covers: DRAG-07 (Translate release overlapping island Y ⇒ snap to nearest flush + merge cascade /
    //         Detach release overlapping Y ⇒ A snaps flush + joins Y via singleton merge / ADR-0019 D4 retained).
    static string Section37_ReleaseOverlapMerge(List<GameObject> spawned)
    {
        // (a) TRANSLATE release overlapping a different island Y ⇒ snap to nearest flush + merge into Y.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,     "A",  0,   0,   280, 180, true);   // dragged singleton
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "Y1", 150, 0,   280, 180, true);   // Y island, within 256 of A
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Y2", 150, 180, 280, 180, true);   // Y2 above Y1, flush
        const string GY = "grp_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy";
        c.SetGroupId("Y1", GY); c.SetGroupId("Y2", GY);
        Vector2 aStart = c.RectOf("A").anchoredPosition;   // (0,0)
        Vector2 cursor = new Vector2(200f, -50f);          // over Y1 ([150,430]×[-180,0]); dist≈206 < 256 ⇒ Translate
        var mode = c.ReleaseDrag("A", aStart, cursor);
        if (mode != FloatingWindowMath.DragMode.Translate) return $"S37a: expected Translate (got {mode})";
        // nearest flush of A(shifted to (200,-50)) against Y1 ⇒ top→bottom (A below Y1): applied (200,-180).
        if (!Approx2(c.RectOf("A").anchoredPosition, new Vector2(200f, -180f))) return $"S37a: A did not snap to nearest flush (got {c.RectOf("A").anchoredPosition})";
        if (c.GroupIdOf("A") != GY) return $"S37a: A did not merge into Y (got {c.GroupIdOf("A")})";
        if (c.GroupIdOf("Y1") != GY || c.GroupIdOf("Y2") != GY) return "S37a: Y island lost its id on merge";

        // (b) DETACH release overlapping a different island Y ⇒ A leaves its island, snaps flush to Y, joins Y.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDER,     "A",   0,   0,    280, 180, true);   // dragged
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "Asib",0,   -200, 280, 180, true);   // A's island sibling
        const string GI = "grp_iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii";
        c2.SetGroupId("A", GI); c2.SetGroupId("Asib", GI);
        c2.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Y1", 400, -200, 280, 180, true);    // external Y island
        c2.Spawn(FloatingWindowCatalog.KIND_BUYING_POWER, "Y2", 400, -20, 280, 180, true);  // Y2 above Y1, flush
        const string GY2 = "grp_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        c2.SetGroupId("Y1", GY2); c2.SetGroupId("Y2", GY2);
        Vector2 a2 = c2.RectOf("A").anchoredPosition;     // (0,0)
        Vector2 cursor2 = new Vector2(450f, -260f);       // over Y1 ([400,680]×[-380,-200]); dist≈521 ≥ 256 ⇒ Detach
        var mode2 = c2.ReleaseDrag("A", a2, cursor2);
        if (mode2 != FloatingWindowMath.DragMode.Detach) return $"S37b: expected Detach (got {mode2})";
        if (c2.GroupIdOf("A") != GY2) return $"S37b: detached A did not join Y (got {c2.GroupIdOf("A")})";
        if (c2.GroupIdOf("Asib") != null) return $"S37b: A's old island remnant did not dissolve (got {c2.GroupIdOf("Asib")})";
        if (c2.GroupIdOf("Y1") != GY2 || c2.GroupIdOf("Y2") != GY2) return "S37b: Y island lost its id";

        // (c) ADR-0019 D4 retained: TRANSLATE release on EMPTY space that happens to land flush (via the
        //     magnetic snap) attaches. Singleton A dragged flush-right to E ⇒ mint a group.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer3, out _);
        var c3 = MakeController(spawned, layer3);
        c3.Spawn(FloatingWindowCatalog.KIND_ORDERS, "A", 0,   0, 280, 180, true);
        c3.Spawn(FloatingWindowCatalog.KIND_ORDER,  "E", 290, 0, 280, 180, true);   // E.left=290; A.right=280 (gap 10)
        Vector2 a3 = c3.RectOf("A").anchoredPosition;
        // offset (5,0) ⇒ A.right=285, gap 5 ≤ 96 ⇒ snap +5 ⇒ A flush at (10,0); empty (cursor not over E).
        var mode3 = c3.ReleaseDrag("A", a3, a3 + new Vector2(5f, 0f));
        if (mode3 != FloatingWindowMath.DragMode.Translate) return $"S37c: expected Translate (got {mode3})";
        string g3 = c3.GroupIdOf("A");
        if (string.IsNullOrEmpty(g3)) return "S37c: incidental flush attach (ADR-0019 D4) did not mint a group";
        if (c3.GroupIdOf("E") != g3) return "S37c: flush partner E did not adopt the minted group";

        // (d) ISLAND-WIDE merge: a multi-member island docks flush to an external window via a NON-dragged
        //     member's edge. A (dragged, top) + B (bottom) island; the island shifts right so B's right
        //     edge kisses Y — but A does NOT y-overlap Y. The merge must still fire (the old dragged-only
        //     flush scan would miss it). Translate EMPTY branch (cursor in empty space) + magnetic snap.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer4, out _);
        var c4 = MakeController(spawned, layer4);
        c4.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);   // dragged (top)
        c4.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);   // island sibling (bottom)
        const string GD = "grp_dddddddddddddddddddddddddddddddd";
        c4.SetGroupId("A", GD); c4.SetGroupId("B", GD);
        c4.Spawn(FloatingWindowCatalog.KIND_ORDER, "Y", 300, -200, 280, 180, true);     // external, aligned with B only
        Vector2 a4 = c4.RectOf("A").anchoredPosition;
        // offset (15,0): island bbox right=280→295, gap 5 ≤ R_SNAP ⇒ snap +5 ⇒ island shifts (20,0).
        // B.right=300=Y.left (flush, y-overlap with Y); A.right=300 but A has NO y-overlap with Y.
        var mode4 = c4.ReleaseDrag("A", a4, a4 + new Vector2(15f, 0f));
        if (mode4 != FloatingWindowMath.DragMode.Translate) return $"S37d: expected Translate (got {mode4})";
        if (!Approx2(c4.RectOf("B").anchoredPosition, new Vector2(20f, -200f))) return $"S37d: island did not snap flush via B (got {c4.RectOf("B").anchoredPosition})";
        if (c4.GroupIdOf("Y") != GD) return $"S37d: island-wide merge missed — Y not absorbed via B's edge (got {c4.GroupIdOf("Y")})";
        if (c4.GroupIdOf("A") != GD || c4.GroupIdOf("B") != GD) return "S37d: island lost its id on merge";

        // (e) OVERLAP-merge is driven by the cursor-over-Y RULE, not a member-level flush rescan: a
        //     multi-member island dropped ON Y merges with Y even when no member ends up geometrically
        //     flush after the bbox snap (review fix — CommitMergeWithTarget). 2-member island, cursor over
        //     external singleton Y, within 256 ⇒ Translate overlap branch.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer5, out _);
        var c5 = MakeController(spawned, layer5);
        c5.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);   // dragged
        c5.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);   // island sibling
        const string GE = "grp_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        c5.SetGroupId("A", GE); c5.SetGroupId("B", GE);
        c5.Spawn(FloatingWindowCatalog.KIND_ORDER, "Y", 200, -50, 280, 180, true);      // external singleton
        Vector2 a5 = c5.RectOf("A").anchoredPosition;
        Vector2 cur5 = new Vector2(230f, -100f);   // over Y ([200,480]×[-230,-50]); dist≈251 < 256 ⇒ Translate
        var mode5 = c5.ReleaseDrag("A", a5, cur5);
        if (mode5 != FloatingWindowMath.DragMode.Translate) return $"S37e: expected Translate (got {mode5})";
        if (c5.GroupIdOf("Y") != GE) return $"S37e: cursor-over-Y multi-member merge missed (got {c5.GroupIdOf("Y")})";
        if (c5.GroupIdOf("A") != GE || c5.GroupIdOf("B") != GE) return "S37e: island lost its id on overlap merge";

        // (f) OVERLAP snap target is the TARGET ISLAND's OUTER bbox, NOT the single frontmost member rect
        //     under the cursor (yRect). Cursor over the BOTTOM member Y2 of a 2-member vertical island:
        //     snapping flush to Y2's own edge would land A on the INTERNAL seam (y∈[-180,0] = on top of
        //     Y1), a wrong placement; the commit must snap to the island's outer bbox (flush ABOVE the
        //     outer top edge). Review fix (FindOverlapWindowAtCursor returns yRect of one member only).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer6, out _);
        var c6 = MakeController(spawned, layer6);
        c6.Spawn(FloatingWindowCatalog.KIND_ORDER,     "A",  0,   0,    280, 180, true);   // dragged singleton, rest (0,0)
        c6.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "Y1", 200, 0,    280, 180, true);   // island TOP    y[-180,0]
        c6.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Y2", 200, -180, 280, 180, true);   // island BOTTOM y[-360,-180]
        const string GF = "grp_ffffffffffffffffffffffffffffffff";
        c6.SetGroupId("Y1", GF); c6.SetGroupId("Y2", GF);   // island bbox = x[200,480] y[-360,0]
        // dragStart (100,-110), cursor (300,-200) over Y2; offset (200,-90), |offset|≈219 < 256 ⇒ Translate.
        // A_shifted bbox x[200,480] y[-270,-90]. Nearest flush to Y2's edge = +90 (A.bottom→Y2.top=-180 ⇒
        // A lands y[-180,0] = the Y1 seam, WRONG). Nearest flush to the island bbox = +270 (A.bottom→bbox.top=0
        // ⇒ A lands y[0,180], flush ABOVE the outer top edge, CORRECT). anchoredPosition must be (200,180).
        var mode6 = c6.ReleaseDrag("A", new Vector2(100f, -110f), new Vector2(300f, -200f));
        if (mode6 != FloatingWindowMath.DragMode.Translate) return $"S37f: expected Translate (got {mode6})";
        if (!Approx2(c6.RectOf("A").anchoredPosition, new Vector2(200f, 180f)))
            return $"S37f: snapped to internal member edge, not the island outer bbox (got {c6.RectOf("A").anchoredPosition}, want (200,180))";
        if (c6.GroupIdOf("A") != GF) return $"S37f: A did not merge into the target island (got {c6.GroupIdOf("A")})";
        if (c6.GroupIdOf("Y1") != GF || c6.GroupIdOf("Y2") != GF) return "S37f: Y island lost its id on merge";
        return null;
    }

    // ---- 38. ADR-0024 §8: ESC cancel during drag ----
    // Covers: DRAG-09 (ESC ⇒ live render reverts to rest, state (groupId / persisted rect) untouched,
    //         MouseUp commits nothing).
    static string Section38_EscCancel(List<GameObject> spawned)
    {
        // (a) Translate then ESC: island reverts to rest; groupId unchanged; ReleaseDrag commits nothing.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "A", 0, 0,    280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "B", 0, -200, 280, 180, true);
        const string G = "grp_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        c.SetGroupId("A", G); c.SetGroupId("B", G);
        Vector2 a0 = c.RectOf("A").anchoredPosition, b0 = c.RectOf("B").anchoredPosition;
        Vector2 dragStart = a0;
        c.BeginDrag("A", dragStart);
        c.DragApplyDelta("A", dragStart, a0 + new Vector2(80f, 0f), new Vector2(80f, 0f));   // translate the island
        if (Approx2(c.RectOf("A").anchoredPosition, a0)) return "S38a: precondition — island did not move before ESC";
        c.CancelDrag("A");
        if (!Approx2(c.RectOf("A").anchoredPosition, a0)) return "S38a: ESC did not revert dragged to rest";
        if (!Approx2(c.RectOf("B").anchoredPosition, b0)) return "S38a: ESC did not revert sibling to rest";
        if (c.GroupIdOf("A") != G || c.GroupIdOf("B") != G) return "S38a: ESC mutated groupId (state must be untouched)";
        // The following MouseUp commits NOTHING.
        c.ReleaseDrag("A", dragStart, a0 + new Vector2(80f, 0f));
        if (!Approx2(c.RectOf("A").anchoredPosition, a0)) return "S38a: post-ESC MouseUp committed a move";
        if (c.GroupIdOf("A") != G) return "S38a: post-ESC MouseUp committed a group change";

        // (b) Detach drag then ESC: dragged reverts to rest; groupId never went null (commit skipped).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "P", 0, 0,    280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Q", 0, -200, 280, 180, true);
        const string G2 = "grp_ffffffffffffffffffffffffffffffff";
        c2.SetGroupId("P", G2); c2.SetGroupId("Q", G2);
        Vector2 p0 = c2.RectOf("P").anchoredPosition;
        c2.BeginDrag("P", p0);
        c2.DragApplyDelta("P", p0, p0 + new Vector2(0f, 400f), new Vector2(0f, 400f));   // detach drag (beyond 256)
        if (Approx2(c2.RectOf("P").anchoredPosition, p0)) return "S38b: precondition — detached dragged did not move";
        c2.CancelDrag("P");
        if (!Approx2(c2.RectOf("P").anchoredPosition, p0)) return "S38b: ESC did not revert detached dragged to rest";
        if (c2.GroupIdOf("P") != G2 || c2.GroupIdOf("Q") != G2) return "S38b: ESC during detach changed groupId (must be untouched)";
        c2.ReleaseDrag("P", p0, p0 + new Vector2(0f, 400f));
        if (c2.GroupIdOf("P") != G2) return "S38b: post-ESC MouseUp committed the detach (must skip)";
        return null;
    }

    // ---- 39. ADR-0024 §3: spring fire-point wiring (non-vacuous) ----
    // Covers: DRAG-10 wiring (the injected spring animator fires on swap / translate / detach commit and ESC).
    static string Section39_SpringFireWiring(List<GameObject> spawned)
    {
        int fires = 0;
        Action<RectTransform, FloatingWindowMath.DockRect, FloatingWindowMath.DockRect> recorder =
            (rt, from, to) => fires++;

        // (a) Swap commit fires the spring (both windows move ⇒ ≥1 fire).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.SetSpringAnimator(recorder);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "orders",    0,  0, 280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "positions", 30, 0, 300, 200, true);
        const string G = "grp_ssssssssssssssssssssssssssssssss";
        c.SetGroupId("orders", G); c.SetGroupId("positions", G);
        fires = 0;
        c.ReleaseDrag("orders", Vector2.zero, new Vector2(35f, -30f));   // swap commit
        if (fires == 0) return "S39a: swap commit did not fire the spring";

        // (b) Detach commit fires the spring.
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer2, out _);
        var c2 = MakeController(spawned, layer2);
        c2.SetSpringAnimator(recorder);
        c2.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "P", 0, 0,    280, 180, true);
        c2.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Q", 0, -200, 280, 180, true);
        const string G2 = "grp_ssssssssssssssssssssssssssssss22";
        c2.SetGroupId("P", G2); c2.SetGroupId("Q", G2);
        fires = 0;
        c2.ReleaseDrag("P", Vector2.zero, new Vector2(0f, 400f));   // detach commit
        if (fires == 0) return "S39b: detach commit did not fire the spring";

        // (c) ESC fires the spring (revert animation).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer3, out _);
        var c3 = MakeController(spawned, layer3);
        c3.SetSpringAnimator(recorder);
        c3.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "X", 0, 0,    280, 180, true);
        c3.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "Z", 0, -200, 280, 180, true);
        const string G3 = "grp_ssssssssssssssssssssssssssssss33";
        c3.SetGroupId("X", G3); c3.SetGroupId("Z", G3);
        c3.BeginDrag("X", Vector2.zero);
        c3.DragApplyDelta("X", Vector2.zero, new Vector2(80f, 0f), new Vector2(80f, 0f));
        fires = 0;
        c3.CancelDrag("X");
        if (fires == 0) return "S39c: ESC cancel did not fire the spring (revert animation)";

        // (d) BeginDrag STOPS any in-flight tween on every island member (review fix: a re-grab within the
        //     200ms commit/ESC tween must settle the leftover tween so it cannot fight the new drag).
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer4, out _);
        var c4 = MakeController(spawned, layer4);
        var stopped = new List<string>();
        c4.SetSpringAnimator(recorder, rt => { if (rt != null) stopped.Add(rt.name); });
        c4.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "M", 0, 0,    280, 180, true);
        c4.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "N", 0, -200, 280, 180, true);
        const string G4 = "grp_ssssssssssssssssssssssssssssss44";
        c4.SetGroupId("M", G4); c4.SetGroupId("N", G4);
        c4.BeginDrag("M", Vector2.zero);
        // Both island members (M, N) must have had their tweens stopped+settled at drag start.
        string stoppedList = string.Join(",", stopped);
        if (!stopped.Contains("W_M") || !stopped.Contains("W_N"))
            return "S39d: BeginDrag did not stop in-flight tweens on island members (stopped: " + stoppedList + ")";
        return null;
    }

    // ---- 40. ADR-0024 §8 / findings 0088 §8: chart:<id> drags like any window ----
    // Covers: DRAG-13 (chart:<id> can form an island with a base window via flush attach, then translate
    //         and detach exactly like the others — programmatic spawn keeps groupId=null until drag-release).
    static string Section40_ChartEquivalence(List<GameObject> spawned)
    {
        BuildCanvasStack(spawned, out _, out _, out RectTransform layer, out _);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_STARTUP, "startup",   0,   0, 280, 180, true);
        c.Spawn(FloatingWindowCatalog.KIND_CHART,   "chart:abc", 280, 0, 280, 180, true);   // flush-right of startup
        // (a) programmatic chart spawn is ungrouped (attach is drag-release exclusive).
        if (c.GroupIdOf("chart:abc") != null) return "S40a: chart spawned with a groupId (must be null)";

        // (b) chart forms an island with startup via flush attach (chart is NOT special).
        c.SnapOnRelease("chart:abc");
        string g = c.GroupIdOf("chart:abc");
        if (string.IsNullOrEmpty(g)) return "S40b: chart did not flush-attach to startup";
        if (c.GroupIdOf("startup") != g) return "S40b: startup did not join the chart's island";

        // (c) chart translates the whole island (cursor outside island, < 256). startup at (0,0,280,180);
        //     chart at (280,0). Drag down-RIGHT so the cursor clears startup's rect (x>280) — a straight-
        //     down drag would land on startup's inclusive right edge and trigger Swap instead.
        Vector2 chartRest = c.RectOf("chart:abc").anchoredPosition;
        Vector2 startupRest = c.RectOf("startup").anchoredPosition;
        Vector2 trOffset = new Vector2(60f, -60f);   // cursor (340,-60): clear of startup, dist≈85 < 256
        var mode = c.DragApplyDelta("chart:abc", chartRest, chartRest + trOffset, trOffset);
        if (mode != FloatingWindowMath.DragMode.Translate) return $"S40c: chart drag not Translate (got {mode})";
        if (!Approx2(c.RectOf("chart:abc").anchoredPosition, chartRest + trOffset)) return "S40c: chart did not translate";
        if (!Approx2(c.RectOf("startup").anchoredPosition, startupRest + trOffset)) return "S40c: island sibling (startup) did not follow chart";

        // (d) chart detaches beyond D_DETACH ⇒ chart leaves the island, remnant (startup) dissolves. Drag
        //     straight down past 256: cursor (280,-300) is below startup (y<-180) so it stays off the rect.
        var mode2 = c.ReleaseDrag("chart:abc", chartRest, chartRest + new Vector2(0f, -300f));
        if (mode2 != FloatingWindowMath.DragMode.Detach) return $"S40d: chart detach not Detach (got {mode2})";
        if (c.GroupIdOf("chart:abc") != null) return "S40d: chart did not detach (groupId not cleared)";
        if (c.GroupIdOf("startup") != null) return "S40d: remnant startup did not dissolve";
        return null;
    }

    static string Section19_SceneWiringBackPlane()
    {
        Scene scene = EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        if (!scene.IsValid()) return "S19: failed to open the workspace scene";

        RectTransform content = null, dockLayer = null, floatingLayer = null;
        foreach (var go in scene.GetRootGameObjects())
            foreach (var rt in go.GetComponentsInChildren<RectTransform>(true))
            {
                if (rt.name == "Content") content = rt;
                else if (rt.name == "DockLayer") dockLayer = rt;
                else if (rt.name == "FloatingWindowLayer") floatingLayer = rt;
            }
        if (content == null) return "S19: Content not found in scene";
        if (dockLayer == null) return "S19: DockLayer not found in scene (scene not rebuilt for #103?)";
        if (floatingLayer == null) return "S19: FloatingWindowLayer not found in scene";

        if (dockLayer.parent != content) return "S19: DockLayer is not a child of Content";
        if (floatingLayer.parent != content) return "S19: FloatingWindowLayer is not a child of Content";
        // earlier sibling = drawn behind (uGUI draws child 0 first). This is the REAL depth ordering.
        if (dockLayer.GetSiblingIndex() >= floatingLayer.GetSiblingIndex())
            return $"S19: DockLayer sibling {dockLayer.GetSiblingIndex()} is not BEHIND FloatingWindowLayer {floatingLayer.GetSiblingIndex()}";

        BackcastWorkspaceRoot root = null;
        foreach (var go in scene.GetRootGameObjects())
        {
            root = go.GetComponentInChildren<BackcastWorkspaceRoot>(true);
            if (root != null) break;
        }
        if (root == null) return "S19: BackcastWorkspaceRoot not found in scene";

        var so = new SerializedObject(root);
        var dockRef = so.FindProperty("_dockLayer");
        var floatRef = so.FindProperty("_floatingLayer");
        if (dockRef == null) return "S19: _dockLayer serialized field missing on BackcastWorkspaceRoot";
        if (dockRef.objectReferenceValue != dockLayer) return "S19: _dockLayer ref does not point to the scene DockLayer";
        if (floatRef == null || floatRef.objectReferenceValue != floatingLayer)
            return "S19: _floatingLayer ref does not point to the scene FloatingWindowLayer";
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

    // #103 (ADR-0018): build Viewport -> Content -> [DockLayer(back, sibling 0), FloatingWindowLayer(front,
    // sibling 1)], mirroring the production scene (BackcastWorkspaceSceneBuilder creates DockLayer FIRST).
    // The InfiniteCanvasController parallax-drives ONLY the floating layer at `floatingFactor`; the dock
    // layer is a plain Content child (1.0×, zero offset). One controller per layer is wired by the caller.
    static void BuildTwoPlaneStack(List<GameObject> spawned, float floatingFactor,
                                   out RectTransform viewport, out RectTransform content,
                                   out RectTransform dockLayer, out RectTransform floatingLayer,
                                   out InfiniteCanvasController canvas)
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

        dockLayer = NewIdentityLayer("DockLayer", content);            // sibling 0 = backmost (created first)
        floatingLayer = NewIdentityLayer("FloatingWindowLayer", content);  // sibling 1 = front

        canvas = new InfiniteCanvasController(content, floatingLayer, floatingFactor);
    }

    // An identity (centre anchor/pivot, zero offset) RectTransform layer under `parent`. The child created
    // earlier gets the lower sibling index (= drawn behind), so call order encodes back-to-front.
    static RectTransform NewIdentityLayer(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
        return rt;
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

    // #104 (ADR-0019 / findings 0082 §1, §11): factory for floatingWindows entries carrying a persisted
    // groupId — the new Slice A round-trip dimension. groupId-null callers keep using W() (above).
    static FloatingWindowLayout WG(string id, string kind, float x, float y, float w, float h, int z, bool visible, string groupId) =>
        new FloatingWindowLayout(id, kind, x, y, w, h, z, visible, groupId);

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
