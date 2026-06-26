// ChartScale.cs — S3 #158 / findings 0119 D-6: pure-function tick generation for price (Y) and
// time (X) axes. C# port of flowsurface `data/src/chart/scale/linear.rs::calc_optimal_price_ticks`
// and `scale/timeseries.rs::calc_optimal_time_step` — purely-mathematical, no Unity dependency
// (so the AXIS-PRICE-01 / AXIS-TIME-01 gates can drive these without standing up a Canvas).
//
// PRICE TICKS: pick a "nice" step from (1, 2, 5) × 10^N that yields ≤ target_count tick lines in
// (min, max). Snap the first tick to a step multiple ≥ min so the labels read cleanly ("120", "130"
// instead of "118.42"). Returns sorted ascending.
//
// TIME TICKS: pick a step from a basis-aware ladder:
//   * Daily basis → 1d / 2d / 5d / 10d / 1mo / 3mo / 6mo / 1y / 5y
//   * Minute basis → 1min / 5min / 15min / 1h / 4h / 1d (carry into Daily ladder)
// Snap first tick to a step-aligned timestamp. Returns sorted ascending.
//
// Format helpers are paired with the step so the label widget can decide "Daily → yyyy-MM-dd",
// "Minute → HH:mm" without re-computing what step we used.

using System;
using System.Collections.Generic;
using UnityEngine;   // S5 VolumeBarHeight uses Mathf.Max.

public static class ChartScale
{
    // ---- price ticks ----

