// ChartPalette.cs — single-source color palette for ChartView (S1, #155) and DepthLadderView (S8, #161).
//
// 方針: ADR-0035 D-10 (Bid/Ask 色の single-source 化). Before this class, ChartView and DepthLadderView
// each read ThemeService.Current.colors.hakoniwa_up / hakoniwa_down independently → a theme variant that
// shifted only one of those fields (legal under ThemeService SoT) could split the chart's BULLISH from
// the ladder's Bid, an invisible Visual divergence between the two halves of the same chart+ladder tile.
//
// This class is the ONE place either widget reads those colors. ChartView paints bullish candles with
// Bullish(); DepthLadderView paints bid rows with Bullish(). Same for Bearish() / Ask. A theme switch
// (ThemeService.Changed) re-evaluates both at next ApplyTheme call — no caching here, so the read is
// always fresh.
//
// LADDER-PALETTE-01 (findings 0120 D-14) gates the single-source property: after SetTheme(NonDefault),
// cv.FirstCandleColor(true) == lv.BestBidColor — driven through ChartPalette, not by a coincidence.

using UnityEngine;

public static class ChartPalette
{
    // Bullish candle color (close >= open) AND Bid ladder row text/bg color.
    // findings 0054 P1 (cream-legible): the Hakoniwa-isolated up role.
    public static Color Bullish() => ThemeService.Current.colors.hakoniwa_up;

    // Bearish candle color (close < open) AND Ask ladder row text/bg color.
    public static Color Bearish() => ThemeService.Current.colors.hakoniwa_down;

    // LAST row text color (DepthLadderView center row) — findings 0054 P1 cream-legible amber.
    public static Color Last() => ThemeService.Current.colors.hakoniwa_last;

    // Chart / Ladder background color (chart pane + ladder pane share one Hakoniwa-isolated bg
    // — findings 0054). The Mesh-based ChartView/DepthLadderView use this for their bg quad.
    public static Color Background() => ThemeService.Current.colors.hakoniwa_chart_background;

    // Grid line color: low-alpha overlay woven into the candle Mesh batch (findings 0119 D-6).
    // Derived from text_muted with alpha=0.06 so it reads on both dark+light variants.
    public static Color Grid()
    {
        var c = ThemeService.Current.colors.hakoniwa_text_muted;
        c.a = 0.06f;
        return c;
    }

    // Axis line color (heavier than grid) — for the L-shaped axes at left/bottom of the plot.
    public static Color Axis() => ThemeService.Current.colors.hakoniwa_text_muted;

    // Last-price dashed line color (findings 0119 D-6). Same alpha as candle, but readable on the
    // chart bg. Uses Last() (amber) so it's distinct from up/down candles.
    public static Color LastPriceLine()
    {
        var c = ThemeService.Current.colors.hakoniwa_last;
        c.a = 0.7f;
        return c;
    }

    // Crosshair line color (findings 0119 D-6).
    public static Color Crosshair()
    {
        var c = ThemeService.Current.colors.hakoniwa_text;
        c.a = 0.5f;
        return c;
    }
}
