// FloatingWindowMath.cs — issue #15 "floating windows" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless float arithmetic for the floating-window seam — the AFK gate
// proves THIS (no playmode, no Canvas update, no input device), exactly as #13's
// CanvasViewMath and #14's HakoniwaGridMath are the authoritative math. NOTHING here touches
// a RectTransform, a render, or input; FloatingWindowController is the thin Unity boundary
// that reads/writes window RectTransforms from these results.
//
// Three pieces of locked design live here:
//
//  1. drag -> canvas-logical delta (findings 0008 §2/§4, owner-locked). A title-bar drag
//     arrives as a VIEWPORT-LOCAL pixel delta (the input boundary already ran it through
//     RectTransformUtility, CanvasScaler-safe, exactly as #13's pan does — NEVER raw
//     eventData.delta). Dividing by the live zoom turns viewport pixels into canvas-logical
//     units, so a window tracks the cursor at any zoom.
//
//  2. z-order normalization (findings 0008 §3, owner-locked). Persisted zOrder is kept verbatim
//     (0 = backmost) and may be non-contiguous / duplicated / negative (hand-authored or
//     forward-evolved). SiblingOrder stable-sorts it to a contiguous 0..n-1 sibling assignment:
//     ascending zOrder, ties broken by ORIGINAL list order. uGUI draws children in sibling order
//     (child 0 = back), so sibling slot k = the k-th window from the back.
//
//  3. magnet snap on release (#99 Slice 1, ADR-0017 / findings 0075 §1, owner-locked). On the
//     title-bar drag's `OnEndDrag`, SnapOffset returns the canvas-logical Δ that aligns the
//     dragged window's nearest edge to the nearest neighbour's edge — either FLUSH (right↔left
//     / top↔bottom, the windows kiss) or SAME-EDGE (left↔left / top↔top, the windows line up).
//     x and y are independent (x may snap to A while y snaps to B). Beyond `threshold` →
//     contribute 0 on that axis. Group concept does NOT exist (each window stays independent
//     after the snap — the next drag of a neighbour does NOT take the snapped window with it),
//     so SnapOffset has no notion of `which neighbour` once the offset is chosen.

using System;
using System.Collections.Generic;
using UnityEngine;

public static class FloatingWindowMath
{
    // ADR-0029 §1 / findings 0106 §1: the TWO drag GESTURE CHANNELS, fixed at OnBeginDrag and NEVER
    // re-evaluated per frame (ADR-0024's cursor-position 3-mode `ResolveDragMode` + the `D_DETACH_PX`
    // distance trigger are SUPERSEDED — owner unhappiness 1/2). The channel is decided from HOW the drag
    // started (Alt held or not) — not from cursor distance — so a drag can never morph mid-gesture.
    //
    //   IslandMove        — a plain title-bar drag. The WHOLE island translates as a unit at UNLIMITED
    //                       distance (no detach, ever — membership only shrinks via SingleWindowPickup).
    //                       Releasing flush against another island merges (owner Q3). A singleton is a
    //                       1-member island.
    //   SingleWindowPickup— Alt+drag (ADR-0032 retired the "⤴" eject handle; Alt is the sole trigger). ONE
    //                       window is lifted out of the island and carried; the DROP POSITION decides the
    //                       outcome (ResolveDropOutcome).
    public enum DragChannel
    {
        IslandMove,
        SingleWindowPickup,
    }

    // ADR-0029 §4 / findings 0106 §3: the outcome of a SingleWindowPickup release, decided PURELY by the
    // release position (owner Q1 — one gesture, drop-decided):
    //   Swap         — the cursor sits over a sibling member of the picked window's island. The two
    //                  exchange position (anchor only — size preserved) + island-scoped reflow (D6/Q5).
    //   MergeToIsland— the picked window lands flush (magnet engaged) against ANOTHER island. It joins
    //                  that island (singleton → merge cascade).
    //   Detach       — neither sibling nor flush (empty space). The window leaves its island
    //                  (groupId=null; the remnant chain-dissolves below 2).
    public enum DropOutcome
    {
        Swap,
        MergeToIsland,
        Detach,
    }

    // ADR-0029 §4 / findings 0106 §3: the result of ResolveDropOutcome — the outcome plus, for Swap, the
    // sibling id under the cursor; for MergeToIsland, the id of a flushed member of the other island.
    // Pure POCO so the AFK gate drives the drop classifier headlessly.
    public struct DropResolution
    {
        public DropOutcome outcome;
        public string swapTargetId;     // non-empty only when outcome == Swap
        public string mergeTargetId;    // non-empty only when outcome == MergeToIsland (a member of the other island)
    }

    // ADR-0024 §3 / findings 0088 §11: the in-drag magnetic-attraction radius in canvas-LOGICAL px.
    // While translating / detaching, an outer edge within R_SNAP_PX of another window's opposite edge
    // (with orthogonal overlap) snaps flush ("プルン"). ~250/2.6 of a tile — strong enough for the
    // owner's "もっと強く" ask without surprise-snapping a brisk drag. Zoom-independent.
    public const float R_SNAP_PX = 96f;

