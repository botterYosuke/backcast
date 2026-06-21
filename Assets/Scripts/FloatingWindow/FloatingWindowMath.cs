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
    // #104 (ADR-0019 / findings 0082 §6): drag mode enum — the 7 cases the title-input boundary picks each
    // frame based on the dragged's group membership, the dragged's |cursor - rest_at_drag_start| distance,
    // the group's Hakoniwa status, the dragged's core status, and whether a swap drop target exists.
    //
    //   SoloDrag             — the dragged is NOT in a group (groupId=null OR group has <2 visible/live
    //                          members). Live geometry: dragged tracks cursor (existing #15 behaviour).
    //   NormalGroupTranslate — group ∧ non-Hakoniwa ∧ |cursor - rest| < D_DETACH. Live geometry: every
    //                          group member is translated by the frame's delta (the ONLY mode that mutates
    //                          live geometry mid-drag — findings 0082 §8 "commit-on-release" rule for
    //                          everything else).
    //   NormalGroupDetach    — group ∧ non-Hakoniwa ∧ |cursor - rest| ≥ D_DETACH. Live geometry: frozen
    //                          during drag; release commits dragged.groupId=null (Slice D).
    //   HakoniwaSwap         — Hakoniwa group ∧ |cursor - rest| < D_DETACH ∧ swap drop target exists.
    //                          Live geometry: frozen; release commits (x,y,w,h) 4-value swap (Slice E2).
    //   HakoniwaSnapBack     — Hakoniwa group ∧ |cursor - rest| < D_DETACH ∧ no target. Live geometry:
    //                          frozen; release leaves dragged at rest (Slice E2).
    //   HakoniwaDetach       — Hakoniwa group ∧ |cursor - rest| ≥ D_DETACH ∧ non-core. Live geometry:
    //                          frozen; release commits detach (Slice D).
    //   HakoniwaCoreLock     — Hakoniwa group ∧ |cursor - rest| ≥ D_DETACH ∧ core. Live geometry: frozen;
    //                          release snaps back to rest (cores are detach-immune — Slice E1).
    public enum DragMode
    {
        SoloDrag,
        NormalGroupTranslate,
        NormalGroupDetach,
        HakoniwaSwap,
        HakoniwaSnapBack,
        HakoniwaDetach,
        HakoniwaCoreLock,
    }

    // #104 (ADR-0019 / findings 0082 §6): drag-mode evaluation context — the structural inputs
    // EvaluateDragMode needs each frame. Pure POCO (no UnityEngine.RectTransform / Unity references) so
    // the AFK gate drives the 7-mode boundary without a scene. The title-input layer fills this in by
    // reading the controller's group state at OnBeginDrag (cached for the duration of the drag — the
    // dragged's group / hakoniwa / core status do not change mid-drag) and updating cursor + hasTarget
    // per frame.
    public struct DragContext
    {
        public Vector2 rest;          // dragged top-left at OnBeginDrag (canvas-logical)
        public Vector2 cursor;        // current cursor canvas-logical position (rest + accumulated delta)
        public bool isInGroup;        // dragged's groupId != null AND group has ≥ 2 visible/live members
        public bool isHakoniwa;       // isInGroup ∧ group has a visible/live core member
        public bool isCore;           // dragged itself is a core member (DockShape.IsCoreKind)
        public bool hasTarget;        // a swap drop target exists this frame (cursor over another group member — Slice E2)
    }

    // #104 (ADR-0019 D6 / findings 0082 §6): the detach distance threshold in canvas-LOGICAL px.
    // Distance one — no velocity, no modifier keys, no direction conditions (the design rejects all
    // these in favour of a single AFK-checkable scalar). Zoom-independent (canvas-logical, like
    // DEFAULT_SNAP_THRESHOLD): the felt distance does not change when the user zooms.
    public const float D_DETACH = 64f;

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
    // caller's drag-mode logic already evaluates "near vs far" via D_DETACH, so edge equality at the
    // resolver is harmless (the swap still produces a defined (x,y,w,h) commit).
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

    // #104 (ADR-0019 / findings 0082 §6): pure 7-mode classifier. Distance test is `> D_DETACH`
    // (strict — `==` belongs with "still close enough", matching the threshold-INCLUSIVE feel of
    // SnapOffset). Branches in spec order so the AFK gate can pin each boundary independently.
    public static DragMode EvaluateDragMode(in DragContext ctx)
    {
        if (!ctx.isInGroup) return DragMode.SoloDrag;
        bool detached = (ctx.cursor - ctx.rest).magnitude > D_DETACH;
        if (ctx.isHakoniwa)
        {
            if (detached) return ctx.isCore ? DragMode.HakoniwaCoreLock : DragMode.HakoniwaDetach;
            return ctx.hasTarget ? DragMode.HakoniwaSwap : DragMode.HakoniwaSnapBack;
        }
        return detached ? DragMode.NormalGroupDetach : DragMode.NormalGroupTranslate;
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
        // (DEFAULT_FLUSH_EPS = 1 px logical); fpSlack is fixed sub-pixel.
        const float fpSlack = 1e-4f;
        // Horizontal-axis flush (vertical edge kiss): the SHARED edge runs along y; need y-overlap > 0.
        if (yOverlap > 0f)
        {
            // a.right ↔ b.left: a sits to the LEFT of b, signed gap = b.Left - a.Right (≥ 0 means no overlap).
            float gap1 = b.Left - a.Right;
            if (gap1 >= -fpSlack && gap1 <= eps) return true;
            // a.left ↔ b.right: a sits to the RIGHT of b, signed gap = a.Left - b.Right.
            float gap2 = a.Left - b.Right;
            if (gap2 >= -fpSlack && gap2 <= eps) return true;
        }
        // Vertical-axis flush (horizontal edge kiss): the shared edge runs along x; need x-overlap > 0.
        if (xOverlap > 0f)
        {
            // a.bottom ↔ b.top: a sits ABOVE b (y up-positive ⇒ a.Bottom > b.Top means no overlap),
            //                   signed gap = a.Bottom - b.Top.
            float gap3 = a.Bottom - b.Top;
            if (gap3 >= -fpSlack && gap3 <= eps) return true;
            // a.top ↔ b.bottom: a sits BELOW b, signed gap = b.Bottom - a.Top.
            float gap4 = b.Bottom - a.Top;
            if (gap4 >= -fpSlack && gap4 <= eps) return true;
        }
        return false;
    }

    // #104 (ADR-0019 / findings 0082 §4): a single merge participant — a group involved in the
    // SnapOnRelease attach commit. The controller projects each flush-adjacent partner's group (and the
    // dragged's own current group) into one of these and hands an array to ResolveMergeWinner. Pure POCO
    // (no UnityEngine) so the cascade is AFK-driveable headlessly. `id`=null marks a SINGLETON
    // participant (dragged or partner with current groupId=null) — the cascade only treats it as a
    // winnable group if every other participant is also null. `memberCount` = visible/live member count
    // for that group (1 for singletons). `hasCore` = the group contains a visible/live core member.
    public struct MergeCandidate
    {
        public string id;
        public int memberCount;
        public bool hasCore;

        public MergeCandidate(string id, int memberCount, bool hasCore)
        {
            this.id = id;
            this.memberCount = memberCount;
            this.hasCore = hasCore;
        }
    }

    // #104 (ADR-0019 / findings 0082 §4): cascade-decide the surviving groupId when a release attach
    // merges several groups (the dragged + each flush-adjacent partner's group).
    //
    // Cascade:
    //   1. **Hakoniwa-priority**: among participants with non-null id AND hasCore, the ONLY one wins
    //      (if a single Hakoniwa group is involved, its identity is protected). If 2+ Hakoniwa
    //      participants tie, fall through to step 2 across the Hakoniwa subset.
    //   2. **Member-count max** among the remaining contenders (Hakoniwa subset if >1 Hakoniwa, else all
    //      non-null-id participants).
    //   3. **Dictionary order min** among the tied contenders (StringCompareOrdinal — deterministic).
    //   4. **All-null** (singleton ↔ singleton attach): returns null — the controller mints a new
    //      `grp_<hex32>` GUID. This branch is the ONLY GUID-mint trigger; everywhere else, an existing
    //      groupId survives.
    //
    // `candidates` must include the dragged AND each flush-adjacent partner (one entry per group). The
    // SAME id may appear multiple times if the dragged and a partner already share a group — the cascade
    // still resolves to that id (its count/hasCore properties are the same). A null array / empty array
    // / all-null-id array returns null.
    public static string ResolveMergeWinner(IList<MergeCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;

        // Filter to participants with a current group (non-null id). All-null ⇒ no winner, caller mints GUID.
        List<MergeCandidate> contenders = new List<MergeCandidate>(candidates.Count);
        foreach (var c in candidates) if (!string.IsNullOrEmpty(c.id)) contenders.Add(c);
        if (contenders.Count == 0) return null;

        // Step 1: Hakoniwa-priority. If exactly one Hakoniwa group is involved, it wins outright.
        // If 2+ are involved, restrict the cascade to the Hakoniwa subset (a non-Hakoniwa group can
        // NEVER survive when a Hakoniwa group is in the merge).
        List<MergeCandidate> hakos = new List<MergeCandidate>(contenders.Count);
        foreach (var c in contenders) if (c.hasCore) hakos.Add(c);
        if (hakos.Count == 1) return hakos[0].id;
        if (hakos.Count >= 2) contenders = hakos;   // tie among Hakoniwa: step 2/3 across the subset only

        // Step 2/3: max member count, then dictionary order (StringCompareOrdinal, deterministic).
        string bestId = null;
        int bestCount = -1;
        foreach (var c in contenders)
        {
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
