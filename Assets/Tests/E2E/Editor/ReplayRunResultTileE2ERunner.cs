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
    const float EPS = 0.01f;   // geometry equality tolerance (anchoredPosition / sizeDelta)

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
            if (fail == null) fail = RunModeVisibilitySections();   // #138 second slice (findings 0110 §7)
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[REPLAY RUNRESULT TILE PASS] RRT-01 empty / RRT-02 running / RRT-03 full-stats / " +
                      "RRT-04 #100① rerun→running (not stale) / RRT-05 rerun→full-stats / " +
                      "RRT-06 mode→visibility (LiveManual hidden, no collateral) / RRT-07 non-destructive / " +
                      "RRT-08 self-heal (persisted-hidden boot, no brick) verified.");
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

    // ── #138 second slice (findings 0110 §7): the Run Result tile is hidden ONLY in LiveManual, the
    // back-plane mirror of DriveOrderTicket. Drives the REAL root: DriveRunResult + FooterModeViewModel.ApplyPoll
    // + the real _dockWindows (no Python, no _lanes). A fresh scene build (independent of the content sections'
    // override seam).
    //   RRT-06 = mode→visibility: run_result visible in Replay/LiveAuto, hidden in LiveManual; and a sibling
    //            dock window (a chart — the orders/positions/buying_power tiles are retired by ADR-0038) stays
    //            VISIBLE in LiveManual (only run_result toggles — no collateral hiding).
    //   RRT-07 = non-destructive: Replay→LiveManual→Replay keeps the SAME window instance + geometry + groupId
    //            (pure SetActive, AC3), restored visible.
    //   RRT-08 = self-heal (Finding 1 guard): first the PREMISE — run_result RIDES CaptureLayout, so a layout
    //            saved while hidden in LiveManual persists visible=false (asserted via CaptureLayout(), making the
    //            SetActive(false) proxy faithful instead of a bare stand-in). Then: ApplyGeometry restores it
    //            hidden; on a Replay boot the ABSOLUTE toggle MUST re-show it — an in-memory remembered-set would
    //            leave it permanently invisible (the brick the review caught). Self-heal simulated via SetActive(false).
    // RED litmus: turn DriveRunResult into a no-op → RRT-06 LiveManual RED. Revert to a remembered-set
    // (re-show only ids it itself hid) → RRT-08 RED (a restored-hidden run_result is never re-shown = bricked).
    static string RunModeVisibilitySections()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "RRT-06: BackcastWorkspaceRoot missing in scene";
        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var driveRunResult = ty.GetMethod("DriveRunResult", BF);
        if (dockWindows == null) return "RRT-06: _dockWindows controller not built (renamed?)";
        if (footerMode == null) return "RRT-06: _footerMode not built (renamed?)";
        if (driveRunResult == null) return "RRT-06: DriveRunResult not found (renamed? — the back-plane mirror is missing)";
        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (applyPoll == null) return "RRT-06: FooterModeViewModel.ApplyPoll not found (renamed?)";

        Func<RectTransform> runResult = () => dockWindows.RectOf("run_result");
        if (runResult() == null) return "RRT-06: run_result base dock window not spawned by BuildWorkspace";
        // ADR-0038 (#174-178): the orders/positions/buying_power sibling base tiles are RETIRED (→ account
        // summary bar). The remaining dock-plane neighbour that MUST stay visible in LiveManual (DriveRunResult
        // toggles ONLY run_result — no collateral) is a chart window; spawn one as the no-collateral witness.
        string[] siblings = { "chart:RRT" };
        dockWindows.Spawn(FloatingWindowCatalog.KIND_CHART, "chart:RRT", 600f, -300f, 520f, 360f, true);
        foreach (var s in siblings)
            if (dockWindows.RectOf(s) == null) return "RRT-06: collateral witness '" + s + "' not spawned (no-collateral test would be vacuous)";

        void Poll(string mode, string venueState)
            => applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"" + venueState + "\"}" });
        void Drive() => driveRunResult.Invoke(root, null);
        bool SiblingsVisible()
        {
            foreach (var s in siblings)
            {
                var rt = dockWindows.RectOf(s);
                if (rt == null || !rt.gameObject.activeSelf) return false;
            }
            return true;
        }

        // ── RRT-06: mode → run_result visibility + no collateral. ──
        Poll("Replay", "");
        Drive();
        if (!runResult().gameObject.activeSelf) return "RRT-06: run_result hidden under Replay (must be visible — backtest needs Python)";
        Poll("LiveAuto", "CONNECTED");
        Drive();
        if (!runResult().gameObject.activeSelf) return "RRT-06: run_result hidden under LiveAuto (the cell drives the strategy — must stay visible)";
        Poll("LiveManual", "CONNECTED");
        Drive();
        if (runResult().gameObject.activeSelf) return "RRT-06: run_result VISIBLE under LiveManual (must be hidden — strategy-run surface is empty there)";
        if (!SiblingsVisible()) return "RRT-06: a sibling dock window (chart) was hidden under LiveManual — DriveRunResult must toggle ONLY run_result via RectOf(WINDOW_ID_RUN_RESULT) (no collateral)";
        Debug.Log("[E2E RRT-06 PASS] run_result toggles with mode — visible in Replay/LiveAuto, hidden in LiveManual; the chart sibling stays visible (no collateral)");

        // ── RRT-07: non-destructive — same instance + geometry + groupId across the round-trip. ──
        Poll("Replay", "");
        Drive();
        var w = runResult();
        int id = w.GetInstanceID();
        Vector2 pos = w.anchoredPosition, size = w.sizeDelta;
        string group = dockWindows.GroupIdOf("run_result");
        Poll("LiveManual", "CONNECTED");
        Drive();
        if (runResult() == null) return "RRT-07: run_result was destroyed on entering LiveManual (must be hide-not-destroy)";
        if (dockWindows.GroupIdOf("run_result") != group) return "RRT-07: run_result groupId changed while hidden (group membership must be preserved — AC3)";
        Poll("Replay", "");
        Drive();
        var back = runResult();
        if (back == null || back.GetInstanceID() != id) return "RRT-07: run_result is a new instance after a LiveManual round-trip (must be hide-not-destroy)";
        if ((back.anchoredPosition - pos).sqrMagnitude > EPS || (back.sizeDelta - size).sqrMagnitude > EPS)
            return "RRT-07: run_result geometry changed across the hide/show (pos/size not preserved — AC3)";
        if (dockWindows.GroupIdOf("run_result") != group) return "RRT-07: run_result groupId changed across the round-trip (AC3)";
        if (!back.gameObject.activeSelf) return "RRT-07: run_result not restored to visible after leaving LiveManual";
        Debug.Log("[E2E RRT-07 PASS] LiveManual hide is pure visibility — same instance + geometry + groupId preserved across the round-trip");

        // ── RRT-08 (Finding 1 regression guard): run_result rides CaptureLayout, so a layout SAVED while
        // hidden in LiveManual persists visible=false and ApplyGeometry restores it hidden on the next boot.
        // Boot mode is Replay (NOT LiveManual), so the absolute toggle MUST self-heal it back to visible — an
        // in-memory remembered-set would leave it permanently invisible with no recovery path (the brick the
        // review caught). Simulate the restored-hidden state with a direct SetActive(false). ──

        // RRT-08 PREMISE (proxy-faithfulness guard): the SetActive(false) below only STANDS IN for a
        // LiveManual-saved layout if run_result genuinely rides CaptureLayout hidden. Prove that first —
        // hide in LiveManual, capture, and assert run_result is on the captured list with visible==false
        // (strategy_editor is excluded at :2553; run_result is NOT). If a future change excluded run_result
        // from capture, a remembered-set would be safe and this whole self-heal section would be over-
        // constraining against a state that can no longer occur — this premise check makes that rot loud.
        Poll("LiveManual", "CONNECTED");
        Drive();   // hides run_result
        var captureLayout = ty.GetMethod("CaptureLayout", BF);
        if (captureLayout == null) return "RRT-08 premise: CaptureLayout not found (renamed?)";
        var doc = captureLayout.Invoke(root, null);
        var captured = doc?.GetType().GetField("floatingWindows")?.GetValue(doc) as System.Collections.IEnumerable;
        if (captured == null) return "RRT-08 premise: CaptureLayout().floatingWindows was null (capture path broken?)";
        bool rrOnList = false, rrCapturedHidden = false;
        foreach (var entry in captured)
        {
            var et = entry.GetType();
            if ((et.GetField("id")?.GetValue(entry) as string) != "run_result") continue;
            rrOnList = true;
            rrCapturedHidden = !(bool)et.GetField("visible").GetValue(entry);
        }
        if (!rrOnList)
            return "RRT-08 premise: run_result is ABSENT from CaptureLayout — it no longer rides capture (added to the :2553 " +
                   "exclusion?); the persisted-hidden boot can't occur and the absolute-toggle rationale is moot";
        if (!rrCapturedHidden)
            return "RRT-08 premise: run_result was captured visible=true while hidden in LiveManual — capture isn't recording " +
                   "the hide, so ApplyGeometry could never restore it hidden and the SetActive(false) proxy below is unfaithful";

        runResult().gameObject.SetActive(false);   // as ApplyGeometry would restore a LiveManual-saved layout
        Poll("Replay", "");
        Drive();
        if (!runResult().gameObject.activeSelf)
            return "RRT-08 (Finding 1): a run_result restored hidden (layout saved in LiveManual) was NOT self-healed " +
                   "by the Replay mode drive — it would be permanently invisible (a remembered-set brick)";
        Debug.Log("[E2E RRT-08 PASS] run_result rides CaptureLayout hidden (premise) AND a persisted-hidden boot self-heals to visible on Replay — never permanently bricked");

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
