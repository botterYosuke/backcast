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
                ?? Section4_RestoreReassertsBaseOrderAndVisibility()
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

    // ── 5. honest Replay empty-state (regression for the code-review HIGH fix, commit cdc09d4) ──
    // #65 2-split (findings 0044 §9). Sub-case A keeps the cdc09d4 anti-stale-live regression lock;
    // B/C add the #65 real-data behaviour and drive through the per-frame RefreshLiveTiles path (NOT
    // PushLiveTiles directly) so the HIGH drive-loop fix is locked: in Replay the live AppliedCount
    // gate is frozen, so RefreshLiveTiles must bypass it and re-render from the get_portfolio_json
    // poll snapshot — otherwise the panels would freeze after the shape flip.
    //   A. no portfolio → every base panel is the empty sentinel, NOT the stale live figure (12345).
    //   B. inject a real Replay portfolio (54321) → panels render it, distinct from the stale live 12345.
    //   C. portfolio drops back to "" (honest-empty, fix #2) → panels return to the empty sentinel.
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
        var refresh = ty.GetMethod("RefreshLiveTiles", BF);
        var bpView = ty.GetField("_buyingPowerView", BF)?.GetValue(root);
        var pfOverride = typeof(WorkspaceEngineHost).GetField("TestPortfolioJsonOverride", BF);
        if (host == null || baseLiveField == null || push == null || refresh == null || bpView == null || pfOverride == null)
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

        // ── A. Replay shape, NO portfolio → EVERY base panel drops the stale live figure (cdc09d4). ──
        baseLiveField.SetValue(root, false);
        refresh.Invoke(root, null);   // per-frame driver: Replay bypasses the AppliedCount gate (HIGH fix)
        foreach (var fn in new[] { "_buyingPowerView", "_ordersView", "_positionsView", "_runResultView" })
        {
            var v = ty.GetField(fn, BF)?.GetValue(root);
            if (v == null) continue;   // _ordersView etc. are #23 scene tiles; absent only if scene-unwired
            string t = ViewText(v);
            if (t != LivePanelTileView.ReplayEmpty)
                return "empty-state(Replay/A): " + fn + " shows '" + t + "' — stale live data leaked into Replay (cdc09d4 regressed)";
        }

        // ── B. inject a real Replay portfolio → panels render it (and NOT the stale live 12345). ──
        // Self-consistent fixture: equity(155321) = cash(54321) + position market value(50×2002=100100),
        // so unrealized = (equity−cash) − cost(50×2000=100000) = 100. (Values only need to be non-zero
        // and recognizable for the binding/render asserts, but a coherent snapshot reads cleanly.)
        const string realPortfolio =
            "{\"buying_power\":54321.0,\"cash\":54321.0,\"equity\":155321.0," +
            "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":100.0}]," +
            "\"orders\":[{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}]," +
            "\"realized_pnl\":0.0,\"unrealized_pnl\":100.0}";
        pfOverride.SetValue(host, realPortfolio);
        refresh.Invoke(root, null);   // drive-loop fix: RefreshLiveTiles must re-render on the new poll payload
        string bpReplay = ViewText(bpView);
        if (bpReplay == LivePanelTileView.ReplayEmpty || bpReplay == null)
            return "real-data(Replay/B): BuyingPower stayed empty — RefreshLiveTiles did not drive PushReplayTiles (drive-loop regressed)";
        if (!bpReplay.Contains("54321"))
            return "real-data(Replay/B): BuyingPower did not render the injected replay buying_power (got: " + bpReplay + ")";
        if (bpReplay.Contains("12345"))
            return "real-data(Replay/B): BuyingPower still shows the stale LIVE figure 12345 (anti-stale-live regressed)";
        string ordersReplay = ViewText(ty.GetField("_ordersView", BF)?.GetValue(root));
        if (ordersReplay != null && !ordersReplay.Contains("FILLED"))
            return "real-data(Replay/B): Orders panel did not render the injected FILLED row (got: " + ordersReplay + ")";

        // ── B2. RunResult running→full switch: inject the union summary_json (with the portfolio still
        // set, unchanged) → RunResult must flip from the running view to the full-stats view. This locks
        // (1) DecodeRunResult.total_pnl binding and (2) the dual payload gate (a summary change must
        // re-render even when the portfolio string is unchanged). ──
        var smOverride = typeof(WorkspaceEngineHost).GetField("TestRunSummaryJsonOverride", BF);
        var rrView = ty.GetField("_runResultView", BF)?.GetValue(root);
        if (smOverride != null && rrView != null)
        {
            string rrRunning = ViewText(rrView);
            if (rrRunning == null || !rrRunning.Contains("running"))
                return "run-result(Replay/B2): pre-summary RunResult is not the running view (got: " + rrRunning + ")";
            smOverride.SetValue(host,
                "{\"fills_count\":2,\"equity_points\":68,\"total_pnl\":-410010.0," +
                "\"max_drawdown\":1234.0,\"sharpe\":0.5,\"sortino\":0.7}");
            refresh.Invoke(root, null);
            string rrFull = ViewText(rrView);
            if (rrFull == null || rrFull.Contains("running"))
                return "run-result(Replay/B2): RunResult did not switch to full-stats on summary inject (dual-gate regressed? got: " + rrFull + ")";
            if (!rrFull.Contains("fills:2"))
                return "run-result(Replay/B2): full-stats missing fills:2 (got: " + rrFull + ")";
            if (!rrFull.Contains("-410010"))
                return "run-result(Replay/B2): full-stats missing total_pnl -410010 (TotalPnl zero-fill / not bound; got: " + rrFull + ")";
            smOverride.SetValue(host, null);
        }

        // ── C. portfolio drops to "" (honest-empty, fix #2) → panels return to the empty sentinel. ──
        pfOverride.SetValue(host, "");
        refresh.Invoke(root, null);
        foreach (var fn in new[] { "_buyingPowerView", "_ordersView", "_positionsView", "_runResultView" })
        {
            var v = ty.GetField(fn, BF)?.GetValue(root);
            if (v == null) continue;
            string t = ViewText(v);
            if (t != LivePanelTileView.ReplayEmpty)
                return "honest-empty(Replay/C): " + fn + " shows '" + t + "' — empty portfolio did not fall back to (no data)";
        }
        pfOverride.SetValue(host, null);   // leave the seam clean for any later section
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