    // Returns ascending tick prices for [min, max] aiming for ~targetCount labels (8 is a good default
    // for a 300-400px tall plot). The step is "nice" — (1/2/5) × 10^N — so labels round cleanly.
    public static List<double> CalcOptimalPriceTicks(double min, double max, int targetCount)
    {
        var ticks = new List<double>();
        if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max)) return ticks;
        if (max <= min || targetCount < 1) return ticks;
        double range = max - min;
        double rough = range / targetCount;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double norm = rough / mag;
        double step;
        if (norm < 1.5) step = 1 * mag;
        else if (norm < 3) step = 2 * mag;
        else if (norm < 7) step = 5 * mag;
        else step = 10 * mag;

        double first = Math.Ceiling(min / step) * step;
        for (double t = first; t <= max + step * 1e-6; t += step)
            ticks.Add(t);
        return ticks;
    }

    // Decide a sensible decimal precision for a tick step ("0.1" → 1 dp, "10" → 0 dp, "0.005" → 3 dp).
    public static int PriceTickDecimals(double step)
    {
        if (step <= 0) return 2;
        if (step >= 1) return 0;
        return Math.Min(6, (int)Math.Ceiling(-Math.Log10(step)));
    }

    // ---- time ticks ----

    // Returns ascending tick timestamps (ms) for [startMs, endMs] aiming for ~targetCount labels.
    // basisMs disambiguates the ladder (Daily vs Minute). Step is picked from a fixed basis-aware
    // sequence so labels land on conventional boundaries (start-of-day / start-of-hour).
    public static List<long> CalcOptimalTimeTicks(long startMs, long endMs, long basisMs, int targetCount)
    {
        var ticks = new List<long>();
        if (endMs <= startMs || targetCount < 1) return ticks;
        long step = CalcOptimalTimeStep(startMs, endMs, basisMs, targetCount);
        if (step <= 0) return ticks;
        long first;
        if (basisMs >= ChartViewState.BASIS_DAILY_MS)
        {
            // Snap to start of UTC day.
            var dtStart = DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime;
            var dayStart = new DateTime(dtStart.Year, dtStart.Month, dtStart.Day, 0, 0, 0, DateTimeKind.Utc);
            long dayStartMs = new DateTimeOffset(dayStart).ToUnixTimeMilliseconds();
            long delta = startMs - dayStartMs;
            first = startMs + ((step - (delta % step)) % step);
        }
        else
        {
            // Snap to step-aligned absolute timestamp.
            first = startMs + ((step - (startMs % step)) % step);
        }
        for (long t = first; t <= endMs; t += step) ticks.Add(t);
        return ticks;
    }

    public static long CalcOptimalTimeStep(long startMs, long endMs, long basisMs, int targetCount)
    {
        long range = endMs - startMs;
        if (range <= 0 || targetCount <= 0) return 0;
        long rough = range / targetCount;
        long[] ladder;
        if (basisMs >= ChartViewState.BASIS_DAILY_MS)
        {
            ladder = new long[] {
                1L * ChartViewState.BASIS_DAILY_MS,
                2L * ChartViewState.BASIS_DAILY_MS,
                5L * ChartViewState.BASIS_DAILY_MS,
                10L * ChartViewState.BASIS_DAILY_MS,
                30L * ChartViewState.BASIS_DAILY_MS,     // ~1 month
                90L * ChartViewState.BASIS_DAILY_MS,     // ~3 months
                180L * ChartViewState.BASIS_DAILY_MS,    // ~6 months
                365L * ChartViewState.BASIS_DAILY_MS,    // ~1 year
                5L * 365L * ChartViewState.BASIS_DAILY_MS,
            };
        }
        else
        {
            ladder = new long[] {
                60_000L,                // 1 min
                5L * 60_000L,           // 5 min
                15L * 60_000L,          // 15 min
                60L * 60_000L,          // 1 hour
                4L * 60L * 60_000L,     // 4 hours
                ChartViewState.BASIS_DAILY_MS,   // 1 day (carry into daily ladder)
            };
        }
        // Pick the smallest step ≥ rough.
        for (int i = 0; i < ladder.Length; i++) if (ladder[i] >= rough) return ladder[i];
        return ladder[ladder.Length - 1];
    }

    public enum TimeLabelStyle { Time, Date }

    // Decide label style: Daily/Date for ≥1d step, Time (HH:mm) for sub-day, DateTime for the
    // 1-day-in-minute-basis boundary so a chart that spans days shows the date at midnight.
    public static TimeLabelStyle StyleFor(long stepMs)
    {
        if (stepMs >= ChartViewState.BASIS_DAILY_MS) return TimeLabelStyle.Date;
        if (stepMs >= 60L * 60_000L) return TimeLabelStyle.Time;
        return TimeLabelStyle.Time;
    }

    public static string FormatTimeLabel(long ms, TimeLabelStyle style)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        switch (style)
        {
            case TimeLabelStyle.Date: return dt.ToString("yyyy-MM-dd");
            case TimeLabelStyle.Time: return dt.ToString("HH:mm");
            default: return dt.ToString("HH:mm");
        }
    }

    // ====== S5 #160: volume sub-pane pure functions (findings 0119 D-6 / volume_area) ======

    // Max volume across visible window bars. Used to normalize bar heights in the volume_area.
    public static double MaxVisibleVolume(System.Collections.Generic.IReadOnlyList<OhlcPoint> bars,
        long winStartMs, long winEndMs)
    {
        double max = 0;
        if (bars == null) return 0;
        for (int i = 0; i < bars.Count; i++)
        {
            var p = bars[i];
            if (p.open_time_ms < winStartMs || p.open_time_ms >= winEndMs) continue;
            if (p.volume > max) max = p.volume;
        }
        return max;
    }

    // Volume bar height in px given a target volume + max + the volume_area height.
    public static float VolumeBarHeight(double volume, double maxVolume, float volumeAreaHeightPx)
    {
        if (volume <= 0 || maxVolume <= 0 || volumeAreaHeightPx <= 0) return 0;
        return Mathf.Max(1f, (float)(volume / maxVolume) * volumeAreaHeightPx);
    }

    // K/M/B abbreviation for volume labels and the crosshair readout. 1234 -> "1.2K".
    public static string FormatVolume(double v)
    {
        double abs = Math.Abs(v);
        if (abs >= 1e9) return (v / 1e9).ToString("0.##") + "B";
        if (abs >= 1e6) return (v / 1e6).ToString("0.##") + "M";
        if (abs >= 1e3) return (v / 1e3).ToString("0.##") + "K";
        return v.ToString("0");
    }
}
