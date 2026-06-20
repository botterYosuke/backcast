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
}
