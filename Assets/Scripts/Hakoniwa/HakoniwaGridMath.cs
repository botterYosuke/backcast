// HakoniwaGridMath.cs — issue #14 "Hakoniwa split-grid" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless float arithmetic for the Hakoniwa split-grid surface — the
// AFK gate proves THIS (no playmode, no Canvas update), exactly as #12's
// LayoutBinder.ToNormalizedRect and #13's CanvasViewMath are the authoritative math.
// NOTHING here touches a RectTransform, a render, or input; HakoniwaController is the thin
// Unity boundary that reads/writes tile RectTransforms from these results.
//
// GRID MODEL (findings 0007 §1/§4, owner-locked; capability parity with TTWR
// src/ui/hakoniwa.rs split_grid_tile_rects / grid_dims, ADR 0011):
//   * LOCKED ceil(√n) grid: cols = ceil(√n), rows = ceil(n / cols). EQUAL fractions only
//     (#14 does NOT do divider resize — that is a future slice, findings 0007 §0).
//   * Cells are ROW-MAJOR, left->right / top->bottom (slot 0 = top-left).
//   * CellRects(n) returns EXACTLY n cells inside that grid: for n=5 -> 3x2 grid with the
//     6th cell left EMPTY (no cell generated). The grid stays ceil(√n).
//
// NORMALIZED 0..1 (findings 0007 §3): a cell is the tile's normalized display rect within
// the HakoniwaRoot — Y is UP-positive (uGUI anchor convention, 0 bottom / 1 top), so row 0
// occupies the TOP band [1 - 1/rows .. 1]. HakoniwaController places each tile by setting
// anchorMin/Max to its cell corners with offsets zeroed (canonical, resolution-independent —
// the SAME canonical-anchor form #12's LayoutBinder.Apply uses).

using System.Collections.Generic;
using UnityEngine;

public static class HakoniwaGridMath
{
    // (cols, rows) for n tiles: cols = ceil(√n), rows = ceil(n / cols). n<=0 -> (0,0).
    public static void GridDims(int n, out int cols, out int rows)
    {
        if (n <= 0) { cols = 0; rows = 0; return; }
        cols = Mathf.CeilToInt(Mathf.Sqrt(n));
        rows = (n + cols - 1) / cols;   // ceil(n / cols)
    }

    // n cells, row-major, equal fractions, normalized 0..1 within the root (Y up). Cell i:
    // col = i % cols, row = i / cols. Returns EXACTLY n rects (an empty trailing cell in the
    // ceil(√n) grid is simply not generated).
    public static List<LayoutRect> CellRects(int n)
    {
        var cells = new List<LayoutRect>(n > 0 ? n : 0);
        if (n <= 0) return cells;

        GridDims(n, out int cols, out int rows);
        float cw = 1f / cols;
        float ch = 1f / rows;

        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            int row = i / cols;
            float minX = col * cw;
            float maxX = (col + 1) * cw;
            // row 0 at the TOP: maxY = 1 - row*ch, minY = 1 - (row+1)*ch.
            float maxY = 1f - row * ch;
            float minY = 1f - (row + 1) * ch;
            cells.Add(new LayoutRect(minX, minY, maxX, maxY));
        }
        return cells;
    }

    // The cell containing a root-local NORMALIZED point (0..1), or -1 if none. Inclusive
    // bounds; returns the first hit (cells are disjoint except on shared edges).
    public static int SlotAt(IList<LayoutRect> cells, Vector2 pointNormalized)
    {
        if (cells == null) return -1;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c == null) continue;
            if (pointNormalized.x >= c.minX && pointNormalized.x <= c.maxX
                && pointNormalized.y >= c.minY && pointNormalized.y <= c.maxY)
                return i;
        }
        return -1;
    }
}
