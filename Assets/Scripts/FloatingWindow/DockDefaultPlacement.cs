// DockDefaultPlacement.cs — #99 Slice 2 (ADR-0017 / findings 0075 §4, owner-locked)
//
// The PURE helper that returns the canvas-LOGICAL rects for the dock-style first-launch /
// reset placement. The Hakoniwa surface used to be a `ceil(√n)` split-grid normalized inside
// HakoniwaRoot; ADR-0017 retires that surface, but a FIRST-LAUNCH (or "reset to defaults")
// experience still needs the base + chart windows to land somewhere sensible — a diagonal
// cascade off (0,0) would scatter many windows into illegible piles. So this helper transplants
// the OLD HakoniwaGridMath grid-dims (cols = ceil(√n), rows = ceil(n/cols)) as the INITIAL
// placement only, in ABSOLUTE canvas-logical coords (NOT normalized 0..1, NOT bounded inside
// a surface GameObject — windows are free to drift anywhere after spawn).
//
// PURE (no UnityEngine field beyond Vector2), so the AFK gate proves the layout headlessly —
// same two-tier discipline as #14 HakoniwaGridMath and #15 FloatingWindowMath. The caller
// (BackcastWorkspaceRoot) decides what kinds get placed AND with what size (the spec defaults
// are typically used); this helper only decides WHERE.
//
// NOT THE LIVE LAYOUT SOT (findings 0075 §4): once the user moves a window or a layout is
// restored from disk, the placement helper is NOT consulted again — the live geometry IS the
// state of truth (persisted via `floatingWindows` x/y/w/h/zOrder/visible). The helper kicks
// in only when there is no saved geometry to honor.

using System.Collections.Generic;
using UnityEngine;

public static class DockDefaultPlacement
{
    // Reasonable defaults for the production composition: a 1200×640 box centred just below the
    // canvas origin (top-left at (-600, 320)) with a small inter-tile gap. The owner can tune
    // these in BackcastWorkspaceRoot; the helper itself stays parameterized so the AFK probe
    // can drive different shapes deterministically.
    public static readonly Vector2 DefaultBoxSize = new Vector2(1200f, 640f);
    public static readonly Vector2 DefaultGap = new Vector2(12f, 12f);

    // Top-left in canvas-LOGICAL coords for a `boxSize` box centred horizontally on x=0 and with
    // its top at y=+boxSize.y/2 (so the box is roughly centred on the canvas origin which the
    // viewport centres on at boot — findings 0006 §2). Suitable as the `anchorTopLeft` for
    // ComputeRects unless the caller wants a different anchor.
    public static Vector2 CentredAnchorTopLeft(Vector2 boxSize)
        => new Vector2(-boxSize.x * 0.5f, boxSize.y * 0.5f);

    // Compute n canvas-LOGICAL rects in a grid-style initial placement:
    //   cols = ceil(√n), rows = ceil(n / cols)   (the OLD HakoniwaGridMath grid-dims, transplanted)
    //   cellW = (boxSize.x - (cols - 1) * gap.x) / cols
    //   cellH = (boxSize.y - (rows - 1) * gap.y) / rows
    // and lays them row-major with slot 0 at the top-left of the box, slot k at column (k % cols),
    // row (k / cols). y is canvas-LOGICAL up-positive (top-left pivot), so row 0 has the HIGHEST y.
    //
    // n ≤ 0 → empty list. The trailing cells of the ceil(√n) grid (e.g. n=5 in a 3×2 grid leaves
    // cell 5 empty) are simply not generated — the helper returns EXACTLY n rects. Degenerate
    // (non-finite / non-positive) box / gap dimensions are NOT sanitized — the caller is expected
    // to pass DefaultBoxSize / DefaultGap (or AFK probe-controlled values).
    public static List<FloatingWindowMath.DockRect> ComputeRects(
        int n, Vector2 anchorTopLeft, Vector2 boxSize, Vector2 gap)
    {
        var rects = new List<FloatingWindowMath.DockRect>(n > 0 ? n : 0);
        if (n <= 0) return rects;

        int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
        if (cols < 1) cols = 1;
        int rows = (n + cols - 1) / cols;

        float cellW = (boxSize.x - (cols - 1) * gap.x) / cols;
        float cellH = (boxSize.y - (rows - 1) * gap.y) / rows;

        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            int row = i / cols;
            float x = anchorTopLeft.x + col * (cellW + gap.x);
            float y = anchorTopLeft.y - row * (cellH + gap.y);   // y is up-positive: row 0 highest
            rects.Add(new FloatingWindowMath.DockRect(x, y, cellW, cellH));
        }
        return rects;
    }

    // Convenience overload using the default 1200×640 / 12px-gap box centred on the canvas origin.
    public static List<FloatingWindowMath.DockRect> ComputeRects(int n)
        => ComputeRects(n, CentredAnchorTopLeft(DefaultBoxSize), DefaultBoxSize, DefaultGap);

    // #105: FLUSH (gap = 0) first-launch placement for the base dock cluster. The cluster is bundled
    // into one Hakoniwa group (BackcastWorkspaceRoot.FormFactoryBaseGroup), and a group's members are
    // flush-adjacent by construction (ADR-0019: groups are born from a flush-snap). A non-zero gap would
    // leave the windows visibly "ungrouped" (隙間あり) — a grouped-but-not-flush state user drag never
    // creates. SpawnBaseDockWindows AND the AFK gate (S32) both call this, so the flush contract has ONE
    // source: if this gap ever becomes non-zero, the gate's IsFlushAdjacent assertion goes RED.
    public static List<FloatingWindowMath.DockRect> ComputeFlushRects(int n)
        => ComputeRects(n, CentredAnchorTopLeft(DefaultBoxSize), DefaultBoxSize, Vector2.zero);
}
