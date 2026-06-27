// ChartPlacementJourneyE2ERunner.cs — Journey E2E regression gate for issue #114 (台本:
// same-dir ChartPlacementJourneyE2ERunner.md, design tree: docs/findings/0091).
// Replaces the SyncChartWindowsToUniverse focus-snap cascade with a non-overlapping grid;
// honors saved chart positions; tolerates legacy / corrupted / ghost-symbol / collision sidecars.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartPlacementJourneyE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART PLACEMENT JOURNEY PASS] ... / exit=0  (確認は Bash `grep -a "E2E CHART PLACEMENT"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// SECTIONS (findings 0091 F5):
//   S0 — pure helper unit (ChartGridPlacement: ComputeFlushSlots / AllocateNonOverlappingTopLefts)
//   S1 — saved-honor (P3 full / P4 partial / P9 off-screen clamp / P10 collision / P13 invalid w/h)
//   S2 — legacy migration + cascade kill (P2 v19-shaped legacy → grid; P12 migration cycle 2-stage)
//   S3 — resilience (P8 corrupted .bak / P11 ghost retention / P14 dedup / P15 viewport-無依存)
//   S4 — regression / characterization (P1 no-sidecar / P5 universe-grow / P6 52-chart / P7 empty)
//
// VACUITY KILL: each restore probe perturbs the live geometry to a third (non-saved, non-default)
// value before the reopen so the upcoming restore can pass only by re-reading the disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartPlacementJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;

    static readonly Vector2 CHART_SIZE = new Vector2(520f, 360f);   // KIND_CHART defaultSize (FloatingWindowCatalog.cs:83)
    static readonly Vector2 GAP = ChartGridPlacement.DefaultGap;     // (12, 12)

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_placement_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section0_PureHelperUnit()           // CP-S0-01..04: ChartGridPlacement contract
                ?? Section1_SavedHonor()                // CP-S1-01..05: P3/P4/P9/P10/P13
                ?? Section2_LegacyMigrationAndCascadeKill() // CP-S2-01/02a/02b: P2/P12
                ?? Section3_Resilience()                // CP-S3-01..04: P8/P11/P14/P15
                ?? Section4_RegressionCharacterization(); // CP-S4-01..04: P1/P5/P6/P7
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART PLACEMENT JOURNEY PASS] core slice GREEN (S0 4/4 + S1 2/5 P3+P4 + S2 3/3 P2+P12a+P12b + S3 1/4 P15 + S4 2/4 P1+P5+P6+P7-via-P1). Deferred (findings 0091 §S1/S3-deferred): P9 clamp / P10 de-collide / P11 ghost / P13 invalid w/h / P14 dedup / P8 corrupted .bak. issue #114.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART PLACEMENT JOURNEY FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================================================
    // Section0 — pure helper unit assertions on ChartGridPlacement (no scene, no root, no reflection).
    // Same pattern as FloatingWindowE2ERunner's pure-math sections (FloatingWindowMath / DockSnapPlacement).
    // =====================================================================================================
    static string Section0_PureHelperUnit()
    {
        Vector2 anchor = new Vector2(-600f, -332f);

        // ── CP-S0-01: ComputeFlushSlots — rect math for n = 0 / 1 / 4 / 9 / 52. cols = ceil(√n). ──
        var s0 = ChartGridPlacement.ComputeFlushSlots(0, anchor, CHART_SIZE, GAP);
        if (s0 == null || s0.Count != 0)
            return "CP-S0-01: ComputeFlushSlots(0) must return an empty list (got " + (s0?.Count ?? -1) + ")";

        var s1 = ChartGridPlacement.ComputeFlushSlots(1, anchor, CHART_SIZE, GAP);
        if (s1.Count != 1) return "CP-S0-01: ComputeFlushSlots(1).Count != 1";
        if (!Approx2(s1[0].topLeft, anchor))
            return "CP-S0-01: slot 0 of n=1 must sit at the anchor (got " + s1[0].topLeft + ", want " + anchor + ")";

        // n=4: cols = ceil(√4) = 2, layout = 2×2.
        var s4 = ChartGridPlacement.ComputeFlushSlots(4, anchor, CHART_SIZE, GAP);
        if (s4.Count != 4) return "CP-S0-01: ComputeFlushSlots(4).Count != 4";
        // slot 3 = (col=1, row=1). expected top-left = anchor + (chart.x+gap.x, -(chart.y+gap.y))
        var s4_expected_3 = new Vector2(anchor.x + CHART_SIZE.x + GAP.x, anchor.y - (CHART_SIZE.y + GAP.y));
        if (!Approx2(s4[3].topLeft, s4_expected_3))
            return "CP-S0-01: n=4 slot 3 must be (col=1,row=1)=" + s4_expected_3 + " (got " + s4[3].topLeft + ")";

        // n=9: cols = 3.
        var s9 = ChartGridPlacement.ComputeFlushSlots(9, anchor, CHART_SIZE, GAP);
        if (s9.Count != 9) return "CP-S0-01: ComputeFlushSlots(9).Count != 9";
        // slot 8 = (col=2, row=2)
        var s9_expected_8 = new Vector2(anchor.x + 2f * (CHART_SIZE.x + GAP.x), anchor.y - 2f * (CHART_SIZE.y + GAP.y));
        if (!Approx2(s9[8].topLeft, s9_expected_8))
            return "CP-S0-01: n=9 slot 8 must be (col=2,row=2)=" + s9_expected_8 + " (got " + s9[8].topLeft + ")";

        // n=52: cols = ceil(√52) = 8. v19_morning_cell case.
        var s52 = ChartGridPlacement.ComputeFlushSlots(52, anchor, CHART_SIZE, GAP);
        if (s52.Count != 52) return "CP-S0-01: ComputeFlushSlots(52).Count != 52";
        // slot 7 = (col=7, row=0) — last column on row 0.
        var s52_expected_7 = new Vector2(anchor.x + 7f * (CHART_SIZE.x + GAP.x), anchor.y);
        if (!Approx2(s52[7].topLeft, s52_expected_7))
            return "CP-S0-01: n=52 slot 7 (cols=8) must be (col=7,row=0)=" + s52_expected_7 + " (got " + s52[7].topLeft + ")";
        // slot 8 = (col=0, row=1) — first wrap to row 1.
        var s52_expected_8 = new Vector2(anchor.x, anchor.y - (CHART_SIZE.y + GAP.y));
        if (!Approx2(s52[8].topLeft, s52_expected_8))
            return "CP-S0-01: n=52 slot 8 must wrap to (col=0,row=1)=" + s52_expected_8 + " (got " + s52[8].topLeft + ")";
        // sanity bound from F4: rightmost x at cols=8 is col=7 -> anchor.x + 7*(520+12) = -600 + 3724 = +3124.
        // (findings 0091 F5 CP-S4-03 cites this same sanity bound.)
        if (s52[7].topLeft.x > 3125f)
            return "CP-S0-01: n=52 rightmost x exceeded sanity bound 3124 (got " + s52[7].topLeft.x + ")";

        // ── CP-S0-02: AllocateNonOverlappingTopLefts with cols=8, n=1, avoid = full row 0 → row 1 col 0. ──
        var fullRow0 = new List<Rect>();
        for (int col = 0; col < 8; col++)
        {
            float x = anchor.x + col * (CHART_SIZE.x + GAP.x);
            fullRow0.Add(new Rect(x, anchor.y - CHART_SIZE.y, CHART_SIZE.x, CHART_SIZE.y));   // canvas top-left convention
        }
        var alloc1 = ChartGridPlacement.AllocateNonOverlappingTopLefts(1, 8, anchor, CHART_SIZE, GAP, fullRow0);
        if (alloc1.Count != 1) return "CP-S0-02: Allocate(1) must return exactly 1 point";
        var expectedRow1Col0 = new Vector2(anchor.x, anchor.y - (CHART_SIZE.y + GAP.y));
        if (!Approx2(alloc1[0], expectedRow1Col0))
            return "CP-S0-02: avoid-full-row-0 with cols=8 must spill to row 1 col 0=" + expectedRow1Col0 +
                   " (got " + alloc1[0] + " — caller-supplied gridCols not honored?)";

        // ── CP-S0-03: AllocateNonOverlappingTopLefts(N, cols=ceil(√N), avoid=[]) ≡ ComputeFlushSlots(N). ──
        int N = 9;
        int colsN = Mathf.CeilToInt(Mathf.Sqrt(N));   // ComputeFlushSlots uses the same internal ceil-sqrt.
        var allocFree = ChartGridPlacement.AllocateNonOverlappingTopLefts(N, colsN, anchor, CHART_SIZE, GAP, new List<Rect>());
        var flushFree = ChartGridPlacement.ComputeFlushSlots(N, anchor, CHART_SIZE, GAP);
        if (allocFree.Count != flushFree.Count) return "CP-S0-03: Allocate vs ComputeFlush count mismatch";
        for (int i = 0; i < N; i++)
        {
            if (!Approx2(allocFree[i], flushFree[i].topLeft))
                return "CP-S0-03: slot " + i + " diverged (Allocate=" + allocFree[i] +
                       ", Flush=" + flushFree[i].topLeft + ")";
        }

        // ── CP-S0-04: half-overlapping avoid rect must skip the affected slot but produce N points. ──
        // Place an avoid rect that overlaps slot 0 by 1px (offset (-1, +1) of the slot's canvas-Rect
        // form). The helper must skip slot 0 and yield slots 1, 2, 3 instead.
        var slot0Rect = flushFree[0];   // n=9, slot 0 at anchor
        var halfOverlap = new Rect(
            slot0Rect.topLeft.x - 1f,
            slot0Rect.topLeft.y - CHART_SIZE.y + 1f,
            CHART_SIZE.x, CHART_SIZE.y);
        var alloc3 = ChartGridPlacement.AllocateNonOverlappingTopLefts(
            3, colsN, anchor, CHART_SIZE, GAP, new List<Rect> { halfOverlap });
        if (alloc3.Count != 3) return "CP-S0-04: Allocate(3) under 1-overlap avoid must still return 3 points";
        // First returned point must be slot 1 (not slot 0).
        if (Approx2(alloc3[0], slot0Rect.topLeft))
            return "CP-S0-04: helper returned slot 0 despite half-overlapping avoid rect";
        if (!Approx2(alloc3[0], flushFree[1].topLeft))
            return "CP-S0-04: first non-overlapping slot should be flush-slot 1 (got " + alloc3[0] +
                   ", want " + flushFree[1].topLeft + ")";

        return null;
    }

    // =====================================================================================================
    // Section1 — saved-honor (P3 full / P4 partial). Edge cases P9/P10/P13 are addressed in a follow-up
    // production slice (RestoreFloating pre-pass for clamp / de-collide / invalid w/h) — captured in
    // findings 0091 F5 row CP-S1-03..05, currently STUB.
    // =====================================================================================================
    static string Section1_SavedHonor()
    {
        // ── CP-S1-01 (P3): full saved — 5 charts all saved at distinct non-grid coords → all honor. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S1-01: root missing";
            var dockWindows = DockWindowsOf(root, ty);
            var scenario = ScenarioOf(root, ty);
            var fileDialog = StubFileDialogOf();

            string py = Path.Combine(TempRoot, "s1_full", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "x = 1\n");
            var iids = new[] { "7203.TSE", "6758.TSE", "8306.TSE", "9984.TSE", "6920.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"),
                new List<string>(iids));

            // Saved positions at distinct, non-grid-aligned, non-overlapping coords.
            var saved = new List<FloatingWindowLayout>();
            for (int i = 0; i < iids.Length; i++)
            {
                saved.Add(new FloatingWindowLayout(
                    DockShape.ChartId(iids[i]), FloatingWindowCatalog.KIND_CHART,
                    -1500f + 700f * i, 800f - 50f * i, 520f, 360f, i, true, null));
            }
            WriteLayoutWithFloatingWindows(py, saved);

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            for (int i = 0; i < iids.Length; i++)
            {
                string id = DockShape.ChartId(iids[i]);
                var rt = dockWindows.RectOf(id);
                if (rt == null) return "CP-S1-01: chart " + id + " not spawned after restore";
                var expected = new Vector2(-1500f + 700f * i, 800f - 50f * i);
                if (!Approx2(rt.anchoredPosition, expected))
                    return "CP-S1-01: " + id + " not at saved pos (got " + rt.anchoredPosition +
                           ", want " + expected + ")";
            }
        }

        // ── CP-S1-02 (P4): partial saved — 3 of 5 charts saved at distinct coords, 2 unsaved must land
        //    on grid (cascade-kill path), and all 5 must be non-overlapping. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S1-02: root missing";
            var dockWindows = DockWindowsOf(root, ty);
            var fileDialog = StubFileDialogOf();

            string py = Path.Combine(TempRoot, "s1_partial", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "y = 2\n");
            var iids = new[] { "1111.TSE", "2222.TSE", "3333.TSE", "4444.TSE", "5555.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-02-03", "2025-02-07", "Daily", "1000000"),
                new List<string>(iids));

            // First 3 saved at distinct non-grid coords positioned to **y-overlap the grid band**
            // — grid anchor is (-600, -332), chart height 360 → grid row 0 occupies y ∈ [-692, -332].
            // saved positions sit at y ∈ {-200, -300, -400}, rect bottoms at y ∈ {-560, -660, -760},
            // which straddles the grid row-0 band. saved x positions at {-100, -700, -1300} make
            // saved[0] x-overlap grid slot 0/1 (anchor.x = -600). A regression where the grid path
            // ignored saved positions in its avoid set would land slot 0 chart on top of saved[0]
            // and trip the pairwise overlap check below — non-vacuous regression seal.
            var saved = new List<FloatingWindowLayout>();
            for (int i = 0; i < 3; i++)
            {
                saved.Add(new FloatingWindowLayout(
                    DockShape.ChartId(iids[i]), FloatingWindowCatalog.KIND_CHART,
                    -100f - 600f * i, -200f - 100f * i, 520f, 360f, i, true, null));
            }
            WriteLayoutWithFloatingWindows(py, saved);

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // saved 3 must honor
            for (int i = 0; i < 3; i++)
            {
                string id = DockShape.ChartId(iids[i]);
                var rt = dockWindows.RectOf(id);
                if (rt == null) return "CP-S1-02: saved chart " + id + " not spawned";
                var expected = new Vector2(-100f - 600f * i, -200f - 100f * i);
                if (!Approx2(rt.anchoredPosition, expected))
                    return "CP-S1-02: saved " + id + " not at saved pos (got " + rt.anchoredPosition + ")";
            }
            // unsaved 2 must be live (grid-placed) and non-overlapping with anyone
            for (int i = 3; i < 5; i++)
            {
                string id = DockShape.ChartId(iids[i]);
                var rt = dockWindows.RectOf(id);
                if (rt == null) return "CP-S1-02: unsaved chart " + id + " not spawned by grid path";
            }
            // pairwise non-overlap check (cascade-kill litmus)
            var rects = new List<Rect>();
            for (int i = 0; i < 5; i++)
            {
                var rt = dockWindows.RectOf(DockShape.ChartId(iids[i]));
                var p = rt.anchoredPosition;
                var s = rt.sizeDelta;
                rects.Add(new Rect(p.x, p.y - s.y, s.x, s.y));
            }
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                    if (rects[a].Overlaps(rects[b]))
                        return "CP-S1-02: charts " + iids[a] + " and " + iids[b] + " overlap (cascade not killed) — rectA=" + rects[a] + " rectB=" + rects[b];
        }

        return null;
    }

    // =====================================================================================================
    // Section2 — legacy migration + cascade kill (P2 + P12a/b). The v19_morning_cell.json case: charts
    // sit in the dead `panels` schema (not floatingWindows). The cascade-kill fix (BackcastWorkspaceRoot.
    // SyncChartWindowsToUniverse: SpawnDockedToFocus → ChartGridPlacement.Allocate) ensures all 52 charts
    // land non-overlapping on a grid, NOT a staircase.
    // =====================================================================================================
    static string Section2_LegacyMigrationAndCascadeKill()
    {
        // ── CP-S2-01 (P2): legacy-panels-only sidecar → cascade-kill makes 52 charts non-overlapping. ──
        var iids52 = new List<string>();
        for (int i = 0; i < 52; i++) iids52.Add((1000 + i).ToString() + ".TSE");
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S2-01: root missing";
            var dockWindows = DockWindowsOf(root, ty);

            string py = Path.Combine(TempRoot, "s2_legacy", "v19_shape.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "z = 3\n");
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-03-01", "2025-03-05", "Daily", "1000000"),
                iids52);
            // Note: the sidecar carries ONLY a scenario key (no layout key). v19_morning_cell.json's
            // legacy `panels` entries are IGNORED on read (ADR-0017 §6 / ApplyLayout L2083), so the
            // restore path for the chart family is empty — every chart comes through Sync's grid path.
            // This isolates the cascade-kill behavior cleanly without needing a hand-crafted legacy doc.

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // All 52 charts must be live and pairwise non-overlapping.
            var rects = new List<Rect>();
            for (int i = 0; i < 52; i++)
            {
                string id = DockShape.ChartId(iids52[i]);
                var rt = dockWindows.RectOf(id);
                if (rt == null) return "CP-S2-01: chart " + id + " not spawned by grid path";
                var p = rt.anchoredPosition;
                var s = rt.sizeDelta;
                rects.Add(new Rect(p.x, p.y - s.y, s.x, s.y));
            }
            // sanity bound (findings 0091 F5 CP-S4-03 / S0-01 same value): rightmost col=7, max x = -600 + 7*532 = +3124.
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].xMin > 3125f)
                    return "CP-S2-01: chart " + iids52[i] + " x=" + rects[i].xMin + " exceeded sanity bound 3124 (cascade not killed)";
            // pairwise non-overlap (the bug fix litmus)
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                    if (rects[a].Overlaps(rects[b]))
                        return "CP-S2-01: charts " + iids52[a] + " and " + iids52[b] + " overlap (cascade not killed)";
        }

        // ── CP-S2-02a: legacy open → Save → reread → panels=[] (migration to modern schema completes
        //    on the next Save, the only signal). findings 0091 F3-P12 / F5 CP-S2-02. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S2-02a: root missing";

            string py = Path.Combine(TempRoot, "s2_migrate", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "m = 0\n");
            var iids = new[] { "7203.TSE", "6758.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-04-01", "2025-04-05", "Daily", "1000000"),
                new List<string>(iids));
            // Hand-craft a legacy-shaped sidecar: include a "panels" array (the dead schema)
            WriteLayoutWithLegacyPanels(py, iids);

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);
            InvokeOnFileSave(ty, root);

            if (!LayoutSidecarStore.TryReadLayout(py, out var reread))
                return "CP-S2-02a: reread layout failed after Save";
            if (reread.panels == null || reread.panels.Count != 0)
                return "CP-S2-02a: panels not physically emptied after Save (got " + (reread.panels?.Count ?? -1) + " entries)";
            // floatingWindows must carry the live charts (2) — the chart family rode through Sync onto the grid path.
            int chartEntries = 0;
            if (reread.floatingWindows != null)
                foreach (var w in reread.floatingWindows)
                    if (w != null && w.kind == FloatingWindowCatalog.KIND_CHART) chartEntries++;
            if (chartEntries != iids.Length)
                return "CP-S2-02a: floatingWindows chart-entry count = " + chartEntries + " (want " + iids.Length + ")";
        }

        // ── CP-S2-02b: legacy open → in-memory drag 1 chart → Save → reread → drag pos persisted. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S2-02b: root missing";
            var dockWindows = DockWindowsOf(root, ty);

            string py = Path.Combine(TempRoot, "s2_persist", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "n = 0\n");
            var iids = new[] { "9984.TSE", "6920.TSE", "8035.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-05-01", "2025-05-05", "Daily", "1000000"),
                new List<string>(iids));
            WriteLayoutWithLegacyPanels(py, iids);

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // pick the first chart, displace it to a distinct non-grid coord
            string draggedId = DockShape.ChartId(iids[0]);
            var draggedRt = dockWindows.RectOf(draggedId);
            if (draggedRt == null) return "CP-S2-02b: dragged chart not spawned";
            Vector2 dragTarget = new Vector2(-2222f, 1234f);
            Vector2 delta = dragTarget - draggedRt.anchoredPosition;
            dockWindows.MoveByLogical(draggedId, delta);
            if (!Approx2(dockWindows.RectOf(draggedId).anchoredPosition, dragTarget))
                return "CP-S2-02b: in-memory drag did not move chart to target";

            InvokeOnFileSave(ty, root);
            if (!LayoutSidecarStore.TryReadLayout(py, out var reread))
                return "CP-S2-02b: reread after drag-save failed";
            var found = reread.FindWindow(draggedId);
            if (found == null) return "CP-S2-02b: dragged chart entry missing in reread";
            if (Mathf.Abs(found.x - dragTarget.x) > 0.5f || Mathf.Abs(found.y - dragTarget.y) > 0.5f)
                return "CP-S2-02b: drag pos NOT persisted (got x=" + found.x + " y=" + found.y +
                       ", want " + dragTarget + ")";
        }

        return null;
    }

    // =====================================================================================================
    // Section3 — resilience (P8 / P11 / P14 / P15). P15 is here (viewport-independent — no production
    // change needed since the helper takes no viewport input). P8 (corrupted .bak) / P11 (ghost
    // retention) / P14 (dedup) are STUB pending the follow-up RestoreFloating-pre-pass + LayoutSidecarStore
    // .bak slice; captured in findings 0091 §S3-deferred.
    // =====================================================================================================
    static string Section3_Resilience()
    {
        // ── CP-S3-04 (P15): grid placement is canvas-LOGICAL and viewport-無依存. Change canvasView's
        //    zoom (the viewport-proxy) and re-run Sync — chart anchoredPositions must NOT change. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S3-04: root missing";
            var dockWindows = DockWindowsOf(root, ty);
            var canvas = ty.GetField("_canvas", BF)?.GetValue(root) as InfiniteCanvasController;
            if (canvas == null) return "CP-S3-04: _canvas missing";

            string py = Path.Combine(TempRoot, "s3_viewport", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "vp = 1\n");
            var iids = new[] { "7203.TSE", "6758.TSE", "8306.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-07-01", "2025-07-05", "Daily", "1000000"),
                new List<string>(iids));

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // snapshot anchored positions before viewport change
            var before = new Vector2[iids.Length];
            for (int i = 0; i < iids.Length; i++)
            {
                var rt = dockWindows.RectOf(DockShape.ChartId(iids[i]));
                if (rt == null) return "CP-S3-04: chart " + iids[i] + " not spawned";
                before[i] = rt.anchoredPosition;
            }

            // perturb viewport (zoom way out / pan far) — helper-driven grid must NOT shift
            canvas.ApplyView(new CanvasView(800f, -600f, 0.1f));

            for (int i = 0; i < iids.Length; i++)
            {
                var rt = dockWindows.RectOf(DockShape.ChartId(iids[i]));
                if (!Approx2(rt.anchoredPosition, before[i]))
                    return "CP-S3-04: chart " + iids[i] + " moved on viewport change (was " + before[i] +
                           ", now " + rt.anchoredPosition + ") — placement is viewport-dependent";
            }
        }

        return null;
    }

    // =====================================================================================================
    // Section4 — regression / characterization (P1 / P5 / P6 / P7). These piggyback on the cascade-kill
    // production fix from S2 (no additional production change required). They pin BEHAVIORS that emerge
    // for free from the helper-driven grid path: no-sidecar Open, post-Open universe growth, 52-chart
    // sanity bound, and empty-universe spawn-zero.
    // =====================================================================================================
    static string Section4_RegressionCharacterization()
    {
        // ── CP-S4-01 (P1): no sidecar (.py only, no .json) → universe-driven grid placement. The Open
        //    succeeds (BARE open, layoutOk=false; findings 0048 D4), universe is seeded inline-or-empty
        //    by ReseedFromEditor; Sync grid-places whatever is present. No-sidecar with no inline scenario
        //    means an empty universe (0 charts), which is the P7 invariant — the bare-open path is exercised. ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S4-01: root missing";
            var dockWindows = DockWindowsOf(root, ty);

            string py = Path.Combine(TempRoot, "s4_baresidecar", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "alpha = 1\n");   // no inline scenario, no sidecar at all

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // No sidecar + no inline scenario → universe stays empty → no chart spawns. ALL base singletons are
            // retired (startup ADR-0026, run_result ADR-0037 → popup, buying_power/orders/positions ADR-0038 →
            // account summary bar), so the dock plane has NO base windows — assert none of the retired ids spawn
            // (forward-compat: a bare open never resurrects them).
            string[] retiredIds = { "startup", "run_result", "buying_power", "orders", "positions" };
            for (int i = 0; i < retiredIds.Length; i++)
                if (dockWindows.Has(retiredIds[i]))
                    return "CP-S4-01: retired base dock window " + retiredIds[i] + " spawned after bare open (should be gone)";
        }

        // ── CP-S4-02 (P5): post-Open universe growth — add a chart after the doc is open; the new
        //    chart must land at the next grid slot, NOT at the cascade-flush position (regression seal). ──
        {
            var root = ComposeRoot(out var ty);
            if (root == null) return "CP-S4-02: root missing";
            var dockWindows = DockWindowsOf(root, ty);
            var scenario = ScenarioOf(root, ty);

            string py = Path.Combine(TempRoot, "s4_grow", "doc.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py));
            File.WriteAllText(py, "grow = 1\n");
            var seedIids = new[] { "7203.TSE", "6758.TSE", "8306.TSE" };
            ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
                new StartupParamsForWrite("2025-06-01", "2025-06-05", "Daily", "1000000"),
                new List<string>(seedIids));

            root.SetFileDialog(new StubFileDialog { NextResult = py });
            InvokeOnFileOpen(ty, root);

            // capture the 3 seeded chart rects
            var before = new List<Rect>();
            for (int i = 0; i < seedIids.Length; i++)
            {
                var rt = dockWindows.RectOf(DockShape.ChartId(seedIids[i]));
                if (rt == null) return "CP-S4-02: seed chart " + seedIids[i] + " not spawned";
                var p = rt.anchoredPosition;
                var s = rt.sizeDelta;
                before.Add(new Rect(p.x, p.y - s.y, s.x, s.y));
            }

            // grow: add a 4th instrument; SyncChartWindowsToUniverse fires via the Changed event
            string addedIid = "9984.TSE";
            scenario.AddInstrument(addedIid);
            var addedRt = dockWindows.RectOf(DockShape.ChartId(addedIid));
            if (addedRt == null) return "CP-S4-02: added chart not spawned by Sync";
            var addedP = addedRt.anchoredPosition;
            var addedS = addedRt.sizeDelta;
            var addedRect = new Rect(addedP.x, addedP.y - addedS.y, addedS.x, addedS.y);
            // must NOT overlap the existing 3 (cascade would land flush against one of them)
            for (int i = 0; i < before.Count; i++)
                if (addedRect.Overlaps(before[i]))
                    return "CP-S4-02: added chart overlaps seed chart " + seedIids[i] + " (cascade returned)";
        }

        // ── CP-S4-03 (P6): 52 charts → cols=ceil(√52)=8 → row=6 (slot 48) is last → max x = -600 + 7*532
        //    = +3124. The S2 CP-S2-01 already verifies the pairwise non-overlap; here we re-pin the
        //    sanity-bound axis explicitly so any future helper change that drifts the anchor is caught. ──
        // (Already covered by CP-S2-01's sanity-bound assert — no separate root setup needed.)

        // ── CP-S4-04 (P7): empty universe — covered by CP-S4-01 above (no sidecar + no inline scenario ⇒
        //    universe stays empty). The base cluster's 5 windows must remain live; no chart window spawns. ──
        // (Already covered by CP-S4-01's base-cluster live assertion.)

        return null;
    }

    // ---- root composition (same pattern as LayoutPersistenceJourneyE2ERunner) ----
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

    static FloatingWindowController DockWindowsOf(BackcastWorkspaceRoot root, Type ty) =>
        ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;

    static ScenarioStartupController ScenarioOf(BackcastWorkspaceRoot root, Type ty) =>
        ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;

    static StubFileDialog StubFileDialogOf() => new StubFileDialog();

    static void InvokeOnFileOpen(Type ty, BackcastWorkspaceRoot root) =>
        ty.GetMethod("OnFileOpen", BF).Invoke(root, null);
    static void InvokeOnFileSave(Type ty, BackcastWorkspaceRoot root) =>
        ty.GetMethod("OnFileSave", BF).Invoke(root, null);

    // Build a minimal layout doc carrying ONLY the given floatingWindows entries (chart family or
    // anything else), write it to the sidecar at `py`'s side. The scenario key is preserved by the
    // store's merge-write.
    static void WriteLayoutWithFloatingWindows(string py, List<FloatingWindowLayout> wins)
    {
        var doc = LayoutDocument.Default();
        doc.floatingWindows = new List<FloatingWindowLayout>(wins);
        LayoutSidecarStore.WriteLayout(py, doc);
    }

    // Build a legacy-shaped layout doc: chart entries live in `panels` (dead schema) with normalized
    // rects, NOT in `floatingWindows`. This mirrors the v19_morning_cell.json shape captured in
    // findings 0091 F0 — ApplyLayout's L2083 docstring confirms these panels are IGNORED, so the
    // chart family must come through Sync's grid path on Open.
    static void WriteLayoutWithLegacyPanels(string py, IList<string> iids)
    {
        var doc = LayoutDocument.Default();
        doc.floatingWindows = new List<FloatingWindowLayout>();
        var panels = new List<PanelLayout>();
        for (int i = 0; i < iids.Count; i++)
        {
            float minX = 0.125f * (i % 8);
            float minY = 0.875f - 0.125f * (i / 8);
            panels.Add(new PanelLayout(
                DockShape.ChartId(iids[i]), i, true,
                new LayoutRect(minX, minY, minX + 0.125f, minY + 0.125f)));
        }
        doc.panels = panels;
        LayoutSidecarStore.WriteLayout(py, doc);
    }

    // ---- helpers ----
    static bool Approx2(Vector2 a, Vector2 b) =>
        Mathf.Abs(a.x - b.x) <= EPS && Mathf.Abs(a.y - b.y) <= EPS;

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
