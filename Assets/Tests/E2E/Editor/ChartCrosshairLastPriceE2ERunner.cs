// ChartCrosshairLastPriceE2ERunner.cs — S4 #159 / findings 0119 D-6: crosshair (hover) +
// last-price dashed line。台本: same-dir ChartCrosshairLastPriceE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartCrosshairLastPriceE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART CROSSHAIR LASTPRICE PASS] / exit=0
//
// WHAT THIS GATES:
//   CROSSHAIR-01: SetCrosshairCursorForTest(plot-center) → CrosshairState fields populated
//                 (cursor_world / hovered_price / hovered_time_ms); OnPopulateMesh emits 2 crosshair
//                 lines into the Mesh batch (LastCrosshairLineCount==2). Exit → all CrosshairState
//                 fields null + LastCrosshairLineCount==0.
//   CROSSHAIR-MAIN-ONLY-01: cursor BELOW main_area (S5 forward-compat: VolumeFrac=0 so this is below
//                 the plot rect, exiting the chart) → hovered_price=null (findings 0119 D-2: price
//                 derive lives in main_area only).
//   LASTPRICE-01: Render(non-empty bars) → LastLastPriceLineCount > 0 (dashed segments emitted at
//                 latest close price). Render(empty) → 0.
//
// LITMUS:
//  * Strip the cross-line emit pair in OnPopulateMesh → CROSSHAIR-01 RED.
//  * Strip the last-price emit loop → LASTPRICE-01 RED.
//  * Forget Crosshair.Clear() on exit → CROSSHAIR-01 exit assert RED (stale hovered_price stays).

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class ChartCrosshairLastPriceE2ERunner
{
    const int TOTAL_BARS = 200;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_Crosshair()
                ?? Section2_CrosshairMainOnly()
                ?? Section3_LastPriceLine();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART CROSSHAIR LASTPRICE PASS] (CROSSHAIR-01) hover sets CrosshairState + "
                    + "Mesh has 2 cross lines; exit clears state and lines; (CROSSHAIR-MAIN-ONLY-01) cursor "
                    + "outside main_area → hovered_price=null (D-2); (LASTPRICE-01) bars present → dashed "
                    + "segments emitted at latest close, bars empty → 0 segments.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART CROSSHAIR LASTPRICE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_Crosshair()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetGranularity(GranularityChoice.Daily);
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            // Cursor at plot-center: rectTransform is centered at origin with size 620×400 so plot
            // (gutter L=60 / B=40 / R=20 / T=20) center ≈ (10, 0).
            cv.SetCrosshairCursorForTest(new Vector2(10f, 0f));
            Canvas.ForceUpdateCanvases();
            if (!cv.Crosshair.cursor_world.HasValue)
                return "S1 CROSSHAIR-01: cursor_world unset after SetCrosshairCursorForTest";
            if (!cv.Crosshair.hovered_price.HasValue)
                return "S1 CROSSHAIR-01: hovered_price unset for cursor inside main_area";
            if (!cv.Crosshair.hovered_time_ms.HasValue)
                return "S1 CROSSHAIR-01: hovered_time_ms unset";
            if (cv.LastCrosshairLineCount != 2)
                return "S1 CROSSHAIR-01: LastCrosshairLineCount=" + cv.LastCrosshairLineCount
                     + ", want 2 (vertical + horizontal crosshair quads)";
            // Exit → cleared.
            ((IPointerExitHandler)cv).OnPointerExit(new PointerEventData(EnsureES()));
            Canvas.ForceUpdateCanvases();
            if (cv.Crosshair.cursor_world.HasValue)
                return "S1 CROSSHAIR-01: cursor_world not cleared on pointer exit (Clear() regression)";
            if (cv.Crosshair.hovered_price.HasValue)
                return "S1 CROSSHAIR-01: hovered_price not cleared on exit";
            if (cv.LastCrosshairLineCount != 0)
                return "S1 CROSSHAIR-01: LastCrosshairLineCount=" + cv.LastCrosshairLineCount
                     + " after exit, want 0";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // Cursor in the gutter (outside plot rect) → hovered_price should be null.
    static string Section2_CrosshairMainOnly()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            // Cursor BELOW the plot (in the bottom gutter): rect.yMin = -200, GutterBottom = 40 →
            // plot.yMin = -160. Cursor at y = -180 sits in the gutter.
            cv.SetCrosshairCursorForTest(new Vector2(0f, -180f));
            Canvas.ForceUpdateCanvases();
            if (cv.Crosshair.hovered_price.HasValue)
                return "S2 CROSSHAIR-MAIN-ONLY-01: hovered_price=" + cv.Crosshair.hovered_price
                     + " for cursor in bottom gutter — main_area-only derive (findings 0119 D-2) regression";
            if (cv.LastCrosshairLineCount != 0)
                return "S2 CROSSHAIR-MAIN-ONLY-01: cursor outside plot rect produced "
                     + cv.LastCrosshairLineCount + " cross lines (gutter cursor should not render).";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section3_LastPriceLine()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
            Canvas.ForceUpdateCanvases();
            if (cv.LastLastPriceLineCount <= 0)
                return "S3 LASTPRICE-01: LastLastPriceLineCount=" + cv.LastLastPriceLineCount
                     + " with " + TOTAL_BARS + " bars — last-price dashed line emit loop missing.";
            // Empty render → 0.
            cv.Render(new ReplayBarFrame { Ohlc = Array.Empty<OhlcPoint>() });
            Canvas.ForceUpdateCanvases();
            if (cv.LastLastPriceLineCount != 0)
                return "S3 LASTPRICE-01: empty Render still produced " + cv.LastLastPriceLineCount
                     + " segments — last-price line should be 0 when _bars is empty.";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers ----
    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartCrosshairCanvas", typeof(Canvas));
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

    static EventSystem EnsureES()
    {
        var es = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
        if (es != null) return es;
        return new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
    }
}