    // ADR-0029 §6 / findings 0106 §4: the island-scoped reflow re-snap pass count. Best-effort (not a
    // convergence guarantee) — 2 passes settle the common 2-3 window swap chains; longer chains may leave a
    // residual gap, accepted as free-placement (owner Q4). See ReflowIslandAfterSwap.
    public const int REFLOW_PASSES = 2;

    // ADR-0024 §3 / findings 0088 §3, §11: the spring rect-interpolation animation. 200ms duration,
    // single overshoot of 8% (ease-out-back), settling to the target. SPRING_BACK_S is the ease-out-back
    // overshoot constant tuned so the peak overshoot is EXACTLY SPRING_OVERSHOOT_RATIO: with the curve
    // e(t)=1+(s+1)(t-1)³+s(t-1)², the peak is 4s³/(27(s+1)²), which equals 0.08 at s = 1.5 (peak at
    // t = 0.6). See SpringEase / SpringRectAt.
    public const int SPRING_DURATION_MS = 200;
    public const float SPRING_OVERSHOOT_RATIO = 0.08f;
    public const float SPRING_BACK_S = 1.5f;

    // #104 (ADR-0019 / findings 0082 §7): a group-member sample for ResolveDropTarget. siblingIndex is
    // the live uGUI sibling index (`rt.GetSiblingIndex()`) — higher = drawn IN FRONT, so when multiple
    // members overlap under the cursor the highest siblingIndex wins (the foremost candidate is what
    // the user sees on top). Pure POCO (no UnityEngine types other than Vector2 via DockRect) so the
    // AFK gate drives the resolver headlessly.
    public struct GroupMember
    {
        public string id;
        public DockRect rect;
        public int siblingIndex;
    }

    // #104 (ADR-0019 / findings 0082 §7): the swap drop-target resolver. Returns the id of the group
    // member sitting UNDER the cursor (excluding the dragged itself); when multiple members overlap,
    // the one with the highest siblingIndex (front-most) wins. Null if no member contains the cursor
    // / null or empty input.
    //
    // Containment uses top-left pivot, y-up-positive geometry (DockRect.Top / Bottom / Left / Right
    // already use those conventions). A cursor exactly on an edge counts as inside (`<=` / `>=`) — the
    // caller's drop classifier (ADR-0029 ResolveDropOutcome) decides swap purely from this containment,
    // so edge equality at the resolver is harmless (swap commits a defined SIZE-PRESERVING anchor swap +
    // island reflow — the ADR-0024 (x,y,w,h) 4-value exchange and the D_DETACH distance trigger are RETIRED).
    public static string ResolveDropTarget(Vector2 cursor, IList<GroupMember> groupMembers, string draggedId)
    {
        if (groupMembers == null || groupMembers.Count == 0) return null;
        string best = null;
        int bestSibling = int.MinValue;
        foreach (var m in groupMembers)
        {
            if (m.id == draggedId) continue;                 // exclude self
            if (cursor.x < m.rect.Left || cursor.x > m.rect.Right) continue;
            if (cursor.y < m.rect.Bottom || cursor.y > m.rect.Top) continue;
            if (m.siblingIndex > bestSibling) { best = m.id; bestSibling = m.siblingIndex; }
        }
        return best;
    }

    // ADR-0029 §3 / ADR-0032 / findings 0113 §1: the gesture-channel discriminator — the ONLY input-derived
    // choice, read ONCE at OnBeginDrag and frozen for the whole drag. A held Alt selects SingleWindowPickup;
    // a plain title-bar grab selects IslandMove. ADR-0032 retired the "⤴" eject handle, so Alt is now the
    // sole engage path. Pure truth table so the AFK gate pins it without an EventSystem / a real Keyboard.
    public static DragChannel ResolveChannel(bool altHeld)
        => altHeld ? DragChannel.SingleWindowPickup : DragChannel.IslandMove;

    // ADR-0029 §4 / findings 0106 §3: the SingleWindowPickup drop classifier — evaluated ONCE at release
    // from the drop POSITION (NOT distance — the distance trigger is retired). `islandMembers` = the
    // picked window's island (it includes `pickedId`, excluded from the swap scan); `pickedRect` is the
    // picked window's rect at the release position; `otherWindows` = every visible/live window NOT in the
    // island (the merge candidates). Branch order is the spec's (owner Q1):
    //
    //   1. Swap         — the cursor sits over a sibling member rect (ResolveDropTarget: top sibling wins,
    //                     self/hidden excluded). Distance is NOT consulted.
    //   2. MergeToIsland— else, the picked rect at release is magnet-flush (within rSnap, ComputeMagneticSnap
    //                     engages) against a non-island window → join that window's island.
    //   3. Detach       — neither (empty space).
    public static DropResolution ResolveDropOutcome(
        Vector2 cursor, DockRect pickedRect, IList<GroupMember> islandMembers, string pickedId,
        IList<GroupMember> otherWindows, float rSnap)
    {
        string swapTarget = ResolveDropTarget(cursor, islandMembers, pickedId);
        if (!string.IsNullOrEmpty(swapTarget))
            return new DropResolution { outcome = DropOutcome.Swap, swapTargetId = swapTarget };

        // Flush-to-another-island: the picked rect at its release position is magnet-engaged against a
        // non-island window — EITHER within rSnap (ComputeMagneticSnap would still pull it) OR ALREADY flush
        // (the in-drag magnet snapped it to a 0-gap kiss, where ComputeMagneticSnap returns zero). Both count
        // as "landed flush on another island". The top-sibling among candidates wins (mirrors the swap scan).
        if (otherWindows != null)
        {
            string best = null; int bestSibling = int.MinValue;
            for (int i = 0; i < otherWindows.Count; i++)
            {
                var o = otherWindows[i];
                if (o.id == pickedId) continue;
                bool engaged = ComputeMagneticSnap(pickedRect, new[] { o.rect }, rSnap) != Vector2.zero
                               || IsFlushAdjacent(pickedRect, o.rect, 1f);
                if (engaged && o.siblingIndex > bestSibling)
                { best = o.id; bestSibling = o.siblingIndex; }
            }
            if (!string.IsNullOrEmpty(best))
                return new DropResolution { outcome = DropOutcome.MergeToIsland, mergeTargetId = best };
        }

        return new DropResolution { outcome = DropOutcome.Detach };
    }

