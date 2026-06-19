// HakoniwaStageMath.cs — issue #93 "Hakoniwa perspective stage" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless float arithmetic for the 斜め俯瞰ジオラマ (perspective stage)
// composition — probe C (HakoniwaPerspectiveStageProbe) asserts THIS, with no playmode / no
// RenderTexture / no Camera, exactly as #14's HakoniwaGridMath and #13's CanvasViewMath are the
// authoritative math. NOTHING here touches a RectTransform, a render, a RenderTexture, or input;
// the runtime Hakoniwa Stage (World-Space Canvas + perspective camera + RT, findings 0068 §4) is
// the thin Unity boundary that REPRODUCES this projection via Camera.WorldToScreenPoint.
//
// MODEL (findings 0068 §4, owner-locked): the board occupies slot-normalized [0,1]^2 (Y up,
// HakoniwaGridMath convention). It is centered in stage-local world as a boardW×boardH quad in its
// own XY plane (surface normal +Z), TILTED by R = Euler(pitch, yaw, 0) so its top edge (sy=1)
// recedes (decision §3: the BOARD tilts, not the camera/floating). A perspective camera at
// (0,0,-camDistance) looking +Z (FOV fovDeg) views it; the 土台 has `thickness` extruded along the
// board's local -Z (the front wall / 土台側面).
//
// GREEN (this commit): ProjectBoardLocal applies R = Euler(pitch, yaw, 0) to the board-local point
// (tilting the surface so its far edge recedes and the -Z thickness extrusion gains screen height),
// then a perspective camera at (0,0,-camDistance) looking +Z projects it. UnprojectToSlot casts the
// pixel's camera ray and intersects the tilted board-surface plane (through world origin, normal
// R*+Z), inverse-rotating the hit back to board-local — the exact inverse for surface (z=0) points.

using UnityEngine;

public static class HakoniwaStageMath
{
    public struct StageParams
    {
        public float pitchDeg, yawDeg, fovDeg, camDistance, thickness, boardW, boardH, rtW, rtH;

        // Spike defaults (findings 0068 §4): pitch 40 / yaw 15 / FOV 35. boardW/H, camDistance,
        // thickness are spike-tunable framing constants (HITL refines the yaw/feel later).
        public static StageParams Default(float rtW, float rtH) => new StageParams
        {
            pitchDeg = 40f, yawDeg = 15f, fovDeg = 35f,
            camDistance = 18f, thickness = 1.0f,
            boardW = 10f, boardH = 6.4f,   // board aspect mirrors the established 1000x640 hakoniwa box (§15 F4): scene world board = 10 x 6.4
            rtW = rtW, rtH = rtH,
        };
    }

    // slot-normalized (sx,sy) in [0,1]^2 (Y up) -> the board's local-space surface point.
    public static Vector3 SlotToBoardLocal(float sx, float sy, StageParams p)
        => new Vector3((sx - 0.5f) * p.boardW, (sy - 0.5f) * p.boardH, 0f);

    // Project a board-LOCAL point (z<0 = into the 土台 / behind the surface) to RT-pixel space
    // (x right, y up, origin bottom-left). GREEN: R=Euler(pitch,yaw,0) tilts the board point into
    // stage-world, then a perspective camera at (0,0,-camDistance) looking +Z projects it.
    public static Vector2 ProjectBoardLocal(Vector3 boardLocal, StageParams p)
    {
        Vector3 world = Quaternion.Euler(p.pitchDeg, p.yawDeg, 0f) * boardLocal;
        float cz = world.z + p.camDistance;        // camera at (0,0,-camDistance) -> depth = world.z + camDistance
        float f = 1f / Mathf.Tan(p.fovDeg * 0.5f * Mathf.Deg2Rad);
        float aspect = p.rtW / p.rtH;
        float ndcX = (f / aspect) * world.x / cz;
        float ndcY = f * world.y / cz;
        return new Vector2((ndcX * 0.5f + 0.5f) * p.rtW, (ndcY * 0.5f + 0.5f) * p.rtH);
    }

    public static Vector2 ProjectSlotPoint(float sx, float sy, StageParams p)
        => ProjectBoardLocal(SlotToBoardLocal(sx, sy, p), p);

    // Inverse: RT-pixel -> slot-normalized (sx,sy). GREEN: cast the pixel's camera ray and intersect
    // the tilted board-surface plane (passes through world origin, normal R*+Z), then inverse-rotate
    // the hit point back to board-local. Exact inverse of ProjectBoardLocal for surface (z=0) points.
    public static Vector2 UnprojectToSlot(Vector2 rtPixel, StageParams p)
    {
        float f = 1f / Mathf.Tan(p.fovDeg * 0.5f * Mathf.Deg2Rad);
        float aspect = p.rtW / p.rtH;
        float ndcX = rtPixel.x / p.rtW * 2f - 1f;
        float ndcY = rtPixel.y / p.rtH * 2f - 1f;
        Vector3 dir = new Vector3(ndcX * aspect / f, ndcY / f, 1f);   // camera ray dir (cam axes == world axes)
        Vector3 origin = new Vector3(0f, 0f, -p.camDistance);
        Quaternion R = Quaternion.Euler(p.pitchDeg, p.yawDeg, 0f);
        Vector3 n = R * Vector3.forward;                             // board-surface plane normal (plane thru world origin)
        float s = -Vector3.Dot(n, origin) / Vector3.Dot(n, dir);
        Vector3 hit = origin + s * dir;
        Vector3 boardLocal = Quaternion.Inverse(R) * hit;
        return new Vector2(boardLocal.x / p.boardW + 0.5f, boardLocal.y / p.boardH + 0.5f);
    }
}
