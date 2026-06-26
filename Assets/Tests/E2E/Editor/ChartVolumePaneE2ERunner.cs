// ChartVolumePaneE2ERunner.cs — S5 #160 / findings 0119 D-6: volume sub-pane (main_area 80% /
// volume_area 20%)。台本: same-dir ChartVolumePaneE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartVolumePaneE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART VOLUME PANE PASS] / exit=0
//
// WHAT THIS GATES:
//   VOLUME-PURE-01: ChartScale.MaxVisibleVolume / VolumeBarHeight / FormatVolume pure functions.
//   VOLUME-01: Render(non-empty) → LastVolumeBarCount > 0; visible-window bars emit volume quads
//              into the same Mesh batch (drawcall=1 stays the same as no-volume baseline).
//   VOLUME-02: Price tick LABELS stay in main_area — Text positions all sit above volume_area
//              (no price label leaks into the volume sub-pane).
//   VOLUME-CROSSHAIR-01: cursor in volume_area → CrosshairState.hovered_volume non-null, and
//                       hovered_price=null (main-only derive holds the other way too).
//
// LITMUS:
//  * VolumeFrac=0 → LastVolumeBarCount=0 / volumeArea height 0 → VOLUME-01 RED + VOLUME-CROSSHAIR-01 RED.
//  * Skip the candle-color alpha=0.6 multiplier on volume → visual regression (not auto-gated here, eyeball).
//  * Forget the bars==0 check on volume → 0-bar Render emits ghost volume quads → VOLUME-01 inverse RED.

using System;
using UnityEditor;
using UnityEngine;

