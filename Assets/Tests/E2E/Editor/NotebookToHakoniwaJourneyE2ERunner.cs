// NotebookToHakoniwaJourneyE2ERunner.cs — issue #95 の中心命題（D6/D7/D8）の縦串 Journey E2E 回帰ゲート
// （台本: 同ディレクトリの NotebookToHakoniwaJourneyE2ERunner.md）。
//
// 「per-cell RUN で notebook が実エンジンを駆動し、結果が Hakoniwa の base tile に逐次出る」縦串の、唯一の
// C# Journey ゲート。Surface（StrategyEditorNotebookE2ERunner S14/S15 = fake executor の制御ロジック半分）と
// Python e2e（test_notebook_replay_afk.py / test_notebook_step_afk.py = エンジン DATA 半分）の間に空いていた
// 縫い目を、実 BackcastWorkspaceRoot を反射駆動して埋める。旧 ReplayToHakoniwaE2ERunner（chart-OHLC 串・#99
// で退役）の縦串の席を、新 Hakoniwa（dockable floating window・ADR-0017）の base tile モデルで継ぐ。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod NotebookToHakoniwaJourneyE2ERunner.Run -logFile <log>
//   # expect: [E2E NB→HAKONIWA PASS] ... / exit=0  （確認は Bash `grep -a "E2E NB→HAKONIWA"`. ripgrep/Select-String は → を取りこぼす）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。Unity ログは UTF-8 = ripgrep で grep。
//
// Python 層分け（**Python-FREE**・findings 0073 / behavior-to-e2e の 2 ゲート分割）— host.InitializePython は呼ばない:
//   * press→制御半分: 実 root を反射合成し、root の本物の private callback（BuildNotebookScenarioJson /
//     SetCellRunButtonState / host.ForceStop / ViewFor / SetCellStaleRegions）に bind した NotebookRunController を、
//     executor だけ Python-FREE な fake に差し替えた同期レーン（startWorker:false）で駆動。scenario は実 _scenario から
//     実 BuildNotebookScenarioJson が組み、glyph は実 WireCellRunButton が作った _cellRunButtons の本物のボタンがトグルする。
//   * 走行スナップショット→Hakoniwa 半分: #65 の test seam WorkspaceEngineHost.TestPortfolioJsonOverride /
//     TestRunSummaryJsonOverride に合成スナップショットを注入し、_lastLiveShape=false（Replay shape）で実
//     RefreshLiveTiles()→PushReplayTiles() を pump。override が満たすのは実 poll lane と同一の LatestPortfolioJson /
//     RunSummaryJson プロパティなので、DecodePortfolio→FormatReplay*→LivePanelTileView.ShowText の鎖は 100% production。
//
// VACUITY / delete-the-production-logic litmus（台本「自動判定」節と対応）:
//   * BuildNotebookScenarioJson の空-universe ガード（return null）を消す → NBHAKO-01/12 FAIL（反転が消える / 空でも ■）。
//   * PushReplayTiles の ShowText/DecodePortfolio を消す → NBHAKO-05/06/07 FAIL（tile が "(no data — Replay)" のまま）。
//   * PushReplayTiles の running/full-stats 分岐を running 固定 → NBHAKO-07 FAIL（summary 後も fills: が出ない）。
//   * RunCell の `drivesReplay && scenarioJson` guard 条件を緩める → NBHAKO-10（step が ■）/ NBHAKO-12（null でも ■）FAIL。
//   * RunCell の _btRunActive 早期 return を消す → NBHAKO-09 FAIL（2nd RUN が executor に到達）。
//   * ApplyResult の _btRunActive=false 解除を消す → NBHAKO-08 FAIL（drain 後も ■）。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class NotebookToHakoniwaJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string R1 = NotebookCellCoordinator.AdoptedRegionId;   // strategy_editor:region_001 (== root WINDOW_ID)
    const string FIRST = "8918.TSE";

    // 走行中スナップショット（get_portfolio_json 形・ReplayPanelDecoder.PortfolioDto に JsonUtility が verbatim bind）。
    // snap1 = 1 建玉 + 1 約定、snap2 = 建玉成長（qty 100→200・equity 変化）で「逐次更新」を非空虚化する。
    const string SNAP1 =
        "{\"buying_power\":887600,\"equity\":1000300," +
        "\"positions\":[{\"symbol\":\"8918.TSE\",\"qty\":100,\"avg_price\":1123,\"unrealized_pnl\":300}]," +
        "\"orders\":[{\"symbol\":\"8918.TSE\",\"side\":\"BUY\",\"qty\":100,\"price\":1123,\"status\":\"FILLED\",\"ts_ms\":1700000000000}]," +
        "\"realized_pnl\":0,\"unrealized_pnl\":300}";
    const string SNAP2 =
        "{\"buying_power\":775200,\"equity\":1001000," +
        "\"positions\":[{\"symbol\":\"8918.TSE\",\"qty\":200,\"avg_price\":1124,\"unrealized_pnl\":1000}]," +
        "\"orders\":[" +
        "{\"symbol\":\"8918.TSE\",\"side\":\"BUY\",\"qty\":100,\"price\":1125,\"status\":\"FILLED\",\"ts_ms\":1700000100000}," +
        "{\"symbol\":\"8918.TSE\",\"side\":\"BUY\",\"qty\":100,\"price\":1124,\"status\":\"FILLED\",\"ts_ms\":1700000000000}]," +
        "\"realized_pnl\":0,\"unrealized_pnl\":1000}";
    // 終端の summary_json（RunResultDto 形）。run_result tile が running → full-stats に切替するのを観測。
    const string SUMMARY =
        "{\"fills_count\":2,\"equity_points\":50,\"total_pnl\":1000,\"max_drawdown\":-200,\"sharpe\":1.25,\"sortino\":1.80}";
    // step leg の 1-bar スナップショット（建玉 qty=50）。
    const string STEPSNAP =
        "{\"buying_power\":943800,\"equity\":1000100," +
        "\"positions\":[{\"symbol\":\"8918.TSE\",\"qty\":50,\"avg_price\":1124,\"unrealized_pnl\":100}]," +
        "\"orders\":[{\"symbol\":\"8918.TSE\",\"side\":\"BUY\",\"qty\":50,\"price\":1124,\"status\":\"FILLED\",\"ts_ms\":1700000000000}]," +
        "\"realized_pnl\":0,\"unrealized_pnl\":100}";

    static WorkspaceEngineHost s_host;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_ReplayDrivesHakoniwa()    // NBHAKO-01..09 (commit → bt.replay press → ■ → 走行スナップショット逐次 → 終端 summary → stop → 2nd-run reject)
                ?? Section2_StepDrivesHakoniwa()        // NBHAKO-10..11 (bt.step press は guard 非活性・scenario 手渡し・step スナップショット → tile)
                ?? Section3_NoScenarioNoRun();          // NBHAKO-12 (空 universe → null scenario → running guard 立たない)
        }
        catch (Exception ex) { fail = "driver: " + ex; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception ex) { Debug.LogWarning("[E2E NB→HAKONIWA] host.Stop failed (non-fatal): " + ex.Message); }
        }

        if (fail == null)
        {
            Debug.Log("[E2E NB→HAKONIWA PASS] committed scenario → per-cell bt.replay RUN entered RUNNING (▶→■) → " +
                      "running snapshots flowed through the production PushReplayTiles chain into the Hakoniwa base tiles " +
                      "(incremental) → summary switched run_result to full-stats → ■ stopped and restored ▶; bt.step armed " +
                      "no guard and its snapshot reached the tiles; an uncommitted universe ran no backtest. (Engine DATA = " +
                      "test_notebook_replay_afk.py / test_notebook_step_afk.py; real pixels = owner HITL.)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E NB→HAKONIWA FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── 1. NBHAKO-01..09: the full replay vertical on ONE composed root. ──
    // Covers: JOURNEY-NBHAKO-01, -02, -03, -04, -05, -06, -07, -08, -09
    static string Section1_ReplayDrivesHakoniwa()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "replay: BackcastWorkspaceRoot missing in scene";
        if (!ReadSeams(root, ty, out var seams, out string err)) return "replay: " + err;

        var exec = new _ReplayExecutor();
        var errors = new List<string>();
        int[] stopCalls = { 0 };
        var controller = RebuildController(root, ty, seams, exec, errors, stopCalls);

        // ── NBHAKO-01: scenario commit → run-config hand-off. NON-VACUOUS: provider is null BEFORE a universe
        //   is committed, then flips to a JSON carrying the instruments (the startup panel reaches the press). ──
        if (seams.ScenarioProvider() != null)
            return "NBHAKO-01: BuildNotebookScenarioJson returned non-null with an EMPTY universe (the empty-universe guard is the reversal anchor)";
        CommitScenario(seams.Scenario);
        string committed = seams.ScenarioProvider();
        if (string.IsNullOrEmpty(committed) || !committed.Contains(FIRST))
            return "NBHAKO-01: committing a universe did not flip BuildNotebookScenarioJson to a JSON carrying the instrument (got [" + committed + "])";

        // ── NBHAKO-02: author a bt.replay cell. ──
        seams.Coordinator.Notebook.Cells[0].SetBody("for bar in bt.replay():\n    pass\n");
        string src = seams.Coordinator.Notebook.SynthesizeLiveSource();
        if (src == null || !src.Contains("bt.replay"))
            return "NBHAKO-02: the authored bt.replay cell did not survive into the synthesised live source";

        // glyph precondition: the REAL _cellRunButtons button starts as ▶.
        var runBtn = ButtonFor(seams.CellButtons, R1);
        if (runBtn == null) return "NBHAKO-04: region_001 has no ▶ RUN button (WireCellRunButton)";
        if (GlyphText(runBtn) != "▶") return "NBHAKO-04: precondition — RUN button does not start as ▶";

        // ── NBHAKO-03 / -04: press → the committed scenario reaches the executor; RUNNING (▶→■). ──
        controller.RunCell(R1);
        if (exec.LastScenario == null || !exec.LastScenario.Contains(FIRST))
            return "NBHAKO-03: the executor did not receive the committed scenario JSON on a bt.replay press (got [" + exec.LastScenario + "])";
        if (exec.LastScenario != committed)
            return "NBHAKO-03: the scenario the press handed over differs from BuildNotebookScenarioJson()";
        if (!controller.IsBacktestRunning)
            return "NBHAKO-04: controller not RUNNING after a bt.replay press";
        if (GlyphText(runBtn) != "■")
            return "NBHAKO-04: the REAL cell button ▶ did not toggle to ■ while running";

        // ── NBHAKO-05: running snapshot bar1 → the 4 Hakoniwa base tiles show the real figures (D6/D8). ──
        SetReplayShape(root, ty);
        SetPortfolioOverride(seams.Host, SNAP1);
        PumpTiles(root, ty);
        string pos1 = TileText(root, ty, "_positionsView");
        string ord1 = TileText(root, ty, "_ordersView");
        string bp1 = TileText(root, ty, "_buyingPowerView");
        string rr1 = TileText(root, ty, "_runResultView");
        if (pos1 == null || !pos1.Contains("qty=100") || !pos1.Contains(FIRST))
            return "NBHAKO-05: positions tile did not reflect the running snapshot (got [" + pos1 + "])";
        if (ord1 == null || !ord1.Contains("filled-order count: 1"))
            return "NBHAKO-05: orders tile did not reflect the filled order (got [" + ord1 + "])";
        if (bp1 == null || !bp1.Contains("bp=887600"))
            return "NBHAKO-05: buying_power tile did not reflect the snapshot (got [" + bp1 + "])";
        if (rr1 == null || !rr1.Contains("running") || !rr1.Contains("o:1"))
            return "NBHAKO-05: run_result tile did not show the running view (got [" + rr1 + "])";

        // ── NBHAKO-06: bar2 snapshot → the SAME tiles follow incrementally (not a one-shot paint). ──
        SetPortfolioOverride(seams.Host, SNAP2);
        PumpTiles(root, ty);
        string pos2 = TileText(root, ty, "_positionsView");
        string bp2 = TileText(root, ty, "_buyingPowerView");
        if (pos2 == null || !pos2.Contains("qty=200"))
            return "NBHAKO-06: positions tile did not advance to the bar2 snapshot (got [" + pos2 + "])";
        if (pos2 == pos1)
            return "NBHAKO-06: positions tile text did not change between bar1 and bar2 (no incremental update)";
        if (bp2 == null || !bp2.Contains("bp=775200") || bp2 == bp1)
            return "NBHAKO-06: buying_power tile did not advance to the bar2 snapshot (got [" + bp2 + "])";

        // ── NBHAKO-07: terminal summary → run_result switches from running view to full-stats. ──
        SetSummaryOverride(seams.Host, SUMMARY);
        PumpTiles(root, ty);
        string rr2 = TileText(root, ty, "_runResultView");
        if (rr2 == null || !rr2.Contains("fills:2"))
            return "NBHAKO-07: run_result tile did not switch to the full-stats view on summary arrival (got [" + rr2 + "])";
        if (rr2 == rr1)
            return "NBHAKO-07: run_result tile stayed on the running view after the summary arrived";

        // ── NBHAKO-09: a 2nd RUN while the backtest is in flight is REJECTED (no second executor call). ──
        int callsBefore = exec.Calls;
        controller.RunCell(R1);
        if (exec.Calls != callsBefore)
            return "NBHAKO-09: a 2nd RUN ran while a backtest was in flight (the running guard failed)";
        if (errors.Count == 0)
            return "NBHAKO-09: the rejected 2nd RUN surfaced no message";

        // ── NBHAKO-08: ■ press → force-stop wiring fires; draining the result clears the guard and restores ▶. ──
        controller.StopRunning();
        if (stopCalls[0] != 1)
            return "NBHAKO-08: ■ press did not invoke the force-stop wiring (host.ForceStop)";
        controller.DrainAndRoute();
        if (controller.IsBacktestRunning)
            return "NBHAKO-08: still RUNNING after the result drained";
        if (GlyphText(runBtn) != "▶")
            return "NBHAKO-08: ■ did not restore to ▶ after the run finished";
        controller.RunCell(R1);   // a fresh press is accepted (the guard is not stuck)
        if (!controller.IsBacktestRunning)
            return "NBHAKO-08: a new run was not accepted after the prior one finished";
        controller.DrainAndRoute();

        ((IDisposable)seams.Lane).Dispose();
        return null;
    }

    // ── 2. NBHAKO-10..11: bt.step press arms no running guard, still hands over the scenario, and its
    //   snapshot reaches the Hakoniwa tiles (bar-by-bar debug → 結果 tile). ──
    // Covers: JOURNEY-NBHAKO-10, -11
    static string Section2_StepDrivesHakoniwa()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "step: BackcastWorkspaceRoot missing in scene";
        if (!ReadSeams(root, ty, out var seams, out string err)) return "step: " + err;

        var exec = new _ReplayExecutor();
        var errors = new List<string>();
        int[] stopCalls = { 0 };
        var controller = RebuildController(root, ty, seams, exec, errors, stopCalls);

        CommitScenario(seams.Scenario);

        // a B3 step cell (no bt.replay in source → the running guard must NOT arm — findings 0074).
        seams.Coordinator.Notebook.Cells[0].SetBody("bar = bt.step()\nbar\n");
        var runBtn = ButtonFor(seams.CellButtons, R1);
        if (runBtn == null) return "NBHAKO-10: region_001 has no ▶ RUN button";
        if (GlyphText(runBtn) != "▶") return "NBHAKO-10: precondition — RUN button does not start as ▶";

        // ── NBHAKO-10: press a step cell → guard NOT active, glyph stays ▶, scenario still handed over. ──
        controller.RunCell(R1);
        if (controller.IsBacktestRunning)
            return "NBHAKO-10: a bt.step press wrongly armed the running guard (step is instant + stateful — findings 0074)";
        if (GlyphText(runBtn) != "▶")
            return "NBHAKO-10: a bt.step press wrongly toggled the glyph to ■";
        if (exec.LastScenario == null || !exec.LastScenario.Contains(FIRST))
            return "NBHAKO-10: the committed scenario did not ride along with the bt.step press (got [" + exec.LastScenario + "])";
        controller.DrainAndRoute();   // route the step's output

        // ── NBHAKO-11: the step's 1-bar snapshot reaches the Hakoniwa positions tile. ──
        SetReplayShape(root, ty);
        SetPortfolioOverride(seams.Host, STEPSNAP);
        PumpTiles(root, ty);
        string pos = TileText(root, ty, "_positionsView");
        if (pos == null || !pos.Contains("qty=50") || !pos.Contains(FIRST))
            return "NBHAKO-11: the bt.step snapshot did not reach the positions tile (got [" + pos + "])";

        ((IDisposable)seams.Lane).Dispose();
        return null;
    }

    // ── 3. NBHAKO-12: an uncommitted universe → null scenario → a bt.replay press arms no running guard
    //   (D5: a backtest only starts against a real scenario). ──
    // Covers: JOURNEY-NBHAKO-12
    static string Section3_NoScenarioNoRun()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "noscn: BackcastWorkspaceRoot missing in scene";
        if (!ReadSeams(root, ty, out var seams, out string err)) return "noscn: " + err;

        var exec = new _ReplayExecutor();
        var errors = new List<string>();
        int[] stopCalls = { 0 };
        var controller = RebuildController(root, ty, seams, exec, errors, stopCalls);

        // NO universe committed → BuildNotebookScenarioJson is null.
        if (seams.ScenarioProvider() != null)
            return "NBHAKO-12: precondition — an empty universe must yield a null scenario";

        seams.Coordinator.Notebook.Cells[0].SetBody("for bar in bt.replay():\n    pass\n");
        var runBtn = ButtonFor(seams.CellButtons, R1);
        if (runBtn == null) return "NBHAKO-12: region_001 has no ▶ RUN button";
        if (GlyphText(runBtn) != "▶") return "NBHAKO-12: precondition — RUN button does not start as ▶";

        // ── NBHAKO-12: press with no committed scenario → the guard condition (drivesReplay && scenarioJson)
        //   is false, so the cell does NOT enter RUNNING and the glyph stays ▶. ──
        controller.RunCell(R1);
        if (controller.IsBacktestRunning)
            return "NBHAKO-12: a bt.replay press with NO committed scenario wrongly armed the running guard";
        if (GlyphText(runBtn) != "▶")
            return "NBHAKO-12: a scenario-less bt.replay press wrongly toggled the glyph to ■";
        controller.DrainAndRoute();

        ((IDisposable)seams.Lane).Dispose();
        return null;
    }

    // ---- composition + seams ----

    // Compose the REAL workspace root Python-FREE (mirrors AuthorToRunJourneyE2ERunner.ComposeRoot):
    // OpenScene → inject a builtin font → FakeMarimoSynthesizer → ResolvePaths → BuildWorkspace.
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }

    sealed class Seams
    {
        public ScenarioStartupController Scenario;
        public NotebookCellCoordinator Coordinator;
        public WorkspaceEngineHost Host;
        public IDictionary CellButtons;            // _cellRunButtons (region → Button)
        public Func<string> ScenarioProvider;      // root.BuildNotebookScenarioJson (real)
        public Func<string, StrategyEditorView> ViewFor;   // root.ViewFor (real)
        public Action<string, bool> OnRunningChanged;      // root.SetCellRunButtonState (real)
        public Action<IReadOnlyList<string>> OnStale;      // root.SetCellStaleRegions (real)
        public NotebookRunLane Lane;               // the rebuilt synchronous Python-FREE lane
    }

    static bool ReadSeams(BackcastWorkspaceRoot root, Type ty, out Seams seams, out string err)
    {
        seams = new Seams();
        seams.Scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        seams.Coordinator = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        seams.Host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        seams.CellButtons = ty.GetField("_cellRunButtons", BF)?.GetValue(root) as IDictionary;
        if (seams.Scenario == null) { err = "_scenario not built (renamed?)"; return false; }
        if (seams.Coordinator == null) { err = "_coordinator not built (renamed?)"; return false; }
        if (seams.Host == null) { err = "_host not built (renamed?)"; return false; }
        if (seams.CellButtons == null) { err = "_cellRunButtons not built (renamed?)"; return false; }

        // bind cell 0 → region_001 (the boot state ResumeLastDocumentOrDefault would set — we skip that to
        // avoid disk/PlayerPrefs). New() = ResetUnboundEmpty + SyncWindowsToNotebook, so CellOf(region_001)
        // resolves and RunCell(region_001) lands on a real cell; the adopted window's ▶ (wired in
        // BuildWorkspace) is reused, so _cellRunButtons[region_001] stays valid.
        seams.Coordinator.New();

        // the 4 Hakoniwa base tile views must have been built by SpawnBaseDockWindows during BuildWorkspace.
        foreach (var f in new[] { "_buyingPowerView", "_ordersView", "_positionsView", "_runResultView" })
            if (ty.GetField(f, BF)?.GetValue(root) == null) { err = f + " not built (SpawnBaseDockWindows renamed?)"; return false; }

        // bind the controller's callbacks to the root's REAL private methods (wiring identity = production).
        var mScenario = ty.GetMethod("BuildNotebookScenarioJson", BF);
        var mViewFor = ty.GetMethod("ViewFor", BF);
        var mRunning = ty.GetMethod("SetCellRunButtonState", BF);
        var mStale = ty.GetMethod("SetCellStaleRegions", BF);
        if (mScenario == null || mViewFor == null || mRunning == null || mStale == null)
        { err = "a root callback method was renamed (BuildNotebookScenarioJson/ViewFor/SetCellRunButtonState/SetCellStaleRegions)"; return false; }
        seams.ScenarioProvider = (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), root, mScenario);
        seams.ViewFor = (Func<string, StrategyEditorView>)Delegate.CreateDelegate(typeof(Func<string, StrategyEditorView>), root, mViewFor);
        seams.OnRunningChanged = (Action<string, bool>)Delegate.CreateDelegate(typeof(Action<string, bool>), root, mRunning);
        seams.OnStale = (Action<IReadOnlyList<string>>)Delegate.CreateDelegate(typeof(Action<IReadOnlyList<string>>), root, mStale);

        ty.GetField("_isOwner", BF)?.SetValue(root, true);
        err = null;
        return true;
    }

    // commit a valid backtest scenario into the REAL ScenarioStartupController (the startup panel SoT
    // BuildNotebookScenarioJson serialises). Shared by the replay + step legs (Section3 commits none).
    static void CommitScenario(ScenarioStartupController scenario)
    {
        scenario.AddInstrument(FIRST);
        scenario.SetStart("2024-10-01");
        scenario.SetEnd("2024-12-31");
        scenario.SetGranularity(GranularityChoice.Daily);
        scenario.SetInitialCash("1000000");
    }

    // Swap the root's worker-thread lane (real HostNotebookCellExecutor → would cross pythonnet) for a
    // SYNCHRONOUS Python-FREE fake lane, and rebuild _notebookRun bound to the root's REAL callbacks. The
    // press path (scenario provider, glyph toggle, force-stop) then runs production wiring; only the cell
    // execution itself is faked (the engine DATA is the Python e2e's job).
    static NotebookRunController RebuildController(
        BackcastWorkspaceRoot root, Type ty, Seams seams,
        INotebookCellExecutor exec, List<string> errors, int[] stopCalls)
    {
        (ty.GetField("_notebookRunLane", BF)?.GetValue(root) as IDisposable)?.Dispose();   // stop the original worker thread

        var lane = new NotebookRunLane(exec, startWorker: false);
        var host = seams.Host;
        var controller = new NotebookRunController(
            seams.Coordinator, seams.ViewFor, lane,
            msg => errors.Add(msg),                          // onError (production wires this to ShowMessage; here we record)
            seams.ScenarioProvider,                          // scenarioJsonProvider = REAL BuildNotebookScenarioJson
            () => { stopCalls[0]++; host.ForceStop(); },     // onStop = record + REAL host.ForceStop (no-op without a server)
            seams.OnRunningChanged,                          // onRunningChanged = REAL SetCellRunButtonState (toggles the real button)
            seams.OnStale);                                  // onStaleRegionsChanged = REAL SetCellStaleRegions

        seams.Lane = lane;
        ty.GetField("_notebookRunLane", BF).SetValue(root, lane);
        ty.GetField("_notebookRun", BF).SetValue(root, controller);
        return controller;
    }

    // ---- tile drive (the #65 override seam → production PushReplayTiles) ----

    static void SetReplayShape(BackcastWorkspaceRoot root, Type ty)
        => ty.GetField("_lastLiveShape", BF).SetValue(root, false);   // Replay shape: RefreshLiveTiles drives PushReplayTiles every frame

    static void SetPortfolioOverride(WorkspaceEngineHost host, string json)
        => host.GetType().GetField("TestPortfolioJsonOverride", BF).SetValue(host, json);

    static void SetSummaryOverride(WorkspaceEngineHost host, string json)
        => host.GetType().GetField("TestRunSummaryJsonOverride", BF).SetValue(host, json);

    // pump the REAL per-frame tile slice (RefreshLiveTiles → PushReplayTiles in Replay shape).
    static void PumpTiles(BackcastWorkspaceRoot root, Type ty)
        => ty.GetMethod("RefreshLiveTiles", BF).Invoke(root, null);

    // ---- observation helpers ----

    static Button ButtonFor(IDictionary cellButtons, string region)
        => cellButtons != null && cellButtons.Contains(region) ? cellButtons[region] as Button : null;

    // the run-button glyph text ("▶" idle / "■" running), read off the real button's "RunGlyph" child Text.
    static string GlyphText(Button btn)
    {
        var glyph = btn != null ? btn.transform.Find("RunGlyph") : null;
        var t = glyph != null ? glyph.GetComponent<Text>() : null;
        return t != null ? t.text : null;
    }

    // a Hakoniwa base tile's rendered text, read off LivePanelTileView's private _content Text.
    static string TileText(BackcastWorkspaceRoot root, Type ty, string viewField)
    {
        var view = ty.GetField(viewField, BF)?.GetValue(root);
        if (view == null) return null;
        var content = view.GetType().GetField("_content", BF)?.GetValue(view) as Text;
        return content != null ? content.text : null;
    }

    // ---- Python-FREE fake executor (records the scenario hand-off + call count; routes the pressed cell) ----

    sealed class _ReplayExecutor : INotebookCellExecutor
    {
        public int Calls;
        public string LastScenario;

        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
        {
            Calls++;
            LastScenario = scenarioJson;
            return new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = "run", Ok = true } },
                Stale = Array.Empty<int>(),
            };
        }

        public int[] Restage(string source) => Array.Empty<int>();
    }
}
