// ChartView.cs — S1 (#155) ChartView Mesh + ChartViewState + visible-window virtualization.
//
// 方針: ADR-0034 §1-§4 / findings 0119 D-1..D-4. The Unity 翻訳 of flowsurface's single CanvasRenderer +
// ShapePainter + ChartViewState pattern. Before S1 this widget paint per-candle Image GameObjects under
// a "Candles" RectTransform (findings 0023 retained-mode) — 1 GPU drawcall per Canvas batch, but
// 2*bar_count GameObjects, fully rebuilt on every new bar. S1 collapses this to:
//
//   * THIS class extends MaskableGraphic. ITS OWN rectTransform IS the chart pane (no _bg / _candleRoot
//     children). OnPopulateMesh writes one UIVertex batch per render pass — background quad + L-shaped
//     axes + visible-window candles (wick quad + body quad each) — so GPU drawcall = 1 / GameObject = 1.
//   * ChartViewState (per-window pure data) carries translation_ms / cell_width_px / auto_scale /
//     basis_ms. The plot rect is the chart's gutter inset; OnPopulateMesh asks ViewState for where
//     each bar lands on screen. VISIBLE WINDOW VIRTUALIZATION: bars outside [translation_ms,
//     translation_ms + plot_width/cell_width*basis_ms) skip the quad emit entirely — RenderedBarCount
//     stays ~90 even when TotalBarCount is 1000 (VIRTUALIZE-01).
//   * Default visible window = right-anchor on the latest bar with DEFAULT_CELL_WIDTH_PX=6.0f (findings
//     0119 D-4). Replay's full-period cold load (S6, #156) hands engine a multi-thousand-bar OhlcPoint
//     array, but the initial visible window is still ~90 bars; pan/zoom (S2) lets the owner navigate
//     the rest. (Live keeps max_history_len=1000; no scope creep into kabu historical backfill.)
//
// PUBLIC API (E2E / ThemeProbe seam — findings 0119 D-8):
//   * int TotalBarCount       — bars received via Render() (CHARTRENDER-01 / VIRTUALIZE-01).
//   * int RenderedBarCount    — bars whose vertices were emitted in the most recent OnPopulateMesh
//                                (visible-window count; VIRTUALIZE-01 floor).
//   * Color FirstCandleColor(bool bullish) — color of the first bar of the requested polarity
//                                in the DATA (not in the visible window — so ThemeProbe's 2-bar mock
//                                still returns the right color even if zoom-out hides one bar).
//   * Color BackgroundColor   — current chart bg color (chart_bg sample in ThemeProbe).
//   * ChartViewState ViewState — pan/zoom/auto_scale test introspection (PAN-01/ZOOM-01/RESET-01).
//   * Text PriceText / ChangeText — title bar, unchanged from the legacy widget so ThemeProbe's
//     "title price/change% format + theme color" gate keeps working (#53 parity stance held).
//
// THEME: this widget self-subscribes to ThemeService.Changed and calls SetVerticesDirty() so the
// Mesh re-emits with the new ChartPalette colors on the next layout pass — equivalent to the legacy
// ApplyTheme that walked each Image in-place. The title bar Text colors are repainted directly.
//
// RETIRED (findings 0119 D-8): `Image Background` getter / `Image FirstCandle(bool)` / `Candles`
// RectTransform child / `_candleRoot`. KabuLiveChartRenderE2ERunner's `CountCandles` (looked up the
// "Candles" name and counted childCount) migrates to TotalBarCount / RenderedBarCount.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(CanvasRenderer))]
public class ChartView : MaskableGraphic
{
    // ---- gutter (preserved from the legacy widget so axis labels — S3 — land in the same rect) ----
    const float GutterLeft = 60f, GutterBottom = 40f, GutterRight = 20f, GutterTop = 20f;
    const float TitleBarHeight = 24f;
    const float AxisThicknessPx = 1f;

    // ---- title bar (legacy parity — Text children, NOT in the Mesh batch since text glyphs use the
    // Canvas's separate font sub-mesh; mesh integration would buy nothing here) ----
    bool _showTitleBar;
    Text _titleLabel, _priceText, _changeText;
    Font _font;

    // ---- data ----
    readonly List<OhlcPoint> _bars = new List<OhlcPoint>();
    bool _hasFrame;
    double _lastClose, _firstOpen;

    // ---- visible-window virtualization state (per chart window — findings 0119 D-2) ----
    public ChartViewState ViewState { get; } = new ChartViewState();

