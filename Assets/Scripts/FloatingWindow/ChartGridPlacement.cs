// ChartGridPlacement.cs — issue #114 (findings 0089 F4): pure helper for placing chart windows
// when they have NO saved x/y. Replaces the SyncChartWindowsToUniverse focus-snap cascade
// (BackcastWorkspaceRoot.cs:937-942) that scattered chart windows down-right into a staircase as
// the universe grew.
//
// PURE (no UnityEngine field beyond Vector2 / Rect), so the AFK gate proves the layout headlessly
// — same two-tier discipline as DockDefaultPlacement / DockSnapPlacement / FloatingWindowMath.
// Caller decides the chart fixed size (#101) AND the grid column count (callers know the universe
// total; if the helper picked cols = ceil(√n) internally, an incremental call with n=1 would yield
// cols=1 and a single-column vertical cascade — a different staircase. The owner caught this in
// the F4 grill, so gridCols is an explicit caller parameter.).
//
// COORDINATES: canvas-LOGICAL, top-left pivot, y up-positive (DockDefaultPlacement convention).
// Slot k is laid row-major: (col = k % gridCols, row = k / gridCols). Row 0 sits at the highest y;
// each successive row descends by chartSize.y + gap.y.
//
// NOT THE LIVE LAYOUT SOT (findings 0075 §4): once a chart is placed and the user moves it (or a
// saved layout restores it), this helper is NOT consulted again — the live geometry IS the state
// of truth (persisted via floatingWindows x/y/w/h/zOrder/visible). The helper kicks in only when
// there is no saved geometry to honor.

using System.Collections.Generic;
using UnityEngine;

public static class ChartGridPlacement
{
    // Default gap between chart cells in canvas-LOGICAL pixels. Same value DockDefaultPlacement
    // uses for the base dock cluster, so the chart grid is visually consistent with the cluster
    // above it.
    public static readonly Vector2 DefaultGap = new Vector2(12f, 12f);

    // The chart grid's top-left in canvas-LOGICAL coords: sits directly BELOW the base dock cluster
    // (DockDefaultPlacement.CentredAnchorTopLeft(1200, 640) = (-600, +320); the cluster's BOTTOM is
    // therefore (-600, -320)). Use DockDefaultPlacement.DefaultGap.y (the cluster's own gap constant)
    // for the cluster→chart-grid clearance so the offset stays bound to the cluster's spec — if the
    // cluster's gap is ever retuned, the chart grid clearance follows. Using ChartGridPlacement's own
    // DefaultGap here would silently desync the two whenever the chart-internal gap is tuned alone.
    public static Vector2 DefaultAnchorTopLeft =>
        new Vector2(-DockDefaultPlacement.DefaultBoxSize.x * 0.5f,
                    -DockDefaultPlacement.DefaultBoxSize.y * 0.5f - DockDefaultPlacement.DefaultGap.y);

    // Compute n chart-grid cells in canvas-LOGICAL coords. cols = ceil(√n), row-major.
    // slot k -> (col = k % cols, row = k / cols). Returns EXACTLY n rects.
    //   n <= 0      -> empty list.
    //   chartSize.x/y are spec-fixed (KIND_CHART defaultSize) — NOT scaled by n (#101).
    public static List<FloatingWindowMath.DockRect> ComputeFlushSlots(
        int n, Vector2 anchorTopLeft, Vector2 chartSize, Vector2 gap)
    {
        var rects = new List<FloatingWindowMath.DockRect>(n > 0 ? n : 0);
        if (n <= 0) return rects;

        int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
        if (cols < 1) cols = 1;

        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            int row = i / cols;
            float x = anchorTopLeft.x + col * (chartSize.x + gap.x);
            float y = anchorTopLeft.y - row * (chartSize.y + gap.y);   // y up-positive: row 0 highest
            rects.Add(new FloatingWindowMath.DockRect(x, y, chartSize.x, chartSize.y));
        }
        return rects;
    }

    // Allocate n top-left placement points (canvas-LOGICAL) that do NOT overlap any `avoid` rect.
    // gridCols is supplied by the caller (NOT derived from n) so incremental calls (n=1) still
    // wrap horizontally — caller passes ceil(√universeTotal) so the layout matches the eventual
    // full grid as charts arrive one at a time.
    //
    // Iterates slot 0, 1, 2, ... laid out as (col = k % gridCols, row = k / gridCols), skipping
    // any slot whose chart-sized rect overlaps an entry in `avoid`. Grid is UNBOUNDED (rows grow
    // as needed), so the slot counter advances until `produced == n` — no max-attempts bound is
    // needed and there is no fallback branch.
    //
    // Overlap test uses Rect.Overlaps with closed-edge logic — windows that exactly butt edges
    // (touch but do not strictly overlap) are CONSIDERED NON-OVERLAPPING (Rect.Overlaps returns
    // false on equal edge), matching the flush-snap convention of DockSnapPlacement.
    //
    // Performance: O(slots * avoid.Count). At realistic universe sizes (≤ a few hundred charts)
    // this is single-digit ms in batchmode — no spatial index needed.
    public static List<Vector2> AllocateNonOverlappingTopLefts(
        int n,
        int gridCols,
        Vector2 anchorTopLeft,
        Vector2 chartSize,
        Vector2 gap,
        IReadOnlyList<Rect> avoid)
    {
        var result = new List<Vector2>(n > 0 ? n : 0);
        if (n <= 0) return result;
        if (gridCols < 1) gridCols = 1;

        int slot = 0;
        while (result.Count < n)
        {
            int col = slot % gridCols;
            int row = slot / gridCols;
            float x = anchorTopLeft.x + col * (chartSize.x + gap.x);
            float y = anchorTopLeft.y - row * (chartSize.y + gap.y);

            // canvas-LOGICAL has y up-positive (top-left pivot). Rect uses (xMin, yMin) with width/
            // height extending positively; we flip y by `y - height` so the resulting Rect's yMin is
            // the candidate's BOTTOM edge in canvas-LOGICAL. Avoid rects built the same way (see
            // BackcastWorkspaceRoot.CollectChartGridAvoidRects: `new Rect(w.x, w.y - w.h, w.w, w.h)`)
            // overlap correctly because both sides share the convention.
            var candidate = new Rect(x, y - chartSize.y, chartSize.x, chartSize.y);
            if (!OverlapsAny(candidate, avoid))
                result.Add(new Vector2(x, y));
            slot++;
        }
        return result;
    }

    static bool OverlapsAny(Rect candidate, IReadOnlyList<Rect> avoid)
    {
        if (avoid == null) return false;
        for (int i = 0; i < avoid.Count; i++)
            if (candidate.Overlaps(avoid[i])) return true;
        return false;
    }
}
