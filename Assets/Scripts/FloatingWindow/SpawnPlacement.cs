// SpawnPlacement.cs — issue #81 "cell-as-floating-window" (ADR-0013, PURE function)
//
// Where a newly spawned cell window lands on the infinite canvas. A PURE function (the layer-1 AFK
// gate unit-tests it) ported from marimo's calcSpawnPosition (cell-3d-renderer.tsx:45-81): start at
// the anchor (the viewport-centre top-left the caller hands in) and, while it collides with an
// existing window, step DIAGONALLY by a fixed offset (marimo baseX+offset / baseZ+offset) — the
// macOS-style cascade. Windows MAY overlap (the cascade is "not nearly the same point", threshold
// <10), so we do NOT force full non-overlap (that would be non-faithful).
//
// The collision set is ALL windows' top-lefts (cell windows AND Order etc.) so a new cell never
// spawns directly under an existing window. ANCHOR semantics: the anchor IS used as the top-left
// verbatim (no half-size centring correction — so this helper needs no w/h; true centring, if ever
// wanted, is the caller's half-size correction). UnityEngine.Vector2 is fine (a struct; this never
// round-trips to disk — only the resolved x,y persist, via the coordinator's cellPositions).

using System.Collections.Generic;
using UnityEngine;

public static class SpawnPlacement
{
    public const float DefaultOffset = 30f;       // marimo SPAWN_OFFSET
    public const float CollisionThreshold = 10f;  // marimo "<10" = effectively the same point

    public static Vector2 Next(IReadOnlyList<Vector2> existingTopLefts, Vector2 anchorTopLeft, float offset)
    {
        Vector2 p = anchorTopLeft;
        // Monotone diagonal cascade: at most one step past the farthest blocker clears it, so the
        // worst case (a full prior cascade exactly on this diagonal) needs <= existing+1 steps.
        int guard = (existingTopLefts?.Count ?? 0) + 1;
        for (int i = 0; i < guard; i++)
        {
            if (!CollidesAny(existingTopLefts, p)) return p;
            p = new Vector2(p.x + offset, p.y + offset);
        }
        return p;   // best-effort (the guard bound should always clear first)
    }

    static bool CollidesAny(IReadOnlyList<Vector2> tops, Vector2 p)
    {
        if (tops == null) return false;
        float t2 = CollisionThreshold * CollisionThreshold;
        for (int i = 0; i < tops.Count; i++)
            if ((p - tops[i]).sqrMagnitude < t2) return true;
        return false;
    }
}
