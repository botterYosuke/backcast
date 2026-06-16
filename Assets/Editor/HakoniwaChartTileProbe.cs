// HakoniwaChartTileProbe.cs — issue #60 "Hakoniwa chart tile family" (headless AFK regression gate)
//
// The headless, Python-FREE gate for the #60 NEW seams (findings 0027 §8). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod HakoniwaChartTileProbe.Run -logFile <log>
//   # expect: [HAKONIWA CHART TILE PASS] ... / exit=0
//
// SECTIONS:
//   1. box-grow arithmetic: ComputeBoxSize matches TTWR compute_hakoniwa_box_size (n=0 default,
//      min-tile floor, default floor, derived from n).
//   2. per-instrument OHLC decode: InstrumentOhlcDecoder pulls the RIGHT id's series out of the
//      dict-keyed per_instrument, is not fooled by a decoy string, empties on absent/null, throws
//      on malformed (the shared-locator contract extracted from DepthDecoder).
//   3. dynamic tile add/remove + slot round-trip: HakoniwaController.AddTile/RemoveTile mutate the
//      order at runtime; a non-default chart:<id> order survives Capture→save→load→Apply; a stale
//      doc id (old fixed "chart") is skipped; a universe-present/doc-absent id appends.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class HakoniwaChartTileProbe
{
    const float EPS = 1e-3f;
    static readonly Vector2 MIN_TILE = new Vector2(280f, 180f);
    static readonly Vector2 DEFAULT_BOX = new Vector2(700f, 450f);

    static string TempDir => Path.Combine(Application.temporaryCachePath, "hakoniwa_chart_tile_probe");
    static string TempLayout => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            fail = Section1_BoxGrowArithmetic()
                ?? Section2_PerInstrumentOhlcDecode()
                ?? Section3_DynamicTilesRoundTrip(spawned);
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
            Debug.Log("[HAKONIWA CHART TILE PASS] box-grow + per-id ohlc decode + dynamic tile round-trip verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA CHART TILE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. box-grow arithmetic (ComputeBoxSize == compute_hakoniwa_box_size) ──
    static string Section1_BoxGrowArithmetic()
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

    // ── 2. per-instrument OHLC decode (shared locator, decoy/null/malformed/absent) ──
    static string Section2_PerInstrumentOhlcDecode()
    {
        // Two ids with DISTINCT series + a decoy "ohlc_points" inside a string value + a third id
        // whose ohlc_points is null. The decoder must pick each id's real array, not the decoy.
        const string state =
            "{\"price\":1.0,\"live_last_error\":\"spurious ohlc_points: [{x}] in a string\"," +
            "\"per_instrument\":{" +
              "\"6758.TSE\":{\"price\":10.0,\"ohlc_points\":[{\"open\":1,\"high\":2,\"low\":1,\"close\":2,\"volume\":100}],\"depth\":null}," +
              "\"9432.TSE\":{\"price\":20.0,\"ohlc_points\":[{\"open\":3,\"high\":5,\"low\":3,\"close\":4,\"volume\":7},{\"open\":4,\"high\":6,\"low\":4,\"close\":5,\"volume\":8}],\"depth\":null}," +
              "\"7203.TSE\":{\"price\":30.0,\"ohlc_points\":null,\"depth\":null}" +
            "}}";

        var a = InstrumentOhlcDecoder.Decode(state, "6758.TSE");
        if (!a.HasSeries || a.Ohlc.Count != 1) return "ohlc: 6758 series wrong count";
        if (Math.Abs(a.Ohlc[0].close - 2.0) > EPS || Math.Abs(a.Ohlc[0].volume - 100.0) > EPS)
            return "ohlc: 6758 values wrong";

        var b = InstrumentOhlcDecoder.Decode(state, "9432.TSE");
        if (!b.HasSeries || b.Ohlc.Count != 2) return "ohlc: 9432 series wrong count (decoy or cross-id leak?)";
        if (Math.Abs(b.Ohlc[1].close - 5.0) > EPS) return "ohlc: 9432 second bar wrong";

        // id present but ohlc_points:null → empty (no throw).
        var c = InstrumentOhlcDecoder.Decode(state, "7203.TSE");
        if (c.HasSeries || c.Ohlc.Count != 0) return "ohlc: null ohlc_points must be empty";

        // absent id / per_instrument absent / null / empty → empty (no throw).
        if (InstrumentOhlcDecoder.Decode(state, "0000.TSE").HasSeries) return "ohlc: absent id must be empty";
        if (InstrumentOhlcDecoder.Decode("{\"price\":1}", "6758.TSE").HasSeries) return "ohlc: no per_instrument must be empty";
        if (InstrumentOhlcDecoder.Decode("null", "6758.TSE").HasSeries) return "ohlc: \"null\" must be empty";
        if (InstrumentOhlcDecoder.Decode("   ", "6758.TSE").HasSeries) return "ohlc: whitespace must be empty";

        // MALFORMED structure while navigating → FormatException (NOT swallowed).
        bool threw = false;
        try { InstrumentOhlcDecoder.Decode("{\"per_instrument\":{\"6758.TSE\":{\"ohlc_points\":[", "6758.TSE"); }
        catch (FormatException) { threw = true; }
        if (!threw) return "ohlc: malformed json must throw FormatException";
        return null;
    }

    // ── 3. dynamic tile add/remove + slot non-collapsing round-trip ──
    static string Section3_DynamicTilesRoundTrip(List<GameObject> spawned)
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
        LayoutStore.Save(doc, TempLayout);
        string text = File.ReadAllText(TempLayout);
        if (!text.Contains("chart:A") || !text.Contains("chart:B")) return "dynamic: chart ids not on disk";
        var loaded = LayoutStore.Load(TempLayout);

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

    static bool Approx(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < EPS && Mathf.Abs(a.y - b.y) < EPS;
}
