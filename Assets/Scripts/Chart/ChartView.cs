// ChartView.cs — issue #53 "candlestick 描画を本番コンポーネント（ChartView）に抽出"
//
// The reusable, production candlestick widget. Before #53 the candle/axis render lived as a
// THREE-WAY copy-paste (ReplayChartHarness / ReplayPanelsHarness / ScenarioStartupHitlHarness
// each carried its own BuildChartUi + RenderCandles + AddRect). This consolidates that single
// rendering into one component; the 3 harnesses and the #44 montage now feed the SAME part.
//
// PARITY STANCE (memory "TTWR parity first"; findings 0023): TTWR's chart is an IMMEDIATE-mode
// Bevy system (`chart_main_render_system` re-paints every candle each frame via ShapePainter on
// a read-only ChartViewState) — there is no "ChartView component" to port 1:1. This retained
// uGUI widget (Image rects rebuilt only when the bar count grows) is therefore a backcast-
// ORIGINAL structure forced by the framework gap, exactly like findings 0020's "切替伝播は
// backcast 独自". Parity is held at the VISUAL/SEMANTIC level: candle up/down = status.long/
// short, wick = high/low, body = open/close, autoscale = visible min/max, and the title bar's
// price/change% formatting matches TTWR title_bar.rs (format_chart_title_price / change_pct).
//
// THEME (issue #44 AC②): this view self-subscribes to ThemeService.Changed and re-applies, so it
// follows a runtime theme switch — structurally curing the build-once bug where the harnesses
// froze colors in `static readonly Color = ThemeService.Current...` at type-init (findings 0018 L1).
//
// SCOPE (v1): candle + axis + background (+ optional title bar). TTWR's richer chrome — grid
// lines, axis tick labels (axes_labels.rs), the last-price dashed line — is NOT yet ported in
// backcast and is split to a follow-up (see findings 0023), keeping this an extraction, not a
// feature add.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ChartView : MonoBehaviour
{
    // gutter that reserves room for the (future) axis labels; the axes + candles live inside it.
    const float GutterLeft = 60f, GutterBottom = 40f, GutterRight = 20f, GutterTop = 20f;
    const float TitleBarHeight = 24f;

    RectTransform _plot;        // inset plot rect (axes + candles)
    RectTransform _candleRoot;  // candle rects parent (full-stretch over _plot)
    Image _bg;
    Image _yAxis, _xAxis;

    bool _showTitleBar;
    Text _titleLabel, _priceText, _changeText;

    Font _font;

    // Retained candle graphics + their direction, so ApplyTheme re-colors in place (no re-decode).
    struct CandlePart { public Image image; public bool bullish; }
    readonly List<CandlePart> _candles = new List<CandlePart>();

    // last rendered frame (for the title bar price/change after a theme switch keeps text values).
    bool _hasFrame;
    double _lastClose, _firstOpen;

    // ---- probe / montage seams (ThemeProbe samples the PRODUCTION graphics, findings 0023) ----
    public Image Background => _bg;
    public Text PriceText => _priceText;     // title bar price (last close) — for ThemeProbe value-assert
    public Text ChangeText => _changeText;   // title bar signed change% (themed long/short) — for ThemeProbe

    // First candle body matching the requested direction (the montage feeds a 2-bar mock so both
    // exist) — lets ThemeProbe assert the real candle color switches, not a throwaway swatch.
    public Image FirstCandle(bool bullish)
    {
        foreach (var c in _candles) if (c.bullish == bullish) return c.image;
        return null;
    }

    // Build the subtree under `parent`. Each harness keeps ONLY its own parent-rect placement
    // (full-screen / 0.62 panel / Hakoniwa tile); the chart internals are identical here.
    public void Build(RectTransform parent, bool showTitleBar)
    {
        _showTitleBar = showTitleBar;
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // background fills the whole parent.
        var bgGo = new GameObject("ChartBg", typeof(RectTransform), typeof(Image));
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.SetParent(parent, false);
        Stretch(bgRt);
        _bg = bgGo.GetComponent<Image>();

        float topInset = GutterTop + (showTitleBar ? TitleBarHeight + 4f : 0f);
        if (showTitleBar) BuildTitleBar(parent);

        // plot inset = the gutter; axes + candles live here so candle sizing uses _plot.rect.
        var plotGo = new GameObject("PlotArea", typeof(RectTransform));
        _plot = plotGo.GetComponent<RectTransform>();
        _plot.SetParent(parent, false);
        _plot.anchorMin = new Vector2(0f, 0f);
        _plot.anchorMax = new Vector2(1f, 1f);
        _plot.offsetMin = new Vector2(GutterLeft, GutterBottom);
        _plot.offsetMax = new Vector2(-GutterRight, -topInset);

        _yAxis = AddAxis(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
        _xAxis = AddAxis(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f));

        var rootGo = new GameObject("Candles", typeof(RectTransform));
        _candleRoot = rootGo.GetComponent<RectTransform>();
        _candleRoot.SetParent(_plot, false);
        Stretch(_candleRoot);
        _candleRoot.pivot = Vector2.zero;

        ThemeService.Changed += ApplyTheme;
        ApplyTheme();
    }

    void BuildTitleBar(RectTransform parent)
    {
        var barGo = new GameObject("TitleBar", typeof(RectTransform));
        var barRt = barGo.GetComponent<RectTransform>();
        barRt.SetParent(parent, false);
        barRt.anchorMin = new Vector2(0f, 1f);
        barRt.anchorMax = new Vector2(1f, 1f);
        barRt.pivot = new Vector2(0.5f, 1f);
        barRt.sizeDelta = new Vector2(0f, TitleBarHeight);
        barRt.anchoredPosition = Vector2.zero;

        _titleLabel = TitleText(barRt, "CHART", new Vector2(0f, 0f), new Vector2(0.5f, 1f), TextAnchor.MiddleLeft);
        _priceText  = TitleText(barRt, "—",     new Vector2(0.5f, 0f), new Vector2(0.78f, 1f), TextAnchor.MiddleRight);
        _changeText = TitleText(barRt, "—",     new Vector2(0.78f, 0f), new Vector2(1f, 1f), TextAnchor.MiddleRight);
    }

    Text TitleText(RectTransform parent, string s, Vector2 aMin, Vector2 aMax, TextAnchor anchor)
    {
        var go = new GameObject("t", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = aMin; rt.anchorMax = aMax;
        rt.offsetMin = new Vector2(6f, 0f); rt.offsetMax = new Vector2(-6f, 0f);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 13; t.text = s; t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    Image AddAxis(Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 size)
    {
        var go = new GameObject("Axis", typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(_plot, false);
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        rt.sizeDelta = size; rt.anchoredPosition = Vector2.zero;
        return go.GetComponent<Image>();
    }

    // Render the cumulative OHLC series. Same algorithm the 3 harnesses shared: autoscale to the
    // visible min/max, x = open_time_ms normalized across the range, wick = high/low, body =
    // open/close, color = close>=open ? hakoniwa_up : hakoniwa_down (findings 0054 P1). Rebuild only on a new bar.
    public void Render(ReplayBarFrame frame)
    {
        var pts = frame.Ohlc;
        int n = pts != null ? pts.Count : 0;

        for (int i = _candleRoot.childCount - 1; i >= 0; i--)
            DestroyChild(_candleRoot.GetChild(i).gameObject);
        _candles.Clear();

        if (n == 0) { _hasFrame = false; UpdateTitle(); return; }

        double minLow = double.MaxValue, maxHigh = double.MinValue;
        long minT = long.MaxValue, maxT = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            OhlcPoint p = pts[i];
            if (p.low  < minLow)  minLow  = p.low;
            if (p.high > maxHigh) maxHigh = p.high;
            if (p.open_time_ms < minT) minT = p.open_time_ms;
            if (p.open_time_ms > maxT) maxT = p.open_time_ms;
        }

        double priceRange = maxHigh - minLow;
        if (priceRange <= 0) priceRange = 1.0;   // flat series guard
        long timeRange = maxT - minT;

        float w = _plot.rect.width;
        float h = _plot.rect.height;
        float bodyW = Mathf.Max(1f, (w / n) * 0.6f);

        var up = ThemeService.Current.colors.hakoniwa_up;     // findings 0054 P1: cream-legible (was status.long)
        var down = ThemeService.Current.colors.hakoniwa_down; // (was status.short)

        for (int i = 0; i < n; i++)
        {
            OhlcPoint p = pts[i];

            float x = timeRange > 0
                ? (float)((p.open_time_ms - minT) / (double)timeRange) * w
                : (n > 1 ? (float)i / (n - 1) * w : w * 0.5f);

            float yOpen  = (float)((p.open  - minLow) / priceRange) * h;
            float yClose = (float)((p.close - minLow) / priceRange) * h;
            float yHigh  = (float)((p.high  - minLow) / priceRange) * h;
            float yLow   = (float)((p.low   - minLow) / priceRange) * h;

            bool bullish = p.close >= p.open;
            Color c = bullish ? up : down;

            AddCandleRect(x - 0.5f, yLow, 1f, Mathf.Max(1f, yHigh - yLow), c, bullish);  // wick
            float bottom = Mathf.Min(yOpen, yClose);
            float bodyH  = Mathf.Max(1f, Mathf.Abs(yClose - yOpen));
            AddCandleRect(x - bodyW * 0.5f, bottom, bodyW, bodyH, c, bullish);            // body
        }

        _firstOpen = pts[0].open;
        _lastClose = pts[n - 1].close;
        _hasFrame = true;
        UpdateTitle();
    }

    void AddCandleRect(float x, float y, float w, float h, Color c, bool bullish)
    {
        var go = new GameObject("c", typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(_candleRoot, false);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        var img = go.GetComponent<Image>();
        img.color = c;
        _candles.Add(new CandlePart { image = img, bullish = bullish });
    }

    // Re-paint bg / axes / candles / title from the active theme. Subscribed to ThemeService.Changed.
    public void ApplyTheme()
    {
        var th = ThemeService.Current;
        if (_bg != null) _bg.color = th.colors.hakoniwa_chart_background;   // findings 0054: Hakoniwa-isolated bg
        if (_yAxis != null) _yAxis.color = th.colors.hakoniwa_text_muted;
        if (_xAxis != null) _xAxis.color = th.colors.hakoniwa_text_muted;
        for (int i = 0; i < _candles.Count; i++)
        {
            var part = _candles[i];
            if (part.image != null) part.image.color = part.bullish ? th.colors.hakoniwa_up : th.colors.hakoniwa_down;   // findings 0054 P1: cream-legible
        }
        UpdateTitle();
    }

    // Title bar price + signed change% (TTWR title_bar.rs: "{:.2}" price; "+{:.2}%" / "-..%" change,
    // long when >=0 else short; "—" when no base). Text is plain; only the color is themed.
    void UpdateTitle()
    {
        if (!_showTitleBar) return;
        var th = ThemeService.Current;
        if (_titleLabel != null) _titleLabel.color = th.colors.hakoniwa_text;

        if (!_hasFrame)
        {
            if (_priceText != null) { _priceText.text = "—"; _priceText.color = th.colors.hakoniwa_text; }
            if (_changeText != null) { _changeText.text = "—"; _changeText.color = th.colors.hakoniwa_text_muted; }
            return;
        }

        if (_priceText != null)
        {
            _priceText.text = _lastClose.ToString("0.00");
            _priceText.color = th.colors.hakoniwa_text;
        }
        if (_changeText != null)
        {
            if (_firstOpen == 0.0)
            {
                _changeText.text = "—";
                _changeText.color = th.colors.hakoniwa_text_muted;
            }
            else
            {
                // sign/color from the ROUNDED value so a sub-0.005% move never prints "-0.00%"
                // (negative-zero) — keeps +/- formatting symmetric (TTWR title_bar.rs parity).
                double pct = System.Math.Round((_lastClose - _firstOpen) / _firstOpen * 100.0, 2);
                bool gain = pct >= 0.0;
                _changeText.text = (gain ? "+" : "-") + System.Math.Abs(pct).ToString("0.00") + "%";
                _changeText.color = gain ? th.colors.hakoniwa_up : th.colors.hakoniwa_down;   // findings 0054 P1: cream-legible
            }
        }
    }

    void OnDestroy() => ThemeService.Changed -= ApplyTheme;

    // Destroy() is illegal in edit mode (the ThemeProbe AFK gate drives this widget headless). The
    // probe's first-and-only Render has no children to clear, so this is latent today, but a future
    // edit-mode re-Render would trip it — keep the candle rebuild edit-mode-safe.
    static void DestroyChild(GameObject go)
    {
        if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }
}