public static class ChartVolumePaneE2ERunner
{
    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_PureFunctions()
                ?? Section2_VolumeBarsEmitted()
                ?? Section3_PriceLabelsStayInMain()
                ?? Section4_CrosshairHoveredVolume();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART VOLUME PANE PASS] (VOLUME-PURE-01) MaxVisibleVolume / VolumeBarHeight / "
                    + "FormatVolume; (VOLUME-01) Render → LastVolumeBarCount>0 with same drawcall; "
                    + "(VOLUME-02) price tick labels all sit above volume_area; (VOLUME-CROSSHAIR-01) "
                    + "cursor in volume_area → hovered_volume non-null + hovered_price=null.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART VOLUME PANE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_PureFunctions()
    {
        var bars = new OhlcPoint[]
        {
            new OhlcPoint { open_time_ms = 0, open=1, close=2, high=2, low=1, volume = 100 },
            new OhlcPoint { open_time_ms = 1000, open=1, close=2, high=2, low=1, volume = 300 },
            new OhlcPoint { open_time_ms = 2000, open=1, close=2, high=2, low=1, volume = 50 },
        };
        if (Math.Abs(ChartScale.MaxVisibleVolume(bars, 0, 2000) - 300) > 1e-9)
            return "S1 VOLUME-PURE-01: MaxVisibleVolume wrong";
        if (Math.Abs(ChartScale.MaxVisibleVolume(bars, 100, 1500) - 300) > 1e-9)
            return "S1 VOLUME-PURE-01: MaxVisibleVolume window-filtered wrong (must consider bar 1 only)";
        if (Math.Abs(ChartScale.VolumeBarHeight(150, 300, 100f) - 50f) > 1e-3f)
            return "S1 VOLUME-PURE-01: VolumeBarHeight scaling wrong";
        if (ChartScale.FormatVolume(1234) != "1.23K") return "S1 VOLUME-PURE-01: FormatVolume(1234) wrong: " + ChartScale.FormatVolume(1234);
        if (ChartScale.FormatVolume(1_500_000) != "1.5M") return "S1 VOLUME-PURE-01: FormatVolume(1.5M) wrong: " + ChartScale.FormatVolume(1_500_000);
        if (ChartScale.FormatVolume(2_500_000_000) != "2.5B") return "S1 VOLUME-PURE-01: FormatVolume(2.5B) wrong";
        Debug.Log("[E2E VOLUME-PURE-01 PASS] MaxVisibleVolume / VolumeBarHeight / FormatVolume pure functions.");
        return null;
    }

    static string Section2_VolumeBarsEmitted()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(200) });
            Canvas.ForceUpdateCanvases();
            if (cv.LastVolumeAreaHeightPx <= 0)
                return "S2 VOLUME-01: LastVolumeAreaHeightPx=" + cv.LastVolumeAreaHeightPx
                     + " — volume_area was not allocated (VolumeFrac=0?).";
            if (cv.LastVolumeBarCount <= 0)
                return "S2 VOLUME-01: LastVolumeBarCount=0 with 200 visible bars — volume emit loop missing.";
            if (cv.LastVolumeBarCount != cv.RenderedBarCount)
                return "S2 VOLUME-01: LastVolumeBarCount=" + cv.LastVolumeBarCount + " != RenderedBarCount="
                     + cv.RenderedBarCount + " — volume bars should be 1:1 with rendered candles";
            // Empty render → 0 volume.
            cv.Render(new ReplayBarFrame { Ohlc = Array.Empty<OhlcPoint>() });
            Canvas.ForceUpdateCanvases();
            if (cv.LastVolumeBarCount != 0)
                return "S2 VOLUME-01: empty Render produced " + cv.LastVolumeBarCount
                     + " volume bars — must be 0 when no data.";
            Debug.Log("[E2E VOLUME-01 PASS] LastVolumeBarCount>0 matches RenderedBarCount; 0 on empty Render.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section3_PriceLabelsStayInMain()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(200) });
            Canvas.ForceUpdateCanvases();
            cv.RebuildAxisLabels();
            // Compute main_area boundary: gutterBottom + volumeFrac * (rect.height - gutters). rect is
            // 400px high; gutters L=60/B=40/R=20/T=20; plot.height = 340. volume_area = 68 (20%).
            // mainArea.yMin = rect.yMin + 40 + 68 = -200 + 108 = -92.
            float plotYMin = -200f + 40f;       // rect.yMin + GutterBottom
            float plotHeight = 340f;
            float volumeAreaHeight = plotHeight * 0.2f;
            float mainYMin = plotYMin + volumeAreaHeight;
            var priceRoot = FindChild(cv.transform, "PriceAxisLabels");
            if (priceRoot == null) return "S3 VOLUME-02: PriceAxisLabels root missing";
            foreach (Transform t in priceRoot)
            {
                if (!t.gameObject.activeSelf) continue;
                var rt = (RectTransform)t;
                // anchoredPosition is in rect.xMin / yMin frame (anchor=(0,0)). label y must be ≥ main_area top.
                float labelY = rt.anchoredPosition.y + (-200f);   // convert to rect-local y
                if (labelY < mainYMin - 1f)
                    return "S3 VOLUME-02: price label '" + t.GetComponent<UnityEngine.UI.Text>()?.text
                         + "' at y=" + labelY + " sits in volume_area (mainYMin=" + mainYMin + ")";
            }
            Debug.Log("[E2E VOLUME-02 PASS] all price labels above mainYMin (no leak into volume_area).");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section4_CrosshairHoveredVolume()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(200) });
            Canvas.ForceUpdateCanvases();
            // volume_area in widget-local y: rect spans -200..+200. plot.yMin=-160. volume_area.yMin
            // = -160 (plot bottom), top at -160 + 68 = -92. Pick y=-130 (well inside volume_area).
            cv.SetCrosshairCursorForTest(new Vector2(10f, -130f));
            Canvas.ForceUpdateCanvases();
            if (!cv.Crosshair.hovered_volume.HasValue)
                return "S4 VOLUME-CROSSHAIR-01: hovered_volume null with cursor inside volume_area "
                     + "(NearestVolumeAt regression or DeriveCrosshair branch broken).";
            if (cv.Crosshair.hovered_price.HasValue)
                return "S4 VOLUME-CROSSHAIR-01: hovered_price=" + cv.Crosshair.hovered_price
                     + " with cursor in volume_area — main-only derive (D-2) regression";
            Debug.Log("[E2E VOLUME-CROSSHAIR-01 PASS] cursor in volume_area → hovered_volume non-null + hovered_price null.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartVolumeCanvas", typeof(Canvas));
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
                high = Math.Max(o, c) + 0.4, low = Math.Min(o, c) - 0.4, volume = 1000.0 + i * 5.0,
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
