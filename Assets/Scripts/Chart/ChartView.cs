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
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(CanvasRenderer))]
public class ChartView : MaskableGraphic,
    IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler, IPointerClickHandler,
    IPointerEnterHandler, IPointerExitHandler, IPointerMoveHandler
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

    // S3 #158 observability: counters that the AXIS-PRICE-01 / AXIS-TIME-01 / GRID-01 gates read
    // to assert the axis labels / grid lines were emitted in the last OnPopulateMesh + label rebuild.
    public int LastGridLineCount { get; private set; }
    public int LastPriceLabelCount => _priceLabels.Count;
    public int LastTimeLabelCount => _timeLabels.Count;
    // S3 + S4: list of price/time tick values the most recent OnPopulateMesh used. Reused by the
    // axis label rebuild path so labels and grid lines stay in lockstep.
    List<double> _lastPriceTicks = new List<double>();
    List<long> _lastTimeTicks = new List<long>();
    long _lastTimeStep;
    bool _axisLabelsDirty;
    RectTransform _priceLabelsRoot, _timeLabelsRoot;
    readonly List<Text> _priceLabels = new List<Text>();
    readonly List<Text> _timeLabels = new List<Text>();

    // S5 #160: main_area (top 80%) / volume_area (bottom 20%) split lives entirely inside this widget
    // — ChartScale.PriceTicks only sees the main_area (VOLUME-02), the volume bars live in volume_area.
    // S4 crosshair derive uses VolumeFrac>0 to decide if hovered_volume is non-null (cursor in volume).
    const float VolumeFrac = 0.20f;

    // S5 observability — VOLUME-01 / VOLUME-CROSSHAIR-01 gates.
    public int LastVolumeBarCount { get; private set; }
    public float LastVolumeAreaHeightPx { get; private set; }

    // S4 #159: CrosshairState lives on ChartView in S4; S9 will hoist ownership to ChartLadderRoot
    // (parent Component) so DepthLadderView can read hovered_price. The accessor remains here so
    // the migration is transparent to ChartView callers (PointerMove etc.). S4 observability gates
    // (CROSSHAIR-01) read these counters.
    public CrosshairState Crosshair { get; } = new CrosshairState();
    public int LastCrosshairLineCount { get; private set; }
    public int LastLastPriceLineCount { get; private set; }
    Text _crosshairPriceBadge, _crosshairTimeBadge;

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

        // S3 axis labels: Text roots live as children of this widget so the labels follow rect /
        // hierarchy moves. The roots themselves are stretched over the FULL rect; per-label Text
        // children are positioned in local coords by RebuildAxisLabels.
        _priceLabelsRoot = NewChildRect("PriceAxisLabels");
        _timeLabelsRoot = NewChildRect("TimeAxisLabels");

        ThemeService.Changed += OnThemeChanged;
        OnThemeChanged();   // sets initial title bar colors; SetVerticesDirty schedules a Mesh emit.
    }

    RectTransform NewChildRect(string n)
    {
        var go = new GameObject(n, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent((RectTransform)transform, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return rt;
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

        // S5 main_area / volume_area split — for S3 (no volume yet) main_area == plot.
        float volumeH = plot.height * VolumeFrac;
        var mainArea = new Rect(plot.x, plot.y + volumeH, plot.width, plot.height - volumeH);
        float mainY0 = mainArea.yMin;
        float mainH  = mainArea.height;

        // 6a. grid lines + axis tick caches (findings 0119 D-6) — emitted into the SAME Mesh batch
        // at alpha=0.06 so GPU drawcall stays 1. Price ticks span main_area only (volume_area in
        // S5 will not carry price gridlines per AXIS-PRICE-01 / VOLUME-02). Time ticks span full plot.
        Color gridColor = ChartPalette.Grid();
        _lastPriceTicks = ChartScale.CalcOptimalPriceTicks(ViewState.visible_min_price, ViewState.visible_max_price, 8);
        _lastTimeStep = ChartScale.CalcOptimalTimeStep(winStart, winEnd, basis, 6);
        _lastTimeTicks = ChartScale.CalcOptimalTimeTicks(winStart, winEnd, basis, 6);
        int gridLines = 0;
        for (int i = 0; i < _lastPriceTicks.Count; i++)
        {
            double price = _lastPriceTicks[i];
            float y = MainPriceToY(price, mainY0, mainH);
            if (y < mainArea.yMin - 0.5f || y > mainArea.yMax + 0.5f) continue;
            EmitQuad(vh, mainArea.xMin, y - 0.5f, mainArea.xMax, y + 0.5f, gridColor);
            gridLines++;
        }
        for (int i = 0; i < _lastTimeTicks.Count; i++)
        {
            long t = _lastTimeTicks[i];
            float x = ViewState.TimeToX(t, plot.xMin, plot.width);
            if (x < plot.xMin - 0.5f || x > plot.xMax + 0.5f) continue;
            EmitQuad(vh, x - 0.5f, plot.yMin, x + 0.5f, plot.yMax, gridColor);
            gridLines++;
        }
        LastGridLineCount = gridLines;
        _axisLabelsDirty = true;   // LateUpdate spawns Text children matching _lastPriceTicks / _lastTimeTicks.

        // 6b. emit candle quads for bars inside [winStart, winEnd]. The for-loop's `continue` is the
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
            float yOpen  = MainPriceToY(p.open,  mainY0, mainH);
            float yClose = MainPriceToY(p.close, mainY0, mainH);
            float yHigh  = MainPriceToY(p.high,  mainY0, mainH);
            float yLow   = MainPriceToY(p.low,   mainY0, mainH);

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

        // 6c. volume bars (S5 #160 / VOLUME-01) — bottom volume_area (the (1-VolumeFrac)..bottom slice
        // of the plot). Same Mesh batch as candles; color = candle color at alpha=0.6. Width = candle
        // body width; height = volume / max_visible_volume * volume_area_height.
        LastVolumeAreaHeightPx = volumeH;
        int volumeBars = 0;
        if (volumeH > 0)
        {
            double maxVol = ChartScale.MaxVisibleVolume(_bars, winStart, winEnd);
            if (maxVol > 0)
            {
                float volTop = mainArea.yMin;        // top edge of volume_area = bottom edge of main_area
                float volBot = plot.yMin;            // bottom of plot
                Color volUp = upColor; volUp.a = 0.6f;
                Color volDn = downColor; volDn.a = 0.6f;
                for (int i = 0; i < _bars.Count; i++)
                {
                    var p = _bars[i];
                    if (p.open_time_ms < winStart) continue;
                    if (p.open_time_ms > winEnd) continue;
                    float x = ViewState.TimeToX(p.open_time_ms, plot.xMin, plot.width);
                    float h = ChartScale.VolumeBarHeight(p.volume, maxVol, volumeH);
                    bool isBull = p.close >= p.open;
                    EmitQuad(vh, x - bodyW * 0.5f, volBot, x + bodyW * 0.5f, volBot + h, isBull ? volUp : volDn);
                    volumeBars++;
                }
            }
        }
        LastVolumeBarCount = volumeBars;

        // 7. last-price dashed line (findings 0119 D-6 / LASTPRICE-01). Horizontal at latest close,
        // alpha=0.7 (LastPriceLine palette), dashed via 6px-on / 4px-off pattern. Lives in the same
        // Mesh batch so drawcall stays 1.
        int lastPriceSegments = 0;
        if (_bars.Count > 0)
        {
            double lastClose = _bars[_bars.Count - 1].close;
            float y = MainPriceToY(lastClose, mainY0, mainH);
            if (y >= mainArea.yMin && y <= mainArea.yMax)
            {
                Color lpc = ChartPalette.LastPriceLine();
                const float DashOn = 6f, DashOff = 4f, Thickness = 1f;
                for (float x = mainArea.xMin; x < mainArea.xMax; x += DashOn + DashOff)
                {
                    float x1 = Mathf.Min(x + DashOn, mainArea.xMax);
                    EmitQuad(vh, x, y - Thickness * 0.5f, x1, y + Thickness * 0.5f, lpc);
                    lastPriceSegments++;
                }
            }
        }
        LastLastPriceLineCount = lastPriceSegments;

        // 8. crosshair (findings 0119 D-6 / CROSSHAIR-01). Two thin lines + 4 readout badges (Text
        // in gutters) when Crosshair.cursor_world is non-null. Mesh integration keeps drawcall=1.
        int crossLines = 0;
        if (Crosshair.cursor_world.HasValue)
        {
            var c = Crosshair.cursor_world.Value;
            // Only render the crosshair when the cursor is inside the plot rect (gutter-overlap
            // would draw lines across the axis labels which reads as visual noise).
            if (c.x >= plot.xMin && c.x <= plot.xMax && c.y >= plot.yMin && c.y <= plot.yMax)
            {
                Color cc = ChartPalette.Crosshair();
                EmitQuad(vh, c.x - 0.5f, plot.yMin, c.x + 0.5f, plot.yMax, cc);   // vertical
                EmitQuad(vh, plot.xMin, c.y - 0.5f, plot.xMax, c.y + 0.5f, cc);   // horizontal
                crossLines = 2;
            }
        }
        LastCrosshairLineCount = crossLines;
    }

    // Price → Y mapping for the main_area (S5 split: main_area occupies the top (1-VolumeFrac) of the
    // plot). S3 grid / candle / axis label all read through this so a future VolumeFrac change moves
    // everything in lockstep with no scattered y-math.
    float MainPriceToY(double price, float mainY0, float mainH)
    {
        double range = ViewState.visible_max_price - ViewState.visible_min_price;
        if (range <= 0 || double.IsNaN(range)) return mainY0 + mainH * 0.5f;
        return mainY0 + (float)((price - ViewState.visible_min_price) / range) * mainH;
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

    // ====== S3 #158: axis label rebuild (text children, off the Mesh batch path) ======
    //
    // Grid lines are inside the Mesh batch (OnPopulateMesh). Text labels are separate Text
    // children — Canvas batches glyphs in its own sub-mesh, so labels live outside the candle
    // batch. We mark `_axisLabelsDirty` whenever OnPopulateMesh updates `_lastPriceTicks` /
    // `_lastTimeTicks` (which happens on every Render / Pan / Zoom / Reset / Resize), and
    // rebuild Text children in LateUpdate so we never spawn GameObjects during Canvas batch
    // processing.

    void LateUpdate()
    {
        if (_axisLabelsDirty)
        {
            _axisLabelsDirty = false;
            RebuildAxisLabels();
        }
    }

    // Public so headless E2E (AXIS-PRICE-01 / AXIS-TIME-01) can force a label rebuild without
    // needing MonoBehaviour.LateUpdate to tick. Idempotent — safe to call multiple times.
    public void RebuildAxisLabels()
    {
        if (_priceLabelsRoot == null || _timeLabelsRoot == null) return;
        EnsureLabelPool(_priceLabels, _priceLabelsRoot, _lastPriceTicks.Count, anchorRight: true);
        EnsureLabelPool(_timeLabels, _timeLabelsRoot, _lastTimeTicks.Count, anchorRight: false);

        var rect = rectTransform.rect;
        float topInset = GutterTop + (_showTitleBar ? TitleBarHeight + 4f : 0f);
        var plot = new Rect(
            rect.xMin + GutterLeft, rect.yMin + GutterBottom,
            Mathf.Max(0f, rect.width - GutterLeft - GutterRight),
            Mathf.Max(0f, rect.height - GutterBottom - topInset));
        float volumeH = plot.height * VolumeFrac;
        float mainY0 = plot.yMin + volumeH;
        float mainH = plot.height - volumeH;

        double step = _lastPriceTicks.Count >= 2 ? (_lastPriceTicks[1] - _lastPriceTicks[0])
                                                 : (ViewState.visible_max_price - ViewState.visible_min_price) / 8.0;
        int decimals = ChartScale.PriceTickDecimals(step);
        string fmt = "F" + decimals;
        var th = ThemeService.Current;
        for (int i = 0; i < _lastPriceTicks.Count; i++)
        {
            float y = MainPriceToY(_lastPriceTicks[i], mainY0, mainH);
            var rt = (RectTransform)_priceLabels[i].transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(GutterRight + 4f, 16f);
            rt.anchoredPosition = new Vector2(plot.xMax - rect.xMin + 2f, y - rect.yMin);
            _priceLabels[i].text = _lastPriceTicks[i].ToString(fmt);
            _priceLabels[i].color = th.colors.hakoniwa_text_muted;
        }

        var style = ChartScale.StyleFor(_lastTimeStep);
        for (int i = 0; i < _lastTimeTicks.Count; i++)
        {
            float x = ViewState.TimeToX(_lastTimeTicks[i], plot.xMin, plot.width);
            var rt = (RectTransform)_timeLabels[i].transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(60f, GutterBottom - 4f);
            rt.anchoredPosition = new Vector2(x - rect.xMin, plot.yMin - rect.yMin - GutterBottom + 2f);
            _timeLabels[i].text = ChartScale.FormatTimeLabel(_lastTimeTicks[i], style);
            _timeLabels[i].color = th.colors.hakoniwa_text_muted;
        }
    }

    void EnsureLabelPool(List<Text> pool, RectTransform parent, int needed, bool anchorRight)
    {
        // Pool grows monotonically; excess Text children are hidden (cheaper than Destroy in
        // headless edit mode and survives a future bar count drop without GameObject churn).
        while (pool.Count < needed)
        {
            var go = new GameObject("axisTick", typeof(RectTransform), typeof(Text));
            var t = go.GetComponent<Text>();
            t.font = _font; t.fontSize = 11; t.text = "";
            t.alignment = anchorRight ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            go.transform.SetParent(parent, false);
            pool.Add(t);
        }
        for (int i = 0; i < pool.Count; i++) pool[i].gameObject.SetActive(i < needed);
    }

    // ====== S2 #157: drag pan / wheel zoom / double-click reset (findings 0119 D-3) ======
    //
    // ScrollRect は不採用 (ADR-0034 §3) — flowsurface 流 chart-native UX を直訳。
    // 左ボタンドラッグ → ChartViewState.Pan / 右・中ドラッグ → no-op（floating window drag に流す）/
    // ホイール → cursor 中心 Zoom / Ctrl+wheel → no-op（PanCam 全体ズームへ譲る） /
    // ダブルクリック → ResetView。drag-tail click は `_draggedThisGesture` で除外
    // （Unity EventSystem の pixelDragThreshold + 明示フラグの両方で保護）。
    //
    // E2E (ChartInputE2ERunner.cs / PAN-01 / ZOOM-01 / RESET-01) は内部 PanByPixels / ZoomByScroll /
    // RequestResetView を直接叩く data-driven テスト + button フィルタの handler-level vacuity floor。

    bool _draggedThisGesture;

    // Pixel-based pan helper exposed to handlers + E2E (PAN-01 reads from here).
    public void PanByPixels(float dx_px)
    {
        ViewState.Pan(dx_px);
        SetVerticesDirty();
    }

    // Cursor-centered wheel zoom (ZOOM-01).
    public void ZoomByScroll(float scrollNotches, float cursorXLocal)
    {
        var rect = rectTransform.rect;
        float plotW = Mathf.Max(1f, rect.width - GutterLeft - GutterRight);
        ViewState.Zoom(scrollNotches, cursorXLocal - GutterLeft, plotW);
        SetVerticesDirty();
    }

    // Double-click ResetView (RESET-01). Returns to right-anchor + DEFAULT cell_width + auto_scale=true.
    public void RequestResetView()
    {
        if (_bars.Count == 0)
        {
            ViewState.cell_width_px = ChartViewState.DEFAULT_CELL_WIDTH_PX;
            ViewState.auto_scale = true;
            SetVerticesDirty();
            return;
        }
        var rect = rectTransform.rect;
        float plotW = Mathf.Max(1f, rect.width - GutterLeft - GutterRight);
        ViewState.ResetView(_bars[_bars.Count - 1].open_time_ms, plotW);
        SetVerticesDirty();
    }

    static bool IsCtrlHeld()
    {
        var kb = Keyboard.current;
        if (kb == null) return false;
        return kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        _draggedThisGesture = false;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        _draggedThisGesture = true;
        // eventData.delta is in screen pixels; Canvas scale (overlay = 1, scaler-driven canvases may
        // differ) is folded into the rectTransform's local rect — but the simplest faithful translation
        // (legacy parity with flowsurface's `cursor_delta`) is to use delta.x directly. Pan(dx_px) flips
        // sign internally so a right-drag scrolls the visible window to a LATER time (canonical chart UX).
        PanByPixels(eventData.delta.x);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        // No-op: drag-tail handling is finalized in OnPointerClick (which Unity only fires when the
        // pointer hasn't moved past the drag threshold, AND our _draggedThisGesture covers anything
        // the EventSystem might let slip past the threshold).
    }

    public void OnScroll(PointerEventData eventData)
    {
        // Ctrl + wheel is reserved for the parent PanCam's global zoom — chart wheel must no-op so
        // both gestures don't fight (findings 0119 D-3 / ADR-0034 §3).
        if (IsCtrlHeld()) return;

        // Convert cursor screen position → local x in this rect.
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out var local))
            return;
        ZoomByScroll(eventData.scrollDelta.y, local.x - rectTransform.rect.xMin);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        if (_draggedThisGesture) return;   // drag-tail click does NOT count as a double-click candidate.
        if (eventData.clickCount != 2) return;
        RequestResetView();
    }

    // ====== S4 #159: crosshair (hover) handlers + derive ======

    public void OnPointerEnter(PointerEventData eventData) => UpdateCrosshair(eventData);
    public void OnPointerMove(PointerEventData eventData) => UpdateCrosshair(eventData);
    public void OnPointerExit(PointerEventData eventData)
    {
        Crosshair.Clear();
        SetVerticesDirty();
    }

    void UpdateCrosshair(PointerEventData eventData)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out var local))
            return;
        Crosshair.cursor_world = local;
        DeriveCrosshair();
        SetVerticesDirty();
    }

    // Public so headless E2E (CROSSHAIR-01) can synthesise a cursor without PointerEventData and
    // exercise the same derive path the real pointer handlers walk.
    public void SetCrosshairCursorForTest(Vector2 local)
    {
        Crosshair.cursor_world = local;
        DeriveCrosshair();
        SetVerticesDirty();
    }

    void DeriveCrosshair()
    {
        if (!Crosshair.cursor_world.HasValue) { Crosshair.Clear(); return; }
        var rect = rectTransform.rect;
        float topInset = GutterTop + (_showTitleBar ? TitleBarHeight + 4f : 0f);
        var plot = new Rect(
            rect.xMin + GutterLeft, rect.yMin + GutterBottom,
            Mathf.Max(0f, rect.width - GutterLeft - GutterRight),
            Mathf.Max(0f, rect.height - GutterBottom - topInset));
        float volumeH = plot.height * VolumeFrac;
        float mainY0 = plot.yMin + volumeH;
        float mainH = plot.height - volumeH;

        var c = Crosshair.cursor_world.Value;
        Crosshair.hovered_time_ms = ViewState.XToTime(c.x, plot.xMin);
        // hovered_price is computed only when cursor is in main_area (findings 0119 D-6 / D-2).
        if (c.y >= mainY0 && c.y <= mainY0 + mainH && mainH > 0)
            Crosshair.hovered_price = ViewState.visible_min_price
                + ((c.y - mainY0) / mainH) * (ViewState.visible_max_price - ViewState.visible_min_price);
        else
            Crosshair.hovered_price = null;
        // hovered_volume: only in volume_area (S5). VolumeFrac=0 → never enters this branch.
        if (VolumeFrac > 0 && c.y >= plot.yMin && c.y < mainY0)
            Crosshair.hovered_volume = NearestVolumeAt(c.x, plot);
        else
            Crosshair.hovered_volume = null;
    }

    // S5 helper — finds the bar closest in time to cursor x and returns its volume.
    double? NearestVolumeAt(float cursorX, Rect plot)
    {
        if (_bars.Count == 0) return null;
        long t = ViewState.XToTime(cursorX, plot.xMin);
        int bestIdx = -1; long bestDt = long.MaxValue;
        for (int i = 0; i < _bars.Count; i++)
        {
            long dt = System.Math.Abs(_bars[i].open_time_ms - t);
            if (dt < bestDt) { bestDt = dt; bestIdx = i; }
        }
        return bestIdx >= 0 ? (double?)_bars[bestIdx].volume : null;
    }
}
