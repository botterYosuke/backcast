// HakoniwaStageInputProbe.cs — issue #93 R3 (findings 0068 §17/§20) — 探索 Probe (batchmode).
// AFK gate for the perspective-stage live-input seam HakoniwaStageInputSurface. Synthetic
// PointerEventData is injected straight into the drag handlers (EventSystem dispatch bypassed —
// RAYCAST-DEAD, §11); real screen-press routing is HITL (HAKONIWA-11b). Later promoted to
// HakoniwaInputRoutingE2ERunner (台帳 HAKONIWA-15/16).
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//           -executeMethod HakoniwaStageInputProbe.Run -logFile <log>
//   GREEN: [HAKONIWA STAGE INPUT PASS] ... / exit=0
//
// SECTIONS (findings 0068 §20). Section ④ (screen→RT pixel conversion) runs FIRST so a harness
// coordinate bug is caught before the stub-dependent header/swap sections (else a harness bug
// would masquerade as the intended RED):
//   ① header-band press -> source slot captured (ActiveDragSourceSlot).
//   ② drag END over another cell -> Swap fires (_order swapped).
//   ③ body press -> pan spy received, Swap NOT fired.
//   ④ screen -> RawImage-local -> RT pixel conversion is correct (identity rect, deterministic).
//   ⑤ runtime-spawned chart:<id> tile root layer == _hakoniwaRoot.layer + Stage canvas on the
//      Hakoniwa layer + no nested Canvas in the tile subtree (§18 CANVAS-CULL premise guard).

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

