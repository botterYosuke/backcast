// NewSeedsObserveNotebookE2ERunner.cs — issue #169 (ADR-0036 / findings 0124) の release-gate slice runner。
// 「New/初期化が観察ノート＋トヨタ universe を種付けし untitled でも Run 可」を実 BackcastWorkspaceRoot を反射駆動
// して固定する。台本: 同ディレクトリの NewSeedsObserveNotebookE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod NewSeedsObserveNotebookE2ERunner.Run -logFile <abs log>
//   # expect: [E2E NEWSEED SLICE PASS] ... / exit=0、各到達点で per-Action-ID タグ [E2E NEWSEED-NN PASS]
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// 層分け（#169 の slice 正本ゲート）:
//   * S2 の synth 出力（owner 指定形）は python/tests/test_marimo_cell_synthesis_golden.py（NEWSEED-05・実 marimo）。
//   * 本 runner は C# 半分 — fresh New/boot が観察セル＋トヨタ universe を種付けし、universe→chart が自動 spawn し、
//     untitled の遅延 scratch 経路（BuildNotebookStrategyPath）と run ゲート（TryStartRun resolver）が成立すること。
//   * 実 replay の bar 観察（DuckDB に 7203 のデータがあるか）は HITL（種コードは cwd/__file__ 非依存・ADR-0036 Consequences）。
//
// OpenScene は 1 回だけ（memory e2e-single-openscene-per-runner: 2 回目は ThemeService.Changed static leak で crash）。
// 全 section は ComposeRoot した同一 root に対し、各々が必要な New/SaveAs で状態を作って走る。

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class NewSeedsObserveNotebookE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string TOYOTA = "7203.TSE";

    static Type _ty;
    static BackcastWorkspaceRoot _root;

    public static void Run()
    {
        string fail;
        try
        {
            _root = ComposeRoot(out _ty);
            if (_root == null) { Done("compose: BackcastWorkspaceRoot missing in scene"); return; }

            fail = Section1_BootFreshSeeds()            // NEWSEED-01/02 (S1): no-resume boot seeds Toyota + observe cell, chart auto-spawns
                ?? Section2_FileNewSeeds()              // NEWSEED-03 (S2): File→New seeds the observe cell body; clean untitled
                ?? Section3_RestorePathNotSeeded()      // NEWSEED-04 (S1 negative): a restore/Open snapshot is honored verbatim (no Toyota injection)
                ?? Section4_UntitledRunReadyScratch()   // NEWSEED-06 (S3): untitled seeded + valid scenario → Ready, StrategyPath = a written scratch .py
                ?? Section5_UntitledEmptyBlocked()      // NEWSEED-07 (S3): untitled with an EMPTY buffer → BlockedNoStrategy
                ?? Section6_NamedDirtyBlocked()         // NEWSEED-08 (S3): named + dirty → BlockedNoStrategy (WYSIWYR unchanged)
                ?? Section7_NewNoScratchThenSaveAs();   // NEWSEED-09 (S3): New writes no scratch (Untitled); Save As → named WYSIWYR (dirty ゲート復活)
        }
        catch (Exception ex) { fail = "driver: " + ex; }

        Done(fail);
    }

    static void Done(string fail)
    {
        if (fail == null)
        {
            Debug.Log("[E2E NEWSEED SLICE PASS] fresh New/boot seeds the observe-replay cell + Toyota universe (chart auto-spawns); " +
                      "restore is honored verbatim; untitled runs from a lazy scratch .py (Ready); empty/named-dirty stay blocked; " +
                      "New is scratch-free (Untitled) and Save As restores named WYSIWYR.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E NEWSEED SLICE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── NEWSEED-01/02 (S1): no-resume boot (OpenFileNewDefault) seeds the Toyota universe + the observe cell,
    //   and the universe→chart wiring auto-spawns chart:7203.TSE. ──
    static string Section1_BootFreshSeeds()
    {
        var coord = Coord(); var scenario = Scenario(); var charts = ChartViews();
        if (coord == null || scenario == null || charts == null) return "S1: root seams not built (renamed?)";

        if (!Invoke("OpenFileNewDefault")) return "S1: OpenFileNewDefault not found (renamed?)";

        // NEWSEED-01: the observe-replay cell seeded into the single cell; clean + unbound (Untitled).
        if (coord.Notebook.CellCount != 1)
            return "S1/NEWSEED-01: boot fresh notebook not 1 cell (got " + coord.Notebook.CellCount + ")";
        if (coord.Notebook.Cells[0].Body != NotebookCellCoordinator.ObserveSeedBody)
            return "S1/NEWSEED-01: boot fresh cell 0 not seeded with the observe body (got [" + coord.Notebook.Cells[0].Body + "])";
        if (coord.Notebook.IsDirty) return "S1/NEWSEED-01: a freshly seeded boot must be clean (not dirty)";
        if (coord.Notebook.IsBound) return "S1/NEWSEED-01: a freshly seeded boot must be unbound (Untitled)";
        if (scenario.Universe.Count != 1 || scenario.Universe.Ids[0] != TOYOTA)
            return "S1/NEWSEED-01: boot fresh universe not [7203.TSE] (got [" + string.Join(",", scenario.Universe.Ids) + "])";
        // the seeded scenario must be VALID (dates + granularity + cash + non-empty universe) — else the observe
        // cell can't run (load_replay_data needs a granularity) and the first Save As fails Commit → universe wiped.
        // delete-the-production-logic litmus: SeedFreshDefaults sets Granularity=Daily; revert it to None → Validate
        // flags granularity → RED here.
        if (scenario.Validate().Any)
            return "S1/NEWSEED-01: the freshly seeded scenario is NOT valid (granularity/dates unset?) — Run/Save As would fail";
        Debug.Log("[E2E NEWSEED-01 PASS] no-resume boot seeds the observe cell + Toyota universe (clean, VALID untitled)");

        // NEWSEED-02: the universe→chart wiring (InstrumentRegistry.Changed → SyncChartWindowsToUniverse)
        // auto-spawned the Toyota chart (chart:7203.TSE) — no extra control.
        if (!charts.Contains(TOYOTA))
            return "S1/NEWSEED-02: the Toyota chart (chart:7203.TSE) did not auto-spawn (universe→chart wiring) — keys [" +
                   KeysOf(charts) + "]";
        Debug.Log("[E2E NEWSEED-02 PASS] Toyota chart auto-spawned from the seeded universe (chart ⊆ universe)");
        return null;
    }

    // ── NEWSEED-03 (S2): File→New (DoFileNew) seeds the observe cell body — same seam as boot, the File→New trigger. ──
    static string Section2_FileNewSeeds()
    {
        var coord = Coord(); var scenario = Scenario();
        if (!Invoke("DoFileNew", new object[] { null }))   // modeReq=null (disconnected): SetExecutionMode side-effect no-ops
            return "S2: DoFileNew(string) not found (renamed?)";
        if (coord.Notebook.CellCount != 1 || coord.Notebook.Cells[0].Body != NotebookCellCoordinator.ObserveSeedBody)
            return "S2/NEWSEED-03: File→New did not seed the observe cell body";
        if (coord.Notebook.IsDirty || coord.Notebook.IsBound)
            return "S2/NEWSEED-03: File→New seeded cell is not a clean untitled";
        if (scenario.Universe.Count != 1 || scenario.Universe.Ids[0] != TOYOTA)
            return "S2/NEWSEED-03: File→New did not seed the Toyota universe";
        if (scenario.Validate().Any)
            return "S2/NEWSEED-03: the File→New seeded scenario is NOT valid (granularity/dates unset?)";
        Debug.Log("[E2E NEWSEED-03 PASS] File→New seeds the observe cell + Toyota universe (VALID scenario)");
        return null;
    }

    // ── NEWSEED-04 (S1 negative): the RESTORE / corrupt-Open seam (ScenarioStartupController.PopulateFrom) is
    //   honored VERBATIM — Toyota is seeded ONLY by the root's fresh-seam SeedFreshDocumentDefaults (NEWSEED-01/03),
    //   never injected over a restored / inline-fallback universe (findings 0124 F8). Driven on a FRESH controller
    //   so the invariant is the PopulateFrom method itself (structurally seed-free), decoupled from the root's
    //   incidental tile-sync dirty state. ──
    static string Section3_RestorePathNotSeeded()
    {
        var ctrl = new ScenarioStartupController();
        var today = new DateTime(2026, 6, 26);
        // a restored document carrying its OWN universe [8035.TSE] → honored wholesale (no Toyota mixed in).
        var snap = new ScenarioSnapshot
        {
            Start = "2025-01-06", End = "2025-01-10", Granularity = "Minute", InitialCash = 1000000,
            Instruments = new System.Collections.Generic.List<string> { "8035.TSE" },
        };
        ctrl.PopulateFrom(snap, today);
        if (ctrl.Universe.Count != 1 || ctrl.Universe.Ids[0] != "8035.TSE")
            return "S3/NEWSEED-04: a restore snapshot was NOT honored verbatim (Toyota leaked into the restore?) — got [" +
                   string.Join(",", ctrl.Universe.Ids) + "]";
        // the corrupt-sidecar / inline-fallback degradation (PopulateFrom(null)) stays EMPTY — no Toyota either.
        ctrl.PopulateFrom(null, today);
        if (ctrl.Universe.Count != 0)
            return "S3/NEWSEED-04: PopulateFrom(null) (corrupt-Open degradation) seeded a universe (must stay empty) — got [" +
                   string.Join(",", ctrl.Universe.Ids) + "]";
        Debug.Log("[E2E NEWSEED-04 PASS] restore/Open (PopulateFrom) honors its own universe; the Toyota seed is fresh-seam-only");
        return null;
    }

    // ── NEWSEED-06 (S3): an untitled SEEDED notebook + a valid scenario is Run-ready immediately, and the run
    //   resolves to a lazily-WRITTEN scratch .py (the per-cell RUN's __file__). ──
    static string Section4_UntitledRunReadyScratch()
    {
        var coord = Coord(); var scenario = Scenario();
        if (!Invoke("DoFileNew", new object[] { null })) return "S4: DoFileNew not found";
        // a valid scenario (the seeded universe already has Toyota; New leaves Params empty → set valid ones).
        scenario.SetStart("2025-01-06");
        scenario.SetEnd("2025-01-10");
        scenario.SetGranularity(GranularityChoice.Minute);
        scenario.SetInitialCash("1000000");
        if (scenario.Validate().Any) return "S4: precondition — the test scenario is not valid";

        string scratch = BuildPath();
        if (string.IsNullOrEmpty(scratch))
            return "S4/NEWSEED-06: untitled seeded notebook resolved a NULL run path (BuildNotebookStrategyPath should write a scratch .py)";
        if (!File.Exists(scratch))
            return "S4/NEWSEED-06: the resolved scratch path was not written to disk (got " + scratch + ")";
        if (coord.Notebook.IsBound)
            return "S4/NEWSEED-06: writing the scratch must NOT bind the notebook (Untitled badge / WYSIWYR floor must hold)";

        // the run gate uses the SAME resolver — untitled seeded + valid scenario → Ready, StrategyPath == scratch.
        var gate = scenario.TryStartRun(BuildPath);
        if (gate.Gate != RunGate.Ready)
            return "S4/NEWSEED-06: untitled seeded run gate not Ready (gate=" + gate.Gate + ", msg=" + gate.Message + ")";
        if (!SamePath(gate.StrategyPath, scratch))
            return "S4/NEWSEED-06: run gate StrategyPath is not the scratch .py (got " + gate.StrategyPath + ")";
        Debug.Log("[E2E NEWSEED-06 PASS] untitled seeded notebook is Run-ready immediately; runs from a lazily-written scratch .py");
        return null;
    }

    // ── NEWSEED-07 (S3): an untitled notebook whose buffer is EMPTY (no authored content) is NOT runnable
    //   — the predicate is non-empty-cell, not merely untitled. ──
    static string Section5_UntitledEmptyBlocked()
    {
        var coord = Coord(); var scenario = Scenario();
        if (!Invoke("DoFileNew", new object[] { null })) return "S5: DoFileNew not found";
        coord.Notebook.Cells[0].SetBody("");   // empty the seeded buffer (no authored content)
        if (coord.Notebook.HasNonEmptyCell) return "S5: precondition — buffer still non-empty after clearing";
        if (!string.IsNullOrEmpty(BuildPath()))
            return "S5/NEWSEED-07: an EMPTY untitled buffer wrongly resolved a run path (must be null → blocked)";
        var gate = scenario.TryStartRun(BuildPath);
        if (gate.Gate != RunGate.BlockedNoStrategy)
            return "S5/NEWSEED-07: an empty untitled buffer was not BlockedNoStrategy (got " + gate.Gate + ")";
        Debug.Log("[E2E NEWSEED-07 PASS] an empty untitled buffer is BlockedNoStrategy (predicate = non-empty cell)");
        return null;
    }

    // ── NEWSEED-08 (S3): a NAMED doc that is dirty stays BlockedNoStrategy — the WYSIWYR gate is unchanged for
    //   bound documents (only untitled is unblocked). ──
    static string Section6_NamedDirtyBlocked()
    {
        var coord = Coord(); var scenario = Scenario();
        if (!Invoke("DoFileNew", new object[] { null })) return "S6: DoFileNew not found";
        // bind via Save As, then dirty the buffer.
        string savePy = Path.Combine(TempRoot, "named_dirty.py");
        _root.SetFileDialog(new StubFileDialog { NextResult = savePy });
        if (!Invoke("OnFileSaveAs")) return "S6: OnFileSaveAs not found (renamed?)";
        if (!coord.Notebook.IsBound) return "S6: precondition — Save As did not bind the notebook";
        coord.Notebook.Cells[0].SetBody(NotebookCellCoordinator.ObserveSeedBody + "\n# edited\n");
        if (!coord.Notebook.IsDirty) return "S6: precondition — editing did not dirty the named notebook";

        if (!string.IsNullOrEmpty(BuildPath()))
            return "S6/NEWSEED-08: a named+dirty notebook wrongly resolved a run path (WYSIWYR: must be null)";
        var gate = scenario.TryStartRun(BuildPath);
        if (gate.Gate != RunGate.BlockedNoStrategy)
            return "S6/NEWSEED-08: a named+dirty notebook was not BlockedNoStrategy (got " + gate.Gate + ")";
        Debug.Log("[E2E NEWSEED-08 PASS] a named+dirty notebook stays BlockedNoStrategy (WYSIWYR unchanged for bound docs)");
        return null;
    }

    // ── NEWSEED-09 (S3): a fresh New writes NO scratch (Untitled, ディスク非汚染 until the first Run); Save As
    //   rebinds to a real .py → named WYSIWYR (a clean bound doc is supplyable; editing re-blocks). ──
    static string Section7_NewNoScratchThenSaveAs()
    {
        var coord = Coord(); var scenario = Scenario();
        // resolve once to learn the scratch path, then delete it so we can prove New does NOT recreate it.
        if (!Invoke("DoFileNew", new object[] { null })) return "S7: DoFileNew not found";
        string scratch = BuildPath();
        if (string.IsNullOrEmpty(scratch)) return "S7: precondition — could not resolve a scratch path to probe";
        try { if (File.Exists(scratch)) File.Delete(scratch); } catch { }

        // a fresh New must NOT write the scratch (it is written lazily on the first Run, not at New).
        if (!Invoke("DoFileNew", new object[] { null })) return "S7: DoFileNew (2nd) not found";
        if (File.Exists(scratch))
            return "S7/NEWSEED-09: File→New wrote a scratch .py (New must be scratch-free until the first Run)";
        if (coord.Notebook.IsBound)
            return "S7/NEWSEED-09: a fresh New must be unbound (Untitled badge / _path=null)";

        // Save As → named WYSIWYR: a clean bound doc is supplyable (BuildNotebookStrategyPath returns the bound .py,
        // NOT a scratch); editing re-blocks (dirty ゲート復活).
        string savePy = Path.Combine(TempRoot, "saveas_named.py");
        _root.SetFileDialog(new StubFileDialog { NextResult = savePy });
        if (!Invoke("OnFileSaveAs")) return "S7: OnFileSaveAs not found";
        // the seeded universe must SURVIVE Save As: OnFileSaveAs commits the (now-VALID) scenario to the sidecar,
        // so the post-save ReseedFromEditor reads [7203.TSE] back. If the seeded scenario were INVALID (e.g.
        // granularity unset), Commit would write nothing and the reseed's PopulateFrom(null) would WIPE the
        // universe — the masked bug. delete-the-production-logic litmus: revert SeedFreshDefaults' Granularity=Daily.
        if (scenario.Universe.Count != 1 || scenario.Universe.Ids[0] != TOYOTA)
            return "S7/NEWSEED-09: Save As did NOT preserve the seeded universe (Commit failed → reseed wiped it?) — got [" +
                   string.Join(",", scenario.Universe.Ids) + "]";
        // set a deterministic scenario for the named Ready check (overwrites the preserved defaults).
        scenario.SetStart("2025-01-06"); scenario.SetEnd("2025-01-10");
        scenario.SetGranularity(GranularityChoice.Minute); scenario.SetInitialCash("1000000");
        string namedPath = BuildPath();
        if (!SamePath(namedPath, savePy))
            return "S7/NEWSEED-09: after Save As the run path is not the bound .py (named WYSIWYR) — got " + namedPath;
        var ready = scenario.TryStartRun(BuildPath);
        if (ready.Gate != RunGate.Ready) return "S7/NEWSEED-09: a clean named doc is not Run-ready (gate=" + ready.Gate + ")";
        coord.Notebook.Cells[0].SetBody(coord.Notebook.Cells[0].Body + "\n# dirty\n");
        if (!string.IsNullOrEmpty(BuildPath()))
            return "S7/NEWSEED-09: a named+dirty doc resolved a run path (dirty ゲート復活 failed)";
        Debug.Log("[E2E NEWSEED-09 PASS] New is scratch-free (Untitled); Save As → named WYSIWYR; editing re-blocks (dirty ゲート復活)");
        return null;
    }

    // ---- helpers ----

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "new_seeds_observe_e2e");

    static NotebookCellCoordinator Coord() => _ty.GetField("_coordinator", BF)?.GetValue(_root) as NotebookCellCoordinator;
    static ScenarioStartupController Scenario() => _ty.GetField("_scenario", BF)?.GetValue(_root) as ScenarioStartupController;
    static IDictionary ChartViews() => _ty.GetField("_chartViews", BF)?.GetValue(_root) as IDictionary;

    static MethodInfo s_buildPath;
    // The REAL BackcastWorkspaceRoot.BuildNotebookStrategyPath (the production run-file resolver: named .py /
    // untitled scratch). Used both as the gate resolver and to assert the scratch write — wiring identity =
    // production. A rename surfaces as a null MethodInfo → "renamed?" rather than a silent vacuous pass.
    static string BuildPath()
    {
        s_buildPath ??= _ty.GetMethod("BuildNotebookStrategyPath", BF);
        return s_buildPath != null ? s_buildPath.Invoke(_root, null) as string : null;
    }

    static bool Invoke(string method, object[] args = null)
    {
        var m = _ty.GetMethod(method, BF);
        if (m == null) return false;
        m.Invoke(_root, args);
        return true;
    }

    static string KeysOf(IDictionary d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var k in d.Keys) { if (sb.Length > 0) sb.Append(','); sb.Append(k); }
        return sb.ToString();
    }

    // path-identity: both sides Path.GetFullPath + OrdinalIgnoreCase (temporaryCachePath uses '/', SaveAs stores '\').
    static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Compose the REAL workspace root Python-FREE (mirrors AuthorToRunJourneyE2ERunner.ComposeRoot):
    // OpenScene → inject a builtin font → FakeMarimoSynthesizer → ResolvePaths → BuildWorkspace. ONE OpenScene
    // for the whole runner (memory e2e-single-openscene-per-runner: a 2nd OpenScene crashes on ThemeService.Changed).
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
        if (!Directory.Exists(TempRoot)) Directory.CreateDirectory(TempRoot);
        return root;
    }
}
