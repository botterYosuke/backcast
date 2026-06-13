// HakoniwaProbe.cs — issue #14 "Hakoniwa split-grid" (THROWAWAY AFK regression gate)
//
// The headless, Python-FREE, render-FREE regression gate for the Hakoniwa split-grid seam.
// Run:
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod HakoniwaProbe.Run -logFile <log>
//   # expect: [HAKONIWA PASS] ... / exit=0
//
// #14's AFK gate is AUTHORITATIVE for the grid arithmetic, the slot reorder, and the slot
// persistence round-trip (findings 0007 §8); the actual header-drag swap FEEL is the
// owner-launched HITL harness (Tools > Backcast > Hakoniwa HITL). Like #12/#13 this probe
// spawns NO auto-bootstrap, so it never re-triggers the single-Play-owner collision
// (findings 0003 §8).
//
// INDEPENDENCE: the rigour lives in PURE grid geometry (coverage / non-overlap / equal
// fractions — S1), the hit-test (S2), and the NON-VACUOUS slot disk round-trip (S4, on-disk
// TEXT proof). The controller section (S3) drives a REAL RectTransform tree and asserts the
// order->cell wiring through Unity-stored anchors (the #12 LayoutBinder-style read-back; the
// risky part of #14 is the slot mapping + persistence, not transform composition as in #13).
//
// SIX SECTIONS (findings 0007 §8), each returns null on pass or a reason string:
//   1. grid arithmetic: GridDims + CellRects (5->3x2, equal, cover, non-overlap, 6th empty)
//   2. SlotAt hit-test (inside cell, empty 6th region, outside)
//   3. controller reorder on a real RectTransform tree (order->cell) + Capture/Apply boundary
//   4. non-vacuous slot disk round-trip (vacuous-green kill, on-disk TEXT proof, fresh load)
//   5. invalid-op no-op + duplicate/out-of-range slot tolerance
//   6. back-compat (missing/unknown ids) + corrupt/missing -> default order

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class HakoniwaProbe
{
    const float EPS = 1e-4f;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "hakoniwa_probe");
    static string TempPath => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);

            fail = Section1_GridArithmetic()
                ?? Section2_SlotHitTest()
                ?? Section3_ControllerReorder(spawned)
                ?? Section4_DiskRoundTripNonVacuous(spawned)
                ?? Section5_InvalidOpAndTolerance(spawned)
                ?? Section6_BackCompatAndFallback(spawned);
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA PASS] grid arithmetic (ceil(√n) 5->3x2, equal/cover/non-overlap, empty 6th) + " +
                      "SlotAt hit-test + controller order->cell reorder (real RectTransform) + Capture/Apply boundary + " +
                      "non-vacuous slot disk round-trip (on-disk text proof, fresh load) + invalid-op no-op + " +
                      "duplicate/out-of-range slot tolerance + back-compat/missing-id + corrupt/missing fallback " +
                      "(Unity-owned versioned schema, slot-only persist, ADR-0003 capability parity, under Unity Mono)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. grid arithmetic ----
    static string Section1_GridArithmetic()
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
            if (cells[i].minX < -EPS || cells[i].maxX > 1f + EPS || cells[i].minY < -EPS || cells[i].maxY > 1f + EPS)
                return $"S1: cell {i} escapes [0,1]";
        }
        // slot 0 = top-left: minX=0, maxY=1. slot 2 = top-right: maxX=1, maxY=1.
        if (!Approx(cells[0].minX, 0f) || !Approx(cells[0].maxY, 1f)) return "S1: slot 0 not top-left";
        if (!Approx(cells[2].maxX, 1f) || !Approx(cells[2].maxY, 1f)) return "S1: slot 2 not top-right";
        // slot 3 = bottom-left: minX=0, minY=0. slot 4 below slot 1 (same column).
        if (!Approx(cells[3].minX, 0f) || !Approx(cells[3].minY, 0f)) return "S1: slot 3 not bottom-left";
        if (!Approx(cells[4].minX, cells[1].minX)) return "S1: slot 4 not under slot 1 (row-major broke)";
        if (cells[4].maxY > cells[1].minY + EPS) return "S1: slot 4 not below slot 1 (Y not top-down)";

        // pairwise interior non-overlap.
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
                if (InteriorsOverlap(cells[i], cells[j]))
                    return $"S1: cells {i} and {j} overlap";
        return null;
    }

    // ---- 2. SlotAt hit-test ----
    static string Section2_SlotHitTest()
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

    // ---- 3. controller reorder on a real RectTransform tree ----
    static string Section3_ControllerReorder(List<GameObject> spawned)
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

    // ---- 4. non-vacuous slot disk round-trip (vacuous-green kill) ----
    static string Section4_DiskRoundTripNonVacuous(List<GameObject> spawned)
    {
        var c1 = BuildController(spawned, out _, HakoniwaController.DEFAULT_ORDER);
        LayoutDocument baseline = c1.Capture();   // slots 0..4 in default order

        c1.Swap(0, 4);                              // chart<->run_result
        LayoutDocument mutated = c1.Capture();
        if (LayoutDocument.StructurallyEqual(mutated, baseline, EPS))
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
        if (LayoutDocument.StructurallyEqual(loaded, baseline, EPS))
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

    // ---- 5. invalid-op no-op + duplicate/out-of-range slot tolerance ----
    static string Section5_InvalidOpAndTolerance(List<GameObject> spawned)
    {
        var c = BuildController(spawned, out _, HakoniwaController.DEFAULT_ORDER);
        var before = new List<string>(c.Order);

        if (c.Swap(2, 2)) return "S5: Swap(i,i) should be a no-op";
        if (c.Swap(0, 99)) return "S5: out-of-range Swap should be a no-op";
        if (c.Swap(-1, 1)) return "S5: negative Swap should be a no-op";
        if (!SameOrder(c.Order, before)) return "S5: a no-op Swap changed the order";

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
        if (!SameOrder(c.Order, new List<string>(expected)))
            return "S5: duplicate/out-of-range slots did not normalize to the expected order";
        return null;
    }

    // ---- 6. back-compat (missing/unknown ids) + corrupt/missing fallback ----
    static string Section6_BackCompatAndFallback(List<GameObject> spawned)
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

    // ---- helpers ----

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
        return ix > EPS && iy > EPS;
    }

    static bool SameOrder(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;
}