    // ADR-0029 §6 / findings 0106 §4: swap = SIZE-PRESERVING anchor exchange + island-scoped best-effort
    // magnetic flush re-snap (owner Q5). A and B keep their own (w,h); only their top-left ANCHORS swap.
    // Then every island member is re-snapped against its island siblings (flush magnet, R_SNAP) so the
    // neighbours "ぷるん" close gaps / overlaps opened by the size mismatch. Returns the post-swap+reflow
    // rect for every island member (id → DockRect) — the SAME pure result the ghost preview and the
    // controller commit both consume, so the AFK gate pins the layout headlessly.
    //
    // Scope is STRICTLY the passed `islandMembers` (findings 0106 §4 / ADR-0029 §7 — no global reflow; other
    // islands / other planes never appear here and so are never moved). Perfect tiling is NOT guaranteed —
    // residual gaps are accepted as free-placement (owner Q4). A stable deterministic scan order (by id
    // ordinal) makes the result reproducible; two convergence passes settle the common chains.
    public static Dictionary<string, DockRect> ReflowIslandAfterSwap(
        IList<GroupMember> islandMembers, string aId, string bId, float rSnap)
    {
        var rects = new Dictionary<string, DockRect>();
        if (islandMembers == null) return rects;
        foreach (var m in islandMembers)
            if (!string.IsNullOrEmpty(m.id)) rects[m.id] = m.rect;

        // 1. Size-preserving anchor swap (4-value exchange is RETIRED — (w,h) stay put).
        if (!string.IsNullOrEmpty(aId) && !string.IsNullOrEmpty(bId)
            && rects.TryGetValue(aId, out var ra) && rects.TryGetValue(bId, out var rb))
        {
            rects[aId] = new DockRect(rb.topLeft, ra.size);   // A keeps its size, takes B's anchor
            rects[bId] = new DockRect(ra.topLeft, rb.size);   // B keeps its size, takes A's anchor
        }

        // 2. Island-scoped best-effort magnetic flush re-snap. Deterministic id-ordinal scan, REFLOW_PASSES
        //    passes so a window that moves can pull a downstream neighbour flush in the next pass. Each window
        //    snaps against the CURRENT rects of its island siblings only (scope strictly the island). This is
        //    best-effort, NOT a convergence guarantee — longer gap chains may leave a residual gap, which is
        //    accepted as free-placement (owner Q4 / ADR-0029 §7); 2 passes settle the common 2-3 window cases.
        var order = new List<string>(rects.Keys);
        order.Sort(StringComparer.Ordinal);
        for (int pass = 0; pass < REFLOW_PASSES; pass++)
        {
            foreach (var id in order)
            {
                var moving = rects[id];
                var others = new List<DockRect>(rects.Count - 1);
                foreach (var kv in rects) if (kv.Key != id) others.Add(kv.Value);
                Vector2 d = ComputeMagneticSnap(moving, others, rSnap);
                if (d != Vector2.zero) rects[id] = new DockRect(moving.topLeft + d, moving.size);
            }
        }
        return rects;
    }

