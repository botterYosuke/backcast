// HakoniwaBaseModeProbe.cs — issue #61 "mode-conditional base tiles" (headless AFK regression gate)
//
// The headless, Python-FREE gate for the #61 NEW seams (findings 0028 §7). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod HakoniwaBaseModeProbe.Run -logFile <log>
//   # expect: [HAKONIWA BASE MODE PASS] ... / exit=0
//
// SECTIONS:
//   1. mode tile kinds: HakoniwaBaseTiles.Kinds(live) matches TTWR hakoniwa_tile_kinds — Replay has
//      startup at index 0 + the 4 panels; Live drops startup; IsLiveShape folds the DisplayMode
//      string to the 2-valued shape (LiveManual/LiveAuto → same Live shape).
//   2. base-only retile (NON-tautological): on a real synthesized root with 2 chart tiles, driving
//      the shape Replay→Live→Replay retiles ONLY the base (startup leaves/returns; the 4 panels stay)
//      while the chart tiles keep their EXACT RectTransform identity and are re-placed after the base;
//      order stays [base…, chart…] and the box-grow tracks n_base+n_chart.
//   3. LiveManual⇄LiveAuto is a no-op: both fold to the Live shape, so a second Live retile does not
//      despawn/respawn anything (identity preserved).

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HakoniwaBaseModeProbe
{
    const float EPS = 1e-3f;
    static readonly Vector2 MIN_TILE = new Vector2(280f, 180f);
    static readonly Vector2 DEFAULT_BOX = new Vector2(700f, 450f);
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section1_ModeTileKinds()
                ?? Section2_BaseOnlyRetilePreservesChartIdentity()
                ?? Section3_LiveManualAutoNoOp()
                ?? Section4_RestoreReassertsBaseOrderAndVisibility();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA BASE MODE PASS] mode tile kinds + base-only retile + chart identity + live-shape no-op verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA BASE MODE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. mode tile kinds (HakoniwaBaseTiles.Kinds == TTWR hakoniwa_tile_kinds) ──
    static string Section1_ModeTileKinds()
    {
        var replay = HakoniwaBaseTiles.Kinds(false);
        var live = HakoniwaBaseTiles.Kinds(true);

        var expReplay = new[] { "startup", "buying_power", "orders", "positions", "run_result" };
        var expLive = new[] { "buying_power", "orders", "positions", "run_result" };
        if (!SeqEqual(replay, expReplay)) return "kinds: Replay shape must be [startup, buying_power, orders, positions, run_result]";
        if (!SeqEqual(live, expLive)) return "kinds: Live shape must drop startup → [buying_power, orders, positions, run_result]";
        if (replay[0] != "startup") return "kinds: startup must be index 0 in Replay (ADR 0013)";

        if (HakoniwaBaseTiles.IsLiveShape("Replay")) return "shape: Replay must fold to !live";
        if (!HakoniwaBaseTiles.IsLiveShape("LiveManual")) return "shape: LiveManual must fold to live";
        if (!HakoniwaBaseTiles.IsLiveShape("LiveAuto")) return "shape: LiveAuto must fold to live";
        if (HakoniwaBaseTiles.IsLiveShape("garbage")) return "shape: unknown must default to Replay (!live)";

        if (!HakoniwaBaseTiles.IsChartId("chart:7203.TSE")) return "chartId: chart:<id> must be a chart tile";
        if (HakoniwaBaseTiles.IsChartId("startup")) return "chartId: startup must NOT be a chart tile";
        return null;
    }

    // ── 2. base-only retile preserves chart identity (the load-bearing, non-tautological check) ──
    static string Section2_BaseOnlyRetilePreservesChartIdentity()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "retile: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hako = ty.GetField("_hako", BF).GetValue(root) as HakoniwaController;
        var chartTiles = ty.GetField("_chartTiles", BF).GetValue(root) as IDictionary<string, RectTransform>;
        var baseTiles = ty.GetField("_baseTiles", BF).GetValue(root) as IDictionary<string, RectTransform>;
        var hakoRoot = ty.GetField("_hakoniwaRoot", BF).GetValue(root) as RectTransform;
        var sync = ty.GetMethod("SyncBaseTilesToMode", BF);
        if (scenario == null || hako == null || chartTiles == null || baseTiles == null || hakoRoot == null || sync == null)
            return "retile: root internals not found (renamed?)";

        // build defaults to the Replay shape: all 5 base tiles tracked in _baseTiles + present in the
        // controller, startup at front. orders/positions/run_result are the #23 scene tiles (LivePanelTileView)
        // and buying_power is the #61 dynamically-spawned LivePanelTileView.
        foreach (var id in HakoniwaBaseTiles.Kinds(false))
        {
            if (!baseTiles.ContainsKey(id)) return "retile: base tile not tracked in _baseTiles at build: " + id;
            if (hako.SlotOf(id) < 0) return "retile: base tile missing from the controller at build: " + id;
        }

        // 2 chart tiles; capture their EXACT RectTransform instances to prove identity across retile.
        scenario.Universe.ReplaceAll(new[] { "AAA.TSE", "BBB.TSE" });
        if (!chartTiles.TryGetValue("AAA.TSE", out RectTransform rtA) || rtA == null) return "retile: chart:AAA tile missing";
        if (!chartTiles.TryGetValue("BBB.TSE", out RectTransform rtB) || rtB == null) return "retile: chart:BBB tile missing";
        if (hako.Count != 7) return "retile: expected 5 base + 2 chart = 7, got " + hako.Count;
        string orderErr = AssertBaseBeforeChart(hako); if (orderErr != null) return "retile(Replay): " + orderErr;

        // → Live: base retiles (startup leaves), the 4 panels stay, charts keep identity + move to the back.
        sync.Invoke(root, new object[] { true });
        if (hako.SlotOf("startup") >= 0) return "retile→Live: startup must despawn (Live drops it, ADR 0013)";
        foreach (var id in HakoniwaBaseTiles.PanelOrder)
            if (hako.SlotOf(id) < 0) return "retile→Live: base panel must SURVIVE (present in both shapes): " + id;
        if (!ReferenceEquals(chartTiles["AAA.TSE"], rtA) || !ReferenceEquals(chartTiles["BBB.TSE"], rtB))
            return "retile→Live: chart tile RectTransform identity LOST (chart must not despawn — #169 ownership)";
        if (hako.SlotOf("chart:AAA.TSE") < 0 || hako.SlotOf("chart:BBB.TSE") < 0)
            return "retile→Live: chart tiles dropped from the controller order";
        if (hako.Count != 6) return "retile→Live: expected 4 base + 2 chart = 6, got " + hako.Count;
        orderErr = AssertBaseBeforeChart(hako); if (orderErr != null) return "retile(Live): " + orderErr;
        var expLive = HakoniwaGridMath.ComputeBoxSize(hako.Count, MIN_TILE, 0f, DEFAULT_BOX);
        if ((hakoRoot.sizeDelta - expLive).sqrMagnitude > EPS)
            return "retile→Live: box-grow not re-derived from n_total (got " + hakoRoot.sizeDelta + ", expected " + expLive + ")";

        // → Replay: startup returns at the front; charts STILL the same instances.
        sync.Invoke(root, new object[] { false });
        if (hako.SlotOf("startup") != 0) return "retile→Replay: startup must return at index 0";
        if (!ReferenceEquals(chartTiles["AAA.TSE"], rtA) || !ReferenceEquals(chartTiles["BBB.TSE"], rtB))
            return "retile→Replay: chart tile identity LOST on the second retile";
        if (hako.Count != 7) return "retile→Replay: expected 7 again, got " + hako.Count;
        orderErr = AssertBaseBeforeChart(hako); if (orderErr != null) return "retile(Replay-2): " + orderErr;
        return null;
    }

    // ── 3. LiveManual⇄LiveAuto is a no-op (same Live shape) ──
    static string Section3_LiveManualAutoNoOp()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "noop: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hako = ty.GetField("_hako", BF).GetValue(root) as HakoniwaController;
        var chartTiles = ty.GetField("_chartTiles", BF).GetValue(root) as IDictionary<string, RectTransform>;
        var sync = ty.GetMethod("SyncBaseTilesToMode", BF);
        if (scenario == null || hako == null || chartTiles == null || sync == null) return "noop: root internals not found";

        scenario.Universe.ReplaceAll(new[] { "AAA.TSE" });
        sync.Invoke(root, new object[] { true });   // enter Live (LiveManual)
        if (!chartTiles.TryGetValue("AAA.TSE", out RectTransform rtA) || rtA == null) return "noop: chart:AAA missing";
        int countAfterFirst = hako.Count;

        sync.Invoke(root, new object[] { true });   // LiveManual → LiveAuto: same shape, must be a no-op
        if (!ReferenceEquals(chartTiles["AAA.TSE"], rtA)) return "noop: LiveManual→LiveAuto must NOT touch chart identity";
        if (hako.Count != countAfterFirst) return "noop: LiveManual→LiveAuto must not change the tile count";
        if (hako.SlotOf("startup") >= 0) return "noop: startup must stay absent across the same Live shape";
        return null;
    }

    // ── 4. restore re-asserts canonical base order + visibility (collision/legacy-safe) ──
    // Regression for the review's HIGH finding: LayoutDocument.Default() (and pre-#61 / #60-era
    // sidecars) carry ids that collide with or omit the base panel ids, so _hako.Apply(doc) scrambles
    // the base region / sinks base tiles behind charts / hides a panel. ReassertBaseAfterRestore (run
    // inside ApplyLayout) must repair it. We reproduce the scramble with Apply, then call the re-assert.
    static string Section4_RestoreReassertsBaseOrderAndVisibility()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "restore: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hako = ty.GetField("_hako", BF).GetValue(root) as HakoniwaController;
        var baseTiles = ty.GetField("_baseTiles", BF).GetValue(root) as IDictionary<string, RectTransform>;
        var reassert = ty.GetMethod("ReassertBaseAfterRestore", BF);
        if (scenario == null || hako == null || baseTiles == null || reassert == null)
            return "restore: root internals not found (renamed?)";

        scenario.Universe.ReplaceAll(new[] { "AAA.TSE", "BBB.TSE" });   // Replay shape: 5 base + 2 chart

        // (a) the REAL collision source: LayoutDocument.Default() has orders/positions/run_result at
        // legacy slots and no startup/buying_power → Apply scrambles base order + drops startup off slot 0.
        hako.Apply(LayoutDocument.Default());
        reassert.Invoke(root, null);
        string err = AssertCanonicalReplayBase(hako); if (err != null) return "restore(Default collision): " + err;

        // (b) a #60-era sidecar [startup, chart:AAA, chart:BBB] → Apply sinks the 4 base panels BEHIND
        // the charts; re-assert must pull them back in front.
        var legacy = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new System.Collections.Generic.List<PanelLayout>
            {
                new PanelLayout("startup", 0, true, new LayoutRect(0, 0, 1, 1)),
                new PanelLayout("chart:AAA.TSE", 1, true, new LayoutRect(0, 0, 1, 1)),
                new PanelLayout("chart:BBB.TSE", 2, true, new LayoutRect(0, 0, 1, 1)),
            },
        };
        hako.Apply(legacy);
        reassert.Invoke(root, null);
        err = AssertCanonicalReplayBase(hako); if (err != null) return "restore(#60-era sidecar): " + err;

        // (c) visibility: a stale visible=false on a colliding base id must NOT leave a base panel hidden.
        var hidden = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new System.Collections.Generic.List<PanelLayout>
            {
                new PanelLayout("orders", 0, false, new LayoutRect(0, 0, 1, 1)),     // hide a base panel
                new PanelLayout("positions", 1, false, new LayoutRect(0, 0, 1, 1)),
            },
        };
        hako.Apply(hidden);
        reassert.Invoke(root, null);
        foreach (var id in new[] { "orders", "positions", "buying_power", "run_result", "startup" })
            if (baseTiles.TryGetValue(id, out var rt) && rt != null && !rt.gameObject.activeSelf)
                return "restore(visibility): base tile left hidden after restore: " + id;
        return null;
    }

    // assert [startup, buying_power, orders, positions, run_result, chart…] with startup at slot 0.
    static string AssertCanonicalReplayBase(HakoniwaController hako)
    {
        var exp = new[] { "startup", "buying_power", "orders", "positions", "run_result" };
        for (int i = 0; i < exp.Length; i++)
            if (hako.SlotOf(exp[i]) != i) return exp[i] + " not at canonical slot " + i + " (got " + hako.SlotOf(exp[i]) + ")";
        return AssertBaseBeforeChart(hako);
    }

    // base tiles (non-chart ids) must all precede every chart:<id> tile (the [base…, chart…] invariant).
    static string AssertBaseBeforeChart(HakoniwaController hako)
    {
        int lastBase = -1, firstChart = int.MaxValue;
        for (int i = 0; i < hako.Order.Count; i++)
        {
            if (HakoniwaBaseTiles.IsChartId(hako.Order[i])) firstChart = Math.Min(firstChart, i);
            else lastBase = Math.Max(lastBase, i);
        }
        if (firstChart != int.MaxValue && lastBase > firstChart)
            return "order invariant broken: a base tile sits after a chart tile";
        return null;
    }

    static bool SeqEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
