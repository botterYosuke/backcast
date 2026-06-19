// HakoniwaBaseModeProbe.cs — STANDING AFK regression gate for the #65 Replay empty-state / running-snapshot
// panel seam. The mode-conditional base-tile sections (tile kinds, base-only retile, live-shape no-op,
// per-mode profile restore) were promoted into HakoniwaE2ERunner (findings 0060); this probe retains ONLY
// the #65 account/RunResult panel empty-state check, which belongs to the panel category and awaits a
// future Panel-surface E2E runner (held here as a standing regression gate in the meantime). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod HakoniwaBaseModeProbe.Run -logFile <log>
//   # expect: [HAKONIWA BASE MODE PASS] ... / exit=0
//
// SECTION:
//   honest Replay empty-state (regression for the code-review HIGH fix, commit cdc09d4). #65 2-split
//   (findings 0044 §9). A keeps the cdc09d4 anti-stale-live regression lock; B/C add the #65 real-data
//   behaviour and drive through the per-frame RefreshLiveTiles path (NOT PushLiveTiles directly) so the
//   HIGH drive-loop fix is locked.

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class HakoniwaBaseModeProbe
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section5_HonestReplayEmptyState();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA BASE MODE PASS] honest Replay empty-state + #65 real-data render + RunResult running→full switch verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA BASE MODE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── honest Replay empty-state (regression for the code-review HIGH fix, commit cdc09d4) ──
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
}
