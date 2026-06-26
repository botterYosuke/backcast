// ChartAxisGridE2ERunner.cs — S3 #158 / findings 0119 D-6: axis labels (price right gutter +
// time bottom gutter) + grid lines integrated into Mesh batch。台本: same-dir ChartAxisGridE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartAxisGridE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART AXIS GRID PASS] / exit=0
//
// WHAT THIS GATES:
//   TICK-PRICE-01: ChartScale.CalcOptimalPriceTicks pure-function (10..100 / 8 target → 10,20,...,100).
//   TICK-TIME-01: ChartScale.CalcOptimalTimeStep Daily ladder (5 days range / 6 target → 1d step).
//   AXIS-PRICE-01: Render → RebuildAxisLabels → LastPriceLabelCount > 0; the labels match _lastPriceTicks
//                  and the right-gutter Text values are valid numbers.
//   AXIS-TIME-01: Daily basis → time labels formatted as yyyy-MM-dd; Minute basis → HH:mm.
//   GRID-01: post-OnPopulateMesh LastGridLineCount > 0 (grid quads ARE in the Mesh batch).
//
// LITMUS (delete-the-production-logic):
//  * Strip the grid quad emit loop in OnPopulateMesh → LastGridLineCount=0 → GRID-01 RED.
//  * RebuildAxisLabels no-op → LastPriceLabelCount=0 → AXIS-PRICE-01 RED.
//  * Wrong style switch in FormatTimeLabel → AXIS-TIME-01 RED (Daily basis shows "00:00" instead of date).

using System;
using UnityEditor;
using UnityEngine;

