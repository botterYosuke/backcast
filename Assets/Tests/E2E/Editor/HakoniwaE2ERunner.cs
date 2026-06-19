// HakoniwaE2ERunner.cs — Hakoniwa split-grid サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// HakoniwaE2ERunner.md）。第二波。4 つの throwaway/standing AFK probe（Assets/Editor の HakoniwaProbe /
// HakoniwaChartTileProbe / HakoniwaBaseModeProbe / HakoniwaProfileProbe）から昇格・統合（ADR-0015 の回帰ゲート
// 命名規約。先例 DepthLadder=findings 0059 等）。各 probe の assert を 1 行も削らず移送し、section ごとの
// Covers: を付与。Python-FREE（grid 幾何・base 集合・slot・profile は kernel 不要）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod HakoniwaE2ERunner.Run -logFile <log>
//   # expect: [E2E HAKONIWA PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// gate 形は probe の Execute()-形（各 section が null=PASS、最初の失敗文字列を返す）を温存。EditorApplication.Exit は
// self-failing gate として無条件化。section ごとに EPS を厳密保存する: HakoniwaProbe 由来の格子幾何 6 section は
// EPS_GRID=1e-4f、ChartTile/BaseMode/Profile 由来は EPS=1e-3f（grill 急所2）。BaseMode 由来 section は section 内で
// 独立に OpenScene+BuildWorkspace する（共有 root へ畳まない・grill 急所3）。
//
// 据え置きの仕分け: ChartTile S2（InstrumentOhlcDecoder のチャート数値読取り）は将来の Chart カテゴリ runner、
// BaseMode S5（#65 口座/RunResult panel empty-state）は将来の Panel カテゴリ runner へ移送予定で、それぞれ
// HakoniwaChartTileProbe / HakoniwaBaseModeProbe に trimmed standing probe として据え置き回帰維持（本 runner では扱わない）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class HakoniwaE2ERunner
{
    const float EPS = 1e-3f;        // ChartTile / BaseMode / Profile 由来 section
    const float EPS_GRID = 1e-4f;   // HakoniwaProbe 由来の格子幾何 6 section
    static readonly Vector2 MIN_TILE = new Vector2(280f, 180f);
    static readonly Vector2 DEFAULT_BOX = new Vector2(700f, 450f);
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly LayoutRect R = new LayoutRect(0f, 0f, 1f, 1f);

    static string TempDir => Path.Combine(Application.temporaryCachePath, "hakoniwa_e2e_runner");
    static string TempPath => Path.Combine(TempDir, "layout.json");
    static string ChartTempDir => Path.Combine(Application.temporaryCachePath, "hakoniwa_e2e_runner_chart");
    static string ChartTempLayout => Path.Combine(ChartTempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            if (Directory.Exists(ChartTempDir)) Directory.Delete(ChartTempDir, true);
            Directory.CreateDirectory(ChartTempDir);

            fail = Section01_GridArithmetic()                       // HAKONIWA-01,02
                ?? Section02_SlotHitTest()                          // HAKONIWA-02
                ?? Section03_ControllerReorder(spawned)             // HAKONIWA-01
                ?? Section04_DiskRoundTripNonVacuous(spawned)       // HAKONIWA-04
                ?? Section05_InvalidOpAndTolerance(spawned)         // HAKONIWA-03,10
                ?? Section06_BackCompatAndFallback(spawned)         // HAKONIWA-10
                ?? Section07_BoxGrowArithmetic()                    // HAKONIWA-06
                ?? Section08_DynamicTilesRoundTrip(spawned)         // HAKONIWA-05
                ?? Section09_ModeTileKinds()                        // HAKONIWA-07
                ?? Section10_BaseOnlyRetilePreservesChartIdentity() // HAKONIWA-07
                ?? Section11_LiveManualAutoNoOp()                   // HAKONIWA-08
                ?? Section12_RestoreAppliesPerModeProfile()         // HAKONIWA-09
                ?? Section13_ProfileValidityHonor()                 // HAKONIWA-09
                ?? Section14_LegacyAndCollisionFallToCanonical()    // HAKONIWA-09
                ?? Section15_ChartOrderExcludedAndPreserved()       // HAKONIWA-09
                ?? Section16_SeedAndDiskRoundTrip();                // HAKONIWA-09
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
            try { if (Directory.Exists(ChartTempDir)) Directory.Delete(ChartTempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[E2E HAKONIWA PASS] grid arithmetic + slot hit-test + controller reorder + non-vacuous slot disk " +
                      "round-trip + invalid-op/tolerance + back-compat/fallback + box-grow + dynamic chart tile round-trip + " +
                      "mode tile kinds + base-only retile (chart identity) + live-shape no-op + per-mode profile honor/canonical + " +
                      "profile validity matrix + chart exclusion + forward-compat seed + per-mode disk round-trip verified.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E HAKONIWA FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ===== HakoniwaProbe 由来（格子幾何・EPS_GRID=1e-4f）=====

    // Covers: HAKONIWA-01,02 — grid arithmetic (GridDims + CellRects: 5->3x2, equal/cover/non-overlap, empty 6th)
    static string Section01_GridArithmetic()
    {
        // GridDims: ceil(√n) cols, ceil(n/cols) rows.
        (int n, int cols, int rows)[] cases =
        {
            (1, 1, 1), (2, 2, 1), (4, 2, 2), (5, 3, 2), (6, 3, 2), (7, 3, 3), (9, 3, 3),
        };
        foreach (var c in cases)
        {
            HakoniwaGridMath.GridDims(c.n, out int gc, out int gr);
            if (gc != c.cols || gr != c.rows)
                return $"S1: GridDims({c.n}) = ({gc},{gr}), expected ({c.cols},{c.rows})";
        }
        HakoniwaGridMath.GridDims(0, out int zc, out int zr);
        if (zc != 0 || zr != 0) return $"S1: GridDims(0) = ({zc},{zr}), expected (0,0)";

        // CellRects(5): exactly 5 cells in a 3x2 grid (6th EMPTY), equal 1/3 x 1/2.
        var cells = HakoniwaGridMath.CellRects(5);
        if (cells.Count != 5) return $"S1: CellRects(5) returned {cells.Count} cells, expected 5 (6th must be empty)";
        for (int i = 0; i < cells.Count; i++)
        {
            float w = cells[i].maxX - cells[i].minX;
            float h = cells[i].maxY - cells[i].minY;
            if (!Approx(w, 1f / 3f) || !Approx(h, 1f / 2f))
                return $"S1: cell {i} size ({w},{h}) != equal (1/3, 1/2)";
            if (cells[i].minX < -EPS_GRID || cells[i].maxX > 1f + EPS_GRID || cells[i].minY < -EPS_GRID || cells[i].maxY > 1f + EPS_GRID)
                return $"S1: cell {i} escapes [0,1]";
        }
        // slot 0 = top-left: minX=0, maxY=1. slot 2 = top-right: maxX=1, maxY=1.
        if (!Approx(cells[0].minX, 0f) || !Approx(cells[0].maxY, 1f)) return "S1: slot 0 not top-left";
        if (!Approx(cells[2].maxX, 1f) || !Approx(cells[2].maxY, 1f)) return "S1: slot 2 not top-right";
        // slot 3 = bottom-left: minX=0, minY=0. slot 4 below slot 1 (same column).
        if (!Approx(cells[3].minX, 0f) || !Approx(cells[3].minY, 0f)) return "S1: slot 3 not bottom-left";
        if (!Approx(cells[4].minX, cells[1].minX)) return "S1: slot 4 not under slot 1 (row-major broke)";
        if (cells[4].maxY > cells[1].minY + EPS_GRID) return "S1: slot 4 not below slot 1 (Y not top-down)";

        // pairwise interior non-overlap.
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
                if (InteriorsOverlap(cells[i], cells[j]))
                    return $"S1: cells {i} and {j} overlap";
        return null;
    }

    // Covers: HAKONIWA-02 — SlotAt hit-test (inside cell, empty 6th region, outside)
    static string Section02_SlotHitTest()
    {
        var cells = HakoniwaGridMath.CellRects(5);

        // centre of each of the 5 cells -> its own slot.
        for (int i = 0; i < cells.Count; i++)
        {
            Vector2 ctr = new Vector2((cells[i].minX + cells[i].maxX) / 2f, (cells[i].minY + cells[i].maxY) / 2f);
            if (HakoniwaGridMath.SlotAt(cells, ctr) != i)
                return $"S2: centre of cell {i} did not hit slot {i}";
        }
        // the EMPTY 6th cell region (col 2, row 1: ~ (5/6, 1/4)) -> -1 (no 6th cell).
        if (HakoniwaGridMath.SlotAt(cells, new Vector2(5f / 6f, 1f / 4f)) != -1)
            return "S2: empty 6th-cell region unexpectedly hit a slot";
        // outside [0,1] -> -1.
        if (HakoniwaGridMath.SlotAt(cells, new Vector2(1.5f, 0.5f)) != -1) return "S2: point right of grid hit a slot";
        if (HakoniwaGridMath.SlotAt(cells, new Vector2(0.5f, -0.2f)) != -1) return "S2: point below grid hit a slot";
        return null;
    }

    // Covers: HAKONIWA-01 — controller reorder on a real RectTransform tree (order->cell) + Capture/Apply boundary
    static string Section03_ControllerReorder(List<GameObject> spawned)
    {
        var controller = BuildController(spawned, out var tiles, HakoniwaController.DEFAULT_ORDER);
        var cells = HakoniwaGridMath.CellRects(5);

        // default order: chart -> cell 0, run_result -> cell 4.
        if (!AnchorsMatch(tiles["chart"], cells[0])) return "S3: default chart not in cell 0";
        if (!AnchorsMatch(tiles["run_result"], cells[4])) return "S3: default run_result not in cell 4";

        // swap slot 0 (chart) and slot 4 (run_result): the tiles must trade cells.
        if (!controller.Swap(0, 4)) return "S3: Swap(0,4) returned false";
        if (!AnchorsMatch(tiles["run_result"], cells[0])) return "S3: after swap run_result not in cell 0";
        if (!AnchorsMatch(tiles["chart"], cells[4])) return "S3: after swap chart not in cell 4";
        if (controller.SlotOf("run_result") != 0 || controller.SlotOf("chart") != 4)
            return "S3: SlotOf disagrees with the swap";

        // Capture/Apply boundary: capture this state, apply it back -> order is stable.
        LayoutDocument doc = controller.Capture();
        if (doc.Find("run_result").slot != 0 || doc.Find("chart").slot != 4)
            return "S3: Capture did not record the swapped slots";
        controller.Apply(doc);
        if (controller.SlotOf("run_result") != 0 || controller.SlotOf("chart") != 4)
            return "S3: Apply(Capture()) was not a fixed point";
        return null;
    }

    // Covers: HAKONIWA-04 — non-vacuous slot disk round-trip (vacuous-green kill, on-disk TEXT proof, fresh load)
    static string Section04_DiskRoundTripNonVacuous(List<GameObject> spawned)
    {
        var c1 = BuildController(spawned, out _, HakoniwaController.DEFAULT_ORDER);
        LayoutDocument baseline = c1.Capture();   // slots 0..4 in default order

        c1.Swap(0, 4);                              // chart<->run_result
        LayoutDocument mutated = c1.Capture();
        if (LayoutDocument.StructurallyEqual(mutated, baseline, EPS_GRID))
            return "S4: swapped capture still equals baseline (mutation no-op)";

        LayoutStore.Save(mutated, TempPath);
        if (!File.Exists(TempPath)) return "S4: Save did not create the sidecar";

        // STRUCTURAL: the swapped slots must reach the on-disk TEXT (catches an in-memory-only
        // round-trip or a serializer that drops slot). Whitespace-insensitive.
        string compact = File.ReadAllText(TempPath).Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        if (!compact.Contains("\"id\":\"run_result\",\"slot\":0"))
            return "S4: run_result@slot0 not in on-disk JSON text";
        if (!compact.Contains("\"id\":\"chart\",\"slot\":4"))
            return "S4: chart@slot4 not in on-disk JSON text";

        // Fresh load + apply to a FRESH controller (new tiles): the swapped order survived.
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (loaded.Find("run_result").slot != 0 || loaded.Find("chart").slot != 4)
            return "S4: loaded slots != mutated";
        if (LayoutDocument.StructurallyEqual(loaded, baseline, EPS_GRID))
            return "S4: loaded == baseline (vacuous round-trip — swap did not persist)";

        var c2 = BuildController(spawned, out var tiles2, HakoniwaController.DEFAULT_ORDER);
        c2.Apply(loaded);
        if (c2.SlotOf("run_result") != 0 || c2.SlotOf("chart") != 4)
            return "S4: fresh controller did not restore the swapped order";
        var cells = HakoniwaGridMath.CellRects(5);
        if (!AnchorsMatch(tiles2["run_result"], cells[0]) || !AnchorsMatch(tiles2["chart"], cells[4]))
            return "S4: restored tiles not placed in the swapped cells";
        return null;
    }

    // Covers: HAKONIWA-03,10 — invalid-op no-op + duplicate/out-of-range slot tolerance
    static string Section05_InvalidOpAndTolerance(List<GameObject> spawned)
    {
        var c = BuildController(spawned, out _, HakoniwaController.DEFAULT_ORDER);
        var before = new List<string>(c.Order);

        if (c.Swap(2, 2)) return "S5: Swap(i,i) should be a no-op";
        if (c.Swap(0, 99)) return "S5: out-of-range Swap should be a no-op";
        if (c.Swap(-1, 1)) return "S5: negative Swap should be a no-op";
        if (!SeqEqual(c.Order, before)) return "S5: a no-op Swap changed the order";

        // duplicate + out-of-range + negative slots: must collapse to a contiguous order of
        // ALL known ids (no throw, no drop), sorted by raw slot then stable tie-break.
        var doc = new LayoutDocument { version = LayoutDocument.CURRENT_VERSION, panels = new List<PanelLayout>() };
        doc.panels.Add(new PanelLayout("status",     -5, true, new LayoutRect()));   // negative -> first
        doc.panels.Add(new PanelLayout("chart",       7, true, new LayoutRect()));   // duplicate slot 7
        doc.panels.Add(new PanelLayout("orders",      7, true, new LayoutRect()));   // duplicate slot 7
        doc.panels.Add(new PanelLayout("positions",   2, true, new LayoutRect()));
        doc.panels.Add(new PanelLayout("run_result", 99, true, new LayoutRect()));   // out-of-range -> last
        c.Apply(doc);
        if (c.Count != 5) return $"S5: tolerance dropped/duplicated tiles (count {c.Count})";
        var seen = new HashSet<string>(c.Order);
        foreach (var id in HakoniwaController.DEFAULT_ORDER)
            if (!seen.Contains(id)) return $"S5: tolerance lost tile '{id}'";
        // expected order by (slot, then original index): status(-5), positions(2), chart(7),
        // orders(7), run_result(99). chart before orders by stable original-order tie-break.
        string[] expected = { "status", "positions", "chart", "orders", "run_result" };
        if (!SeqEqual(c.Order, new List<string>(expected)))
            return "S5: duplicate/out-of-range slots did not normalize to the expected order";
        return null;
    }

    // Covers: HAKONIWA-10 — back-compat (missing/unknown ids) + corrupt/missing fallback
    static string Section06_BackCompatAndFallback(List<GameObject> spawned)
    {
        // (a) doc names only SOME ids + an UNKNOWN id: known ones ordered, missing ones kept
        // (appended after), unknown ignored — all 5 live tiles still present, none added.
        var c = BuildController(spawned, out _, HakoniwaController.DEFAULT_ORDER);
        var doc = new LayoutDocument { version = LayoutDocument.CURRENT_VERSION, panels = new List<PanelLayout>() };
        doc.panels.Add(new PanelLayout("run_result", 0, true, new LayoutRect()));
        doc.panels.Add(new PanelLayout("chart",      1, true, new LayoutRect()));
        doc.panels.Add(new PanelLayout("ghost_tile", 2, true, new LayoutRect()));   // unknown -> ignored
        c.Apply(doc);
        if (c.Count != 5) return $"S6a: count changed to {c.Count} (unknown id added or tile dropped)";
        if (c.SlotOf("ghost_tile") != -1) return "S6a: unknown id leaked into the order";
        if (c.SlotOf("run_result") != 0 || c.SlotOf("chart") != 1)
            return "S6a: doc-named ids not placed first in slot order";
        // the three ids absent from the doc keep their relative default order, after the named ones.
        if (!(c.SlotOf("status") < c.SlotOf("positions") && c.SlotOf("positions") < c.SlotOf("orders")))
            return "S6a: missing ids did not keep default relative order";
        if (c.SlotOf("status") < 2) return "S6a: missing ids not appended after doc-named ids";

        // (b) corrupt JSON -> LayoutStore default; Apply(Default()) restores the canonical order.
        LayoutDocument def = LayoutStore.LoadFromJson("{not valid json");
        if (!LayoutDocument.StructurallyEqual(def, LayoutDocument.Default(), 1e-3f))
            return "S6b: corrupt JSON did not fall back to default";
        c.Apply(def);
        for (int i = 0; i < HakoniwaController.DEFAULT_ORDER.Length; i++)
            if (c.Order[i] != HakoniwaController.DEFAULT_ORDER[i])
                return "S6b: Apply(Default()) did not restore the canonical default order";

        // (c) missing file -> default -> same canonical order.
        LayoutDocument missing = LayoutStore.Load(Path.Combine(TempDir, "does_not_exist.json"));
        c.Apply(missing);
        if (c.Order[0] != "chart" || c.Order[4] != "run_result")
            return "S6c: missing-file fallback did not restore the canonical order";
        return null;
    }

    // ===== HakoniwaChartTileProbe 由来（EPS=1e-3f）=====

    // Covers: HAKONIWA-06 — box-grow arithmetic (ComputeBoxSize == compute_hakoniwa_box_size)
    static string Section07_BoxGrowArithmetic()
    {
        // n<=0 → default verbatim.
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(0, MIN_TILE, 0f, DEFAULT_BOX), DEFAULT_BOX))
            return "box-grow: n=0 must return default";

        // small n stays at the default floor (cols/rows*min below default).
        // n=1 → 1x1: max(700,280),max(450,180)=(700,450). n=3 → 2x2: max(700,560),max(450,360)=(700,450).
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(1, MIN_TILE, 0f, DEFAULT_BOX), DEFAULT_BOX))
            return "box-grow: n=1 should stay at default floor";
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(3, MIN_TILE, 0f, DEFAULT_BOX), DEFAULT_BOX))
            return "box-grow: n=3 (2x2) should stay at default floor";

        // n=6 → 3x2: x grows (3*280=840 > 700), y stays (2*180=360 < 450).
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(6, MIN_TILE, 0f, DEFAULT_BOX), new Vector2(840f, 450f)))
            return "box-grow: n=6 (3x2) wrong (expected 840x450)";

        // n=12 → 4x3: both grow (4*280=1120, 3*180=540).
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(12, MIN_TILE, 0f, DEFAULT_BOX), new Vector2(1120f, 540f)))
            return "box-grow: n=12 (4x3) wrong (expected 1120x540)";

        // dragHeight reserves extra y (parity param for #63): n=12 + drag 32 → y = 540+32 = 572.
        if (!Approx(HakoniwaGridMath.ComputeBoxSize(12, MIN_TILE, 32f, DEFAULT_BOX), new Vector2(1120f, 572f)))
            return "box-grow: dragHeight not added to y term";

        // grid arithmetic the box-grow rides on: ceil(√n).
        HakoniwaGridMath.GridDims(6, out int c6, out int r6);
        if (c6 != 3 || r6 != 2) return "box-grow: GridDims(6) != 3x2";
        return null;
    }

    // Covers: HAKONIWA-05 — dynamic tile add/remove + slot non-collapsing round-trip
    static string Section08_DynamicTilesRoundTrip(List<GameObject> spawned)
    {
        var rootGo = new GameObject("probe_hako_root", typeof(RectTransform));
        spawned.Add(rootGo);
        var root = (RectTransform)rootGo.transform;
        root.sizeDelta = new Vector2(900f, 600f);

        RectTransform NewTile(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            spawned.Add(go);
            var rt = (RectTransform)go.transform;
            rt.SetParent(root, false);
            return rt;
        }

        // base [startup] only (the #60 base), then dynamically add chart:<id> tiles.
        var startup = NewTile("startup");
        var hako = new HakoniwaController(root,
            new Dictionary<string, RectTransform> { { "startup", startup } },
            new[] { "startup" });
        if (hako.Count != 1) return "dynamic: base should be [startup] only";

        var cA = NewTile("chart:A");
        var cB = NewTile("chart:B");
        if (!hako.AddTile("chart:A", cA)) return "dynamic: AddTile(chart:A) should be new";
        if (!hako.AddTile("chart:B", cB)) return "dynamic: AddTile(chart:B) should be new";
        if (hako.AddTile("chart:A", cA)) return "dynamic: re-AddTile(chart:A) should NOT be new";
        if (hako.Count != 3) return "dynamic: expected 3 tiles after adds";
        if (hako.Order[0] != "startup" || hako.Order[1] != "chart:A" || hako.Order[2] != "chart:B")
            return "dynamic: order wrong after adds (base first, charts appended)";

        // swap charts, then round-trip the (non-default) order through disk.
        if (!hako.Swap(1, 2)) return "dynamic: swap charts failed";
        var doc = hako.Capture();
        LayoutStore.Save(doc, ChartTempLayout);
        string text = File.ReadAllText(ChartTempLayout);
        if (!text.Contains("chart:A") || !text.Contains("chart:B")) return "dynamic: chart ids not on disk";
        var loaded = LayoutStore.Load(ChartTempLayout);

        // fresh controller with the SAME live tiles applies the saved slots → order survives.
        var hako2 = new HakoniwaController(root,
            new Dictionary<string, RectTransform> { { "startup", startup }, { "chart:A", cA }, { "chart:B", cB } },
            new[] { "startup", "chart:A", "chart:B" });
        hako2.Apply(loaded);
        if (hako2.Order[1] != "chart:B" || hako2.Order[2] != "chart:A")
            return "dynamic: swapped slot order not restored from disk";

        // stale doc id (old fixed "chart") is skipped; a live id absent from the doc appends.
        var staleDoc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>
            {
                new PanelLayout("chart", 0, true, new LayoutRect(0, 0, 1, 1)),     // stale → skip
                new PanelLayout("chart:B", 1, true, new LayoutRect(0, 0, 1, 1)),
                new PanelLayout("startup", 2, true, new LayoutRect(0, 0, 1, 1)),
                // chart:A absent from the doc → appends after doc-ordered ones.
            },
        };
        hako2.Apply(staleDoc);
        if (hako2.SlotOf("chart") >= 0) return "dynamic: stale 'chart' id must not be adopted";
        if (hako2.Order[hako2.Count - 1] != "chart:A") return "dynamic: doc-absent live id must append last";

        // remove a chart tile.
        if (!hako.RemoveTile("chart:A")) return "dynamic: RemoveTile(chart:A) should report removed";
        if (hako.SlotOf("chart:A") >= 0) return "dynamic: removed tile still in order";
        if (hako.RemoveTile("chart:A")) return "dynamic: re-RemoveTile should be false";
        return null;
    }

    // ===== HakoniwaBaseModeProbe 由来（EPS=1e-3f・section 内で独立 OpenScene+BuildWorkspace）=====

    // Covers: HAKONIWA-07 — mode tile kinds (HakoniwaBaseTiles.Kinds == TTWR hakoniwa_tile_kinds)
    static string Section09_ModeTileKinds()
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

    // Covers: HAKONIWA-07 — base-only retile preserves chart identity (the load-bearing, non-tautological check)
    static string Section10_BaseOnlyRetilePreservesChartIdentity()
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

    // Covers: HAKONIWA-08 — LiveManual⇄LiveAuto is a no-op (same Live shape)
    static string Section11_LiveManualAutoNoOp()
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

    // Covers: HAKONIWA-09 — restore applies the per-mode profile: collision/legacy → canonical, valid → honored
    static string Section12_RestoreAppliesPerModeProfile()
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

    // ===== HakoniwaProfileProbe 由来（EPS=1e-3f）=====

    // Covers: HAKONIWA-09 — a VALID profile honors the saved (user-swapped) base order; Replay ≠ Live
    static string Section13_ProfileValidityHonor()
    {
        var p = new HakoniwaLayoutProfiles();
        // Replay: startup + 4 panels, with orders swapped ahead of buying_power (a user header-drag swap).
        p.Set(false, Panels("startup", "orders", "buying_power", "positions", "run_result", "chart:A", "chart:B"));
        // Live: the 4 panels, with run_result pulled to the front (a DIFFERENT per-mode arrangement).
        p.Set(true, Panels("run_result", "buying_power", "orders", "positions", "chart:A"));

        if (!p.IsValidForMode(false)) return "(a) valid Replay profile (base set matches Kinds(Replay)) must be valid";
        if (!p.IsValidForMode(true)) return "(a) valid Live profile (base set matches Kinds(Live)) must be valid";

        var rep = p.BaseOrderForMode(false);
        if (!SeqEqual(rep, new[] { "startup", "orders", "buying_power", "positions", "run_result" }))
            return "(a) Replay base order not honored (got [" + string.Join(",", rep) + "])";
        var liv = p.BaseOrderForMode(true);
        if (!SeqEqual(liv, new[] { "run_result", "buying_power", "orders", "positions" }))
            return "(a) Live base order not honored (got [" + string.Join(",", liv) + "])";
        // the whole point of per-mode: the two orders DIFFER.
        if (SeqEqual(rep, liv)) return "(a) Replay and Live base orders must DIFFER (per-mode is meaningless otherwise)";
        return null;
    }

    // Covers: HAKONIWA-09 — legacy / collision / set-mismatch → invalid → canonical Kinds(mode)
    static string Section14_LegacyAndCollisionFallToCanonical()
    {
        var canonReplay = HakoniwaBaseTiles.Kinds(false);
        var canonLive = HakoniwaBaseTiles.Kinds(true);

        // LayoutDocument.Default() = [chart, status, positions, orders, run_result]: base set
        // {positions, orders, run_result} ≠ Kinds(Replay) (no startup/buying_power) → invalid.
        var def = new HakoniwaLayoutProfiles();
        def.SeedFromLegacy(LayoutDocument.Default().panels);
        if (def.IsValidForMode(false)) return "(b) Default() seed must be INVALID for Replay";
        if (def.IsValidForMode(true)) return "(b) Default() seed must be INVALID for Live";
        if (!SeqEqual(def.BaseOrderForMode(false), canonReplay)) return "(b) Default() Replay must fall to canonical";
        if (!SeqEqual(def.BaseOrderForMode(true), canonLive)) return "(b) Default() Live must fall to canonical";

        // #60-era sidecar [startup, chart:A, chart:B]: base set {startup} ≠ Kinds(Replay) → invalid → canonical.
        var era60 = new HakoniwaLayoutProfiles();
        era60.Set(false, Panels("startup", "chart:A", "chart:B"));
        if (era60.IsValidForMode(false)) return "(b) #60-era seed must be INVALID for Replay";
        if (!SeqEqual(era60.BaseOrderForMode(false), canonReplay)) return "(b) #60-era Replay must fall to canonical";

        // a Live profile that still carries startup: base set has an EXTRA id vs Kinds(Live) → invalid.
        var liveWithStartup = new HakoniwaLayoutProfiles();
        liveWithStartup.Set(true, Panels("startup", "buying_power", "orders", "positions", "run_result"));
        if (liveWithStartup.IsValidForMode(true)) return "(b) Live profile with startup must be INVALID (extra base id)";
        if (!SeqEqual(liveWithStartup.BaseOrderForMode(true), canonLive)) return "(b) over-set Live must fall to canonical";

        // a Replay profile MISSING a base id (run_result dropped): base set short → invalid → canonical.
        var missing = new HakoniwaLayoutProfiles();
        missing.Set(false, Panels("startup", "buying_power", "orders", "positions"));
        if (missing.IsValidForMode(false)) return "(b) Replay profile missing run_result must be INVALID";
        if (!SeqEqual(missing.BaseOrderForMode(false), canonReplay)) return "(b) under-set Replay must fall to canonical";

        // null/absent profile → invalid → canonical.
        var empty = new HakoniwaLayoutProfiles();
        if (empty.IsValidForMode(false) || empty.IsValidForMode(true)) return "(b) empty profiles must be invalid for both modes";
        if (!SeqEqual(empty.BaseOrderForMode(false), canonReplay)) return "(b) empty → Replay canonical";
        if (!SeqEqual(empty.BaseOrderForMode(true), canonLive)) return "(b) empty → Live canonical";
        return null;
    }

    // Covers: HAKONIWA-09 — chart ids never affect validity and are excluded from the base prefix; chart order preserved
    static string Section15_ChartOrderExcludedAndPreserved()
    {
        var p = new HakoniwaLayoutProfiles();
        // a valid Replay base set, with charts in a specific (non-id-sorted) order B before A.
        p.Set(false, Panels("startup", "buying_power", "orders", "positions", "run_result", "chart:B", "chart:A"));

        // charts must NOT break validity (membership is universe-owned, #60).
        if (!p.IsValidForMode(false)) return "(c) chart tiles must NOT affect base-set validity";

        // the base prefix excludes every chart id.
        foreach (var id in p.BaseOrderForMode(false))
            if (HakoniwaBaseTiles.IsChartId(id)) return "(c) BaseOrderForMode must exclude chart ids (got " + id + ")";

        // the stored profile preserves the chart order B,A (honored at apply via _hako.Apply).
        var stored = p.Get(false);
        int idxB = stored.FindIndex(x => x.id == "chart:B");
        int idxA = stored.FindIndex(x => x.id == "chart:A");
        if (idxB < 0 || idxA < 0 || idxB > idxA) return "(c) stored chart order (B before A) not preserved";
        return null;
    }

    // Covers: HAKONIWA-09 — forward-compat seed (AC2) + LayoutStore disk round-trip of hakoniwaProfiles
    static string Section16_SeedAndDiskRoundTrip()
    {
        // forward-compat: an OLD single-`panels` doc (no hakoniwaProfiles) seeds BOTH modes non-empty.
        // Drive it through LoadFromJson (the real persistence boundary) so JsonUtility's absent-field
        // behavior is exercised, not a hand-built object.
        string oldJson =
            "{\"version\":1,\"panels\":[" +
            "{\"id\":\"startup\",\"slot\":0,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"buying_power\",\"slot\":1,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"orders\",\"slot\":2,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"positions\",\"slot\":3,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"run_result\",\"slot\":4,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}]}";
        var oldDoc = LayoutStore.LoadFromJson(oldJson);
        var seeded = HakoniwaLayoutProfiles.FromDocument(oldDoc);
        if (seeded.Get(false) == null || seeded.Get(false).Count != 5) return "(d) forward-compat: Replay must seed 5 panels from legacy `panels`";
        if (seeded.Get(true) == null || seeded.Get(true).Count != 5) return "(d) forward-compat: Live must seed 5 panels from legacy `panels`";
        if (!seeded.IsValidForMode(false)) return "(d) forward-compat: a Replay-shaped legacy doc must seed a VALID Replay profile";
        if (seeded.IsValidForMode(true)) return "(d) forward-compat: a Replay-shaped legacy doc must be INVALID for Live (has startup) → canonical on load";

        // disk round-trip of a #62 doc with DISTINCT per-mode orders.
        var profiles = new HakoniwaLayoutProfiles();
        profiles.Set(false, Panels("startup", "orders", "buying_power", "positions", "run_result"));   // Replay: orders swapped
        profiles.Set(true, Panels("run_result", "buying_power", "orders", "positions"));               // Live: run_result first
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = profiles.Get(false),               // active-mode mirror
            hakoniwaProfiles = profiles.Clone(),
            canvasView = CanvasView.Identity(),
            floatingWindows = new List<FloatingWindowLayout>(),
            strategyEditors = new List<StrategyEditorState>(),
        };

        string path = Path.Combine(Application.temporaryCachePath, "hakoniwa_e2e_runner_profile.json");
        if (File.Exists(path)) File.Delete(path);
        LayoutStore.Save(doc, path);
        if (!File.Exists(path)) return "(d) sidecar not written";

        string text = File.ReadAllText(path);
        if (!text.Contains("hakoniwaProfiles")) return "(d) on-disk text missing the hakoniwaProfiles field";
        if (!text.Contains("\"replay\"") || !text.Contains("\"live\"")) return "(d) on-disk text missing replay/live sub-profiles";

        var loaded = LayoutStore.Load(path);
        if (!LayoutDocument.StructurallyEqual(loaded, doc, EPS)) return "(d) per-mode disk round-trip not structurally equal";
        if (loaded.hakoniwaProfiles == null) return "(d) loaded hakoniwaProfiles is null";

        // non-vacuous: the loaded profiles carry the DISTINCT per-mode orders (not collapsed to seed).
        var lp = HakoniwaLayoutProfiles.FromDocument(loaded);
        if (!SeqEqual(lp.BaseOrderForMode(false), new[] { "startup", "orders", "buying_power", "positions", "run_result" }))
            return "(d) loaded Replay base order lost the swap";
        if (!SeqEqual(lp.BaseOrderForMode(true), new[] { "run_result", "buying_power", "orders", "positions" }))
            return "(d) loaded Live base order lost the swap";
        if (SeqEqual(lp.BaseOrderForMode(false), lp.BaseOrderForMode(true)))
            return "(d) loaded Replay/Live orders collapsed to the same — per-mode not persisted";

        // and a #62 doc is NOT re-seeded from `panels` (per-mode present takes precedence).
        if (!lp.IsValidForMode(true)) return "(d) loaded Live profile must be valid (used directly, not re-seeded)";

        File.Delete(path);
        return null;
    }

    // ===== helpers =====

    // Build a HakoniwaRoot (sized) with the 5 default tiles as children, return a controller.
    static HakoniwaController BuildController(List<GameObject> spawned, out Dictionary<string, RectTransform> tiles, IList<string> order)
    {
        var rootGo = new GameObject("ProbeHakoniwaRoot", typeof(RectTransform));
        spawned.Add(rootGo);
        var root = rootGo.GetComponent<RectTransform>();
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = new Vector2(900f, 600f);

        tiles = new Dictionary<string, RectTransform>();
        foreach (var id in HakoniwaController.DEFAULT_ORDER)
        {
            var go = new GameObject("Tile_" + id, typeof(RectTransform));
            go.transform.SetParent(root, false);
            tiles[id] = go.GetComponent<RectTransform>();
        }
        return new HakoniwaController(root, tiles, order);
    }

    static bool AnchorsMatch(RectTransform rt, LayoutRect cell) =>
        Approx(rt.anchorMin.x, cell.minX) && Approx(rt.anchorMin.y, cell.minY)
        && Approx(rt.anchorMax.x, cell.maxX) && Approx(rt.anchorMax.y, cell.maxY)
        && rt.offsetMin == Vector2.zero && rt.offsetMax == Vector2.zero;

    static bool InteriorsOverlap(LayoutRect a, LayoutRect b)
    {
        float ix = Mathf.Min(a.maxX, b.maxX) - Mathf.Max(a.minX, b.minX);
        float iy = Mathf.Min(a.maxY, b.maxY) - Mathf.Max(a.minY, b.minY);
        return ix > EPS_GRID && iy > EPS_GRID;
    }

    // float Approx for the grid-geometry sections (EPS_GRID = 1e-4f).
    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS_GRID;

    // Vector2 Approx for the box-grow section (EPS = 1e-3f).
    static bool Approx(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < EPS && Mathf.Abs(a.y - b.y) < EPS;

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

    // build a panels list with sequential slots and a unit rect (slot = index, all visible).
    static List<PanelLayout> Panels(params string[] ids)
    {
        var list = new List<PanelLayout>(ids.Length);
        for (int i = 0; i < ids.Length; i++) list.Add(new PanelLayout(ids[i], i, true, R.Clone()));
        return list;
    }
}
