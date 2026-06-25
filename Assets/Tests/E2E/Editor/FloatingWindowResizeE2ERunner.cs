// FloatingWindowResizeE2ERunner.cs — issue #139 / ADR-0030 floating-window RESIZE surface E2E gate
// (台本: 同ディレクトリの FloatingWindowResizeE2ERunner.md). New runner (NOT folded into
// FloatingWindowE2ERunner) so the resize concern carries its own Action-ID namespace RESIZE-NN and the
// existing 41-section drag/snap/group runner stays untouched. Python-FREE, render-FREE, real-root-FREE:
// it reflects a headless Viewport→Content→FloatingWindowLayer RectTransform tree and drives
// FloatingWindowController's resize session + FloatingWindowMath.ResizeIslandPush directly.
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FloatingWindowResizeE2ERunner.Run -logFile <abs log>
//   # expect: [E2E FLOATING WINDOW RESIZE PASS] ... / exit=0  (確認は Bash `grep -a "RESIZE-"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// AFK は math + controller を直駆動で証明する。唯一 AFK 化できない残置 = 実つまみジェスチャ発火（カーソルを
// つまみに当ててドラッグ→resize）は raycast+pointer glue が batchmode 駆動不能なので owner HITL 1 行
// （RESIZE-13-HITL・unity-afk-gesture-glue-hitl-only / ADR-0029・#136 と同じ責務分割）。
//
// SECTIONS (Action-ID RESIZE-NN):
//   1. pure ResizeIslandPush: top-left fixed, right/bottom grow (grow + shrink, single window)     [RESIZE-01]
//   2. pure: island flush FOLLOW push-out — symmetric (grow=push/shrink=pull), chained, x/y indep,
//      members move only (own size kept)                                                            [RESIZE-02]
//   3. pure: island scope — non-flush members + left/top neighbours never move (negative control)   [RESIZE-03]
//   4. controller real rect: engine==math top-left-fixed right-bottom grow (anchored fixed, size up) [RESIZE-04]
//   5. controller real rect: flush follow (symmetric, chained) + non-island neighbour unmoved (neg)  [RESIZE-05]
//   6. controller: min clamp (spec.minSize floor, no max)                                            [RESIZE-06]
//   7. controller: ESC revert (resized size + pushed members back to rest, state untouched, no commit) [RESIZE-07]
//   8. Body child follows parent sizeDelta (stretch anchors, ForceRebuildLayoutImmediate)            [RESIZE-08]
//   9. non-vacuous disk round-trip (resize + push geometry → on-disk text → fresh load + Apply)      [RESIZE-09]
//  10. ADR-0029 separateness: grip resize session ⊥ title drag (IsResizing flips, IsDragging stays)  [RESIZE-10]
//  11. resize grip affordance (always-visible "◢", raycast target, OWN drag handler, last sibling)   [RESIZE-11]
//  12. raise on grab: BeginResize ⇒ NoteUserFocus (SetAsLastSibling + focus target recorded)         [RESIZE-12]

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class FloatingWindowResizeE2ERunner
{
    const float EPS = 1e-4f;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "floating_window_resize_e2e");
    static string TempPath => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);

            fail = Section1_PushPureSingle()
                ?? Section2_PushPureFlushFollow()
                ?? Section3_PushPureIslandScope()
                ?? Section4_ControllerTopLeftFixedGrow(spawned)
                ?? Section5_ControllerFlushFollowAndNegControl(spawned)
                ?? Section6_ControllerMinClamp(spawned)
                ?? Section7_ControllerEscRevert(spawned)
                ?? Section8_BodyFollowsParentSize(spawned)
                ?? Section9_DiskRoundTripNonVacuous(spawned)
                ?? Section10_SeparateFromDragChannel(spawned)
                ?? Section11_ResizeGripAffordance(spawned)
                ?? Section12_RaiseAndFocusOnGrab(spawned);
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
            Debug.Log("[E2E FLOATING WINDOW RESIZE PASS] ResizeIslandPush pure (top-left fixed, right/bottom " +
                      "edges grow/shrink; island flush FOLLOW push-out symmetric grow=push / shrink=pull, " +
                      "chained, x/y independent, members move-only own-size-kept; island scope — non-flush + " +
                      "left/top neighbours never move) + controller real RectTransform (engine==math top-left-" +
                      "fixed right-bottom grow: anchoredPosition unchanged, sizeDelta up; flush follow symmetric+" +
                      "chained with non-island neighbour unmoved; min clamp spec.minSize floor no-max; ESC revert " +
                      "resized size + pushed members to rest, groupId untouched, release commits nothing) + Body " +
                      "child follows parent sizeDelta (stretch anchors, ForceRebuildLayoutImmediate) + non-vacuous " +
                      "disk round-trip (resize + push geometry → on-disk text proof → fresh load + Apply restore) + " +
                      "ADR-0029 separateness (grip resize session ⊥ title drag: IsResizing flips while IsDragging " +
                      "stays false; grip has its OWN IDragHandler so it never bubbles to ResolveChannel) + resize " +
                      "grip affordance (always-visible \"◢\", raycast-target chip, OWN drag handler, bottom-right " +
                      "anchor, last sibling, glyph non-blocking, idempotent) + raise+focus on grab (BeginResize ⇒ " +
                      "NoteUserFocus = SetAsLastSibling + focus target recorded) " +
                      "(schema-add 0 — existing Capture w/h+x/y; ADR-0030 island-scope carve-out of ADR-0017/0029) " +
                      "[RESIZE-01..12; RESIZE-13 = HITL real-grip gesture]");
            // E2E-CONVENTIONS §5: the NAME tag above has spaces, so E2ERollup.ps1 (single-token Action-ID regex)
            // ignores it. Emit per-Action-ID single-token PASS tags so the resize behaviours land on the rollup
            // (RESIZE-13 is HITL-only — no AFK tag; the rollup reads its absence as "HITL, not an AFK miss").
            for (int n = 1; n <= 12; n++) Debug.Log($"[E2E RESIZE-{n:00} PASS]");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E FLOATING WINDOW RESIZE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. pure ResizeIslandPush: top-left fixed, right/bottom grow (single window) ----
    // Covers: RESIZE-01 — anchoredPosition (top-left) is INVARIANT, only sizeDelta changes; right edge moves
    //         +dW, bottom edge moves down by dH. Grow AND shrink (symmetric on the resized window itself).
    static string Section1_PushPureSingle()
    {
        var R = new FloatingWindowMath.DockRect(0, 0, 300, 200);
        var island = new Dictionary<string, FloatingWindowMath.DockRect> { { "R", R } };

        // grow
        var grown = FloatingWindowMath.ResizeIslandPush("R", island, new Vector2(400, 260), 1f);
        if (!Approx2(grown["R"].topLeft, Vector2.zero)) return $"S1: grow moved top-left (got {grown["R"].topLeft})";
        if (!Approx2(grown["R"].size, new Vector2(400, 260))) return $"S1: grow size {grown["R"].size} != (400,260)";

        // shrink
        var shrunk = FloatingWindowMath.ResizeIslandPush("R", island, new Vector2(250, 150), 1f);
        if (!Approx2(shrunk["R"].topLeft, Vector2.zero)) return "S1: shrink moved top-left";
        if (!Approx2(shrunk["R"].size, new Vector2(250, 150))) return $"S1: shrink size {shrunk["R"].size} != (250,150)";

        // degenerate: null / unknown id → verbatim-ish (no throw, resized absent so no entry mutated).
        var none = FloatingWindowMath.ResizeIslandPush("ghost", island, new Vector2(400, 260), 1f);
        if (!Approx2(none["R"].size, new Vector2(300, 200))) return "S1: unknown resizedId mutated the island";
        return null;
    }

    // ---- 2. pure: island flush FOLLOW push-out (symmetric, chained, x/y independent, size kept) ----
    // Covers: RESIZE-02 — members flush to the moving (right/bottom) edge translate by the SAME signed delta,
    //         kissed; grow pushes out, shrink pulls back (symmetric); chains propagate; x and y are
    //         independent; every pushed member keeps its OWN (w,h).
    static string Section2_PushPureFlushFollow()
    {
        // Horizontal row A|B|C (A resized). A.right=200=B.left, B.right=350=C.left (flush chain).
        var row = new Dictionary<string, FloatingWindowMath.DockRect>
        {
            { "A", new FloatingWindowMath.DockRect(0,   0, 200, 100) },
            { "B", new FloatingWindowMath.DockRect(200, 0, 150, 100) },
            { "C", new FloatingWindowMath.DockRect(350, 0, 120, 100) },
        };
        // grow A width +50 → B,C each +50 in x; sizes preserved.
        var g = FloatingWindowMath.ResizeIslandPush("A", row, new Vector2(250, 100), 1f);
        if (!Approx2(g["A"].size, new Vector2(250, 100))) return "S2: A not grown";
        if (!Approx2(g["B"].topLeft, new Vector2(250, 0))) return $"S2: B did not follow right edge (got {g["B"].topLeft})";
        if (!Approx2(g["C"].topLeft, new Vector2(400, 0))) return $"S2: C did not chain-follow (got {g["C"].topLeft})";
        if (!Approx2(g["B"].size, new Vector2(150, 100)) || !Approx2(g["C"].size, new Vector2(120, 100)))
            return "S2: pushed members did not keep their own size";

        // shrink A width -30 → B,C pulled back (symmetric).
        var s = FloatingWindowMath.ResizeIslandPush("A", row, new Vector2(170, 100), 1f);
        if (!Approx2(s["B"].topLeft, new Vector2(170, 0))) return $"S2: shrink did not pull B back (got {s["B"].topLeft})";
        if (!Approx2(s["C"].topLeft, new Vector2(320, 0))) return $"S2: shrink did not pull C back (got {s["C"].topLeft})";

        // Vertical bottom-flush: A(0,0,200,100) bottom=-100; D below at top=-100. grow height +40 ⇒ D follows -40 in y.
        var stack = new Dictionary<string, FloatingWindowMath.DockRect>
        {
            { "A", new FloatingWindowMath.DockRect(0,    0, 200, 100) },
            { "D", new FloatingWindowMath.DockRect(0, -100, 200,  80) },   // D.top=-100 == A.bottom
        };
        var gy = FloatingWindowMath.ResizeIslandPush("A", stack, new Vector2(200, 140), 1f);
        if (!Approx2(gy["D"].topLeft, new Vector2(0, -140))) return $"S2: D did not follow bottom edge (got {gy["D"].topLeft})";
        if (!Approx2(gy["D"].size, new Vector2(200, 80))) return "S2: bottom-flush member changed size";

        // x/y INDEPENDENT: A grows both; B flush-right (x only), D flush-below (y only).
        var cross = new Dictionary<string, FloatingWindowMath.DockRect>
        {
            { "A", new FloatingWindowMath.DockRect(0,    0, 200, 100) },
            { "B", new FloatingWindowMath.DockRect(200,  0, 150, 100) },   // flush-right
            { "D", new FloatingWindowMath.DockRect(0, -100, 200,  80) },   // flush-below
        };
        var gc = FloatingWindowMath.ResizeIslandPush("A", cross, new Vector2(260, 140), 1f);   // dW=+60, dH=+40
        if (!Approx2(gc["B"].topLeft, new Vector2(260, 0))) return $"S2: B (x-only) got wrong move (got {gc["B"].topLeft})";
        if (!Approx2(gc["D"].topLeft, new Vector2(0, -140))) return $"S2: D (y-only) got wrong move (got {gc["D"].topLeft})";
        return null;
    }

    // ---- 3. pure: island scope — non-flush + left/top neighbours never move (negative control) ----
    // Covers: RESIZE-03 — only members flush to a MOVING edge follow. A neighbour flush to the LEFT/TOP edge
    //         (which do NOT move) stays put; a disjoint member stays put. Scope is strictly the moving edges.
    static string Section3_PushPureIslandScope()
    {
        var island = new Dictionary<string, FloatingWindowMath.DockRect>
        {
            { "A", new FloatingWindowMath.DockRect(0,    0, 200, 100) },   // resized
            { "L", new FloatingWindowMath.DockRect(-150, 0, 150, 100) },   // L.right=0 == A.left (flush LEFT — must NOT move)
            { "T", new FloatingWindowMath.DockRect(0,  150, 200,  50) },   // T.bottom=100 == A.top (flush TOP — must NOT move)
            { "Z", new FloatingWindowMath.DockRect(500,-300, 100, 100) },  // disjoint — must NOT move
            { "B", new FloatingWindowMath.DockRect(200,  0, 150, 100) },   // flush RIGHT — SHOULD move
        };
        var g = FloatingWindowMath.ResizeIslandPush("A", island, new Vector2(260, 130), 1f);   // grow both
        if (!Approx2(g["L"].topLeft, new Vector2(-150, 0))) return $"S3: left-flush neighbour moved (got {g["L"].topLeft})";
        if (!Approx2(g["T"].topLeft, new Vector2(0, 150))) return $"S3: top-flush neighbour moved (got {g["T"].topLeft})";
        if (!Approx2(g["Z"].topLeft, new Vector2(500, -300))) return $"S3: disjoint member moved (got {g["Z"].topLeft})";
        if (!Approx2(g["B"].topLeft, new Vector2(260, 0))) return "S3: right-flush member did NOT follow (sanity)";
        return null;
    }

    // ---- 4. controller real rect: engine==math top-left-fixed right-bottom grow ----
    // Covers: RESIZE-04 — BeginResize/ResizeApply on a real RectTransform: anchoredPosition (top-left) is
    //         INVARIANT, sizeDelta grows by (Δx, -Δy), and the live rect equals FloatingWindowMath.
    static string Section4_ControllerTopLeftFixedGrow(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        Vector2 L = new Vector2(80f, -50f);
        var R = c.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "R", L.x, L.y, 400f, 260f, true);
        if (R == null) return "S4: spawn returned null";

        c.BeginResize("R", Vector2.zero);
        // grip drag right+down: cursor (60,-40) ⇒ desired size = (400+60, 260-(-40)) = (460,300).
        Vector2 applied = c.ResizeApply("R", Vector2.zero, new Vector2(60f, -40f));
        if (!Approx2(applied, new Vector2(460f, 300f))) return $"S4: applied size {applied} != (460,300)";
        if (!Approx2(R.anchoredPosition, L)) return $"S4: top-left moved (got {R.anchoredPosition}, want {L})";
        if (!Approx2(R.sizeDelta, new Vector2(460f, 300f))) return $"S4: sizeDelta {R.sizeDelta} != (460,300)";

        // engine==math cross-check against the pure push.
        var math = FloatingWindowMath.ResizeIslandPush(
            "R", new Dictionary<string, FloatingWindowMath.DockRect> { { "R", new FloatingWindowMath.DockRect(L, new Vector2(400, 260)) } },
            new Vector2(460, 300), FloatingWindowController.DEFAULT_FLUSH_EPS);
        if (!Approx2(R.anchoredPosition, math["R"].topLeft) || !Approx2(R.sizeDelta, math["R"].size))
            return "S4: engine != math";
        c.ReleaseResize("R");
        if (c.IsResizing) return "S4: ReleaseResize did not close the session";
        return null;
    }

    // ---- 5. controller real rect: flush follow (symmetric, chained) + non-island neighbour unmoved ----
    // Covers: RESIZE-05 — a grouped island A|B|C follows A's resize on real rects (chained); shrink pulls
    //         back (symmetric); an EXTERNAL non-group neighbour Z flush to the chain end NEVER moves (island
    //         scope — Z is not in the snapshotted island restRects).
    static string Section5_ControllerFlushFollowAndNegControl(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        // order minSize 280x180 → use 280x180 tiles flush in a row.
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,     "A", 0f,    0f, 280f, 180f, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS,    "B", 280f,  0f, 280f, 180f, true);
        c.Spawn(FloatingWindowCatalog.KIND_POSITIONS, "C", 560f,  0f, 280f, 180f, true);
        c.Spawn(FloatingWindowCatalog.KIND_BUYING_POWER, "Z", 840f, 0f, 280f, 180f, true);   // external (no group)
        const string G = "grp_rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr";
        c.SetGroupId("A", G); c.SetGroupId("B", G); c.SetGroupId("C", G);

        // grow A width +40 ⇒ B→+40, C→+40 (chained); Z (not in island) stays.
        c.BeginResize("A", Vector2.zero);
        c.ResizeApply("A", Vector2.zero, new Vector2(40f, 0f));   // desired (320,180) ≥ min
        if (!Approx2(c.RectOf("A").sizeDelta, new Vector2(320f, 180f))) return "S5: A not grown";
        if (!Approx2(c.RectOf("B").anchoredPosition, new Vector2(320f, 0f))) return $"S5: B did not follow (got {c.RectOf("B").anchoredPosition})";
        if (!Approx2(c.RectOf("C").anchoredPosition, new Vector2(600f, 0f))) return $"S5: C did not chain-follow (got {c.RectOf("C").anchoredPosition})";
        if (!Approx2(c.RectOf("Z").anchoredPosition, new Vector2(840f, 0f))) return $"S5: external Z moved (island scope broken) (got {c.RectOf("Z").anchoredPosition})";
        if (!Approx2(c.RectOf("B").sizeDelta, new Vector2(280f, 180f))) return "S5: pushed member B changed size";

        // shrink back to rest width (symmetric): cursor (0,0) ⇒ desired (280,180) ⇒ B,C back to rest.
        c.ResizeApply("A", Vector2.zero, Vector2.zero);
        if (!Approx2(c.RectOf("B").anchoredPosition, new Vector2(280f, 0f))) return "S5: symmetric pull-back failed for B";
        if (!Approx2(c.RectOf("C").anchoredPosition, new Vector2(560f, 0f))) return "S5: symmetric pull-back failed for C";
        c.ReleaseResize("A");
        return null;
    }

    // ---- 6. controller: min clamp (spec.minSize floor, no max) ----
    // Covers: RESIZE-06 — a resize below spec.minSize clamps UP to minSize (no shrink past it); a large grow
    //         is applied verbatim (no max — infinite canvas); a flush island FOLLOWER tracks the CLAMPED delta,
    //         NOT the pre-clamp desired delta (clamp × follower interaction).
    static string Section6_ControllerMinClamp(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER, "R", 0f, 0f, 400f, 300f, true);   // order minSize = 280x180

        c.BeginResize("R", Vector2.zero);
        // huge negative drag: cursor (-500,500) ⇒ desired (400-500, 300-500) = (-100,-200) ⇒ clamp to (280,180).
        Vector2 clamped = c.ResizeApply("R", Vector2.zero, new Vector2(-500f, 500f));
        if (!Approx2(clamped, new Vector2(280f, 180f))) return $"S6: min clamp failed (got {clamped}, want 280x180)";
        if (!Approx2(c.RectOf("R").sizeDelta, new Vector2(280f, 180f))) return "S6: sizeDelta not clamped to min";

        // large grow: applied verbatim (no max).
        Vector2 big = c.ResizeApply("R", Vector2.zero, new Vector2(2000f, -2000f));
        if (!Approx2(big, new Vector2(2400f, 2300f))) return $"S6: grow should be unbounded (got {big})";
        c.ReleaseResize("R");

        // clamp × FOLLOWER interaction: a flush island member must follow by the CLAMPED delta, NOT the
        // pre-clamp desired delta. ResizeApply feeds the clamped size into ResizeIslandPush — a regression that
        // pushed neighbours by `desired` would over-push, and neither S5 (no clamp) nor the single-window cases
        // above would catch it. R2 rest 400 wide, Bf flush at its right (x=400). Shrink R2 by desired -200 ⇒
        // desired width 200 < min 280 ⇒ clamp to 280 ⇒ R2 shrinks by 120, so Bf pulls back to x=280 (clamped),
        // NOT x=200 (desired).
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,  "R2", 0f,   -400f, 400f, 180f, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "Bf", 400f, -400f, 280f, 180f, true);
        const string GC = "grp_cccccccccccccccccccccccccccccccc";
        c.SetGroupId("R2", GC); c.SetGroupId("Bf", GC);
        c.BeginResize("R2", Vector2.zero);
        Vector2 cl = c.ResizeApply("R2", Vector2.zero, new Vector2(-200f, 0f));
        if (!Approx2(cl, new Vector2(280f, 180f))) return $"S6: grouped clamp wrong (got {cl}, want 280x180)";
        if (!Approx2(c.RectOf("Bf").anchoredPosition, new Vector2(280f, -400f)))
            return $"S6: follower used the pre-clamp desired delta, not the clamped delta (got {c.RectOf("Bf").anchoredPosition}, want x=280)";
        c.ReleaseResize("R2");
        return null;
    }

    // ---- 7. controller: ESC revert (resized size + pushed members to rest, state untouched, no commit) ----
    // Covers: RESIZE-07 — CancelResize reverts the resized window's size AND every pushed member's position
    //         to the rest snapshot; groupId is untouched; the following ReleaseResize commits nothing.
    static string Section7_ControllerEscRevert(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,  "A", 0f,   0f, 280f, 180f, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "B", 280f, 0f, 280f, 180f, true);
        const string G = "grp_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        c.SetGroupId("A", G); c.SetGroupId("B", G);
        Vector2 aSize0 = c.RectOf("A").sizeDelta, bPos0 = c.RectOf("B").anchoredPosition;

        c.BeginResize("A", Vector2.zero);
        c.ResizeApply("A", Vector2.zero, new Vector2(80f, 0f));   // grow ⇒ B pushed
        if (Approx2(c.RectOf("A").sizeDelta, aSize0)) return "S7: precondition — A did not grow before ESC";
        if (Approx2(c.RectOf("B").anchoredPosition, bPos0)) return "S7: precondition — B did not move before ESC";

        c.CancelResize("A");
        if (!Approx2(c.RectOf("A").sizeDelta, aSize0)) return "S7: ESC did not revert resized size";
        if (!Approx2(c.RectOf("B").anchoredPosition, bPos0)) return "S7: ESC did not revert pushed member to rest";
        if (c.GroupIdOf("A") != G || c.GroupIdOf("B") != G) return "S7: ESC mutated groupId (state must be untouched)";

        // The following release commits NOTHING (geometry already at rest).
        c.ResizeApply("A", Vector2.zero, new Vector2(80f, 0f));   // post-ESC frames are ignored (canceled latched)
        if (!Approx2(c.RectOf("A").sizeDelta, aSize0)) return "S7: post-ESC ResizeApply committed a size change";
        c.ReleaseResize("A");
        if (!Approx2(c.RectOf("A").sizeDelta, aSize0)) return "S7: release after ESC changed size";
        return null;
    }

    // ---- 8. Body child follows parent sizeDelta (stretch anchors) ----
    // Covers: RESIZE-08 — the window Body (anchorMin=(0,0)/anchorMax=(1,1)+insets) auto-stretches when the root
    //         sizeDelta changes — no content reflow arithmetic. Drives the PRODUCTION StrategyEditorWindowFrame.Build
    //         (not a hand-built tree) so a real TitleHeight / Body-anchor regression turns it RED (non-vacuous).
    static string Section8_BodyFollowsParentSize(List<GameObject> spawned)
    {
        // Drive the PRODUCTION frame builder (not a hand-built hierarchy) so the assert is over backcast's real
        // Body contract: a regression that changed StrategyEditorWindowFrame.TitleHeight or dropped the Body's
        // stretch anchors / insets turns this RED. (The previous hand-built version hardcoded TitleH=28 and the
        // anchors, so it only proved Unity's anchor math — no production logic to delete = vacuous.)
        var root = StrategyEditorWindowFrame.Build("s8", out _, out var body);
        spawned.Add(root.gameObject);
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0f, 1f);
        root.sizeDelta = new Vector2(400f, 300f);

        float titleH = StrategyEditorWindowFrame.TitleHeight;   // production constant (not a literal 28)
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
        float w0 = body.rect.width, h0 = body.rect.height;
        // production Body insets: offsetMin=(4,4), offsetMax=(-4, -(TitleHeight+2)) ⇒
        //   width = root.w - (left 4 + right 4); height = root.h - (bottom 4 + top (TitleHeight+2)).
        if (!Approx(w0, 400f - 8f)) return $"S8: precondition body width {w0} != {400f - 8f}";
        if (!Approx(h0, 300f - (4f + titleH + 2f))) return $"S8: precondition body height {h0}";

        // grow the root: the production body must follow (stretch anchors), gaining the SAME (Δw, Δh).
        root.sizeDelta = new Vector2(520f, 400f);
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
        if (!Approx(body.rect.width, 520f - 8f)) return $"S8: body width did not follow parent (got {body.rect.width}, want {520f - 8f})";
        if (!Approx(body.rect.height, 400f - (4f + titleH + 2f))) return $"S8: body height did not follow parent (got {body.rect.height})";
        if (!(body.rect.width > w0 && body.rect.height > h0)) return "S8: body did not grow with the parent (vacuous)";
        return null;
    }

    // ---- 9. non-vacuous disk round-trip (resize + push geometry → on-disk text → fresh load + Apply) ----
    // Covers: RESIZE-09 — after a resize that grows the window AND pushes a flush member, Capture → Save →
    //         (on-disk TEXT proof) → fresh Load → Apply restores both the grown size and the pushed position.
    static string Section9_DiskRoundTripNonVacuous(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        // .5 values serialize unambiguously. A.right rest = 100.5+300.5 = 401.0 = B.left (flush).
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,  "A", 100.5f, -50.5f, 300.5f, 200.5f, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "B", 401.0f, -50.5f, 280.5f, 200.5f, true);
        const string G = "grp_dddddddddddddddddddddddddddddddd";
        c.SetGroupId("A", G); c.SetGroupId("B", G);

        c.BeginResize("A", Vector2.zero);
        c.ResizeApply("A", Vector2.zero, new Vector2(40f, 0f));   // A.w → 340.5; B.x → 441.0
        c.ReleaseResize("A");
        if (!Approx2(c.RectOf("A").sizeDelta, new Vector2(340.5f, 200.5f))) return "S9: precondition A not grown";
        if (!Approx2(c.RectOf("B").anchoredPosition, new Vector2(441.0f, -50.5f))) return "S9: precondition B not pushed";

        LayoutDocument cap = c.Capture();
        LayoutStore.Save(cap, TempPath);
        if (!File.Exists(TempPath)) return "S9: Save did not create the sidecar";
        string compact = File.ReadAllText(TempPath).Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        // Only .5 float needles (Unity JsonUtility prints whole-number floats without a decimal — match the
        // sibling FloatingWindowE2ERunner S5 convention). The grown A.w (340.5) + fixed A.x (100.5) prove the
        // resize geometry hit disk; the PUSHED B position is proven non-vacuously by the restore assert below.
        string[] needles = { "\"id\":\"A\"", "\"w\":340.5", "\"x\":100.5", "\"id\":\"B\"" };
        foreach (var n in needles) if (!compact.Contains(n)) return $"S9: on-disk JSON missing {n}";

        // fresh controller restore.
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        BuildLayer(spawned, out RectTransform layer2);
        var c2 = MakeController(spawned, layer2);
        c2.Apply(loaded);
        if (!Approx2(c2.RectOf("A").sizeDelta, new Vector2(340.5f, 200.5f))) return $"S9: restored A size {c2.RectOf("A")?.sizeDelta}";
        if (!Approx2(c2.RectOf("A").anchoredPosition, new Vector2(100.5f, -50.5f))) return "S9: restored A top-left wrong";
        if (!Approx2(c2.RectOf("B").anchoredPosition, new Vector2(441.0f, -50.5f))) return $"S9: restored B position {c2.RectOf("B")?.anchoredPosition}";
        return null;
    }

    // ---- 10. ADR-0029 separateness: grip resize session ⊥ title drag ----
    // Covers: RESIZE-10 — a resize flips IsResizing but NEVER IsDragging (the grip drives its own session,
    //         not the gesture-channel drag machinery), and the grip MonoBehaviour IS its own IDragHandler
    //         (so its drag is swallowed and never reaches ResolveChannel — opposite of the eject handle).
    static string Section10_SeparateFromDragChannel(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER, "R", 0f, 0f, 320f, 220f, true);

        if (c.IsResizing || c.IsDragging) return "S10: precondition — a session is already live";
        c.BeginResize("R", Vector2.zero);
        if (!c.IsResizing) return "S10: BeginResize did not flip IsResizing";
        if (c.IsDragging) return "S10: a resize flipped IsDragging (must stay false — separate from the drag channel)";
        c.ReleaseResize("R");
        if (c.IsResizing) return "S10: ReleaseResize did not clear IsResizing";

        // The grip MonoBehaviour IS an IDragHandler (its drag is swallowed, NOT bubbled to ResolveChannel).
        var bareRoot = new GameObject("BareRoot", typeof(RectTransform)); spawned.Add(bareRoot);
        var grip = FloatingWindowResizeHandle.Attach((RectTransform)bareRoot.transform, null);
        if (grip == null) return "S10: grip Attach returned null";
        if (grip.GetComponent(typeof(UnityEngine.EventSystems.IDragHandler)) == null)
            return "S10: grip has no IDragHandler (its drag would bubble into ResolveChannel — must be self-handled)";
        if (grip.GetComponent<FloatingWindowResizeGrip>() == null) return "S10: grip missing FloatingWindowResizeGrip";
        return null;
    }

    // ---- 11. resize grip affordance (always-visible "◢", raycast target, OWN drag handler, last sibling) ----
    // Covers: RESIZE-11 — every window root grows an always-visible bottom-right grip: a raycast-target chip
    //         Image with its OWN drag handler, anchored bottom-right, the LAST sibling (wins the raycast), a
    //         non-blocking "◢" glyph; idempotent find-or-create. ALSO the production WIRING SEAM
    //         (FloatingWindowTitleInput.Initialize → AttachResizeGrip → grip.Initialize) — the grip is attached
    //         AND Initialized on a real window, else OnDrag silently no-ops.
    static string Section11_ResizeGripAffordance(List<GameObject> spawned)
    {
        var rootGo = new GameObject("WindowRoot", typeof(RectTransform)); spawned.Add(rootGo);
        var root = (RectTransform)rootGo.transform;
        // a pre-existing child so "last sibling" is non-vacuous.
        var filler = new GameObject("Filler", typeof(RectTransform)); filler.transform.SetParent(root, false);

        var grip = FloatingWindowResizeHandle.Attach(root, null);
        if (grip == null) return "S11: Attach returned null";

        if (grip.transform.parent != root.transform) return "S11: grip is not a child of the window root";
        if (grip.name != FloatingWindowResizeHandle.NodeName) return $"S11: grip name {grip.name} != {FloatingWindowResizeHandle.NodeName}";
        if (!grip.activeSelf) return "S11: grip is not active (must be always visible)";

        var img = grip.GetComponent<Image>();
        if (img == null || !img.raycastTarget) return "S11: grip chip is not a raycast target";
        if (grip.GetComponent(typeof(UnityEngine.EventSystems.IDragHandler)) == null)
            return "S11: grip has no OWN IDragHandler (resize would not engage / would bubble)";
        var gripRt = (RectTransform)grip.transform;
        if (gripRt.anchorMin != new Vector2(1f, 0f) || gripRt.anchorMax != new Vector2(1f, 0f) || gripRt.pivot != new Vector2(1f, 0f))
            return "S11: grip is not anchored bottom-right";
        if (grip.transform.GetSiblingIndex() != root.childCount - 1) return "S11: grip is not the last sibling (press would not win the raycast)";

        var glyph = grip.transform.Find("ResizeGlyph");
        if (glyph == null) return "S11: resize glyph child missing";
        var t = glyph.GetComponent<Text>();
        if (t == null || t.text != FloatingWindowResizeHandle.Glyph) return "S11: glyph text wrong";
        if (t.raycastTarget) return "S11: glyph blocks raycasts (only the chip Image should be the target)";

        // idempotent: a second Attach returns the SAME grip (no duplicate).
        var again = FloatingWindowResizeHandle.Attach(root, null);
        if (again != grip) return "S11: Attach is not idempotent (duplicated the grip)";

        // production WIRING SEAM: FloatingWindowTitleInput.Initialize → AttachResizeGrip → grip.Initialize.
        // The blocks above drive the builder directly; this proves the grip is actually attached AND Initialized
        // on a real window via the uniform title-input path (the only thing that wires it on every window).
        // Without grip.Initialize, OnDrag early-returns (_windows==null) and resize never fires — a silent
        // "looks wired, does nothing" hole that only HITL would otherwise catch. (The pointer/raycast half of the
        // gesture stays HITL — RESIZE-13 — but this pure-C# wiring is AFK-drivable.)
        BuildLayer(spawned, out RectTransform layer2);
        var c2 = MakeController(spawned, layer2);
        var wRootGo = new GameObject("WiredWindow", typeof(RectTransform)); spawned.Add(wRootGo);
        ((RectTransform)wRootGo.transform).SetParent(layer2, false);
        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(FloatingWindowTitleInput));
        titleGo.transform.SetParent(wRootGo.transform, false);
        titleGo.GetComponent<FloatingWindowTitleInput>().Initialize(c2, null, null, "wired_id");

        var wired = wRootGo.transform.Find(FloatingWindowResizeHandle.NodeName);
        if (wired == null) return "S11: TitleInput.Initialize did not attach a resize grip to the window root (wiring seam broken)";
        var wiredGrip = wired.GetComponent<FloatingWindowResizeGrip>();
        if (wiredGrip == null) return "S11: wired grip missing FloatingWindowResizeGrip";
        var idFld = typeof(FloatingWindowResizeGrip).GetField("_windowId", BindingFlags.NonPublic | BindingFlags.Instance);
        var winFld = typeof(FloatingWindowResizeGrip).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        if (idFld == null || winFld == null) return "S11: grip field renamed? (_windowId/_windows not found — update the seam assert)";
        if ((string)idFld.GetValue(wiredGrip) != "wired_id" || !ReferenceEquals(winFld.GetValue(wiredGrip), c2))
            return "S11: grip attached but NOT Initialized via the title-input seam (its _windows/_windowId are unset ⇒ OnDrag would silently no-op)";
        return null;
    }

    // ---- 12. raise + focus on grab (BeginResize ⇒ NoteUserFocus) ----
    // Covers: RESIZE-12 — grabbing the grip raises the window to the front (SetAsLastSibling) AND records it
    //         as the user-focus target (NoteUserFocus), exactly like a title-bar press.
    static string Section12_RaiseAndFocusOnGrab(List<GameObject> spawned)
    {
        BuildLayer(spawned, out RectTransform layer);
        var c = MakeController(spawned, layer);
        c.Spawn(FloatingWindowCatalog.KIND_ORDER,  "back",  0f, 0f, 300f, 200f, true);
        c.Spawn(FloatingWindowCatalog.KIND_ORDERS, "front", 0f, 0f, 300f, 200f, true);   // spawned later ⇒ in front
        if (c.RectOf("back").GetSiblingIndex() >= c.RectOf("front").GetSiblingIndex())
            return "S12: precondition — 'back' is not behind 'front'";

        c.BeginResize("back", Vector2.zero);
        if (c.RectOf("back").GetSiblingIndex() != c.RectOf("back").parent.childCount - 1)
            return "S12: BeginResize did not raise 'back' to the last sibling (front)";
        // focus recorded: a dock target resolver now picks 'back' (NoteUserFocus wrote the focus target).
        if (!c.TryResolveDockTarget(Vector2.zero, "front", out var target))
            return "S12: focus target not resolvable after grab";
        var back = c.RectOf("back");
        if (!Approx2(target.topLeft, back.anchoredPosition) || !Approx2(target.size, back.sizeDelta))
            return "S12: grip grab did not record 'back' as the focus target";
        c.ReleaseResize("back");
        return null;
    }

    // ---- helpers ----

    // A minimal Content → FloatingWindowLayer (identity) under a throwaway viewport. The resize sections
    // drive the controller directly (no pan/zoom), so an InfiniteCanvasController is not needed here.
    static void BuildLayer(List<GameObject> spawned, out RectTransform layer)
    {
        var contentGo = new GameObject("ProbeContent", typeof(RectTransform));
        spawned.Add(contentGo);
        var content = (RectTransform)contentGo.transform;
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        var layerGo = new GameObject("FloatingWindowLayer", typeof(RectTransform));
        layer = (RectTransform)layerGo.transform;
        layer.SetParent(content, false);
        layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(0.5f, 0.5f);
        layer.anchoredPosition = Vector2.zero;
        layer.sizeDelta = Vector2.zero;
    }

    // A controller whose factory mints BARE RectTransforms (the AFK gate proves geometry, not visuals) and
    // whose remover uses DestroyImmediate (Destroy is a no-op outside play mode).
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

    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;
    static bool Approx2(Vector2 a, Vector2 b) => Approx(a.x, b.x) && Approx(a.y, b.y);
}
