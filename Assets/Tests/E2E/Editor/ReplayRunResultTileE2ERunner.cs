// ReplayRunResultTileE2ERunner.cs — Surface E2E (Panel category) for the Replay RunResult tile
// (PushReplayTiles の running↔full-stats 分岐). 台本: ReplayRunResultTileE2ERunner.md.
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod ReplayRunResultTileE2ERunner.Run -logFile <log>
//   # expect: [REPLAY RUNRESULT TILE PASS] ... / exit=0
//
// なぜ存在するか:
//   #99 (commit 77e39c7) が Hakoniwa を floating-window 化した際、HakoniwaE2ERunner (836 行) を
//   run_result タイルの B2 (running→full-stats switch) カバレッジごと wholesale 削除し「将来 Panel
//   カテゴリ runner へ移送予定」のまま re-home されなかった。結果 PushReplayTiles の running↔full-stats
//   分岐は C# AFK で無カバー化し、その死角に #100 ① (bt.replay 2 連続実行で run2 走行中ずっと run1 の
//   full-stats が残る) が landed した。本 runner は削除された B2 を Panel surface として復活させ、
//   さらに #100 ① の「再実行で summary が空へ戻ったら running へ戻る」不変条件 (RRT-04) を追加する。
//
// 2 ゲート分割 (behavior-to-e2e: DATA 経路の C#↔Python 跨ぎ):
//   * DATA 半分 (#100 ① の根本原因 = engine.last_run_summary を run-begin で clear する source 挙動) は
//     Python e2e が正本: python/tests/test_notebook_replay_afk.py
//       ::test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale (findings 0077 §2)。
//   * RENDER 半分 = 「summary source が空なら running、非空なら full-stats を描く」= Unity でしか
//     証明できない。本 runner がそれ。両ゲートで #100 ① を C#/Python の両層から固定する。
//
// Python-FREE: WorkspaceEngineHost.TestPortfolioJsonOverride / TestRunSummaryJsonOverride で poll
// snapshot を直接注入し、_lanes / 実 backend を一切起こさない (削除された HakoniwaBaseModeProbe と同型)。
//
// RED litmus (delete-the-production-logic): BackcastWorkspaceRoot.PushReplayTiles の
//   `string.IsNullOrWhiteSpace(summaryJson) ? running : complete` 分岐を complete 固定に潰すと
//   RRT-02 と RRT-04 が RED。summary を sticky (最後の非空値を記憶) にする旧 #100 バグ形でも RRT-04 が RED。

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ReplayRunResultTileE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    // Self-consistent Replay portfolio snapshot: equity(155321) = cash(54321) + position MV(50×2002),
    // BUY 50 @2000 FILLED. Only needs to be non-empty + recognizable for the running-view render.
    const string PortfolioRun1 =
        "{\"buying_power\":54321.0,\"cash\":54321.0,\"equity\":155321.0," +
        "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":100.0}]," +
        "\"orders\":[{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}]," +
        "\"realized_pnl\":0.0,\"unrealized_pnl\":100.0}";

    // run2's running snapshot — a DIFFERENT portfolio string (one more fill) so the dual-payload gate
    // re-renders even though we also clear the summary. Mirrors a real 2nd run streaming fresh fills.
    const string PortfolioRun2Streaming =
        "{\"buying_power\":54000.0,\"cash\":54000.0,\"equity\":155000.0," +
        "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":80.0}]," +
        "\"orders\":[" +
        "{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}," +
        "{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":10.0,\"price\":2001.0,\"status\":\"FILLED\",\"ts_ms\":3}]," +
        "\"realized_pnl\":0.0,\"unrealized_pnl\":80.0}";

    const string SummaryRun1 =
        "{\"fills_count\":2,\"equity_points\":68,\"total_pnl\":-410010.0," +
        "\"max_drawdown\":1234.0,\"sharpe\":0.5,\"sortino\":0.7}";

    const string SummaryRun2 =
        "{\"fills_count\":3,\"equity_points\":70,\"total_pnl\":12345.0," +
        "\"max_drawdown\":222.0,\"sharpe\":1.25,\"sortino\":1.5}";

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = RunSections();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[REPLAY RUNRESULT TILE PASS] RRT-01 empty / RRT-02 running / RRT-03 full-stats / " +
                      "RRT-04 #100① rerun→running (not stale) / RRT-05 rerun→full-stats verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[REPLAY RUNRESULT TILE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static string RunSections()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot missing from scene";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        var liveShape = ty.GetField("_lastLiveShape", BF);                 // #99: replaced #61 _baseLive
        var refresh = ty.GetMethod("RefreshLiveTiles", BF);               // per-frame driver → PushReplayTiles in Replay
        var rrView = ty.GetField("_runResultView", BF)?.GetValue(root);
        var hostTy = typeof(WorkspaceEngineHost);
        var pfOverride = hostTy.GetField("TestPortfolioJsonOverride", BF);
        var smOverride = hostTy.GetField("TestRunSummaryJsonOverride", BF);
        if (host == null || liveShape == null || refresh == null || pfOverride == null || smOverride == null)
            return "root internals not found (renamed?) — host/_lastLiveShape/RefreshLiveTiles/overrides";
        if (rrView == null)
            return "run_result tile not wired by BuildWorkspace (_runResultView is null) — the whole surface is uncovered";

        // Replay shape: RefreshLiveTiles routes to PushReplayTiles and drives the 4 base tiles from the
        // get_portfolio_json / get_run_summary_json poll snapshots (here the test overrides).
        liveShape.SetValue(root, false);

        Action drive = () => refresh.Invoke(root, null);
        Action<string> pf = v => pfOverride.SetValue(host, v);
        Action<string> sm = v => smOverride.SetValue(host, v);

        try
        {
            // ── RRT-01: no portfolio committed → RunResult (and all 4 tiles) honest-empty. ──
            pf(null); sm(null); drive();
            string t = ViewText(rrView);
            if (t != LivePanelTileView.ReplayEmpty)
                return "RRT-01 empty: RunResult is '" + t + "', expected the honest-empty sentinel before any run";

            // ── RRT-02: run1 streaming (portfolio present, summary still empty) → RUNNING view. ──
            pf(PortfolioRun1); sm(null); drive();
            string running1 = ViewText(rrView);
            if (running1 == null || !running1.Contains("running"))
                return "RRT-02 running: portfolio-present + empty-summary did NOT render the running view (got: " + running1 + ")";
            if (running1.Contains("fills:"))
                return "RRT-02 running: running view leaked full-stats text 'fills:' (branch collapsed? got: " + running1 + ")";

            // ── RRT-03: run1 completes (summary published) → FULL-STATS view (DecodeRunResult bound). ──
            sm(SummaryRun1); drive();
            string full1 = ViewText(rrView);
            if (full1 == null || full1.Contains("running"))
                return "RRT-03 full-stats: summary inject did NOT switch off the running view (dual-gate regressed? got: " + full1 + ")";
            if (!full1.Contains("fills:2"))
                return "RRT-03 full-stats: missing fills:2 (got: " + full1 + ")";
            if (!full1.Contains("-410010"))
                return "RRT-03 full-stats: missing total_pnl -410010 (TotalPnl unbound / zero-filled; got: " + full1 + ")";

            // ── RRT-04 (#100 ① GATE): bt.replay pressed a 2nd time → run2 begins, summary is cleared at
            // run-begin (here override→"") WHILE the portfolio streams fresh fills. The RunResult tile MUST
            // return to the RUNNING view — NOT keep showing run1's stale full-stats. Pre-#100 this stuck on
            // run1's full-stats for the entire run2 (the bug). Render-layer half of findings 0077 D1. ──
            pf(PortfolioRun2Streaming); sm(""); drive();
            string rerunRunning = ViewText(rrView);
            if (rerunRunning == null || !rerunRunning.Contains("running"))
                return "RRT-04 #100①: a 2nd run with a cleared summary did NOT return to the running view — " +
                       "run1's stale full-stats persisted into run2 (the #100 bug; got: " + rerunRunning + ")";
            if (rerunRunning.Contains("fills:2") || rerunRunning.Contains("-410010"))
                return "RRT-04 #100①: run1's stale full-stats (fills:2 / -410010) leaked into run2's running view (got: " + rerunRunning + ")";

            // ── RRT-05: run2 completes (new summary) → full-stats again, with run2's numbers (cycle closes,
            // proving the running↔full-stats flip is repeatable, not a one-shot). ──
            sm(SummaryRun2); drive();
            string full2 = ViewText(rrView);
            if (full2 == null || full2.Contains("running"))
                return "RRT-05 rerun-complete: run2 summary did NOT switch back to full-stats (got: " + full2 + ")";
            if (!full2.Contains("fills:3") || !full2.Contains("12345"))
                return "RRT-05 rerun-complete: full-stats did not show run2's numbers fills:3 / 12345 (stale run1? got: " + full2 + ")";
        }
        finally
        {
            // Leave the probe seam clean for any later runner in the same Unity process.
            pf(null); sm(null);
        }
        return null;
    }

    // Read the rendered Text of a LivePanelTileView via its private _content field.
    static string ViewText(object view)
    {
        if (view == null) return null;
        var content = view.GetType().GetField("_content", BF)?.GetValue(view) as Text;
        return content != null ? content.text : null;
    }
}
