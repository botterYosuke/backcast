// DockSnapPlacement.cs — #101 Slice 1 (fix #99 regression; 方針: ADR-0017 / findings 0078)
//
// The PURE helper that decides WHERE a newly spawned dock window lands so it SNAPS FLUSH to a
// target window's edge instead of taking a count-dependent grid slot. #99 spawned chart windows
// at `DockDefaultPlacement.ComputeRects(N)[i]`, so a chart's SIZE changed with the live chart
// count N (1200×640 ÷ ceil(√N) grid cell). findings 0078 supersedes that for charts: the size is
// now the spec default (count-INDEPENDENT) and the POSITION is a flush-adjacent snap chosen here.
//
// PURE (no UnityEngine field beyond Vector2 — same two-tier discipline as DockDefaultPlacement /
// FloatingWindowMath), so the AFK gate proves the placement headlessly (FloatingWindowE2ERunner
// S14). The caller (FloatingWindowController.SpawnDockedToFocus) decides the target window and the
// size (the spec default); this helper only decides the top-left.
//
// CONTRACT (canvas-LOGICAL coords, top-left pivot, y up-positive — the FloatingWindowController
// convention shared with FloatingWindowMath.DockRect): given a `target` rect, the `newSize` of the
// window to place, the rects of all OTHER live windows, and a seam `gap`, return the top-left that
//   1. is FLUSH adjacent to `target` on the first FREE edge, searched RIGHT → DOWN → LEFT → UP, and
//   2. aligns the shared perpendicular edge with `target` (right/left → align TOPs; down/up → align
//      LEFTs), and
//   3. does NOT overlap any rect in `others` (strict AABB — touching edges is NOT an overlap, so a
//      flush placement that merely kisses `target` counts as free).
// `gap` is the seam spacing; the production caller passes 0 (owner intent: "flush（隙間 0）"). When
// ALL FOUR edges are occupied, fall back to a deterministic diagonal cascade off the right edge
// (mirrors SpawnPlacement.Next) so the result is always defined and never overlaps. The size is the
// caller's `newSize` verbatim — this helper NEVER changes the size (WHERE only, never HOW BIG).

using System.Collections.Generic;
using UnityEngine;

public static class DockSnapPlacement
{
    // The diagonal step used when every flush edge is occupied. Reuses the floating-window cascade
    // magnitude (SpawnPlacement.DefaultOffset = marimo SPAWN_OFFSET) so the dock overflow and the
    // cell overflow feel the same. Kept INDEPENDENT of `gap` so a flush (gap=0) request still makes
    // forward progress instead of stepping by zero and overlapping forever.
    public static float CascadeStep => SpawnPlacement.DefaultOffset;

    // Place `newSize` flush against `target`, picking the first non-overlapping edge in the order
    // right → down → left → up; if all four overlap something in `others`, cascade diagonally off the
    // right edge until clear. Returns the canvas-logical TOP-LEFT (size is the caller's `newSize`).
    public static Vector2 PlaceAdjacent(
        FloatingWindowMath.DockRect target, Vector2 newSize,
        IList<FloatingWindowMath.DockRect> others, float gap)
    {
        // Candidate top-lefts in search order. Each is flush (separated only by `gap`) on one edge
        // and aligned on the shared perpendicular edge (y up-positive: Top = higher y, Bottom = lower):
        //   right: left edge = target.Right + gap ; align Top  (new.Top = target.Top)
        //   down : top  edge = target.Bottom - gap ; align Left (new.Left = target.Left)
        //   left : right edge = target.Left - gap  -> top-left.x = target.Left - gap - newSize.x ; align Top
        //   up   : bottom edge = target.Top + gap  -> top-left.y = target.Top  + gap + newSize.y ; align Left
        var candidates = new Vector2[]
        {
            new Vector2(target.Right + gap,                 target.Top),                       // right
            new Vector2(target.Left,                        target.Bottom - gap),              // down
            new Vector2(target.Left - gap - newSize.x,      target.Top),                       // left
            new Vector2(target.Left,                        target.Top + gap + newSize.y),     // up
        };

        for (int i = 0; i < candidates.Length; i++)
            if (!OverlapsAny(candidates[i], newSize, others))
                return candidates[i];

        // All four edges blocked → deterministic diagonal cascade off the right candidate (down-right:
        // +x, -y under y-up-positive) until clear. Once the new window's LEFT edge passes EVERY other's
        // RIGHT edge, no x-overlap (hence no overlap) is possible, so bound the guard by exactly the steps
        // needed to reach that point. (others.Count steps — the marimo SpawnPlacement bound for POINT
        // collisions — is far too few here: one 30px step does not clear a 520-wide window, so a dense
        // cluster of full-size windows would otherwise return an OVERLAPPING best-effort.)
        Vector2 p = candidates[0];
        float maxRight = target.Right;
        int otherCount = others?.Count ?? 0;
        for (int i = 0; i < otherCount; i++)
            if (others[i].Right > maxRight) maxRight = others[i].Right;
        int guard = Mathf.Max(1, Mathf.CeilToInt((maxRight - p.x) / CascadeStep) + 2);
        for (int i = 0; i < guard; i++)
        {
            if (!OverlapsAny(p, newSize, others)) return p;
            p = new Vector2(p.x + CascadeStep, p.y - CascadeStep);
        }
        return p;   // best-effort (the guard guarantees p.x ≥ maxRight ⇒ non-overlap before this)
    }

    static bool OverlapsAny(Vector2 topLeft, Vector2 size, IList<FloatingWindowMath.DockRect> others)
    {
        if (others == null) return false;
        var r = new FloatingWindowMath.DockRect(topLeft, size);
        for (int i = 0; i < others.Count; i++)
            if (Overlaps(r, others[i])) return true;
        return false;
    }

    // Strict AABB overlap: edges that merely TOUCH (a.Left == b.Right, etc.) are NOT an overlap, so a
    // flush-adjacent placement that kisses the target is treated as free. y up-positive: Top > Bottom.
    static bool Overlaps(FloatingWindowMath.DockRect a, FloatingWindowMath.DockRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
}
