// FloatingWindowMath.cs — issue #15 "floating windows" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless float arithmetic for the floating-window seam — the AFK gate
// proves THIS (no playmode, no Canvas update, no input device), exactly as #13's
// CanvasViewMath and #14's HakoniwaGridMath are the authoritative math. NOTHING here touches
// a RectTransform, a render, or input; FloatingWindowController is the thin Unity boundary
// that reads/writes window RectTransforms from these results.
//
// Two pieces of locked design live here (findings 0008 §2/§4, owner-locked):
//
//  1. drag -> canvas-logical delta. A title-bar drag arrives as a VIEWPORT-LOCAL pixel delta
//     (the input boundary already ran it through RectTransformUtility, CanvasScaler-safe,
//     exactly as #13's pan does — NEVER raw eventData.delta). Dividing by the live zoom turns
//     viewport pixels into canvas-logical units, so a window tracks the cursor at any zoom.
//
//  2. z-order normalization. Persisted zOrder is kept verbatim (0 = backmost) and may be non-
//     contiguous / duplicated / negative (hand-authored or forward-evolved). SiblingOrder
//     stable-sorts it to a contiguous 0..n-1 sibling assignment: ascending zOrder, ties broken
//     by ORIGINAL list order (findings 0008 §3). uGUI draws children in sibling order (child 0
//     = back), so sibling slot k = the k-th window from the back.

using System;
using System.Collections.Generic;
using UnityEngine;

public static class FloatingWindowMath
{
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

    static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
}
