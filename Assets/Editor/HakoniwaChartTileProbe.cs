// HakoniwaChartTileProbe.cs — STANDING AFK regression gate for the per-instrument chart OHLC decode
// (#60 "Hakoniwa chart tile family"). The grid/tile sections (box-grow arithmetic, dynamic tile slot
// round-trip) were promoted into HakoniwaE2ERunner (findings 0060); this probe retains ONLY the chart
// numeric-read seam (InstrumentOhlcDecoder), which belongs to the chart category and awaits a future
// Chart-surface E2E runner (held here as a standing regression gate in the meantime). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod HakoniwaChartTileProbe.Run -logFile <log>
//   # expect: [HAKONIWA CHART TILE PASS] ... / exit=0
//
// SECTION:
//   per-instrument OHLC decode: InstrumentOhlcDecoder pulls the RIGHT id's series out of the dict-keyed
//   per_instrument, is not fooled by a decoy string, empties on absent/null, throws on malformed (the
//   shared-locator contract extracted from DepthDecoder).

using System;
using UnityEditor;
using UnityEngine;

public static class HakoniwaChartTileProbe
{
    const float EPS = 1e-3f;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section2_PerInstrumentOhlcDecode();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA CHART TILE PASS] per-id ohlc decode (decoy/null/absent/malformed) verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA CHART TILE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── per-instrument OHLC decode (shared locator, decoy/null/malformed/absent) ──
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
}