    // ADR-0030 §4 / findings 0112: the island-scoped RESIZE push-out (the resize-trigger carve-out of
    // ADR-0017 "no resize coupling" / ADR-0029 D7 "reflow is swap-only"). DISTINCT from the swap reflow
    // (ReflowIslandAfterSwap, best-effort magnetic flush that only closes gaps): this is a FORCED flush
    // FOLLOW along the moving edges — always tile-preserving, symmetric, chained.
    //
    // The resized window keeps its TOP-LEFT anchor and takes `newSize` (left/top edges stay put; the
    // RIGHT and BOTTOM edges move). Every island member FLUSH to a MOVING edge follows it, kissed, by the
    // SAME signed translation — SYMMETRIC (grow → push out, shrink → pull back) and CHAINED (a member
    // flush to a translated member follows too). x and y propagate INDEPENDENTLY (the whole seam decides
    // x/y separately — SnapOffset / ComputeMagneticSnap). Members MOVE ONLY — their own (w,h) is preserved
    // (the swap size-retain spirit, ADR-0030 §4). Scope is STRICTLY `islandRects` (the caller passes ONLY
    // the resized window's island; other islands / other planes never appear here and so are never moved —
    // ADR-0030 §5 island-scope, the negative control).
    //
    // Edge convention (top-left pivot, y up-positive): Right = topLeft.x + size.x ; Bottom = topLeft.y -
    // size.y. Growing width (dW>0) moves the right edge +dW; growing height (dH>0) moves the bottom edge
    // DOWN by dH (bottom.y -= dH). So a RIGHT-flush member (m.Left ≈ resized.Right) translates (+dW, 0); a
    // BOTTOM-flush member (m.Top ≈ resized.Bottom) translates (0, -dH). Because every member on a chain
    // translates by the SAME delta, flush adjacency is read on the REST rects (the relative kiss is
    // preserved through a uniform translation), so the result is order-independent and deterministic.
    //
    // Returns the post-resize rect for EVERY member (id → DockRect): the resized at newSize (top-left
    // fixed), each followed member translated, everyone else verbatim. `eps` is the flush kiss tolerance
    // (production passes DEFAULT_FLUSH_EPS = 1px). Null/empty islandRects or an unknown resizedId → a
    // verbatim copy (degenerate; the controller writes nothing new).
    public static Dictionary<string, DockRect> ResizeIslandPush(
        string resizedId, IDictionary<string, DockRect> islandRects, Vector2 newSize, float eps)
    {
        var result = new Dictionary<string, DockRect>();
        if (islandRects == null) return result;
        foreach (var kv in islandRects) result[kv.Key] = kv.Value;
        if (string.IsNullOrEmpty(resizedId) || !result.TryGetValue(resizedId, out var R)) return result;

        float dW = newSize.x - R.size.x;
        float dH = newSize.y - R.size.y;
        result[resizedId] = new DockRect(R.topLeft, newSize);   // top-left fixed; right/bottom edges move

        // x/y propagate INDEPENDENTLY (mirrors SnapOffset / ComputeMagneticSnap). xMoved = members reached
        // via a RIGHT-flush chain from the resized window; yMoved = via a BOTTOM-flush chain. Computed on
        // the REST rects (relative kiss preserved through the uniform per-chain translation).
        var xMoved = PropagateFlush(resizedId, islandRects, eps, rightEdge: true);
        var yMoved = PropagateFlush(resizedId, islandRects, eps, rightEdge: false);

        foreach (var id in xMoved)
        {
            if (id == resizedId) continue;
            var r = result[id];
            result[id] = new DockRect(new Vector2(r.topLeft.x + dW, r.topLeft.y), r.size);   // follow right edge
        }
        foreach (var id in yMoved)
        {
            if (id == resizedId) continue;
            var r = result[id];                                                              // read post-x so a
            result[id] = new DockRect(new Vector2(r.topLeft.x, r.topLeft.y - dH), r.size);   // both-axis member gets both
        }
        return result;
    }

    // ADR-0030 §4 / findings 0112: BFS the island members reachable from `seedId` via a DIRECTIONAL flush
    // chain on the REST rects. rightEdge=true → "m is flush to the RIGHT edge of n" (m.Left ≈ n.Right ∧
    // y-overlap > 0); rightEdge=false → "m is flush BELOW n" (m.Top ≈ n.Bottom ∧ x-overlap > 0). The
    // returned set INCLUDES the seed (the resized window; the caller skips it when translating). Chained:
    // a member flush to an already-reached member is reached too.
    static HashSet<string> PropagateFlush(string seedId, IDictionary<string, DockRect> rects, float eps, bool rightEdge)
    {
        var moved = new HashSet<string> { seedId };
        if (!rects.TryGetValue(seedId, out _)) return moved;
        var frontier = new Queue<string>();
        frontier.Enqueue(seedId);
        while (frontier.Count > 0)
        {
            var n = rects[frontier.Dequeue()];
            foreach (var kv in rects)
            {
                if (moved.Contains(kv.Key)) continue;
                bool flush = rightEdge ? IsFlushRightOf(n, kv.Value, eps) : IsFlushBelowOf(n, kv.Value, eps);
                if (flush) { moved.Add(kv.Key); frontier.Enqueue(kv.Key); }
            }
        }
        return moved;
    }

    // The kiss semantics MUST match IsFlushAdjacent (post-#104 F6): "flush" means kiss (gap ≈ 0), NOT overlap.
    // The signed gap may be at most `eps` POSITIVE (a hair of separation) and only a sub-pixel `FLUSH_FP_SLACK`
    // NEGATIVE (round-off), so a genuine ≥1px overlap is NOT counted as flush — exactly the asymmetric bound
    // F6 introduced for IsFlushAdjacent (using -eps here would re-introduce the pre-F6 overlap-as-flush bug).
    const float FLUSH_FP_SLACK = 1e-4f;

