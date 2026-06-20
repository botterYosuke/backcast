// AuthorToRunJourneyE2ERunner.cs — author→run の横断 Journey E2E 回帰ゲート（台本: 同ディレクトリの
// AuthorToRunJourneyE2ERunner.md）。第二波・全行新規オーサリング。空 notebook → セル編集 → scenario 設定 →
// Save As → provider 5条件 supplyable → scenario commit → host.TryStartRun 受理、までの author→run の縫い目を
// 実 BackcastWorkspaceRoot を反射駆動して観測する。run 開始**後**の kernel replay→箱庭は ReplayToHakoniwa の
// 責務（JOURNEY-AUTHOR-13＝対象外・終端から参照を張るだけ）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod AuthorToRunJourneyE2ERunner.Run -logFile <log>
//   # expect: [E2E AUTHOR→RUN PASS] ... / exit=0  （確認は Bash `grep -a "E2E AUTHOR→RUN"`. ripgrep/Select-String は → を取りこぼす）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。Unity ログは UTF-8 = ripgrep で grep。
//
// 層分け — 各 surface の単体挙動の正本は既存 runner（ScenarioStartupE2ERunner / StrategyEditorNotebookE2ERunner /
// RunButtonE2ERunner / MenuBar 台本）。本 Journey は移送せず、実 root を通した author→run の横断データ伝播
// （セル dirty→provider 反転→gate commit→host 受理）を観測する＝重複ではなく縫い目。
//
// Python 層分け — steps 2-9 は Python-FREE（New/編集/scenario/SaveAs/provider/commit は host 非依存）。step 10 の
// host.TryStartRun 受理のみ実 server を要するので、その観測点に限り host.InitializePython("MOCK") を直呼びし
// _isOwner=true を設定（batchmode の所有権スキップを迂回する正当手・ReplayToHakoniwaE2ERunner と同型）。
//
// VACUITY（memory e2e-wave2-runner-promotion・findings 0066）:
//   * provider 弧は false(step3 未バインド) → true(step8 SaveAs) → false(step12 dirty) で非空虚化。**dirty ガード
//     （MarimoNotebookDocument 条件②）の delete-litmus は step12 に帰属**（step3 の false は未バインド由来＝条件①/③で
//     dirty 由来ではない。台本の「条件②削除で step3 が落ちる」はコード上は step12 のみ落ちる＝findings 0066 で訂正）。
//   * scenario commit は SaveAs も Commit する（OnFileSaveAs）ので、step9 を「sidecar に scenario」で素朴 assert すると
//     空虚。SaveAs 後に AddInstrument(SECOND) で universe を成長させ、step9 の TryStartRun の Commit だけが書ける
//     delta（SECOND）を sidecar で観測する（TryStartRun の Commit を消すと SECOND 欠落で step9 FAIL）。
//   * reject（11/12）は positive（08 true / 09 Ready / 10 accept）を先に実証してから観測する。
//
// path-identity（#3 RUN-01 / #5 JOURNEY-LAYOUT-01 で2回踏んだ罠）: MarimoNotebookDocument.SaveAs は
//   _path=Path.GetFullPath（\ 正規化）で保存し provider はそれを返す。_currentLayoutPath は dialog 生値
//   （temporaryCachePath の / 区切り）。path 一致は必ず両辺 Path.GetFullPath + StringComparison.OrdinalIgnoreCase で比較する。
//
// delete-the-production-logic litmus（findings 0066）:
//   * MarimoNotebookDocument.SaveAs の dirty クリア（_dirty=false / _openedOrSaved=true）を消す → steps 7-8 FAIL（provider が true へ反転しない）。
//   * ScenarioStartupController.TryStartRun の Commit 呼びを消す → step 9 FAIL（sidecar に SECOND が乗らない）。
//   * MarimoNotebookDocument.TryGetStrategyFile の `if (_dirty) return false`（条件②）を消す → step 12 FAIL（dirty でも true）。
//   * BackcastWorkspaceRoot.OnRun の _host.TryStartRun(req) を消す → step 10 FAIL（run が起動しない）。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AuthorToRunJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string ADOPTED_CELL = NotebookCellCoordinator.AdoptedRegionId;   // strategy_editor:region_001
    const string FIRST = "8918.TSE";
    const string SECOND = "7203.TSE";
    const int LAUNCH_POLL = 50;          // step 10: bounded poll for the run-launched signal (absorb the finally micro-window)
    const int LAUNCH_SLEEP_MS = 20;

    static WorkspaceEngineHost s_host;

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "author_to_run_journey_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_AuthorToRunHappyPath()   // JOURNEY-AUTHOR-01..10 (blank → authored → saved supplyable → committed → accepted)
                ?? Section2_RunGateRejects();          // JOURNEY-AUTHOR-11..12 (invalid scenario / dirty editor block the run)
        }
        catch (Exception ex) { fail = "driver: " + ex; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception ex) { Debug.LogWarning("[E2E AUTHOR→RUN] host.Stop failed (non-fatal): " + ex.Message); }
            TryDeleteDir(TempRoot);
        }

        if (fail == null)
        {
            Debug.Log("[E2E AUTHOR→RUN PASS] blank notebook → authored cell + scenario → save made the strategy supplyable → run gate committed the scenario sidecar → host.TryStartRun accepted the run.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E AUTHOR→RUN FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── 1. JOURNEY-AUTHOR-01..10: the full author→run seam on ONE composed root. steps 2-9 are Python-FREE;
    //   step 10 claims Python ("MOCK") to observe host.TryStartRun acceptance through the production OnRun. ──
    // Covers: JOURNEY-AUTHOR-01, JOURNEY-AUTHOR-02, JOURNEY-AUTHOR-03, JOURNEY-AUTHOR-04, JOURNEY-AUTHOR-05,
    //         JOURNEY-AUTHOR-06, JOURNEY-AUTHOR-07, JOURNEY-AUTHOR-08, JOURNEY-AUTHOR-09, JOURNEY-AUTHOR-10
    static string Section1_AuthorToRunHappyPath()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "happy: BackcastWorkspaceRoot missing in scene";

        // ── JOURNEY-AUTHOR-01: the composed root wired notebook / scenario / provider. ──
        var coordinator = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var provider = ty.GetProperty("EditorFileProvider", BF)?.GetValue(root) as IStrategyFileProvider;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var currentPathField = ty.GetField("_currentLayoutPath", BF);
        if (coordinator == null) return "JOURNEY-AUTHOR-01: _coordinator not built (renamed?)";
        if (scenario == null) return "JOURNEY-AUTHOR-01: _scenario not built (renamed?)";
        if (provider == null) return "JOURNEY-AUTHOR-01: EditorFileProvider not wired (renamed?)";
        if (chartViews == null) return "JOURNEY-AUTHOR-01: _chartViews not built (renamed?)";
        if (currentPathField == null) return "JOURNEY-AUTHOR-01: _currentLayoutPath not found (renamed?)";

        var onFileNew = ty.GetMethod("OnFileNew", BF);
        var onGuardDiscard = ty.GetMethod("OnGuardDiscard", BF);   // #87: File→New on a dirty notebook defers behind the SaveGuard; Discard proceeds
        var onFileSaveAs = ty.GetMethod("OnFileSaveAs", BF);
        var onRun = ty.GetMethod("OnRun", BF);
        var isOwnerField = ty.GetField("_isOwner", BF);
        if (onFileNew == null || onGuardDiscard == null || onFileSaveAs == null || onRun == null) return "JOURNEY-AUTHOR-01: File/Run ops not found (renamed?)";
        if (isOwnerField == null) return "JOURNEY-AUTHOR-01: _isOwner not found (renamed?)";

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "JOURNEY-AUTHOR-01: _host not built (renamed?)";
        s_host = host;
        isOwnerField.SetValue(root, true);   // OnRun's owner guard (read only at step 10; harmless before)

        // ── JOURNEY-AUTHOR-02: File→New clears to one empty cell + empty universe + untitled. NON-VACUOUS:
        //   dirty the workspace first (a 2nd cell + an edit + an instrument), so New's CLEAR is what we
        //   observe — not the build-time blank. #87: File→New on a DIRTY document is GUARDED — onFileNew
        //   opens the SaveGuard modal and DEFERS the clear; choosing Discard (OnGuardDiscard) proceeds. ──
        coordinator.AddCell();
        coordinator.Notebook.Cells[0].SetBody("dirty = 1\n");
        scenario.AddInstrument(SECOND);
        onFileNew.Invoke(root, null);
        // #87 non-vacuous: the dirty guard DEFERRED the clear (modal open) — the 2-cell dirty notebook is
        // still intact here, proving New did NOT clear immediately (delete the #87 guard → this fails).
        if (coordinator.Notebook.CellCount != 2)
            return "JOURNEY-AUTHOR-02: File→New on a DIRTY notebook did not DEFER the clear behind the SaveGuard (#87) — count " + coordinator.Notebook.CellCount;
        onGuardDiscard.Invoke(root, null);   // "Don't Save" → New proceeds with the clear
        if (coordinator.Notebook.CellCount != 1)
            return "JOURNEY-AUTHOR-02: File→New (after Discard) left " + coordinator.Notebook.CellCount + " cells (expected 1)";
        if (!string.IsNullOrEmpty(coordinator.Notebook.Cells[0].Body))
            return "JOURNEY-AUTHOR-02: File→New cell 0 is not empty";
        if (scenario.Universe.Count != 0)
            return "JOURNEY-AUTHOR-02: File→New did not clear the universe (count " + scenario.Universe.Count + ")";
        if ((currentPathField.GetValue(root) as string) != "")
            return "JOURNEY-AUTHOR-02: File→New did not drop _currentLayoutPath to untitled";
        if (provider.TryGetStrategyFile(out _))
            return "JOURNEY-AUTHOR-02: an untitled notebook must not be supplyable";

        // ── JOURNEY-AUTHOR-03: author the cell body → notebook dirty → provider NOT supplyable. (The notebook
        //   is still UNBOUND here, so this false is the "untitled/unsaved → can't run" guard; the DIRTY-specific
        //   guard is litmus'd at step 12 where the notebook is bound-but-dirty.) ──
        coordinator.Notebook.Cells[0].SetBody("def on_bar():\n    submit_market(1)\n");
        if (!coordinator.Notebook.IsDirty)
            return "JOURNEY-AUTHOR-03: editing the cell body did not mark the notebook dirty";
        if (provider.TryGetStrategyFile(out _))
            return "JOURNEY-AUTHOR-03: an edited-but-unsaved notebook must not be supplyable";

        // ── JOURNEY-AUTHOR-04: add then delete a cell — the set operation is consistent and the adopted
        //   region_001 shell survives (never destroyed). ──
        var added = coordinator.AddCell();
        if (coordinator.Notebook.CellCount != 2)
            return "JOURNEY-AUTHOR-04: AddCell did not grow the notebook to 2 cells";
        string addedRegion = coordinator.RegionOf(added);
        if (string.IsNullOrEmpty(addedRegion) || addedRegion == ADOPTED_CELL)
            return "JOURNEY-AUTHOR-04: the added cell did not get a fresh region (region_002+)";
        if (!coordinator.DeleteCell(addedRegion))
            return "JOURNEY-AUTHOR-04: DeleteCell(region_002) failed";
        if (coordinator.Notebook.CellCount != 1)
            return "JOURNEY-AUTHOR-04: DeleteCell did not shrink the notebook back to 1 cell";
        if (coordinator.CellOf(ADOPTED_CELL) == null)
            return "JOURNEY-AUTHOR-04: the adopted region_001 cell was lost (must survive delete)";

        // ── JOURNEY-AUTHOR-05: add a universe instrument → it reaches the scenario SoT AND spawns its chart
        //   tile (#60 universe→tile wiring). ──
        scenario.AddInstrument(FIRST);
        if (!new List<string>(scenario.Universe.Ids).Contains(FIRST))
            return "JOURNEY-AUTHOR-05: AddInstrument did not reach the universe SoT";
        if (!chartViews.Contains(FIRST))
            return "JOURNEY-AUTHOR-05: chart tile for " + FIRST + " not spawned (universe→tile wiring)";

        // ── JOURNEY-AUTHOR-06: set a valid backtest window / granularity / cash → Validate() is clean. ──
        scenario.SetStart("2024-10-01");
        scenario.SetEnd("2024-12-31");
        scenario.SetGranularity(GranularityChoice.Daily);
        scenario.SetInitialCash("1000000");
        if (scenario.Validate().Any)
            return "JOURNEY-AUTHOR-06: a valid scenario still reports validation errors";

        // ── JOURNEY-AUTHOR-07/08: File→Save As writes <name>.py, rebinds, clears dirty → provider flips
        //   false→TRUE and returns the canonical absolute saved path (the 5 conditions all hold). ──
        string savePy = Path.Combine(TempRoot, "authored.py");
        root.SetFileDialog(new StubFileDialog { NextResult = savePy });
        onFileSaveAs.Invoke(root, null);
        var boundPath = currentPathField.GetValue(root) as string;
        if (string.IsNullOrEmpty(boundPath) || !SamePath(boundPath, savePy))
            return "JOURNEY-AUTHOR-07: Save As did not rebind _currentLayoutPath to the new .py (got '" + boundPath + "')";
        if (!File.Exists(savePy))
            return "JOURNEY-AUTHOR-07: Save As did not write the .py to disk";
        if (!provider.TryGetStrategyFile(out string supplied))
            return "JOURNEY-AUTHOR-08: provider still FALSE after save (the false→true reversal is the seam)";
        if (!SamePath(supplied, savePy))
            return "JOURNEY-AUTHOR-08: provider returned the wrong path (got '" + supplied + "', want '" + Path.GetFullPath(savePy) + "')";

        // ── JOURNEY-AUTHOR-09: ▶ Run gate. NON-VACUOUS commit attribution: SaveAs already committed the
        //   scenario, so grow the universe by SECOND AFTER save — only the run gate's own Commit can persist
        //   that delta. (provider stays TRUE: adding an instrument dirties the SCENARIO, not the notebook.)
        //   TryStartRun must be Ready and the sidecar must carry BOTH instruments. ──
        scenario.AddInstrument(SECOND);
        if (!provider.TryGetStrategyFile(out _))
            return "JOURNEY-AUTHOR-09: growing the universe wrongly flipped the (notebook) provider to false";
        var gate = scenario.TryStartRun(provider);
        if (!gate.IsReady)
            return "JOURNEY-AUTHOR-09: run gate not Ready (gate=" + gate.Gate + ", msg=" + gate.Message + ")";
        var committed = ScenarioSidecarStore.ReadScenario(savePy);
        if (committed == null)
            return "JOURNEY-AUTHOR-09: run gate did not commit the scenario sidecar (ReadScenario null)";
        if (!committed.Instruments.Contains(FIRST) || !committed.Instruments.Contains(SECOND))
            return "JOURNEY-AUTHOR-09: committed sidecar missing instruments (got [" + string.Join(",", committed.Instruments) +
                   "]) — the run gate's Commit did not persist the post-save universe";

        // ── JOURNEY-AUTHOR-10: claim Python ("MOCK") ONLY now (steps 2-9 were Python-FREE), then drive the
        //   production OnRun → host.TryStartRun ACCEPTS the run (the seam's terminal). Observed at the host: a
        //   fresh host (not running / not finished) transitions to a launched run. We don't run real data —
        //   MOCK load_replay_data may fail fast; ACCEPTANCE is the observation. Run's finally tears it down. ──
        host.InitializePython("MOCK");
        if (!host.ServerReady)
            return "JOURNEY-AUTHOR-10: host server not ready after InitializePython(MOCK)";
        if (host.IsRunning || host.RunFinished)
            return "JOURNEY-AUTHOR-10: precondition — host already running/finished before OnRun";
        onRun.Invoke(root, null);
        bool launched = host.IsRunning || host.RunFinished;
        for (int i = 0; i < LAUNCH_POLL && !launched; i++) { Thread.Sleep(LAUNCH_SLEEP_MS); launched = host.IsRunning || host.RunFinished; }
        if (!launched)
            return "JOURNEY-AUTHOR-10: OnRun did not launch a run (host.TryStartRun refused or was not called)";

        return null;
    }

    // ── 2. JOURNEY-AUTHOR-11..12: the two run-gate REJECT paths, proved AFTER an in-section positive anchor
    //   (Save As → provider TRUE) so the negatives are non-vacuous. Python-FREE (the gate blocks before any
    //   run, never touching the host). ──
    // Covers: JOURNEY-AUTHOR-11, JOURNEY-AUTHOR-12
    static string Section2_RunGateRejects()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "rejects: BackcastWorkspaceRoot missing in scene";

        var coordinator = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var provider = ty.GetProperty("EditorFileProvider", BF)?.GetValue(root) as IStrategyFileProvider;
        var onFileSaveAs = ty.GetMethod("OnFileSaveAs", BF);
        if (coordinator == null || scenario == null) return "rejects: root seams not built (renamed?)";
        if (provider == null) return "rejects: EditorFileProvider not wired (renamed?)";
        if (onFileSaveAs == null) return "rejects: OnFileSaveAs not found (renamed?)";

        // anchor: author + valid scenario + Save As → provider TRUE (the positive the rejects deviate from).
        coordinator.Notebook.Cells[0].SetBody("def on_bar():\n    pass\n");
        scenario.AddInstrument(FIRST);
        scenario.SetStart("2024-10-01");
        scenario.SetEnd("2024-12-31");
        scenario.SetGranularity(GranularityChoice.Daily);
        scenario.SetInitialCash("1000000");
        string savePy = Path.Combine(TempRoot, "rejects.py");
        root.SetFileDialog(new StubFileDialog { NextResult = savePy });
        onFileSaveAs.Invoke(root, null);
        if (!provider.TryGetStrategyFile(out _))
            return "rejects (anchor): provider FALSE after save — cannot prove the negatives non-vacuously";

        // ── JOURNEY-AUTHOR-11: an INVALID scenario (empty universe) blocks with BlockedInvalidScenario and
        //   leaves the sidecar UNCHANGED (Commit returns before writing). provider stays TRUE so the block is
        //   attributable to the scenario, not a missing strategy. ──
        string json = ScenarioSidecarStore.SidecarPathFor(savePy);
        string jsonBefore = File.ReadAllText(json);
        scenario.RemoveInstrument(FIRST);
        if (!provider.TryGetStrategyFile(out _))
            return "JOURNEY-AUTHOR-11: clearing the universe wrongly flipped the (notebook) provider to false";
        var invalidGate = scenario.TryStartRun(provider);
        if (invalidGate.Gate != RunGate.BlockedInvalidScenario)
            return "JOURNEY-AUTHOR-11: an empty-universe run was not BlockedInvalidScenario (got " + invalidGate.Gate + ")";
        if (File.ReadAllText(json) != jsonBefore)
            return "JOURNEY-AUTHOR-11: a blocked run MUTATED the scenario sidecar (Commit must write nothing on invalid)";

        // ── JOURNEY-AUTHOR-12: a DIRTY editor blocks with BlockedNoStrategy (WYSIWYR). Re-add the universe so
        //   the scenario is valid again — the ONLY remaining block reason is the unsaved (dirty) notebook.
        //   This is the dirty-guard litmus (document condition ②): provider was TRUE at the anchor, the edit
        //   makes it FALSE. ──
        scenario.AddInstrument(FIRST);
        coordinator.Notebook.Cells[0].SetBody("def on_bar():\n    submit_market(2)   # unsaved edit\n");
        if (!coordinator.Notebook.IsDirty)
            return "JOURNEY-AUTHOR-12: re-editing the cell did not mark the notebook dirty";
        if (provider.TryGetStrategyFile(out _))
            return "JOURNEY-AUTHOR-12: a dirty (unsaved) notebook must not be supplyable";
        var dirtyGate = scenario.TryStartRun(provider);
        if (dirtyGate.Gate != RunGate.BlockedNoStrategy)
            return "JOURNEY-AUTHOR-12: a dirty-editor run was not BlockedNoStrategy (got " + dirtyGate.Gate + ")";

        return null;
    }

    // ---- helpers ----

    // Compose the REAL workspace root Python-FREE (mirrors LayoutPersistenceJourneyE2ERunner.ComposeRoot):
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

    // path-identity: both sides Path.GetFullPath + OrdinalIgnoreCase. Unity's temporaryCachePath uses '/',
    // MarimoNotebookDocument.SaveAs stores GetFullPath ('\') — compare normalized to avoid the separator RED.
    static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