public static class HakoniwaStageInputProbe
{
    const float RTW = 1000f, RTH = 640f;   // mirror the production HakoniwaStage RT (1000x640)
    const float HEADER_FRAC = 0.2f;        // board-normalized header band (matches production HAKO_HEADER_FRAC)
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section4_Conversion()
                ?? Section1_HeaderCapturesSource()
                ?? Section2_DragEndSwaps()
                ?? Section3_BodyPansNoSwap()
                ?? Section5_RuntimeTileLayer();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA STAGE INPUT PASS] header→swap / body→pan / screen→RT pixel / runtime tile layer verified (findings 0068 §17/§20).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA STAGE INPUT FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ④ screen->RawImage-local->RT pixel: for the identity rect (pivot bottom-left at origin, size
    // RTW×RTH) a screen pixel equals the RT pixel. Deterministic, no Camera (findings 0068 §20).
    static string Section4_Conversion()
    {
        var h = BuildHarness();
        try
        {
            Vector2 probe = new Vector2(123.5f, 456.5f);
            Vector2 rt = h.surface.ScreenToRtPixel(new PointerEventData(EventSystem.current) { position = probe });
            if (Vector2.Distance(rt, probe) > 0.5f)
                return $"④変換: ScreenToRtPixel(screen {probe}) = {rt}; for the identity rect a screen pixel must equal the RT pixel (screen→RawImage-local→RT pixel mapping wrong)";
            return null;
        }
        finally { h.Teardown(); }
    }

    // ① header-band press -> the surface captures the pressed slot as the swap-drag source.
    static string Section1_HeaderCapturesSource()
    {
        var h = BuildHarness();
        try
        {
            const int S = 0;
            var (sx, sy) = HeaderPoint(h.cells, S);
            h.surface.OnBeginDrag(PressAtBoard(sx, sy, h.stage));
            if (h.surface.ActiveDragSourceSlot != S)
                return $"①header: header-band press at slot {S} (board {sx:F3},{sy:F3}) captured ActiveDragSourceSlot={h.surface.ActiveDragSourceSlot}, expected {S} — header→swap-drag not started";
            return null;
        }
        finally { h.Teardown(); }
    }

    // ② drag from slot S header, END over slot T center -> Swap(S,T) fires (_order swapped).
    static string Section2_DragEndSwaps()
    {
        var h = BuildHarness();
        try
        {
            const int S = 0, T = 3;
            var (hsx, hsy) = HeaderPoint(h.cells, S);
            h.surface.OnBeginDrag(PressAtBoard(hsx, hsy, h.stage));
            var (tsx, tsy) = CenterPoint(h.cells, T);
            h.surface.OnEndDrag(PressAtBoard(tsx, tsy, h.stage));
            if (h.controller.Order[S] != "t3" || h.controller.Order[T] != "t0")
                return $"②swap: drag from slot {S} header to slot {T} did not swap _order (now [{string.Join(",", h.controller.Order)}]) — header drag→Swap not wired";
            return null;
        }
        finally { h.Teardown(); }
    }

    // ③ body press -> pan spy receives the gesture, no swap-drag source captured, _order unchanged.
    static string Section3_BodyPansNoSwap()
    {
        var h = BuildHarness();
        try
        {
            const int S = 1;
            var (bsx, bsy) = BodyPoint(h.cells, S);
            var press = PressAtBoard(bsx, bsy, h.stage);
            h.surface.OnBeginDrag(press);
            if (h.surface.ActiveDragSourceSlot != -1)
                return $"③body: body press at slot {S} captured a swap-drag source ({h.surface.ActiveDragSourceSlot}); expected -1 (body→pan)";
            if (h.panBegin != 1)
                return $"③body: body press did not reach the pan spy (panBegin={h.panBegin}) — body→pan fall-through broken";
            h.surface.OnDrag(press);
            if (h.panDrag != 1)
                return $"③body: body drag did not forward to the pan spy (panDrag={h.panDrag})";
            string before = string.Join(",", h.controller.Order);
            h.surface.OnEndDrag(press);
            if (string.Join(",", h.controller.Order) != before)
                return "③body: body drag mutated _order (Swap fired on a body drag — must be pan-only)";
            return null;
        }
        finally { h.Teardown(); }
    }

    // ⑤ runtime layer propagation (findings 0068 §18/§19): spawn a chart tile through the real root
    // and assert its root layer == _hakoniwaRoot.layer, the Stage canvas is on the Hakoniwa layer,
    // and the tile subtree has NO nested Canvas (a nested Canvas is a separate cull root, §18).
    static string Section5_RuntimeTileLayer()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "⑤layer: BackcastWorkspaceRoot not found in scene";
        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hakoRoot = ty.GetField("_hakoniwaRoot", BF).GetValue(root) as RectTransform;
        var chartTiles = ty.GetField("_chartTiles", BF).GetValue(root) as IDictionary<string, RectTransform>;
        if (scenario == null || hakoRoot == null || chartTiles == null)
            return "⑤layer: reflection handles (_scenario/_hakoniwaRoot/_chartTiles) not resolved";

        scenario.Universe.ReplaceAll(new[] { "AAA.TSE" });   // spawns chart:AAA.TSE via SyncChartTilesToUniverse
        if (!chartTiles.TryGetValue("AAA.TSE", out var tile) || tile == null)
            return "⑤layer: chart tile for AAA.TSE was not spawned (membership sync broken)";

        int hakoLayer = hakoRoot.gameObject.layer;
        if (tile.gameObject.layer != hakoLayer)
            return $"⑤layer: runtime chart tile layer {tile.gameObject.layer} != _hakoniwaRoot layer {hakoLayer} (step-3 runtime layer propagation missing — findings 0068 §18/§19)";

        int hakoNamed = LayerMask.NameToLayer("Hakoniwa");
        var stageCanvas = hakoRoot.GetComponentInParent<Canvas>();
        if (stageCanvas == null || stageCanvas.gameObject.layer != hakoNamed)
            return $"⑤canvas: Stage canvas layer != 'Hakoniwa' ({hakoNamed}) — CANVAS-CULL premise broken (§18)";
        var nested = tile.GetComponentsInChildren<Canvas>(true);
        if (nested.Length != 0)
            return $"⑤canvas: runtime tile has {nested.Length} nested Canvas component(s) — a nested Canvas is a separate cull root and breaks CANVAS-CULL (§18)";
        return null;
    }

    // ---- harness ----
    class Harness
    {
        public HakoniwaStageInputSurface surface;
        public HakoniwaController controller;
        public List<LayoutRect> cells;
        public HakoniwaStageMath.StageParams stage;
        public int panBegin, panDrag;
        public GameObject riGo, rootGo;
        public void Teardown()
        {
            if (riGo != null) UnityEngine.Object.DestroyImmediate(riGo);
            if (rootGo != null) UnityEngine.Object.DestroyImmediate(rootGo);
        }
    }

    static Harness BuildHarness()
    {
        const int N = 4;
        var h = new Harness();
        h.stage = HakoniwaStageMath.StageParams.Default(RTW, RTH);
        h.cells = HakoniwaGridMath.CellRects(N);

        h.rootGo = new GameObject("HakoRoot", typeof(RectTransform));
        var root = (RectTransform)h.rootGo.transform;
        var dict = new Dictionary<string, RectTransform>();
        var order = new List<string>();
        for (int i = 0; i < N; i++)
        {
            var tgo = new GameObject("t" + i, typeof(RectTransform));
            var trt = (RectTransform)tgo.transform;
            trt.SetParent(root, false);
            dict["t" + i] = trt;
            order.Add("t" + i);
        }
        h.controller = new HakoniwaController(root, dict, order);

        // identity RawImage rect: parentless, pivot bottom-left at world origin, size RTW×RTH -> a
        // screen pixel maps 1:1 onto an RT pixel (deterministic, no Camera/Canvas needed).
        h.riGo = new GameObject("StageRawImage", typeof(RectTransform), typeof(HakoniwaStageInputSurface));
        var riRt = (RectTransform)h.riGo.transform;
        riRt.anchorMin = riRt.anchorMax = Vector2.zero;
        riRt.pivot = Vector2.zero;
        riRt.sizeDelta = new Vector2(RTW, RTH);
        riRt.position = Vector3.zero;

        h.surface = h.riGo.GetComponent<HakoniwaStageInputSurface>();
        h.surface.Initialize(h.controller, riRt, h.stage, HEADER_FRAC,
            _ => h.panBegin++, _ => h.panDrag++);
        return h;
    }

    // A synthetic press whose screen position equals the RT pixel the board point projects to (the
    // harness rect is identity, so the surface recovers the same RT pixel -> UnprojectToSlot -> board).
    static PointerEventData PressAtBoard(float sx, float sy, HakoniwaStageMath.StageParams p)
    {
        Vector2 rtPixel = HakoniwaStageMath.ProjectSlotPoint(sx, sy, p);
        return new PointerEventData(EventSystem.current) { position = rtPixel };
    }

    static (float, float) CenterPoint(List<LayoutRect> cells, int i)
    {
        var c = cells[i];
        return ((c.minX + c.maxX) * 0.5f, (c.minY + c.maxY) * 0.5f);
    }

    static (float, float) HeaderPoint(List<LayoutRect> cells, int i)
    {
        var c = cells[i];
        float cx = (c.minX + c.maxX) * 0.5f;
        float ch = c.maxY - c.minY;
        return (cx, c.maxY - HEADER_FRAC * ch * 0.5f);   // inside the top header band
    }

    static (float, float) BodyPoint(List<LayoutRect> cells, int i)
    {
        var c = cells[i];
        float cx = (c.minX + c.maxX) * 0.5f;
        float ch = c.maxY - c.minY;
        return (cx, c.minY + 0.25f * ch);   // well below the header band
    }
}