    // "m sits flush to the RIGHT edge of n": m.Left kisses n.Right (gap ≈ 0 within eps/slack) AND they overlap
    // along y (a corner-only touch with y-overlap ≤ 0 is NOT flush — same orthogonal-overlap rule as IsFlushAdjacent).
    static bool IsFlushRightOf(DockRect n, DockRect m, float eps)
    {
        if (!IsFinite(eps) || eps <= 0f) return false;
        float yOverlap = Math.Min(n.Top, m.Top) - Math.Max(n.Bottom, m.Bottom);
        if (yOverlap <= 0f) return false;
        float gap = m.Left - n.Right;
        return gap >= -FLUSH_FP_SLACK && gap <= eps;
    }

    // "m sits flush BELOW n" (n above, m below): m.Top kisses n.Bottom (gap ≈ 0) AND they overlap along x.
    static bool IsFlushBelowOf(DockRect n, DockRect m, float eps)
    {
        if (!IsFinite(eps) || eps <= 0f) return false;
        float xOverlap = Math.Min(n.Right, m.Right) - Math.Max(n.Left, m.Left);
        if (xOverlap <= 0f) return false;
        float gap = n.Bottom - m.Top;
        return gap >= -FLUSH_FP_SLACK && gap <= eps;
    }

    // ADR-0024 §3 / findings 0088 §2: in-drag magnetic snap. Returns the canvas-logical Δ to add to
    // `moving`'s top-left so its outer edge kisses the nearest other window's OPPOSITE edge, within
    // R_SNAP. Only FLUSH pairings count (right↔left / left↔right / top↔bottom — NOT same-edge align,
    // which is the dock-on-release affordance, not the magnet), and only when the ORTHOGONAL axis has a
    // strictly positive overlap (a corner near-miss does not pull). x and y are decided INDEPENDENTLY
    // (an island can snap horizontally to one neighbour and vertically to another), each picking the
    // smallest |Δ| ≤ R_SNAP; ties keep the first in list order. Beyond R_SNAP on an axis → 0 there
    // (free drag). The result IS the stickiness: while the cursor keeps `moving` within R_SNAP of the
    // neighbour, the same flush Δ recurs every frame (the window stays kissed); drag the cursor past
    // R_SNAP and the Δ drops to 0 (the window releases and follows freely).
    //
    // For TRANSLATE pass the ISLAND bounding box as `moving` (the outer 4 edges); for DETACH pass the
    // dragged's own rect. `others` excludes every island member. rSnap ≤ 0 / non-finite or empty
    // `others` → Vector2.zero.
    public static Vector2 ComputeMagneticSnap(DockRect moving, IList<DockRect> others, float rSnap)
    {
        if (!IsFinite(rSnap) || rSnap <= 0f) return Vector2.zero;
        if (others == null || others.Count == 0) return Vector2.zero;

        float bestDx = 0f, absDx = float.PositiveInfinity;
        float bestDy = 0f, absDy = float.PositiveInfinity;
        for (int i = 0; i < others.Count; i++)
        {
            DockRect o = others[i];
            float xOverlap = Math.Min(moving.Right, o.Right) - Math.Max(moving.Left, o.Left);
            float yOverlap = Math.Min(moving.Top,   o.Top)   - Math.Max(moving.Bottom, o.Bottom);
            // Vertical-edge kiss (x snap) requires the windows to overlap along y.
            if (yOverlap > 0f)
            {
                ConsiderAxis(o.Left  - moving.Right, rSnap, ref bestDx, ref absDx);   // moving.right ↔ o.left
                ConsiderAxis(o.Right - moving.Left,  rSnap, ref bestDx, ref absDx);   // moving.left  ↔ o.right
            }
            // Horizontal-edge kiss (y snap) requires overlap along x.
            if (xOverlap > 0f)
            {
                ConsiderAxis(o.Bottom - moving.Top,    rSnap, ref bestDy, ref absDy); // moving.top    ↔ o.bottom
                ConsiderAxis(o.Top    - moving.Bottom, rSnap, ref bestDy, ref absDy); // moving.bottom ↔ o.top
            }
        }
        return new Vector2(bestDx, bestDy);
    }

    // ADR-0024 §4 / findings 0088 §4: resolve the SINGLE-axis offset that snaps `moving` flush against
    // `target`'s nearest edge — used at release when an island/detached window is dropped OVERLAPPING
    // another island (commit = snap to nearest flush, then merge). The 4 candidates are moving.right→
    // target.left / left→right / bottom→top / top→bottom; a candidate is valid only when the ORTHOGONAL
    // axis overlaps ≥ 1px (so the post-snap windows actually share an edge segment). The smallest |Δ|
    // wins; ties break left/right (horizontal dock) over top/bottom (vertical). No valid candidate →
    // Vector2.zero (degenerate; the caller falls back to a plain translate commit).
    public static Vector2 ResolveNearestFlush(DockRect moving, DockRect target)
    {
        float xOverlap = Math.Min(moving.Right, target.Right) - Math.Max(moving.Left, target.Left);
        float yOverlap = Math.Min(moving.Top,   target.Top)   - Math.Max(moving.Bottom, target.Bottom);

        Vector2 best = Vector2.zero;
        float bestAbs = float.PositiveInfinity;
        // Horizontal candidates FIRST so a tie resolves to left/right (the spec tie-break).
        if (yOverlap >= 1f)
        {
            ConsiderFlushVec(new Vector2(target.Left  - moving.Right, 0f), ref best, ref bestAbs); // right→left
            ConsiderFlushVec(new Vector2(target.Right - moving.Left,  0f), ref best, ref bestAbs); // left→right
        }
        if (xOverlap >= 1f)
        {
            ConsiderFlushVec(new Vector2(0f, target.Top    - moving.Bottom), ref best, ref bestAbs); // bottom→top
            ConsiderFlushVec(new Vector2(0f, target.Bottom - moving.Top),    ref best, ref bestAbs); // top→bottom
        }
        return best;
    }