    // ---- last-render derived (filled inside OnPopulateMesh — read by E2E gates / ThemeProbe) ----
    public int TotalBarCount => _bars.Count;
    public int RenderedBarCount { get; private set; }
    Color _firstBullColor, _firstBearColor;
    bool _seenBull, _seenBear;

    // S2 (drag/wheel) and S4 (crosshair) need pointer events translated to plot-local space; this
    // is the plot rect computed inside the most recent OnPopulateMesh (gutter inset of rectTransform.rect).
    public Rect LastPlotRect { get; private set; }

    // ---- ThemeProbe / E2E color seams (replaces FirstCandle(bool)→Image) ----

    public Color BackgroundColor => ChartPalette.Background();

    // Returns the color of the FIRST bar with the requested polarity in the DATA. Falls back to
    // ChartPalette when no such bar exists, so a zero-data probe still sees the right hue (a degraded
    // null was the v1 fragility — code `if (img == null) skip` everywhere).
    public Color FirstCandleColor(bool bullish)
    {
        if (bullish && _seenBull) return _firstBullColor;
        if (!bullish && _seenBear) return _firstBearColor;
        // Recompute from data (handles "Render called but OnPopulateMesh hasn't fired yet" in headless).
        for (int i = 0; i < _bars.Count; i++)
        {
            var p = _bars[i];
            bool isBull = p.close >= p.open;
            if (isBull == bullish) return bullish ? ChartPalette.Bullish() : ChartPalette.Bearish();
        }
        return bullish ? ChartPalette.Bullish() : ChartPalette.Bearish();
    }

    public Text PriceText => _priceText;
    public Text ChangeText => _changeText;

    // ---- Build (preserves legacy signature: callers in BackcastWorkspaceRoot / ThemeHitlHarness /
    // KabuLiveChartRenderE2ERunner / all chart-spawning sites pass parent + showTitleBar) ----

    public void Build(RectTransform parent, bool showTitleBar)
    {
        _showTitleBar = showTitleBar;
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Sanity: this MaskableGraphic must live on `parent` — callers spawn the GO and AddComponent<ChartView>()
        // on the same GameObject whose RectTransform we receive. Old code's _bg / _yAxis / _xAxis / _candleRoot
        // children are no longer created — the Mesh batch carries bg + axes + candles.
        if (transform != parent)
        {
            Debug.LogWarning("[ChartView] Build(parent) called with a RectTransform that isn't this widget's own. "
                           + "ChartView IS the chart pane — AddComponent<ChartView>() on the chartArea GO directly.");
        }

        color = Color.white;   // per-vertex colors carry all hues; Graphic.color must NOT tint.
        raycastTarget = true;  // S2 (#157) needs pointer events for drag/wheel/dblclick.

        if (showTitleBar) BuildTitleBar((RectTransform)transform);

        ThemeService.Changed += OnThemeChanged;
        OnThemeChanged();   // sets initial title bar colors; SetVerticesDirty schedules a Mesh emit.
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
        t.raycastTarget = false;   // S2 pointer events go to the chart, not the title text.
        return t;
    }

    // ---- Render: ingest a new ReplayBarFrame, infer basis_ms if needed, right-anchor when auto_scale ----

    public void Render(ReplayBarFrame frame)
    {
        var pts = frame.Ohlc;
        _bars.Clear();
        if (pts != null) for (int i = 0; i < pts.Count; i++) _bars.Add(pts[i]);

        // Infer basis_ms from the bar diff if SetGranularity wasn't called (Replay full-period cold load
        // in S6 calls Render with multi-year Daily data; auto-infer keeps the chart sensible without
        // requiring every caller to thread granularity through).
        if (!ViewState.basis_ms.HasValue)
        {
            var inferred = ChartViewState.InferBasisMs(_bars);
            if (inferred.HasValue) ViewState.basis_ms = inferred;
        }

        if (_bars.Count == 0)
        {
            _hasFrame = false;
            UpdateTitle();
            SetVerticesDirty();
            return;
        }

        _firstOpen = _bars[0].open;
        _lastClose = _bars[_bars.Count - 1].close;
        _hasFrame = true;

        // Auto-scale = right-anchor to the latest bar (findings 0119 D-4). Manual pan/zoom (S2)
        // sets auto_scale=false; in that case the user's chosen translation_ms is preserved across
        // new bars so they don't get jerked back to the right edge mid-pan.
        if (ViewState.auto_scale)
        {
            var rect = rectTransform.rect;
            float plotW = Mathf.Max(1f, rect.width - GutterLeft - GutterRight);
            ViewState.ResetView(_bars[_bars.Count - 1].open_time_ms, plotW);
        }

        UpdateTitle();
        SetVerticesDirty();
    }