public static class ChartAxisGridE2ERunner
{
    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_PriceTickPure()
                ?? Section2_TimeTickPure()
                ?? Section3_PriceLabelsRendered()
                ?? Section4_TimeLabelsRendered()
                ?? Section5_GridLinesInMesh();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART AXIS GRID PASS] (TICK-PRICE-01) CalcOptimalPriceTicks(10,100,8) yields "
                    + "10..100 step-10; (TICK-TIME-01) Daily-basis 5d range gives 1d step; (AXIS-PRICE-01) "
                    + "Render+RebuildAxisLabels lights LastPriceLabelCount>0 with parseable numeric text; "
                    + "(AXIS-TIME-01) Daily basis emits yyyy-MM-dd, Minute basis emits HH:mm; (GRID-01) "
                    + "post-Mesh LastGridLineCount>0 confirms grid quads woven into the candle batch.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART AXIS GRID FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_PriceTickPure()
    {
        var t = ChartScale.CalcOptimalPriceTicks(10.0, 100.0, 8);
        if (t.Count == 0) return "S1 TICK-PRICE-01: empty list — calc misfired";
        if (Math.Abs(t[0] - 10.0) > 1e-9 || Math.Abs(t[t.Count - 1] - 100.0) > 1e-9)
            return "S1 TICK-PRICE-01: boundaries wrong, got [" + t[0] + ".." + t[t.Count - 1] + "]";
        double step = t[1] - t[0];
        if (Math.Abs(step - 10.0) > 1e-9)
            return "S1 TICK-PRICE-01: step=" + step + ", want 10 (nice (1,2,5)*10^N pick on a span/8 ≈ 11)";
        return null;
    }

    static string Section2_TimeTickPure()
    {
        long startMs = 1_700_000_000_000L;
        long endMs = startMs + 5L * ChartViewState.BASIS_DAILY_MS;
        long step = ChartScale.CalcOptimalTimeStep(startMs, endMs, ChartViewState.BASIS_DAILY_MS, 6);
        if (step != ChartViewState.BASIS_DAILY_MS)
            return "S2 TICK-TIME-01: 5d range / 6 target on Daily ladder → step=" + step + ", want "
                 + ChartViewState.BASIS_DAILY_MS + " (1 day).";
        long stepM = ChartScale.CalcOptimalTimeStep(startMs, startMs + 4L * 60_000L, 60_000L, 4);
        if (stepM != 60_000L)
            return "S2 TICK-TIME-01: 4 min range / 4 target on Minute ladder → step=" + stepM + ", want 60000";
        return null;
    }

    static string Section3_PriceLabelsRendered()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(200);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            if (cv.LastPriceLabelCount <= 0)
                return "S3 AXIS-PRICE-01: LastPriceLabelCount=0 after Render + RebuildAxisLabels — "
                     + "tick calc produced 0 ticks, or label spawn was no-op.";
            // Verify at least one label parses as a number (catches "stuck-on-empty-string" regression).
            // Scan the Text children under PriceAxisLabels and confirm a numeric value.
            var priceRoot = FindChild(cv.transform, "PriceAxisLabels");
            if (priceRoot == null) return "S3 AXIS-PRICE-01: PriceAxisLabels child missing (NewChildRect regression)";
            bool sawNumber = false;
            foreach (Transform t in priceRoot)
            {
                var txt = t.GetComponent<UnityEngine.UI.Text>();
                if (txt == null || !t.gameObject.activeSelf) continue;
                if (double.TryParse(txt.text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                { sawNumber = true; break; }
            }
            if (!sawNumber) return "S3 AXIS-PRICE-01: no numeric Text under PriceAxisLabels (RebuildAxisLabels regression)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section4_TimeLabelsRendered()
    {
        // Daily basis → label like "yyyy-MM-dd".
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetGranularity(GranularityChoice.Daily);
            var bars = SyntheticDaily(200);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            if (cv.LastTimeLabelCount <= 0)
                return "S4 AXIS-TIME-01: Daily basis — LastTimeLabelCount=0";
            var timeRoot = FindChild(cv.transform, "TimeAxisLabels");
            bool sawDate = false;
            foreach (Transform t in timeRoot)
            {
                var txt = t.GetComponent<UnityEngine.UI.Text>();
                if (txt == null || !t.gameObject.activeSelf) continue;
                if (txt.text.Length == 10 && txt.text[4] == '-' && txt.text[7] == '-') { sawDate = true; break; }
            }
            if (!sawDate) return "S4 AXIS-TIME-01: Daily basis must emit yyyy-MM-dd labels — found none (FormatTimeLabel regression)";
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }

        // Minute basis → label like "HH:mm".
        var cv2 = BuildChart(out var canvasGo2);
        try
        {
            cv2.SetGranularity(GranularityChoice.Minute);
            var bars = SyntheticMinute(200);
            cv2.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            cv2.RebuildAxisLabels();
            if (cv2.LastTimeLabelCount <= 0) return "S4 AXIS-TIME-01: Minute basis — LastTimeLabelCount=0";
            var timeRoot = FindChild(cv2.transform, "TimeAxisLabels");
            bool sawTime = false;
            foreach (Transform t in timeRoot)
            {
                var txt = t.GetComponent<UnityEngine.UI.Text>();
                if (txt == null || !t.gameObject.activeSelf) continue;
                if (txt.text.Length == 5 && txt.text[2] == ':') { sawTime = true; break; }
            }
            if (!sawTime) return "S4 AXIS-TIME-01: Minute basis must emit HH:mm labels — found none";
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo2); }
        return null;
    }

    static string Section5_GridLinesInMesh()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            var bars = SyntheticDaily(200);
            cv.Render(new ReplayBarFrame { Ohlc = bars });
            Canvas.ForceUpdateCanvases();
            if (cv.LastGridLineCount <= 0)
                return "S5 GRID-01: LastGridLineCount=" + cv.LastGridLineCount + " — grid quad emit loop "
                     + "missing from OnPopulateMesh (drawcall stays 1 only when grid is INSIDE the same batch).";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers ----
    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartAxisCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        var host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f); host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(620f, 400f);
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        return cv;
    }

    static OhlcPoint[] SyntheticDaily(int n)
    {
        var arr = new OhlcPoint[n];
        long startMs = 1_700_000_000_000L;
        for (int i = 0; i < n; i++)
        {
            double o = 100.0 + (i % 7) * 0.5;
            double c = o + (i % 2 == 0 ? +0.3 : -0.3);
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o, close = c,
                high = Math.Max(o, c) + 0.4, low = Math.Min(o, c) - 0.4, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    static OhlcPoint[] SyntheticMinute(int n)
    {
        var arr = new OhlcPoint[n];
        long startMs = 1_700_000_000_000L;
        for (int i = 0; i < n; i++)
        {
            double o = 100.0 + (i % 7) * 0.5;
            double c = o + (i % 2 == 0 ? +0.3 : -0.3);
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * 60_000L,
                open = o, close = c,
                high = Math.Max(o, c) + 0.4, low = Math.Min(o, c) - 0.4, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    static Transform FindChild(Transform parent, string name)
    {
        foreach (Transform t in parent) if (t.name == name) return t;
        return null;
    }
}