    static void ConsiderFlushVec(Vector2 candidate, ref Vector2 best, ref float bestAbs)
    {
        float a = Math.Abs(candidate.x) + Math.Abs(candidate.y);   // one axis is 0, so this is |Δ|
        if (!IsFinite(a)) return;
        if (a < bestAbs) { best = candidate; bestAbs = a; }        // strict < ⇒ earlier (x) wins ties
    }

    // ADR-0024 §4 / findings 0088 §5 (review fix): the union bounding box of a set of rects — the OUTER
    // 4 edges of an island. Used at overlap commit to snap the moving island/window flush to the TARGET
    // ISLAND's outer bbox (not the single member rect under the cursor). Null/empty → default (zero) rect.
    public static DockRect UnionBbox(IList<DockRect> rects)
    {
        if (rects == null || rects.Count == 0) return default;
        float left = float.PositiveInfinity, top = float.NegativeInfinity;
        float right = float.NegativeInfinity, bottom = float.PositiveInfinity;
        for (int i = 0; i < rects.Count; i++)
        {
            DockRect r = rects[i];
            if (r.Left < left) left = r.Left;
            if (r.Right > right) right = r.Right;
            if (r.Top > top) top = r.Top;
            if (r.Bottom < bottom) bottom = r.Bottom;
        }
        return new DockRect(new Vector2(left, top), new Vector2(right - left, top - bottom));
    }

    // ADR-0024 §3 / findings 0088 §3: the pure ease-out-back curve for the spring "プルン". e(0)=0,
    // e(1)=1, single overshoot peaking at 1 + SPRING_OVERSHOOT_RATIO (8%) at t = 0.6, then settling.
    // t is clamped to [0,1]. This is the AFK-authoritative animation shape (DRAG-10 pins the overshoot
    // peak exactly); the production spring driver samples it over SPRING_DURATION_MS.
    public static float SpringEase(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        float p = t - 1f;
        return 1f + (SPRING_BACK_S + 1f) * p * p * p + SPRING_BACK_S * p * p;
    }

    // ADR-0024 §3 / findings 0088 §3: the spring-interpolated rect at normalized time t ∈ [0,1].
    // Position AND size interpolate through SpringEase (swap commit animates size too). LerpUnclamped so
    // the >1 overshoot value carries past the target before settling.
    public static DockRect SpringRectAt(DockRect from, DockRect to, float t)
    {
        float e = SpringEase(t);
        return new DockRect(
            Vector2.LerpUnclamped(from.topLeft, to.topLeft, e),
            Vector2.LerpUnclamped(from.size,    to.size,    e));
    }
    // #99: a window's geometry in canvas-LOGICAL coordinates for the magnet-snap math (findings
    // 0075 §1). topLeft is the FloatingWindowController contract (anchoredPosition = top-left
    // pivot, x right-positive, y up-positive); size is sizeDelta (w,h > 0). Edges are derived
    // here so the snap math compares apples to apples without forcing the caller to know that
    // "Top" is y_high and "Bottom" is y_low under the top-left-pivot convention.
    public struct DockRect
    {
        public Vector2 topLeft;
        public Vector2 size;

        public DockRect(Vector2 topLeft, Vector2 size) { this.topLeft = topLeft; this.size = size; }
        public DockRect(float x, float y, float w, float h) { topLeft = new Vector2(x, y); size = new Vector2(w, h); }

        public float Left => topLeft.x;
        public float Right => topLeft.x + size.x;
        public float Top => topLeft.y;                       // higher-Y edge (y is up-positive)
        public float Bottom => topLeft.y - size.y;           // lower-Y edge
    }

    // Viewport-local pixel drag delta -> canvas-logical delta. zoom is the live CanvasView zoom
    // (> 0 after sanitize). A non-positive/non-finite zoom is a degenerate transform; we guard
    // by returning a zero delta rather than dividing (the controller stays a no-op that frame).
    public static Vector2 ViewportDeltaToLogical(Vector2 viewportLocalDelta, float zoom)
    {
        if (!IsFinite(zoom) || zoom <= 0f) return Vector2.zero;
        return viewportLocalDelta / zoom;
    }

    // Stable backmost-first ordering of n windows by their persisted zOrder. Returns a
    // permutation `order` of [0..n): order[k] = the index (into the INPUT list) of the window
    // that belongs at sibling slot k (slot 0 = backmost). Ascending zOrder; ties keep the input
    // order. Duplicate / negative / non-contiguous zOrder all collapse to a contiguous 0..n-1.
    public static int[] SiblingOrder(IList<int> zOrders)
    {
        int n = zOrders?.Count ?? 0;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        // Explicit index tie-break makes the result deterministic regardless of Array.Sort's
        // (unspecified) stability, AND realizes the "ties keep original list order" rule.
        Array.Sort(order, (a, b) =>
            zOrders[a] != zOrders[b] ? zOrders[a].CompareTo(zOrders[b]) : a.CompareTo(b));
        return order;
    }