    // Allow callers (BackcastWorkspaceRoot) to thread the scenario granularity through so basis_ms
    // is locked from the spawn frame, not inferred from bar diffs. Auto-scale resets translation.
    public void SetGranularity(GranularityChoice g)
    {
        long b = ChartViewState.BasisMsFor(g);
        ViewState.basis_ms = b;
        if (ViewState.auto_scale && _bars.Count > 0)
        {
            var rect = rectTransform.rect;
            float plotW = Mathf.Max(1f, rect.width - GutterLeft - GutterRight);
            ViewState.ResetView(_bars[_bars.Count - 1].open_time_ms, plotW);
        }
        SetVerticesDirty();
    }

    // ---- OnPopulateMesh — the heart of D-1 + D-2 (single batch, visible window only) ----

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        RenderedBarCount = 0;
        _seenBull = _seenBear = false;
        _firstBullColor = ChartPalette.Bullish();
        _firstBearColor = ChartPalette.Bearish();

        var rect = rectTransform.rect;
        if (rect.width <= 0 || rect.height <= 0) return;

        // 1. background quad — spans the FULL rect (gutter shows bg, matching the legacy _bg).
        Color bg = ChartPalette.Background();
        EmitQuad(vh, rect.xMin, rect.yMin, rect.xMax, rect.yMax, bg);

        // 2. plot rect = gutter inset (title bar eats the top inset when present).
        float topInset = GutterTop + (_showTitleBar ? TitleBarHeight + 4f : 0f);
        var plot = new Rect(
            rect.xMin + GutterLeft,
            rect.yMin + GutterBottom,
            Mathf.Max(0f, rect.width  - GutterLeft - GutterRight),
            Mathf.Max(0f, rect.height - GutterBottom - topInset));
        LastPlotRect = plot;
        if (plot.width <= 0 || plot.height <= 0) return;

        // 3. L-shaped axes (legacy parity — uGUI Image bars previously, now Mesh quads).
        Color axisColor = ChartPalette.Axis();
        EmitQuad(vh, plot.xMin - AxisThicknessPx, plot.yMin, plot.xMin, plot.yMax, axisColor);   // y-axis
        EmitQuad(vh, plot.xMin, plot.yMin - AxisThicknessPx, plot.xMax, plot.yMin, axisColor);   // x-axis

        if (_bars.Count == 0) return;

        // 4. compute visible window time bounds (D-2). basis_ms may still be null on a 1-bar Live tick
        // — fall back to a sane Minute so the lone bar lands on-screen instead of vanishing.
        long basis = ViewState.basis_ms ?? ChartViewState.BASIS_MINUTE_MS;
        long winStart = ViewState.translation_ms;
        long winEnd = winStart + (long)((plot.width / Mathf.Max(1e-3f, ViewState.cell_width_px)) * basis);

