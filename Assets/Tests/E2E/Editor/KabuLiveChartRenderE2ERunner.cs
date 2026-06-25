// KabuLiveChartRenderE2ERunner.cs — Surface E2E regression gate for the RENDER half of the
// live chart: "a live-mode state JSON whose per_instrument[id].ohlc_points is non-empty MUST
// paint candles in ChartView; an empty ohlc series MUST paint nothing (even when depth is present)."
// 台本: same-dir KabuLiveChartRenderE2ERunner.md. 出自: 2026-06-25 kabu live chart-not-updating 調査
// (python/spike/kabu_push_probe.py + kabu_pipeline_probe.py が実 prod で「板は live・約定が来れば
// per_instrument[id].ohlc_points が C# 向け JSON まで埋まる」を実証。fixture = 実 prod 9501 capture)。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod KabuLiveChartRenderE2ERunner.Run -logFile <abs>
//   # expect: [E2E KABU LIVE CHART RENDER PASS] ... / exit=0  (確認は Bash `grep -a "CHARTRENDER"`)
//   # compile-only: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHY THIS GATE EXISTS — the gap no existing runner covers:
//   * AddChartLadderJourneyE2ERunner (ADDLADDER-01..05) gates SPAWN/COMPOSITION (tile composes
//     ChartView + ladder, ladder active/inset). It explicitly never feeds a state JSON nor inspects
//     painted candles ("It only decodes the STATE JSON — it never inspects the spawned chart window").
//   * LiveSubscribeWiringE2ERunner / DepthLadderE2ERunner gate the DEPTH half (DepthDecoder.HasDepth).
//   * NOBODY gated: state JSON -> InstrumentOhlcDecoder.Decode -> ChartView.Render -> candles painted.
//     The live chart-not-updating report (board full, chart empty) lives exactly in this seam.
//
// WHAT IT PINS (the investigation's conclusion, made a deterministic gate):
//   CHARTRENDER-01: REAL prod capture (9501 @ ~480, 14 per-instrument bars + 10x10 board, AND a
//     top-level ohlc_points) -> Decode("9501.TSE").HasSeries=true, count=14 (NOT the top-level series)
//     -> Render paints 2*14 candle rects. Proves the prod-shaped JSON renders end-to-end in C#.
//   CHARTRENDER-02 (non-vacuity / absent-id control): Decode the SAME JSON for an id NOT present
//     -> HasSeries=false -> Render(empty) paints 0 candles. Floors CHARTRENDER-01 (a decoder that
//     returned a constant non-empty series would make 01 vacuous; this catches it).
//   CHARTRENDER-03 (THE reported symptom, deterministic repro): depth present but ohlc_points=[]
//     (board streaming, no trades aggregated yet) -> HasSeries=true, count=0 -> Render paints 0
//     candles. This is "板満杯・チャート空": the empty chart is the empty TRADE series, not a
//     decode/render fault. 01 vs 03 pin "candles appear  <=>  per_instrument ohlc_points non-empty".
//
// LITMUS (delete-the-production-logic): break InstrumentOhlcDecoder (return Empty for valid ohlc) or
// ChartView.Render (skip AddCandleRect) -> CHARTRENDER-01 RED. Make Render paint on an empty series
// -> CHARTRENDER-03 RED. Python-FREE: no InitializePython; pure C# decode+paint on synthetic state.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class KabuLiveChartRenderE2ERunner
{
    const string IID = "9501.TSE";

    // depth present, ohlc empty (board streaming, no trades yet) — the reported "board full, chart empty".
    const string EMPTY_OHLC_STATE =
        "{\"execution_mode\":\"LiveManual\",\"per_instrument\":{\"9501.TSE\":{\"price\":480.9,"
        + "\"ohlc_points\":[],\"depth\":{\"bids\":[{\"price\":480.8,\"size\":3600.0}],"
        + "\"asks\":[{\"price\":480.9,\"size\":2100.0}]}}}}";

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_RealProdJsonPaintsCandles()
                ?? Section2_AbsentIdPaintsNothing()
                ?? Section3_EmptyOhlcPaintsNothing();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E KABU LIVE CHART RENDER PASS] real prod live-state JSON (9501 @ ~480, 14 "
                + "per_instrument bars + 10x10 board + a top-level ohlc_points) decodes to the PER-INSTRUMENT "
                + "series (not the top-level one) and ChartView paints 2*14 candle rects; an absent id and an "
                + "empty ohlc_points array (depth present) both paint 0 candles. Conclusion of the 2026-06-25 "
                + "kabu live chart investigation: board ticks live + the pipeline fills per_instrument ohlc; "
                + "an empty chart == an empty TRADE series, decode/paint are correct. DATA half (real subscribe "
                + "-> board + bars) = KABU-LIVE-02 / SUBWIRE-02 (HITL prod).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E KABU LIVE CHART RENDER FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── CHARTRENDER-01: real prod capture -> per-instrument decode (14) -> 2*14 candles painted. ──
    static string Section1_RealProdJsonPaintsCandles()
    {
        string fixturePath = Path.Combine(Application.dataPath, "Tests/E2E/Editor/Fixtures/KabuProdChartState.json");
        if (!File.Exists(fixturePath))
            return "S1 CHARTRENDER-01: fixture missing at " + fixturePath;
        string state = File.ReadAllText(fixturePath);

        InstrumentOhlcFrame f = InstrumentOhlcDecoder.Decode(state, IID);
        if (!f.HasSeries)
            return "S1 CHARTRENDER-01: Decode(" + IID + ") HasSeries=false on a state with a non-empty "
                 + "per_instrument ohlc_points (decoder regression, or picked the wrong member)";
        int n = f.Ohlc != null ? f.Ohlc.Count : 0;
        if (n != 14)
            return "S1 CHARTRENDER-01: Decode count=" + n + " want 14 — likely decoded the TOP-LEVEL "
                 + "ohlc_points (1 bar) instead of per_instrument[" + IID + "] (14 bars). Locator regression.";

        var cv = BuildChart(out var host);
        cv.Render(new ReplayBarFrame { Ohlc = f.Ohlc });
        int candles = CountCandles(host);
        if (candles != 2 * n)
            return "S1 CHARTRENDER-01: painted " + candles + " candle rects, want " + (2 * n)
                 + " (2 per bar: wick+body). ChartView.Render regression.";
        Debug.Log("[E2E CHARTRENDER-01 PASS] real prod 9501 live-state -> per_instrument decode (14 bars, "
                + "not the top-level series) -> ChartView painted " + candles + " candle rects.");
        UnityEngine.Object.DestroyImmediate(host.parent.gameObject);  // canvas root (host + canvas)
        return null;
    }

    // ── CHARTRENDER-02: absent id -> HasSeries=false -> Render(empty) -> 0 candles (non-vacuity floor). ──
    static string Section2_AbsentIdPaintsNothing()
    {
        string fixturePath = Path.Combine(Application.dataPath, "Tests/E2E/Editor/Fixtures/KabuProdChartState.json");
        string state = File.ReadAllText(fixturePath);

        InstrumentOhlcFrame f = InstrumentOhlcDecoder.Decode(state, "0000.TSE");  // not in per_instrument
        if (f.HasSeries)
            return "S2 CHARTRENDER-02: Decode of an ABSENT id returned HasSeries=true — decoder ignores the "
                 + "id (CHARTRENDER-01 would be vacuous)";

        var cv = BuildChart(out var host);
        cv.Render(new ReplayBarFrame { Ohlc = f.Ohlc });   // empty series
        int candles = CountCandles(host);
        if (candles != 0)
            return "S2 CHARTRENDER-02: painted " + candles + " candles for an absent id — Render paints on "
                 + "empty (CHARTRENDER-01 vacuous)";
        Debug.Log("[E2E CHARTRENDER-02 PASS] absent id -> HasSeries=false -> ChartView painted 0 candles "
                + "(floors CHARTRENDER-01: candles require a real decoded series).");
        UnityEngine.Object.DestroyImmediate(host.parent.gameObject);  // canvas root (host + canvas)
        return null;
    }

    // ── CHARTRENDER-03: depth present + ohlc empty -> 0 candles (the reported symptom, deterministic). ──
    static string Section3_EmptyOhlcPaintsNothing()
    {
        InstrumentOhlcFrame f = InstrumentOhlcDecoder.Decode(EMPTY_OHLC_STATE, IID);
        // ohlc_points:[] present -> the decoder reports a (empty) series for the id.
        if (!f.HasSeries)
            return "S3 CHARTRENDER-03: Decode of present-but-empty ohlc_points returned HasSeries=false "
                 + "(expected an empty series for the present id)";
        int n = f.Ohlc != null ? f.Ohlc.Count : 0;
        if (n != 0)
            return "S3 CHARTRENDER-03: empty ohlc_points decoded to " + n + " bars, want 0";

        var cv = BuildChart(out var host);
        cv.Render(new ReplayBarFrame { Ohlc = f.Ohlc });
        int candles = CountCandles(host);
        if (candles != 0)
            return "S3 CHARTRENDER-03: painted " + candles + " candles for an EMPTY trade series — the "
                 + "'board full, chart empty' symptom would be a render fault, not an empty series";
        Debug.Log("[E2E CHARTRENDER-03 PASS] depth present + ohlc_points=[] -> ChartView painted 0 candles "
                + "= the reported '板満杯・チャート空' is exactly an empty TRADE series (not a decode/render bug).");
        UnityEngine.Object.DestroyImmediate(host.parent.gameObject);  // canvas root (host + canvas)
        return null;
    }

    // ---- helpers ----
    static ChartView BuildChart(out RectTransform host)
    {
        var canvasGo = new GameObject("ChartRenderCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f);
        host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(540f, 400f);   // mirror the screenshot tile
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        return cv;
    }

    // ChartView paints candle rects as GameObjects named "c" under a "Candles" RectTransform.
    static int CountCandles(RectTransform host)
    {
        foreach (var t in host.GetComponentsInChildren<Transform>(true))
            if (t.name == "Candles") return t.childCount;
        return 0;
    }
}
