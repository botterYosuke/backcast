// HakoniwaPerspectiveStageProbe.cs — issue #93 "Hakoniwa perspective stage" / probe C
// (構図検証 AFK gate, findings 0068 §7). Headless, Camera/RT-FREE: asserts the COMPOSITION
// INVARIANTS of HakoniwaStageMath (不変条件/レンジ; 絶対ピクセル値は assert しない).
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod HakoniwaPerspectiveStageProbe.Run -logFile <log>
//   # expect (GREEN): [HAKONIWA PERSPECTIVE STAGE PASS] ... / exit=0
//
// RED→GREEN (findings 0068 §10): with the RED STUB HakoniwaStageMath (front-parallel), ① and ②
// FAIL (a flat board has no 奥収束 and a zero-height wall) — that RED proves the gate discriminates
// flat from perspective. After the GREEN tilt+thickness lands, ①②③ all pass.
//
// THREE INVARIANT SECTIONS (findings 0068 §7), each returns null on pass or a reason string:
//   ① 奥収束: projected width of the FAR edge (sy=1) < projected width of the NEAR edge (sy=0).
//   ② 厚み可視: the front wall (土台側面, surface z=0 -> z=-thickness at the near edge) projects to
//      a NON-zero screen height.
//   ③ 再投影ラウンドトリップ: each slot center forward-projects then UnprojectToSlot returns the SAME
//      normalized point AND lands in the SAME HakoniwaGridMath slot.

using System;
using UnityEditor;
using UnityEngine;