        // 5. auto-scale price range from VISIBLE bars only (zooming changes what bars are in view, so
        // the price axis must follow). Pan-after-zoom keeps the same scale until owner double-clicks.
        if (ViewState.auto_scale)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < _bars.Count; i++)
            {
                var p = _bars[i];
                if (p.open_time_ms < winStart || p.open_time_ms > winEnd) continue;
                if (p.low  < lo) lo = p.low;
                if (p.high > hi) hi = p.high;
            }
            if (lo == double.MaxValue || hi == double.MinValue)
            {
                // No bars in view yet (e.g. translation_ms past the data) — fall back to full-range so
                // an autoscale chart never blanks out.
                lo = double.MaxValue; hi = double.MinValue;
                for (int i = 0; i < _bars.Count; i++)
                {
                    var p = _bars[i];
                    if (p.low  < lo) lo = p.low;
                    if (p.high > hi) hi = p.high;
                }
            }
            if (hi <= lo) { hi = lo + 1.0; }   // flat-series guard (legacy parity).
            // 5% top/bottom padding so the top/bottom candles don't kiss the gutters.
            double pad = (hi - lo) * 0.05;
            ViewState.visible_min_price = lo - pad;
            ViewState.visible_max_price = hi + pad;
        }

        // 6. emit candle quads for bars inside [winStart, winEnd]. The for-loop's `continue` is the
        // virtualization (D-2): bars outside the window never produce vertices.
        Color upColor = ChartPalette.Bullish();
        Color downColor = ChartPalette.Bearish();
        float bodyW = Mathf.Max(1f, ViewState.cell_width_px * 0.6f);

        int rendered = 0;
        for (int i = 0; i < _bars.Count; i++)
        {
            var p = _bars[i];
            if (p.open_time_ms < winStart) continue;
            if (p.open_time_ms > winEnd) continue;

            float x = ViewState.TimeToX(p.open_time_ms, plot.xMin, plot.width);
            float yOpen  = ViewState.PriceToY(p.open,  plot.yMin, plot.height);
            float yClose = ViewState.PriceToY(p.close, plot.yMin, plot.height);
            float yHigh  = ViewState.PriceToY(p.high,  plot.yMin, plot.height);
            float yLow   = ViewState.PriceToY(p.low,   plot.yMin, plot.height);

            bool bullish = p.close >= p.open;
            Color c = bullish ? upColor : downColor;

            // Wick quad (1px wide, low→high). Min height 1px so a doji is still visible.
            EmitQuad(vh, x - 0.5f, yLow, x + 0.5f, Mathf.Max(yLow + 1f, yHigh), c);
            // Body quad (bodyW wide, open↔close). Min height 1px so a doji body still paints.
            float bodyBottom = Mathf.Min(yOpen, yClose);
            float bodyTop = Mathf.Max(bodyBottom + 1f, Mathf.Max(yOpen, yClose));
            EmitQuad(vh, x - bodyW * 0.5f, bodyBottom, x + bodyW * 0.5f, bodyTop, c);

            if (bullish && !_seenBull) { _firstBullColor = c; _seenBull = true; }
            if (!bullish && !_seenBear) { _firstBearColor = c; _seenBear = true; }
            rendered++;
        }
        RenderedBarCount = rendered;
    }

    // Emit a 2-triangle quad with a single color. UIVertex.simpleVert positions vertices in this widget's
    // local rect space (the Canvas converts to world). uv = (0,0) — the default white texture handles
    // solid color fill.
    static void EmitQuad(VertexHelper vh, float x0, float y0, float x1, float y1, Color c)
    {
        int idx = vh.currentVertCount;
        var v = UIVertex.simpleVert;
        v.color = c;
        v.position = new Vector3(x0, y0); vh.AddVert(v);
        v.position = new Vector3(x0, y1); vh.AddVert(v);
        v.position = new Vector3(x1, y1); vh.AddVert(v);
        v.position = new Vector3(x1, y0); vh.AddVert(v);
        vh.AddTriangle(idx + 0, idx + 1, idx + 2);
        vh.AddTriangle(idx + 0, idx + 2, idx + 3);
    }

    // ---- title bar (preserved from legacy — Text + theme color repaint) ----

    void OnThemeChanged()
    {
        SetVerticesDirty();   // bg + axes + candles re-emit with new palette colors.
        UpdateTitle();
    }

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
                // sign / color from ROUNDED value so a sub-0.005% move never prints "-0.00%" (legacy parity).
                double pct = System.Math.Round((_lastClose - _firstOpen) / _firstOpen * 100.0, 2);
                bool gain = pct >= 0.0;
                _changeText.text = (gain ? "+" : "-") + System.Math.Abs(pct).ToString("0.00") + "%";
                _changeText.color = gain ? th.colors.hakoniwa_up : th.colors.hakoniwa_down;
            }
        }
    }

    protected override void OnDestroy()
    {
        ThemeService.Changed -= OnThemeChanged;
        base.OnDestroy();
    }

    protected override void OnRectTransformDimensionsChange()
    {
        base.OnRectTransformDimensionsChange();
        // Resize → visible window pixel-extent changes → re-emit Mesh; auto_scale will also
        // recompute right-anchor on next OnPopulateMesh so the latest bar stays at the right edge.
        if (_bars.Count > 0 && ViewState.auto_scale)
        {
            var rect = rectTransform.rect;
            float plotW = Mathf.Max(1f, rect.width - GutterLeft - GutterRight);
            ViewState.ResetView(_bars[_bars.Count - 1].open_time_ms, plotW);
        }
        SetVerticesDirty();
    }
}
