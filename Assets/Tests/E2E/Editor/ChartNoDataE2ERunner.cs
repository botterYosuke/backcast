// ChartNoDataE2ERunner.cs — #182 (副次 AC): a chart with no bar series shows a "no data" marker
// and suppresses the misleading 1970-epoch / stale time axis. 台本: same-dir ChartNoDataE2ERunner.md.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartNoDataE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART NO DATA PASS] / exit=0
//
// WHAT THIS GATES:
//   CHART-NODATA-01: a freshly-built ChartView (never rendered) shows NoDataShown=true and emits
//                    ZERO active time-axis labels — no default 1970-epoch axis frames the empty plot.
//   CHART-NODATA-02: a chart rendered WITH a series (active time labels > 0, NoDataShown=false) that
//                    is then rendered EMPTY drops back to NoDataShown=true with ZERO active time
//                    labels — the stale axis is cleared, not left framing an empty chart. (The
//                    non-vacuous case: a never-rendered chart already has 0 labels; this proves the
//                    clear-on-empty path actually runs.)
//
// LITMUS (delete-the-production-logic):
//  * Remove `_noDataLabel`/UpdateNoData → NoDataShown=false → CHART-NODATA-01/02 RED.
//  * Drop the `_lastTimeTicks.Clear()` + `_axisLabelsDirty=true` in Render's empty branch →
//    CHART-NODATA-02 keeps the stale labels (ActiveTimeLabelCount stays > 0) → RED.

using System;
using UnityEditor;
using UnityEngine;

public static class ChartNoDataE2ERunner
{
    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_FreshChartShowsNoData()
                ?? Section2_SeriesThenEmptyClearsAxis();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART NO DATA PASS] (CHART-NODATA-01) fresh ChartView shows NoDataShown=true "
                    + "with 0 active time labels (no 1970-epoch axis); (CHART-NODATA-02) a rendered series "
                    + "going empty drops to NoDataShown=true and clears the stale time axis to 0 labels.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART NO DATA FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_FreshChartShowsNoData()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            if (!cv.NoDataShown)
                return "S1 CHART-NODATA-01: a freshly-built chart must show the no-data marker (NoDataShown=false).";
            if (cv.ActiveTimeLabelCount != 0)
                return "S1 CHART-NODATA-01: a never-rendered chart must emit 0 active time labels, got "
                     + cv.ActiveTimeLabelCount + " — a default time axis is framing the empty plot.";
            Debug.Log("[E2E CHART-NODATA-01 PASS] fresh chart → NoDataShown, 0 active time labels.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section2_SeriesThenEmptyClearsAxis()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.SetGranularity(GranularityChoice.Daily);
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(120) });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            // Precondition: with a series the chart shows real labels and no marker (non-vacuity floor).
            if (cv.NoDataShown)
                return "S2 CHART-NODATA-02: with a rendered series the no-data marker must be hidden.";
            if (cv.ActiveTimeLabelCount <= 0)
                return "S2 CHART-NODATA-02: precondition failed — a rendered Daily series must emit "
                     + "active time labels (so the empty-after clear is a real transition, not vacuous).";

            // Now the series goes empty (instrument left the window / streamed nothing).
            cv.Render(new ReplayBarFrame { Ohlc = Array.Empty<OhlcPoint>() });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            if (!cv.NoDataShown)
                return "S2 CHART-NODATA-02: after the series went empty the no-data marker must show.";
            if (cv.ActiveTimeLabelCount != 0)
                return "S2 CHART-NODATA-02: stale time axis not cleared — ActiveTimeLabelCount="
                     + cv.ActiveTimeLabelCount + " after the series went empty (1970/stale axis persists).";

            // Litmus tail: rendering a series again brings the axis back and hides the marker.
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(120) });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            if (cv.NoDataShown || cv.ActiveTimeLabelCount <= 0)
                return "S2 CHART-NODATA-02: re-rendering a series must hide the marker and restore labels.";
            Debug.Log("[E2E CHART-NODATA-02 PASS] series→empty clears the axis to 0 labels + shows marker; re-render restores it.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers (mirror ChartAxisGridE2ERunner) ----
    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartNoDataCanvas", typeof(Canvas));
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
}
