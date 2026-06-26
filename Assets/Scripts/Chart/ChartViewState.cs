// ChartViewState.cs — per-window pan/zoom/auto_scale state for ChartView (S1, #155).
//
// 方針: ADR-0034 §2 / findings 0119 D-2. The Unity翻訳 of flowsurface's ChartViewState
// (translation_ms / cell_width / cell_height / basis), held as plain mutable data on the
// MonoBehaviour-side (not a ScriptableObject) so multiple chart windows opened on the same
// instrument each carry their own independent state.
//
// FIELDS (matching findings 0119 D-2):
//   * translation_ms : long      — visible window's LEFT-EDGE timestamp (the bar open_time_ms
//                                  at the leftmost pixel of the plot rect).
//   * cell_width_px  : float     — one bar's horizontal pixel footprint (MIN / DEFAULT / MAX
//                                  clamped). 6.0f default → ~90 bars visible on a 540px plot.
//   * auto_scale     : bool      — TRUE = autoscale price-axis to visible bars' high/low and
//                                  right-anchor translation_ms to the latest bar. Pan / zoom
//                                  set FALSE; ResetView() sets TRUE.
//   * basis_ms       : long?     — derived from granularity. Daily=86_400_000, Minute=60_000,
//                                  null=hold previous (no-change). Auto-inferred from bar
//                                  open_time_ms diffs when SetGranularity hasn't been called.
//   * cell_height_norm : float   — auto-scaled px / (price_range / tick_size). Derived; NOT
//                                  persisted (findings 0119 D-7: only translation_ms +
//                                  cell_width_px + auto_scale go to the layout sidecar).
//
// DERIVED (px<->price / px<->time) live HERE so OnPopulateMesh stays a thin renderer that
// asks ViewState for "where does bar i go on screen". The same conversions are reused by S2
// (drag pan: dx_px → dt_ms) and S4 (crosshair: cursor_x → hovered_time_ms).

using UnityEngine;

public class ChartViewState
{
    public const float MIN_CELL_WIDTH_PX = 1.0f;
    public const float DEFAULT_CELL_WIDTH_PX = 6.0f;
    public const float MAX_CELL_WIDTH_PX = 64.0f;

    public const long BASIS_DAILY_MS  = 86_400_000L;
    public const long BASIS_MINUTE_MS = 60_000L;

    public long translation_ms;
    public float cell_width_px = DEFAULT_CELL_WIDTH_PX;
    public bool auto_scale = true;
    public long? basis_ms;             // null = unknown / hold previous
    public float cell_height_norm = 1f;  // derived in autoscale; falls back to 1.0 when no bars

    // Auto-scale price-axis cache (derived per Render or OnPopulateMesh). NOT persisted.
    public double visible_min_price = double.NaN;
    public double visible_max_price = double.NaN;

    // ---- mutators ----

    public void Pan(float dx_px)
    {
        long basis = basis_ms ?? BASIS_MINUTE_MS;
        long dt_ms = (long)((-dx_px / Mathf.Max(1e-3f, cell_width_px)) * basis);
        translation_ms += dt_ms;
        auto_scale = false;
    }

    // Wheel zoom with cursor-relative pivot: the bar under cursor_x_px should stay under cursor_x_px
    // after the zoom (the flowsurface apply_cursor_zoom translation).
    public void Zoom(float scroll_notches, float cursor_x_px, float plot_width_px)
    {
        if (plot_width_px <= 0f) return;
        long basis = basis_ms ?? BASIS_MINUTE_MS;
        float oldCw = cell_width_px;
        float newCw = Mathf.Clamp(cell_width_px * Mathf.Pow(1.1f, scroll_notches),
                                  MIN_CELL_WIDTH_PX, MAX_CELL_WIDTH_PX);
        auto_scale = false;
        if (Mathf.Approximately(newCw, oldCw)) return;

        // Time under cursor BEFORE zoom = translation_ms + (cursor_x / oldCw) * basis.
        // After zoom we want SAME time under SAME cursor: translation_ms' + (cursor_x / newCw) * basis == t_cursor.
        long t_cursor = translation_ms + (long)((cursor_x_px / oldCw) * basis);
        cell_width_px = newCw;
        translation_ms = t_cursor - (long)((cursor_x_px / newCw) * basis);
        auto_scale = false;
    }

    // ResetView: right-anchor on latest bar with DEFAULT_CELL_WIDTH_PX (findings 0119 D-4).
    // Called by double-click (S2) and on initial spawn.
    public void ResetView(long latest_bar_open_time_ms, float plot_width_px)
    {
        cell_width_px = DEFAULT_CELL_WIDTH_PX;
        auto_scale = true;
        long basis = basis_ms ?? BASIS_MINUTE_MS;
        // Right-anchor: the latest bar should sit at the right edge.
        float visibleBars = plot_width_px / cell_width_px;
        translation_ms = latest_bar_open_time_ms - (long)((visibleBars - 1) * basis);
    }

    // ---- derived geometry ----

    public float TimeToX(long t_ms, float plot_x0, float plot_width_px)
    {
        long basis = basis_ms ?? BASIS_MINUTE_MS;
        if (basis <= 0) basis = BASIS_MINUTE_MS;
        return plot_x0 + ((t_ms - translation_ms) / (float)basis) * cell_width_px;
    }

    public long XToTime(float x_px, float plot_x0)
    {
        long basis = basis_ms ?? BASIS_MINUTE_MS;
        return translation_ms + (long)(((x_px - plot_x0) / Mathf.Max(1e-3f, cell_width_px)) * basis);
    }

    // Map price → main_area y. mainY0 = main_area.yMin in widget-local space, mainH = main_area
    // height (volume_area is excluded — findings 0119 D-2: price-axis lives in main_area only).
    public float PriceToY(double price, float mainY0, float mainH)
    {
        double range = visible_max_price - visible_min_price;
        if (range <= 0 || double.IsNaN(range)) return mainY0 + mainH * 0.5f;
        return mainY0 + (float)((price - visible_min_price) / range) * mainH;
    }

    // ---- basis_ms inference (used when SetGranularity wasn't called) ----

    // Infer basis from median consecutive open_time_ms diff. Returns null when undecidable.
    public static long? InferBasisMs(System.Collections.Generic.IReadOnlyList<OhlcPoint> bars)
    {
        if (bars == null || bars.Count < 2) return null;
        // Use the SMALLEST positive diff between adjacent bars — robust against gaps / weekends.
        long min = long.MaxValue;
        for (int i = 1; i < bars.Count; i++)
        {
            long d = bars[i].open_time_ms - bars[i - 1].open_time_ms;
            if (d > 0 && d < min) min = d;
        }
        if (min == long.MaxValue) return null;
        // Snap to Daily / Minute / hold.
        if (min >= BASIS_DAILY_MS / 2) return BASIS_DAILY_MS;
        if (min >= BASIS_MINUTE_MS) return BASIS_MINUTE_MS;
        return min;   // sub-minute (Live tick) — keep as-is so zoom math doesn't divide by huge constant.
    }

    public static long BasisMsFor(GranularityChoice g)
    {
        switch (g)
        {
            case GranularityChoice.Daily: return BASIS_DAILY_MS;
            case GranularityChoice.Minute: return BASIS_MINUTE_MS;
            default: return BASIS_DAILY_MS;
        }
    }
}