    // #99 Slice 1 (ADR-0017 / findings 0075 §1): the magnet-snap offset for a window just
    // released from a title-bar drag. Returns the canvas-logical Δ to add to `dragged.topLeft`
    // so its nearest edge kisses or lines up with the nearest neighbour's edge.
    //
    // For every other window, 8 candidates are enumerated (4 per axis): on x, the dragged's
    // {left,right} matched against the neighbour's {left,right} as both FLUSH (right↔left,
    // left↔right — windows touch) and SAME-EDGE (left↔left, right↔right — windows line up);
    // identically on y with {top,bottom}. The signed Δ with the smallest |Δ| ≤ threshold per
    // AXIS wins (x and y are decided INDEPENDENTLY — findings 0075 §1: "x は A に、y は B
    // に揃ってよい"). Beyond `threshold` on an axis → that axis's Δ is 0 (no group concept, no
    // resize, no push-out — findings 0075 §1 / §0 "結合なし"). threshold ≤ 0 or non-finite
    // → returns Vector2.zero (degenerate; the controller stays a no-op release).
    //
    // The dragged window itself MUST NOT appear in `others` (caller's job; the controller's
    // SnapOnRelease excludes the dragged id). An empty `others` → Vector2.zero.
    //
    // No view of "which neighbour": the snap result is a single Δ to apply to the dragged
    // window. The neighbours are unchanged, no group is formed, the next drag of any window
    // moves only that window — exactly matching ADR-0017 Decision 2 ("常に各 window 独立").
    public static Vector2 SnapOffset(DockRect dragged, IList<DockRect> others, float threshold)
    {
        if (!IsFinite(threshold) || threshold <= 0f) return Vector2.zero;
        if (others == null || others.Count == 0) return Vector2.zero;

        float bestDx = 0f, absDx = float.PositiveInfinity;
        float bestDy = 0f, absDy = float.PositiveInfinity;

        for (int i = 0; i < others.Count; i++)
        {
            DockRect b = others[i];

            // x candidates: 2 FLUSH + 2 SAME-EDGE.
            ConsiderAxis(b.Left  - dragged.Right, threshold, ref bestDx, ref absDx);   // A.right ↔ B.left   (flush right)
            ConsiderAxis(b.Right - dragged.Left,  threshold, ref bestDx, ref absDx);   // A.left  ↔ B.right  (flush left)
            ConsiderAxis(b.Left  - dragged.Left,  threshold, ref bestDx, ref absDx);   // A.left  ↔ B.left   (align)
            ConsiderAxis(b.Right - dragged.Right, threshold, ref bestDx, ref absDx);   // A.right ↔ B.right  (align)

            // y candidates (y up-positive: Top = higher edge, Bottom = lower edge).
            ConsiderAxis(b.Bottom - dragged.Top,    threshold, ref bestDy, ref absDy); // A.top    ↔ B.bottom (flush top — A above B)
            ConsiderAxis(b.Top    - dragged.Bottom, threshold, ref bestDy, ref absDy); // A.bottom ↔ B.top    (flush bottom — A below B)
            ConsiderAxis(b.Top    - dragged.Top,    threshold, ref bestDy, ref absDy); // A.top    ↔ B.top    (align)
            ConsiderAxis(b.Bottom - dragged.Bottom, threshold, ref bestDy, ref absDy); // A.bottom ↔ B.bottom (align)
        }

        return new Vector2(bestDx, bestDy);
    }

    // Keep the candidate Δ if its magnitude is finite, ≤ threshold, and strictly smaller than
    // the current best on this axis (ties → keep first = list order — owner-locked
    // deterministic tie-break; the controller hands `others` in dictionary order).
    static void ConsiderAxis(float candidate, float threshold, ref float best, ref float bestAbs)
    {
        if (!IsFinite(candidate)) return;
        float a = Math.Abs(candidate);
        if (a > threshold) return;
        if (a < bestAbs) { best = candidate; bestAbs = a; }
    }

