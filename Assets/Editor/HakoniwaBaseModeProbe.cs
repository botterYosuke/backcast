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
using UnityEngine.UI;

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
                ?? Section4_RestoreAppliesPerModeProfile()
                ?? Section5_HonestReplayEmptyState();
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

    // ── 4. restore applies the per-mode profile: collision/legacy → canonical, valid → honored ──
    // #62 (findings 0029 §3) generalizes #61's ReassertBaseAfterRestore (always-canonical) into
    // ApplyProfileOrder (validated honor / canonical). This locks BOTH halves on the REAL root:
    //   - the #61 collision regression: LayoutDocument.Default() / #60-era sidecars / stale visible=false
    //     have a base id set that does NOT match the mode → invalid → canonical base order + base visible
    //     (the HIGH review fix stays GREEN);
    //   - the #62 new capability: a VALID Replay profile with a user-swapped base order is HONORED.
    // We drive ApplyProfileOrder by seeding _profiles (the restore path), exactly as ApplyLayout does.
    static string Section4_RestoreAppliesPerModeProfile()
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
        var profilesField = ty.GetField("_profiles", BF);
        var apply = ty.GetMethod("ApplyProfileOrder", BF);
        if (scenario == null || hako == null || baseTiles == null || profilesField == null || apply == null)
            return "restore: root internals not found (renamed?)";

        scenario.Universe.ReplaceAll(new[] { "AAA.TSE", "BBB.TSE" });   // Replay shape: 5 base + 2 chart

        // Seed _profiles from a legacy single-panels doc and apply the Replay (false) profile.
        System.Action<LayoutDocument> seedAndApply = doc =>
        {
            var profiles = new HakoniwaLayoutProfiles();
            profiles.SeedFromLegacy(doc.panels);
            profilesField.SetValue(root, profiles);
            apply.Invoke(root, new object[] { false });
        };

        // (a) the REAL collision source: LayoutDocument.Default() has orders/positions/run_result at
        // legacy slots and no startup/buying_power → base set mismatch → invalid → canonical.
        seedAndApply(LayoutDocument.Default());
        string err = AssertCanonicalReplayBase(hako); if (err != null) return "restore(Default collision): " + err;

        // (b) a #60-era sidecar [startup, chart:AAA, chart:BBB] → base set {startup} ≠ Kinds(Replay) →
        // invalid → canonical (the 4 base panels are pulled back in front of the charts).
        seedAndApply(new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new System.Collections.Generic.List<PanelLayout>
            {
                new PanelLayout("startup", 0, true, new LayoutRect(0, 0, 1, 1)),
                new PanelLayout("chart:AAA.TSE", 1, true, new LayoutRect(0, 0, 1, 1)),
                new PanelLayout("chart:BBB.TSE", 2, true, new LayoutRect(0, 0, 1, 1)),
            },
        });
        err = AssertCanonicalReplayBase(hako); if (err != null) return "restore(#60-era sidecar): " + err;

        // (c) visibility: a stale visible=false on a colliding base id must NOT leave a base panel hidden.
        seedAndApply(new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new System.Collections.Generic.List<PanelLayout>
            {
                new PanelLayout("orders", 0, false, new LayoutRect(0, 0, 1, 1)),     // hide a base panel
                new PanelLayout("positions", 1, false, new LayoutRect(0, 0, 1, 1)),
            },
        });
        foreach (var id in new[] { "orders", "positions", "buying_power", "run_result", "startup" })
            if (baseTiles.TryGetValue(id, out var rt) && rt != null && !rt.gameObject.activeSelf)
                return "restore(visibility): base tile left hidden after restore: " + id;

        // (d) #62 NEW: a VALID Replay profile with a user-swapped base order (orders before buying_power)
        // is HONORED — the base set matches Kinds(Replay) so is_valid_for passes. Charts stay after base.
        var valid = new HakoniwaLayoutProfiles();
        valid.Set(false, new System.Collections.Generic.List<PanelLayout>
        {
            new PanelLayout("startup", 0, true, new LayoutRect(0, 0, 1, 1)),
            new PanelLayout("orders", 1, true, new LayoutRect(0, 0, 1, 1)),         // swapped ahead of buying_power
            new PanelLayout("buying_power", 2, true, new LayoutRect(0, 0, 1, 1)),
            new PanelLayout("positions", 3, true, new LayoutRect(0, 0, 1, 1)),
            new PanelLayout("run_result", 4, true, new LayoutRect(0, 0, 1, 1)),
            new PanelLayout("chart:AAA.TSE", 5, true, new LayoutRect(0, 0, 1, 1)),
            new PanelLayout("chart:BBB.TSE", 6, true, new LayoutRect(0, 0, 1, 1)),
        });
        profilesField.SetValue(root, valid);
        apply.Invoke(root, new object[] { false });
        var expHonor = new[] { "startup", "orders", "buying_power", "positions", "run_result" };
        for (int i = 0; i < expHonor.Length; i++)
            if (hako.SlotOf(expHonor[i]) != i)
                return "restore(valid honor): swapped base order not honored — " + expHonor[i] + " expected slot " + i + ", got " + hako.SlotOf(expHonor[i]);
        string orderErr = AssertBaseBeforeChart(hako); if (orderErr != null) return "restore(valid honor): " + orderErr;
        return null;
    }

    // ── 5. honest Replay empty-state (regression for the code-review HIGH fix, commit cdc09d4) ──
    // _host.Panel (LivePanelViewModel) is MONOTONIC — never cleared. Inject a live AccountEvent, render
    // the base panels in Live (they show the figure), then flip _baseLive=false and re-render: every base
    // panel MUST read "(no data — Replay)" — NOT the stale live figure. This is the exact desync the fix
    // closes, and the part of HITL checkpoint #4 we can take off the owner's plate (findings 0028 §3/§11).
    static string Section5_HonestReplayEmptyState()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "empty-state: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        var baseLiveField = ty.GetField("_baseLive", BF);
        var push = ty.GetMethod("PushLiveTiles", BF);
        var bpView = ty.GetField("_buyingPowerView", BF)?.GetValue(root);
        if (host == null || baseLiveField == null || push == null || bpView == null)
            return "empty-state: root internals not found (renamed?)";

        // inject a live account snapshot with a recognizable buying_power.
        host.Panel.Apply("{\"AccountEvent\":{\"cash\":777.0,\"buying_power\":12345.0,\"ts_ms\":1}}");
        if (!host.Panel.HasAccount) return "empty-state: AccountEvent not applied to the VM";

        // Live shape → the panel renders the live figure (and is NOT the empty sentinel).
        baseLiveField.SetValue(root, true);
        push.Invoke(root, null);
        string liveText = ViewText(bpView);
        if (liveText == LivePanelTileView.ReplayEmpty) return "empty-state(Live): BuyingPower shows the empty sentinel instead of live data";
        if (liveText == null || !liveText.Contains("12345")) return "empty-state(Live): BuyingPower did not render the injected buying_power (got: " + liveText + ")";

        // Replay shape → EVERY base panel must drop the stale live figure for the empty sentinel.
        baseLiveField.SetValue(root, false);
        push.Invoke(root, null);
        foreach (var fn in new[] { "_buyingPowerView", "_ordersView", "_positionsView", "_runResultView" })
        {
            var v = ty.GetField(fn, BF)?.GetValue(root);
            if (v == null) continue;   // _ordersView etc. are #23 scene tiles; absent only if scene-unwired
            string t = ViewText(v);
            if (t != LivePanelTileView.ReplayEmpty)
                return "empty-state(Replay): " + fn + " shows '" + t + "' — stale live data leaked into Replay (cdc09d4 regressed)";
        }
        return null;
    }

    // read the rendered Text of a LivePanelTileView via its private _content field.
    static string ViewText(object view)
    {
        if (view == null) return null;
        var content = view.GetType().GetField("_content", BF)?.GetValue(view) as Text;
        return content != null ? content.text : null;
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