public static class HakoniwaPerspectiveStageProbe
{
    const float RTW = 1000f, RTH = 640f;   // mirror the production HakoniwaStage RT (1000x640) so probe==production aspect (§15 F4)

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section1_DepthConvergence()
                ?? Section2_WallThicknessVisible()
                ?? Section3_ReprojectionRoundTrip()
                ?? Section4_BoardPointRouting();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA PERSPECTIVE STAGE PASS] 奥収束 + 厚み可視 + 再投影ラウンドトリップ + 入力ルーティング verified (headless composition invariants, findings 0068 §7/§14).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA PERSPECTIVE STAGE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ① 奥収束: far edge (sy=1) narrower than near edge (sy=0). Require a MEANINGFUL convergence
    // (>=5%), not mere inequality, so a near-flat projection cannot pass vacuously.
    static string Section1_DepthConvergence()
    {
        var p = HakoniwaStageMath.StageParams.Default(RTW, RTH);
        float widthFar  = Mathf.Abs(HakoniwaStageMath.ProjectSlotPoint(1f, 1f, p).x
                                  - HakoniwaStageMath.ProjectSlotPoint(0f, 1f, p).x);
        float widthNear = Mathf.Abs(HakoniwaStageMath.ProjectSlotPoint(1f, 0f, p).x
                                  - HakoniwaStageMath.ProjectSlotPoint(0f, 0f, p).x);
        if (widthNear <= 0f) return "①: near-edge projected width is zero (degenerate projection)";
        if (!(widthFar < widthNear * 0.95f))
            return $"①奥収束: far-edge width {widthFar:F2} is not < 95% of near-edge width {widthNear:F2} " +
                   "(board is front-parallel / not converging — perspective tilt absent)";
        return null;
    }

    // ② 厚み可視: the near-edge front wall (surface z=0 -> z=-thickness) has non-zero screen height.
    static string Section2_WallThicknessVisible()
    {
        var p = HakoniwaStageMath.StageParams.Default(RTW, RTH);
        Vector3 surface = HakoniwaStageMath.SlotToBoardLocal(0.5f, 0f, p);      // near-edge mid, z=0
        Vector3 wallBottom = surface + new Vector3(0f, 0f, -p.thickness);        // extruded into the 土台
        float ys = HakoniwaStageMath.ProjectBoardLocal(surface, p).y;
        float yw = HakoniwaStageMath.ProjectBoardLocal(wallBottom, p).y;
        float wallHeight = Mathf.Abs(ys - yw);
        if (!(wallHeight > 1f))
            return $"②厚み可視: front-wall projected height {wallHeight:F3}px is not > 1px " +
                   "(thickness not extruded / edge-on — 土台側面 invisible)";
        return null;
    }

    // ③ 再投影ラウンドトリップ: forward-project each slot center, UnprojectToSlot back, assert it
    // returns the same normalized point AND the same HakoniwaGridMath slot. n=4 (2x2) grid.
    static string Section3_ReprojectionRoundTrip()
    {
        const int N = 4;
        const float EPS = 1e-3f;
        var p = HakoniwaStageMath.StageParams.Default(RTW, RTH);
        var cells = HakoniwaGridMath.CellRects(N);
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            float sx = (c.minX + c.maxX) * 0.5f;
            float sy = (c.minY + c.maxY) * 0.5f;
            Vector2 px = HakoniwaStageMath.ProjectSlotPoint(sx, sy, p);
            Vector2 back = HakoniwaStageMath.UnprojectToSlot(px, p);
            if (Mathf.Abs(back.x - sx) > EPS || Mathf.Abs(back.y - sy) > EPS)
                return $"③ラウンドトリップ: slot {i} center ({sx:F3},{sy:F3}) round-tripped to ({back.x:F3},{back.y:F3}) — inverse projection inconsistent";
            int slot = HakoniwaGridMath.SlotAt(cells, back);
            if (slot != i)
                return $"③ラウンドトリップ: slot {i} center round-tripped into slot {slot} (cell mismatch)";
        }
        return null;
    }

    // ④ 入力ルーティング (math-pick, findings 0068 §13-14): board-normalized point -> (slot, inHeader).
    // (a) header band (top HEADER_FRAC of a cell) -> (slot=S, inHeader=true) = swap-drag.
    // (b) body -> (slot=S, inHeader=false) = pan fall-through.
    // (c) off-board/gap -> (slot=-1, inHeader=false) = pan. Pure routing on the UnprojectToSlot output
    // space (the projection round-trip itself is ③/Section3), so production==gate (§12).
    static string Section4_BoardPointRouting()
    {
        const int N = 4;
        const float HEADER_FRAC = 0.2f;
        var cells = HakoniwaGridMath.CellRects(N);
        if (cells.Count != N)
            return $"④ルーティング: CellRects({N}) returned {cells.Count} cells (grid build broken — (a)/(b)/(c) asserts would be vacuous)";

        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            float cx = (c.minX + c.maxX) * 0.5f;
            float h = c.maxY - c.minY;

            // (a) point inside the top HEADER_FRAC band of cell i -> swap (slot=i, inHeader=true).
            Vector2 headerPt = new Vector2(cx, c.maxY - HEADER_FRAC * h * 0.5f);
            int hs = HakoniwaGridMath.RouteBoardPoint(cells, headerPt, HEADER_FRAC, out bool hIn);
            if (hs != i || !hIn)
                return $"④(a)header: slot {i} header point ({headerPt.x:F3},{headerPt.y:F3}) routed to (slot={hs}, inHeader={hIn}); expected (slot={i}, inHeader=true) — header band → swap broken";

            // (b) point well below the header band -> pan (slot=i, inHeader=false).
            Vector2 bodyPt = new Vector2(cx, c.minY + 0.25f * h);
            int bs = HakoniwaGridMath.RouteBoardPoint(cells, bodyPt, HEADER_FRAC, out bool bIn);
            if (bs != i || bIn)
                return $"④(b)body: slot {i} body point ({bodyPt.x:F3},{bodyPt.y:F3}) routed to (slot={bs}, inHeader={bIn}); expected (slot={i}, inHeader=false) — body → pan broken";
        }

        // (c) off-board / gap -> (slot=-1, inHeader=false) = pan fall-through.
        Vector2[] offBoard = { new Vector2(1.5f, 0.5f), new Vector2(-0.1f, 0.5f), new Vector2(0.5f, 1.5f) };
        foreach (var pt in offBoard)
        {
            int os = HakoniwaGridMath.RouteBoardPoint(cells, pt, HEADER_FRAC, out bool oIn);
            if (os != -1 || oIn)
                return $"④(c)off-board: point ({pt.x:F3},{pt.y:F3}) routed to (slot={os}, inHeader={oIn}); expected (slot=-1, inHeader=false) — off-board → pan broken";
        }
        return null;
    }
}
