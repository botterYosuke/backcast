// LayoutPersistenceJourneyE2ERunner.cs — Journey E2E regression gate for layout persistence (台本:
// same-dir LayoutPersistenceJourneyE2ERunner.md). 第二波・全行新規オーサリング。複数サーフェスをまたぐ実
// ユーザーストーリー＝配置（箱庭 tile 順 / floating window rect / infinite-canvas pan・zoom / notebook cell
// 位置 / per-mode profile）を <strategy>.json の layout キーへ Save → 汚す → File→Open で復元、の round-trip を
// 実 BackcastWorkspaceRoot を反射駆動して観測する。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod LayoutPersistenceJourneyE2ERunner.Run -logFile <log>
//   # expect: [E2E LAYOUT JOURNEY PASS] ... / exit=0  （確認は Bash `grep -a "E2E LAYOUT JOURNEY"`. ripgrep/Select-String は取りこぼす）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。Unity ログは UTF-8 = ripgrep で grep。
//
// 層分け — 純データ probe（ReplayLayoutProbe / MultiDocLayoutProbe）との非重複: 各次元の純データ round-trip
// （panels の Capture/Apply・layout-key の WriteLayout/TryReadLayout・scenario⇄layout coexist・SaveAs の独立）は
// それら scene-free / Python-FREE probe が AFK 権威。本 Journey はそれらが「picker UI + root 配線は HITL」と切り
// 出した部分＝実 BackcastWorkspaceRoot を通した「配置 → OnFileSave → OnFileOpen → 復元」を埋める（重複ではなく
// 縫い目を観測）。よって純データ probe は移送しない（別レイヤ・別正本）。
//
// VACUITY（最重要・findings 0029/0048・memory e2e-wave2-runner-promotion）: 「復元された」を assert する前に
//   (a) 保存前に baseline（build 既定）と非既定であること、(b) Save 後に live を別状態へ汚し「保存値と別物」で
//   あること、を実証してから OnFileOpen で「保存値へ戻る」を assert する。これを踏まないと「既定のまま何も起き
//   てない」を「復元 OK」と誤認する。
//   ⚠️ 生産コードの落とし穴: OnFileNew は canvas / Hakoniwa / floating の geometry を **リセットしない**（生産
//   BackcastWorkspaceRoot 1592「canvas pan/zoom + Hakoniwa are NOT reset」）。cell も _coordinator.New() →
//   SyncWindowsToNotebook(null) が region_001 を Show で現在位置のまま保つ＝**リセットしない**。File→New が真に
//   汚すのは _currentLayoutPath（→""）と notebook 内容だけ（JOURNEY-LAYOUT-08）。だから 4 つの geometry 次元
//   （canvas/tile/window/cell）の round-trip 非空虚化は、File→New 後に **明示的に第3の値へ perturb** し「保存値
//   と別物」を assert してから Open で戻す（BackcastWorkspaceProbe S12 の "perturb the live order, then restore
//   from disk" を実 OnFileSave/OnFileOpen seam へ持ち上げた形）。
//
// delete-the-production-logic litmus（findings 0065）: ApplyLayout の _canvas.ApplyView を no-op 化 →
//   JOURNEY-LAYOUT-10 が FAIL。ApplyProfileOrder を no-op 化 → 11/13 が FAIL。RestoreFloating を no-op 化 →
//   12 が FAIL。OnFileOpen の coordinator.Open に渡す cellPositions を null 固定 → 05(restore) が FAIL。
//   OnFileSave の TryWriteLayout を消す → 07 が FAIL。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LayoutPersistenceJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;
    const string ORDER_WINDOW_ID = "order:region_001";          // KIND_ORDER (non-cell floating window)
    const string CELL_WINDOW_ID = "strategy_editor:region_001"; // KIND_STRATEGY_EDITOR (cell window, region_001 shell)

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "layout_journey_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_RealRootRoundTrip()   // JOURNEY-LAYOUT-01..13 (the 9-step round-trip on one root)
                ?? Section2_NoWipeBareOpen()       // JOURNEY-LAYOUT-14 (scenario-only/corrupt open keeps geometry)
                ?? Section3_SaveAsFork();          // JOURNEY-LAYOUT-15 (Save As forks a new pair, old untouched)
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E LAYOUT JOURNEY PASS] real workspace round-tripped 5 layout dimensions (canvas/tiles/windows/cells/per-mode profile) through the <strategy>.json layout key and restored them on reopen.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E LAYOUT JOURNEY FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── 1. JOURNEY-LAYOUT-01..13: the real round-trip on ONE composed root. Python-FREE: the layout
    //   round-trip needs no kernel; OnFileSave/OnFileOpen's mode side-effect is a no-op while disconnected
    //   (SendModeSideEffect skips a null mode). per-mode profile is driven by reflect-invoking
    //   SyncBaseTilesToMode(bool) — NOT the footer UI (BackcastWorkspaceProbe S12 parity). ──
    // Covers: JOURNEY-LAYOUT-01, JOURNEY-LAYOUT-02, JOURNEY-LAYOUT-03, JOURNEY-LAYOUT-04, JOURNEY-LAYOUT-05,
    //         JOURNEY-LAYOUT-06, JOURNEY-LAYOUT-07, JOURNEY-LAYOUT-08, JOURNEY-LAYOUT-09, JOURNEY-LAYOUT-10,
    //         JOURNEY-LAYOUT-11, JOURNEY-LAYOUT-12, JOURNEY-LAYOUT-13
    static string Section1_RealRootRoundTrip()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "roundtrip: BackcastWorkspaceRoot missing in scene";

        var hako = ty.GetField("_hako", BF)?.GetValue(root) as HakoniwaController;
        var canvas = ty.GetField("_canvas", BF)?.GetValue(root) as InfiniteCanvasController;
        var windows = ty.GetField("_windows", BF)?.GetValue(root) as FloatingWindowController;
        var coordinator = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        if (hako == null) return "roundtrip: _hako not built (renamed?)";
        if (canvas == null) return "roundtrip: _canvas not built (renamed?)";
        if (windows == null) return "roundtrip: _windows not built (renamed?)";
        if (coordinator == null) return "roundtrip: _coordinator not built (renamed?)";
        if (scenario == null) return "roundtrip: _scenario not built (renamed?)";

        var onOpen = ty.GetMethod("OnFileOpen", BF);
        var onSave = ty.GetMethod("OnFileSave", BF);
        var onNew = ty.GetMethod("OnFileNew", BF);
        var syncMode = ty.GetMethod("SyncBaseTilesToMode", BF);
        var captureLayout = ty.GetMethod("CaptureLayout", BF);
        var currentPathField = ty.GetField("_currentLayoutPath", BF);
        var baseLiveField = ty.GetField("_baseLive", BF);
        if (onOpen == null || onSave == null || onNew == null) return "roundtrip: File ops not found (renamed?)";
        if (syncMode == null) return "roundtrip: SyncBaseTilesToMode not found (renamed?)";
        if (captureLayout == null) return "roundtrip: CaptureLayout not found (renamed?)";
        if (currentPathField == null || baseLiveField == null) return "roundtrip: root fields not found (renamed?)";

        if (!windows.Has(ORDER_WINDOW_ID)) return "roundtrip: Order ticket window not adopted (renamed?)";
        if (!windows.Has(CELL_WINDOW_ID)) return "roundtrip: cell window region_001 not adopted (renamed?)";

        // ── JOURNEY-LAYOUT-01: open the document baseline. The fixture <strategy>.json carries ONLY a
        //   scenario key (the v19 shape), so OnFileOpen does a BARE open (layoutOk=false → ApplyLayout
        //   skipped); _currentLayoutPath binds to the .py and the live geometry stays at the canonical
        //   build default — that default IS the baseline we will deviate from. ──
        string py = Path.Combine(TempRoot, "doc", "strat.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"),
            new List<string> { "7203.TSE" });
        if (LayoutSidecarStore.TryReadLayout(py, out _))
            return "JOURNEY-LAYOUT-01: precondition — scenario-only fixture unexpectedly carries a layout key";

        root.SetFileDialog(new StubFileDialog { NextResult = py });
        onOpen.Invoke(root, null);
        var boundPath01 = currentPathField.GetValue(root) as string;
        if (string.IsNullOrEmpty(boundPath01) ||
            !string.Equals(Path.GetFullPath(boundPath01), Path.GetFullPath(py), StringComparison.OrdinalIgnoreCase))
            return "JOURNEY-LAYOUT-01: open did not bind _currentLayoutPath to the fixture .py (got '" +
                   boundPath01 + "', want '" + Path.GetFullPath(py) + "')";
        if ((bool)baseLiveField.GetValue(root))
            return "JOURNEY-LAYOUT-01: build did not start in Replay shape (_baseLive should be false)";

        // base-only Hakoniwa: drop the seeded chart tile so per-mode BASE swaps honor cleanly (the honor
        // rule is base-id-SET equality — IsValidForMode; a chart tile is universe-owned noise here). The
        // on-disk scenario key still carries 7203.TSE (used for the coexist assert at step 7).
        foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);

        var baseWinPos = windows.RectOf(ORDER_WINDOW_ID).anchoredPosition;
        var baseCellPos = coordinator.CapturePositions()[0];

        // saved (non-default) targets for the 5 dimensions.
        var savedCanvas = new CanvasView(120f, -80f, 1.6f);
        var savedWinPos = baseWinPos + new Vector2(64f, -48f);
        var savedCellPos = baseCellPos + new Vector2(40f, 56f);
        var replayBase = new[] { "startup", "orders", "buying_power", "positions", "run_result" };
        var liveBase = new[] { "run_result", "orders", "positions", "buying_power" };

        // ── JOURNEY-LAYOUT-02: reorder Hakoniwa base tiles in Replay → a non-canonical (but valid-set)
        //   Replay profile. canonical order would re-sort to a no-op (#59 教訓), so swap to deviate. ──
        if (!hako.Swap(hako.SlotOf("buying_power"), hako.SlotOf("orders")))
            return "JOURNEY-LAYOUT-02: Replay base swap failed";
        string e = AssertBaseOrder(hako, replayBase); if (e != null) return "JOURNEY-LAYOUT-02 (capture): " + e;

        // ── JOURNEY-LAYOUT-03: move the Order ticket floating window (canvas-logical delta). ──
        windows.MoveByLogical(ORDER_WINDOW_ID, savedWinPos - baseWinPos);
        if (!V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, savedWinPos, EPS))
            return "JOURNEY-LAYOUT-03 (capture): Order window did not move to the non-default rect";
        if (V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, baseWinPos, EPS))
            return "JOURNEY-LAYOUT-03 (capture): Order window rect is still the baseline (perturb no-op)";

        // ── JOURNEY-LAYOUT-04: pan/zoom the infinite canvas to a non-identity view. ──
        canvas.ApplyView(savedCanvas);
        if (!CanvasView.Approx(canvas.CaptureView(), savedCanvas, EPS))
            return "JOURNEY-LAYOUT-04 (capture): canvas view did not take the non-default pan/zoom";
        if (CanvasView.Approx(canvas.CaptureView(), CanvasView.Identity(), EPS))
            return "JOURNEY-LAYOUT-04 (capture): canvas view is still identity (perturb no-op)";

        // ── JOURNEY-LAYOUT-05: move the notebook cell window (region_001) to a non-default position. ──
        windows.MoveByLogical(CELL_WINDOW_ID, savedCellPos - baseCellPos);
        if (!V2Approx(coordinator.CapturePositions()[0], savedCellPos, EPS))
            return "JOURNEY-LAYOUT-05 (capture): cell position did not move to the non-default point";
        if (V2Approx(coordinator.CapturePositions()[0], baseCellPos, EPS))
            return "JOURNEY-LAYOUT-05 (capture): cell position is still the baseline (perturb no-op)";

        // ── JOURNEY-LAYOUT-06: flip to Live (stashes the Replay profile) and give Live its OWN distinct
        //   base order. canvas/window/cell are mode-FLAT (shared) so the flip leaves them intact. ──
        syncMode.Invoke(root, new object[] { true });
        if (hako.SlotOf("startup") >= 0) return "JOURNEY-LAYOUT-06: startup tile must leave on the Live flip";
        if (!hako.Swap(hako.SlotOf("run_result"), hako.SlotOf("buying_power")))
            return "JOURNEY-LAYOUT-06: Live base swap failed";
        e = AssertBaseOrder(hako, liveBase); if (e != null) return "JOURNEY-LAYOUT-06 (Live capture): " + e;
        if (!CanvasView.Approx(canvas.CaptureView(), savedCanvas, EPS))
            return "JOURNEY-LAYOUT-06: the mode flip disturbed the (mode-flat) canvas view";
        if (!V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, savedWinPos, EPS))
            return "JOURNEY-LAYOUT-06: the mode flip disturbed the (mode-flat) Order window";

        // steps 2-6 aggregation: CaptureLayout() must fold ALL 5 dimensions into the LayoutDocument (the
        // cell window is EXCLUDED from floatingWindows — single source of truth is cellPositions).
        var capDoc = captureLayout.Invoke(root, null) as LayoutDocument;
        if (capDoc == null) return "JOURNEY-LAYOUT-02..06: CaptureLayout returned null";
        if (capDoc.canvasView == null || !CanvasView.Approx(capDoc.canvasView, savedCanvas, EPS))
            return "JOURNEY-LAYOUT-04 (doc): canvasView not aggregated into the document";
        var capWin = capDoc.FindWindow(ORDER_WINDOW_ID);
        if (capWin == null || !V2Approx(new Vector2(capWin.x, capWin.y), savedWinPos, EPS))
            return "JOURNEY-LAYOUT-03 (doc): Order window not aggregated into floatingWindows";
        if (capDoc.FindWindow(CELL_WINDOW_ID) != null)
            return "JOURNEY-LAYOUT-05 (doc): cell window leaked into floatingWindows (must live in cellPositions only)";
        if (capDoc.cellPositions == null || capDoc.cellPositions.Count < 1 ||
            !V2Approx(new Vector2(capDoc.cellPositions[0].x, capDoc.cellPositions[0].y), savedCellPos, EPS))
            return "JOURNEY-LAYOUT-05 (doc): cellPositions not aggregated into the document";
        if (capDoc.hakoniwaProfiles == null ||
            capDoc.hakoniwaProfiles.replay == null || capDoc.hakoniwaProfiles.replay.panels == null ||
            capDoc.hakoniwaProfiles.replay.panels.Count == 0 ||
            capDoc.hakoniwaProfiles.live == null || capDoc.hakoniwaProfiles.live.panels == null ||
            capDoc.hakoniwaProfiles.live.panels.Count == 0)
            return "JOURNEY-LAYOUT-06 (doc): per-mode hakoniwaProfiles not aggregated (both modes expected)";

        // ── JOURNEY-LAYOUT-07: Save merges the layout key into <strategy>.json; the scenario key survives. ──
        onSave.Invoke(root, null);
        if (!LayoutSidecarStore.TryReadLayout(py, out _))
            return "JOURNEY-LAYOUT-07: layout key missing after save (TryWriteLayout failed?)";
        var savedScn = ScenarioSidecarStore.ReadScenario(py);
        if (savedScn == null || savedScn.Instruments.Count != 1 || savedScn.Instruments[0] != "7203.TSE")
            return "JOURNEY-LAYOUT-07: save CLOBBERED the scenario key (coexist merge broken)";

        // ── JOURNEY-LAYOUT-08: File→New dirties the document identity (path/notebook). It does NOT reset
        //   geometry — so this is the path/notebook 汚し only; the geometry 汚し is explicit below. ──
        onNew.Invoke(root, null);
        if ((currentPathField.GetValue(root) as string) != "")
            return "JOURNEY-LAYOUT-08: File→New did not drop _currentLayoutPath to untitled";

        // geometry 汚し (round-trip non-vacuity): perturb each geometry dimension to a THIRD value, asserted
        // distinct from the SAVED value, so the upcoming restore can only pass by re-reading the disk.
        canvas.ApplyView(CanvasView.Identity());
        if (CanvasView.Approx(canvas.CaptureView(), savedCanvas, EPS))
            return "JOURNEY-LAYOUT-08 (dirty): canvas perturb was a no-op (still the saved view)";
        hako.Reorder(HakoniwaBaseTiles.Kinds(true));   // canonical Live order ≠ liveBase
        if (AssertBaseOrder(hako, liveBase) == null)
            return "JOURNEY-LAYOUT-08 (dirty): tile perturb was a no-op (still the saved Live order)";
        windows.MoveByLogical(ORDER_WINDOW_ID, new Vector2(-220f, 140f));
        if (V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, savedWinPos, EPS))
            return "JOURNEY-LAYOUT-08 (dirty): window perturb was a no-op (still the saved rect)";
        windows.MoveByLogical(CELL_WINDOW_ID, new Vector2(-160f, -90f));
        if (V2Approx(coordinator.CapturePositions()[0], savedCellPos, EPS))
            return "JOURNEY-LAYOUT-08 (dirty): cell perturb was a no-op (still the saved position)";

        // ── JOURNEY-LAYOUT-09: reopen the SAME document — now the layout key is present, so TryReadLayout
        //   is true and OnFileOpen drives ApplyLayout + coordinator.Open(cellPositions). ──
        if (!LayoutSidecarStore.TryReadLayout(py, out _))
            return "JOURNEY-LAYOUT-09: the saved sidecar no longer reads a valid layout key";
        root.SetFileDialog(new StubFileDialog { NextResult = py });
        onOpen.Invoke(root, null);
        var boundPath09 = currentPathField.GetValue(root) as string;
        if (string.IsNullOrEmpty(boundPath09) ||
            !string.Equals(Path.GetFullPath(boundPath09), Path.GetFullPath(py), StringComparison.OrdinalIgnoreCase))
            return "JOURNEY-LAYOUT-09: reopen did not rebind _currentLayoutPath";

        // ── JOURNEY-LAYOUT-10: canvas pan/zoom restored to the saved view. ──
        if (!CanvasView.Approx(canvas.CaptureView(), savedCanvas, EPS))
            return "JOURNEY-LAYOUT-10: canvas view NOT restored on reopen (got " +
                   canvas.CaptureView().panX + "," + canvas.CaptureView().panY + "," + canvas.CaptureView().zoom + ")";

        // ── JOURNEY-LAYOUT-11: active-mode (Live) base tile order restored (validated honor). A chart tile
        //   re-seeded from the scenario key sits AFTER the base region (AssertBaseOrder tolerates it). ──
        e = AssertBaseOrder(hako, liveBase); if (e != null) return "JOURNEY-LAYOUT-11 (Live restore): " + e;

        // ── JOURNEY-LAYOUT-12: floating window restored in place (rect). It is the only non-cell window so
        //   its zOrder is trivially 0 (front); the meaningful restored dimension here is the rect. ──
        if (!V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, savedWinPos, EPS))
            return "JOURNEY-LAYOUT-12: Order window rect NOT restored on reopen";

        // ── JOURNEY-LAYOUT-05 (restore half): cell position restored via coordinator.Open(cellPositions). ──
        if (!V2Approx(coordinator.CapturePositions()[0], savedCellPos, EPS))
            return "JOURNEY-LAYOUT-05 (restore): cell position NOT restored on reopen";

        // ── JOURNEY-LAYOUT-13: per-mode profile independence + restore. Active=Live already restored to
        //   liveBase; flipping to Replay must yield the DISTINCT replayBase. NON-VACUOUS (S12 reasoning):
        //   the doc's `panels` mirror == the Live order, so getting replayBase back can ONLY come from the
        //   persisted hakoniwaProfiles.replay sub-profile (a dropped field would seed Replay from the Live
        //   mirror → liveBase, failing here). ──
        syncMode.Invoke(root, new object[] { false });
        e = AssertBaseOrder(hako, replayBase); if (e != null) return "JOURNEY-LAYOUT-13 (Replay restore — distinct per-mode): " + e;
        syncMode.Invoke(root, new object[] { true });
        e = AssertBaseOrder(hako, liveBase); if (e != null) return "JOURNEY-LAYOUT-13 (Live re-flip): " + e;

        return null;
    }

    // ── 2. JOURNEY-LAYOUT-14: a scenario-only / corrupt sidecar open is a BARE open — ApplyLayout is
    //   SKIPPED (layoutOk=false, findings 0048 D4), so the live geometry is NOT wiped. NON-VACUOUS:
    //   Section1 proved the SAME OnFileOpen DOES restore geometry when a layout key is present; here, with
    //   no layout key, it must leave the perturbed geometry untouched. ──
    // Covers: JOURNEY-LAYOUT-14
    static string Section2_NoWipeBareOpen()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "no-wipe: BackcastWorkspaceRoot missing in scene";

        var hako = ty.GetField("_hako", BF)?.GetValue(root) as HakoniwaController;
        var canvas = ty.GetField("_canvas", BF)?.GetValue(root) as InfiniteCanvasController;
        var windows = ty.GetField("_windows", BF)?.GetValue(root) as FloatingWindowController;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        if (hako == null || canvas == null || windows == null || scenario == null) return "no-wipe: root seams not built (renamed?)";
        if (onOpen == null) return "no-wipe: OnFileOpen not found (renamed?)";
        if (!windows.Has(ORDER_WINDOW_ID)) return "no-wipe: Order ticket window not adopted (renamed?)";

        // build a live geometry the user "owns": a non-canonical tile order + a moved window + a panned canvas.
        foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);
        if (!hako.Swap(hako.SlotOf("buying_power"), hako.SlotOf("orders"))) return "no-wipe: precondition base swap failed";
        canvas.ApplyView(new CanvasView(200f, 50f, 2.0f));
        windows.MoveByLogical(ORDER_WINDOW_ID, new Vector2(33f, 22f));
        var beforeOrder = new List<string>(hako.Order);
        var beforeCanvas = canvas.CaptureView();
        var beforeWin = windows.RectOf(ORDER_WINDOW_ID).anchoredPosition;

        // open a scenario-only sidecar (no layout key) → bare open.
        string py = Path.Combine(TempRoot, "bare", "bare.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "y = 2\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite("2025-03-03", "2025-03-07", "Daily", "1000000"), new List<string>());
        if (LayoutSidecarStore.TryReadLayout(py, out _))
            return "JOURNEY-LAYOUT-14: precondition — scenario-only sidecar unexpectedly carries a layout key";

        root.SetFileDialog(new StubFileDialog { NextResult = py });
        onOpen.Invoke(root, null);

        // the geometry must be UNCHANGED (no-wipe).
        if (!CanvasView.Approx(canvas.CaptureView(), beforeCanvas, EPS))
            return "JOURNEY-LAYOUT-14: bare open WIPED the canvas view";
        if (!V2Approx(windows.RectOf(ORDER_WINDOW_ID).anchoredPosition, beforeWin, EPS))
            return "JOURNEY-LAYOUT-14: bare open moved the Order window";
        if (hako.Order.Count != beforeOrder.Count)
            return "JOURNEY-LAYOUT-14: bare open changed the tile count";
        for (int i = 0; i < beforeOrder.Count; i++)
            if (hako.Order[i] != beforeOrder[i])
                return "JOURNEY-LAYOUT-14: bare open WIPED the tile order (slot " + i + ")";
        return null;
    }

    // ── 3. JOURNEY-LAYOUT-15: Save As forks the document to a NEW (.py + .json) pair carrying BOTH the
    //   scenario and layout keys; the OLD .json is left untouched and _currentLayoutPath rebinds to new. ──
    // Covers: JOURNEY-LAYOUT-15
    static string Section3_SaveAsFork()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "saveas: BackcastWorkspaceRoot missing in scene";

        var canvas = ty.GetField("_canvas", BF)?.GetValue(root) as InfiniteCanvasController;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        var onSaveAs = ty.GetMethod("OnFileSaveAs", BF);
        var currentPathField = ty.GetField("_currentLayoutPath", BF);
        if (canvas == null) return "saveas: _canvas not built (renamed?)";
        if (onOpen == null || onSaveAs == null) return "saveas: File ops not found (renamed?)";
        if (currentPathField == null) return "saveas: _currentLayoutPath not found (renamed?)";

        // open an existing document (scenario-only) so there is a bound scenario buffer to fork.
        string oldPy = Path.Combine(TempRoot, "fork", "old.py");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPy));
        File.WriteAllText(oldPy, "z = 3\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            oldPy, new StartupParamsForWrite("2025-04-01", "2025-04-05", "Daily", "700000"),
            new List<string> { "6758.TSE" });
        root.SetFileDialog(new StubFileDialog { NextResult = oldPy });
        onOpen.Invoke(root, null);
        string oldJson = ScenarioSidecarStore.SidecarPathFor(oldPy);
        string oldJsonBefore = File.ReadAllText(oldJson);

        // perturb a layout dimension so the forked layout key is non-trivial.
        var forkCanvas = new CanvasView(77f, 33f, 1.4f);
        canvas.ApplyView(forkCanvas);

        string newPy = Path.Combine(TempRoot, "fork", "new.py");
        root.SetFileDialog(new StubFileDialog { NextResult = newPy });
        onSaveAs.Invoke(root, null);

        // the new pair exists with BOTH keys; _currentLayoutPath rebinds.
        if (!File.Exists(newPy)) return "JOURNEY-LAYOUT-15: Save As did not write the new .py";
        string newJson = ScenarioSidecarStore.SidecarPathFor(newPy);
        if (!File.Exists(newJson)) return "JOURNEY-LAYOUT-15: Save As did not write the new .json";
        if (!LayoutSidecarStore.TryReadLayout(newPy, out var newDoc) || newDoc == null)
            return "JOURNEY-LAYOUT-15: new sidecar has no layout key";
        if (newDoc.canvasView == null || !CanvasView.Approx(newDoc.canvasView, forkCanvas, EPS))
            return "JOURNEY-LAYOUT-15: forked layout did not carry the perturbed canvas view";
        var newScn = ScenarioSidecarStore.ReadScenario(newPy);
        if (newScn == null || newScn.Instruments.Count != 1 || newScn.Instruments[0] != "6758.TSE")
            return "JOURNEY-LAYOUT-15: forked .json missing the scenario key (Save As did not fork scenario+layout)";
        var boundPath15 = currentPathField.GetValue(root) as string;
        if (string.IsNullOrEmpty(boundPath15) ||
            !string.Equals(Path.GetFullPath(boundPath15), Path.GetFullPath(newPy), StringComparison.OrdinalIgnoreCase))
            return "JOURNEY-LAYOUT-15: _currentLayoutPath not rebound to the new .py";

        // the OLD pair is untouched (independent fork).
        if (File.ReadAllText(oldJson) != oldJsonBefore)
            return "JOURNEY-LAYOUT-15: Save As mutated the OLD .json (must be independent)";
        return null;
    }

    // ---- helpers ----
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

    // base tile order assert (BackcastWorkspaceProbe parity): wanted[i] sits at slot i, chart tiles after.
    static string AssertBaseOrder(HakoniwaController hako, string[] wanted)
    {
        for (int i = 0; i < wanted.Length; i++)
            if (hako.SlotOf(wanted[i]) != i)
                return wanted[i] + " expected slot " + i + ", got " + hako.SlotOf(wanted[i]) +
                       " (order [" + string.Join(",", hako.Order) + "])";
        for (int i = 0; i < hako.Order.Count; i++)
            if (HakoniwaBaseTiles.IsChartId(hako.Order[i]) && i < wanted.Length)
                return "chart tile " + hako.Order[i] + " sits inside the base region";
        return null;
    }

    static bool V2Approx(Vector2 a, Vector2 b, float eps) =>
        Mathf.Abs(a.x - b.x) <= eps && Mathf.Abs(a.y - b.y) <= eps;

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