    static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    // #104 (ADR-0019 / findings 0082 §3): flush-adjacency test for the SnapOnRelease post-snap attach
    // trigger. After SnapOffset has aligned a release, two windows are "flush" iff one of their opposing
    // edges sits within `eps` AND the orthogonal axis has a strictly positive overlap segment (a corner-
    // only contact has overlap=0 and is NOT flush). same-edge alignment (left↔left etc., the OTHER
    // SnapOffset case) is NOT flush even when the eps gate passes — windows lined up next to each other
    // with a gap are not group members. Pure / headless / AFK权威.
    //
    // The 4 candidate flush pairings (each requires non-degenerate perpendicular-axis overlap):
    //   a.right == b.left  (a is the LEFT  neighbour kissed onto b's left edge) ∧ y-overlap > 0
    //   a.left  == b.right (a is the RIGHT neighbour)                            ∧ y-overlap > 0
    //   a.bottom == b.top  (a is BELOW b — y up-positive ⇒ a.bottom = lower; b.top = higher) ∧ x-overlap > 0
    //   a.top   == b.bottom (a is ABOVE b)                                       ∧ x-overlap > 0
    //
    // eps ≤ 0 / non-finite ⇒ false (degenerate threshold ⇒ no attach). Default production eps is 1 px
    // (findings 0082 §3); the caller chooses (the math takes no constant).
    public static bool IsFlushAdjacent(DockRect a, DockRect b, float eps)
    {
        if (!IsFinite(eps) || eps <= 0f) return false;
        float xOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        float yOverlap = Math.Min(a.Top,   b.Top)   - Math.Max(a.Bottom, b.Bottom);
        // F6 (#104 correctness): "flush" means kiss (gap == 0) within FP slack, NOT overlap.
        // The old |gap| <= eps fired symmetrically on a 0.5px genuine overlap; we now require
        // the gap to be non-negative (subject to a tiny FP slack that absorbs round-off from
        // SnapOffset's anchoredPosition adds). eps stays the production-tunable kiss tolerance
        // (DEFAULT_FLUSH_EPS = 1 px logical); the sub-pixel slack is the shared FLUSH_FP_SLACK (single source —
        // the resize push's IsFlushRightOf/IsFlushBelowOf use the SAME constant per the "MUST match" note there).
        // Horizontal-axis flush (vertical edge kiss): the SHARED edge runs along y; need y-overlap > 0.
        if (yOverlap > 0f)
        {
            // a.right ↔ b.left: a sits to the LEFT of b, signed gap = b.Left - a.Right (≥ 0 means no overlap).
            float gap1 = b.Left - a.Right;
            if (gap1 >= -FLUSH_FP_SLACK && gap1 <= eps) return true;
            // a.left ↔ b.right: a sits to the RIGHT of b, signed gap = a.Left - b.Right.
            float gap2 = a.Left - b.Right;
            if (gap2 >= -FLUSH_FP_SLACK && gap2 <= eps) return true;
        }
        // Vertical-axis flush (horizontal edge kiss): the shared edge runs along x; need x-overlap > 0.
        if (xOverlap > 0f)
        {
            // a.bottom ↔ b.top: a sits ABOVE b (y up-positive ⇒ a.Bottom > b.Top means no overlap),
            //                   signed gap = a.Bottom - b.Top.
            float gap3 = a.Bottom - b.Top;
            if (gap3 >= -FLUSH_FP_SLACK && gap3 <= eps) return true;
            // a.top ↔ b.bottom: a sits BELOW b, signed gap = b.Bottom - a.Top.
            float gap4 = b.Bottom - a.Top;
            if (gap4 >= -FLUSH_FP_SLACK && gap4 <= eps) return true;
        }
        return false;
    }

    // ADR-0024 §5 / findings 0088 §5: a single merge participant — a group (or singleton) involved in a
    // release attach / overlap merge. The controller projects each contributing group into one of these
    // and hands an array to ResolveMergeWinner. Pure POCO so the cascade is AFK-driveable headlessly.
    // `id`=null marks a SINGLETON participant (current groupId=null); the cascade treats it as a winnable
    // group only if EVERY participant is null (then a fresh GUID is minted). `memberCount` = visible/live
    // member count (1 for singletons). The ADR-0019 Hakoniwa-priority field is RETIRED (ADR-0024 §1 —
    // no core special-casing).
    public struct MergeCandidate
    {
        public string id;
        public int memberCount;

        public MergeCandidate(string id, int memberCount)
        {
            this.id = id;
            this.memberCount = memberCount;
        }
    }

    // ADR-0024 §5 / findings 0088 §5: cascade-decide the surviving groupId when a release attach / merge
    // unites several groups (the dragged + each flush-adjacent partner / the overlapped island).
    // Hakoniwa-priority is GONE (ADR-0024 §1); the cascade is now a clean two-key sort:
    //   1. **Member-count max** among participants with a current group (non-null id).
    //   2. **Dictionary order min** among the tied (StringCompareOrdinal — deterministic).
    //   3. **All-null** (singleton ↔ singleton attach): returns null — the controller mints a new
    //      `grp_<hex32>` GUID. This is the ONLY GUID-mint trigger; everywhere else an existing id wins.
    //
    // `candidates` must include the dragged AND each contributing partner (one entry per group). The
    // SAME id may appear multiple times if the dragged and a partner already share a group — the cascade
    // still resolves to that id. A null / empty / all-null-id array returns null.
    public static string ResolveMergeWinner(IList<MergeCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;

        // Step 1/2: max member count, then dictionary order (StringCompareOrdinal, deterministic).
        // Participants with a null/empty id (singletons) are skipped; if NONE has an id ⇒ null (mint).
        string bestId = null;
        int bestCount = -1;
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.id)) continue;
            if (c.memberCount > bestCount
                || (c.memberCount == bestCount && (bestId == null || string.CompareOrdinal(c.id, bestId) < 0)))
            {
                bestId = c.id;
                bestCount = c.memberCount;
            }
        }
        return bestId;
    }
}
