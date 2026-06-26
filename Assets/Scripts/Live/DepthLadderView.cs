// DepthLadderView.cs — S8 #161 / ADR-0035 / findings 0120 D-9..D-14: Mesh-based 21-row ladder
// with ChartPalette single-source for Bid/Ask + diff highlight (S9 #163 will wire diff Timer +
// chart↔ladder hover via ChartLadderRoot).
//
// Replaces the legacy #54 widget (per-row `Image` bg + `Text` label as separate GameObjects, full
// despawn/respawn on every Render). The new widget IS a `MaskableGraphic`: its OWN rectTransform
// IS the ladder pane. `OnPopulateMesh(VertexHelper vh)` emits in ONE batch:
//   * 21 per-side alpha bg quads (ROW_BG_ALPHA = 0.22) for ask[9..0] / LAST / bid[0..9]
//   * placeholder bg quad when !HasDepth (single "no board" row, no alpha bg)
//   * diff highlight tint quads (S9 — D-12)
//   * hover highlight tint quad (S9 — D-11)
// → GPU drawcall = 1. GameObject count = 1 + 21 retained Text children (one per row, in-place updated).
//
// Text labels are uGUI Text — retained in `_rowTexts[21]` and updated in-place by Render(). The Canvas
// handles glyph batching in its own sub-mesh, so labels don't share the same UIVertex buffer as the bg
// quads. findings 0120 D-9 says this is fine; TMP/SDF migration is a separate slice.
//
// PUBLIC API (findings 0120 D-13 — replaces `Image Background` / `Text BestBid()` / `Text BestAsk()` /
// `Text LastRow()`):
//   * Color BestBidColor / BestAskColor / LastRowColor       — ThemeProbe seam (single-source via
//                                                              ChartPalette → in lockstep with ChartView).
//   * Color BackgroundColor                                  — chart_bg / ladder_bg shared role.
//   * int RowCount                                            — 21 when HasDepth, 1 when placeholder.
//   * Color GetRowHighlightTint(int rowIndex)                 — current hover/diff tint Color or Color.clear.
//   * DepthSnapshotView CurrentSnapshot                       — last Render() input (test introspection).
//   * string BestBidRowText / BestAskRowText / LastRowText   — current text of those rows (NOT a UI
//                                                              widget; just the string, for E2E asserts).
//   * int RebuildCount                                        — increments on each effective Render call
//                                                              (early-out skips don't bump this).

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(CanvasRenderer))]
public class DepthLadderView : MaskableGraphic
{
    public const int LadderDepth = 10;     // levels per side (TTWR LADDER_DEPTH)
    public const int TotalRows = 21;       // 10 ask + 1 LAST + 10 bid (TTWR fixed 21-row ladder)
    const float RowBgAlpha = 0.22f;        // per-side row background alpha (TTWR ROW_BG_ALPHA)
    const float HeaderHeight = 20f;
    const float PadX = 8f;
    const int FontSize = 13;

    enum RowKind { Ask, Bid, Last, Placeholder }

    Text _header;
    Font _font;
    readonly Text[] _rowTexts = new Text[TotalRows];
    RectTransform _rowsRoot;

    // Per-row highlight tints (S9 #163 — D-11 hover / D-12 diff). Index 0..20 maps to display row
    // (ask[9] at index 0, LAST at index 10, bid[0] at index 11, bid[9] at index 20). For S8 nothing
    // sets these (they stay Color.clear); S9 will write into them via a public seam.
    readonly Color[] _rowHighlightTints = new Color[TotalRows];

    // Current snapshot (test introspection + diff base for S9 — D-12).
    public DepthSnapshotView CurrentSnapshot { get; private set; }
    public DepthSnapshotView PreviousSnapshot { get; private set; }   // S9: diff base.
    double? _lastPrice;

    // Rebuild observability (DEPTH-10 migration). Bumps once per Render() call. The root-level
    // early-out (`BackcastWorkspaceRoot.DepthSignature` / `_depthRendered`) decides whether Render
    // gets called at all — when sig matches, Render isn't called and RebuildCount doesn't move.
    public int RebuildCount { get; private set; }

    // ---- new public API (findings 0120 D-13) ----

    public Color BestBidColor => ChartPalette.Bullish();
    public Color BestAskColor => ChartPalette.Bearish();
    public Color LastRowColor => ChartPalette.Last();
    public Color BackgroundColor => ChartPalette.Background();

    public int RowCount
    {
        get
        {
            if (CurrentSnapshot.HasDepth) return TotalRows;
            // Placeholder mode (e.g. Replay / not-yet-streamed) — 1 row.
            return 1;
        }
    }

    public Color GetRowHighlightTint(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rowHighlightTints.Length) return Color.clear;
        return _rowHighlightTints[rowIndex];
    }

    // S9 seam — for diff highlight Timer / hover write. Returns Color.clear when row out of range.
    public void SetRowHighlightTint(int rowIndex, Color tint)
    {
        if (rowIndex < 0 || rowIndex >= _rowHighlightTints.Length) return;
        _rowHighlightTints[rowIndex] = tint;
        SetVerticesDirty();
    }

    public void ClearAllHighlights()
    {
        for (int i = 0; i < _rowHighlightTints.Length; i++) _rowHighlightTints[i] = Color.clear;
        SetVerticesDirty();
    }

    public string BestBidRowText
    {
        get
        {
            if (!CurrentSnapshot.HasDepth) return null;
            return _rowTexts[LadderDepth + 1] != null ? _rowTexts[LadderDepth + 1].text : null;   // index 11 = bid[0]
        }
    }
    public string BestAskRowText
    {
        get
        {
            if (!CurrentSnapshot.HasDepth) return null;
            return _rowTexts[LadderDepth - 1] != null ? _rowTexts[LadderDepth - 1].text : null;   // index 9 = ask[0]
        }
    }
    public string LastRowText
    {
        get
        {
            if (!CurrentSnapshot.HasDepth) return _rowTexts[0] != null ? _rowTexts[0].text : null;
            return _rowTexts[LadderDepth] != null ? _rowTexts[LadderDepth].text : null;   // index 10 = LAST
        }
    }

    // ---- Build (legacy signature: callers pass parent only) ----

    public void Build(RectTransform parent)
    {
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (transform != parent)
        {
            Debug.LogWarning("[DepthLadderView] Build(parent) called with a different rect — widget IS the pane");
        }
        color = Color.white;   // bg is per-vertex; Graphic.color must not tint.
        raycastTarget = true;

        var prt = (RectTransform)transform;
        // Header (legacy parity — pinned to top strip).
        var headerGo = new GameObject("header", typeof(RectTransform), typeof(Text));
        var headerRt = headerGo.GetComponent<RectTransform>();
        headerRt.SetParent(prt, false);
        headerRt.anchorMin = new Vector2(0f, 1f); headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(-PadX * 2f, HeaderHeight);
        headerRt.anchoredPosition = Vector2.zero;
        _header = headerGo.GetComponent<Text>();
        _header.font = _font; _header.fontSize = FontSize; _header.text = "price          size";
        _header.alignment = TextAnchor.MiddleLeft; _header.supportRichText = false;
        _header.horizontalOverflow = HorizontalWrapMode.Overflow;
        _header.verticalOverflow = VerticalWrapMode.Overflow;
        _header.raycastTarget = false;

        // Rows root = area below header. RectMask2D clips short panes (legacy parity).
        var rootGo = new GameObject("Rows", typeof(RectTransform), typeof(RectMask2D));
        _rowsRoot = rootGo.GetComponent<RectTransform>();
        _rowsRoot.SetParent(prt, false);
        _rowsRoot.anchorMin = new Vector2(0f, 0f); _rowsRoot.anchorMax = new Vector2(1f, 1f);
        _rowsRoot.offsetMin = new Vector2(0f, 0f); _rowsRoot.offsetMax = new Vector2(0f, -HeaderHeight);

        // Pre-allocate 21 Text children (in-place updated by Render — no despawn/respawn).
        for (int i = 0; i < TotalRows; i++)
        {
            var txtGo = new GameObject("row" + i, typeof(RectTransform), typeof(Text));
            var rt = txtGo.GetComponent<RectTransform>();
            rt.SetParent(_rowsRoot, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(PadX, 0f); rt.offsetMax = new Vector2(-PadX, 0f);
            var t = txtGo.GetComponent<Text>();
            t.font = _font; t.fontSize = FontSize; t.text = "";
            t.alignment = TextAnchor.MiddleLeft;
            t.supportRichText = false; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            _rowTexts[i] = t;
        }

        ThemeService.Changed += OnThemeChanged;
        OnThemeChanged();
    }

    // ---- Render: in-place update Text + bump RebuildCount on real change (DEPTH-10 early-out) ----

    public void Render(DepthSnapshotView snapshot, double? lastPrice = null)
    {
        PreviousSnapshot = CurrentSnapshot;   // S9 #163 diff base (level-by-level size deltas).
        CurrentSnapshot = snapshot;
        _lastPrice = lastPrice;
        RebuildCount++;

        if (!snapshot.HasDepth)
        {
            _rowTexts[0].text = "(no board — Replay/None or not yet streamed)";
            _rowTexts[0].alignment = TextAnchor.MiddleLeft;
            _rowTexts[0].gameObject.SetActive(true);
            for (int i = 1; i < TotalRows; i++) _rowTexts[i].gameObject.SetActive(false);
        }
        else
        {
            // Ask block: rows 0..9 are ask[9] (worst) → ask[0] (best, sits just above LAST at row 10).
            for (int displayIdx = 0; displayIdx < LadderDepth; displayIdx++)
            {
                int askLevelIdx = LadderDepth - 1 - displayIdx;
                var lvl = LevelAt(snapshot.Asks, askLevelIdx);
                _rowTexts[displayIdx].text = FormatLevel("ASK", lvl);
                _rowTexts[displayIdx].alignment = TextAnchor.MiddleLeft;
                _rowTexts[displayIdx].gameObject.SetActive(true);
            }
            // LAST row (row 10).
            _rowTexts[LadderDepth].text = lastPrice.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "LAST {0:0.00}", lastPrice.Value)
                : "LAST ---";
            _rowTexts[LadderDepth].alignment = TextAnchor.MiddleCenter;
            _rowTexts[LadderDepth].gameObject.SetActive(true);
            // Bid block: rows 11..20 are bid[0] (best, just below LAST) → bid[9] (worst, bottom).
            for (int bidLevelIdx = 0; bidLevelIdx < LadderDepth; bidLevelIdx++)
            {
                int displayIdx = LadderDepth + 1 + bidLevelIdx;
                var lvl = LevelAt(snapshot.Bids, bidLevelIdx);
                _rowTexts[displayIdx].text = FormatLevel("BID", lvl);
                _rowTexts[displayIdx].alignment = TextAnchor.MiddleLeft;
                _rowTexts[displayIdx].gameObject.SetActive(true);
            }
        }

        LayoutRows();
        ApplyTheme();
        SetVerticesDirty();
    }

    static DepthLevelView? LevelAt(IReadOnlyList<DepthLevelView> levels, int i) =>
        (levels != null && i < levels.Count) ? levels[i] : (DepthLevelView?)null;

    static string FormatLevel(string side, DepthLevelView? lv) =>
        lv.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0}   {1,10:0.0####}   {2,10:0.###}", side, lv.Value.Price, lv.Value.Size)
            : side + "   ---";

    void LayoutRows()
    {
        if (_rowsRoot == null) return;
        float available = _rowsRoot.rect.height;
        int activeCount = CurrentSnapshot.HasDepth ? TotalRows : 1;
        float rowH = available > 1f ? Mathf.Max(10f, available / TotalRows) : 16f;
        for (int i = 0; i < activeCount; i++)
        {
            if (_rowTexts[i] == null) continue;
            var rt = (RectTransform)_rowTexts[i].transform;
            rt.sizeDelta = new Vector2(0f, rowH);
            rt.anchoredPosition = new Vector2(0f, -i * rowH);
        }
    }

    public void ApplyTheme()
    {
        var th = ThemeService.Current;
        if (_header != null) _header.color = th.colors.hakoniwa_text_muted;
        if (!CurrentSnapshot.HasDepth)
        {
            if (_rowTexts[0] != null) _rowTexts[0].color = th.colors.hakoniwa_text_muted;
            return;
        }
        // Ask rows 0..9.
        for (int i = 0; i < LadderDepth; i++)
            if (_rowTexts[i] != null) _rowTexts[i].color = ChartPalette.Bearish();
        // LAST row 10.
        if (_rowTexts[LadderDepth] != null) _rowTexts[LadderDepth].color = ChartPalette.Last();
        // Bid rows 11..20.
        for (int i = LadderDepth + 1; i < TotalRows; i++)
            if (_rowTexts[i] != null) _rowTexts[i].color = ChartPalette.Bullish();
        SetVerticesDirty();
    }

    void OnThemeChanged() { ApplyTheme(); SetVerticesDirty(); }

    protected override void OnDestroy()
    {
        ThemeService.Changed -= OnThemeChanged;
        base.OnDestroy();
    }

    // ---- OnPopulateMesh: bg + 21 per-side alpha bg quads + LAST row bg + hover/diff highlight ----

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var rect = rectTransform.rect;
        if (rect.width <= 0 || rect.height <= 0) return;

        // Full-rect background (legacy parity — chart/ladder share Hakoniwa-isolated bg role).
        EmitQuad(vh, rect.xMin, rect.yMin, rect.xMax, rect.yMax, ChartPalette.Background());

        // Rows area = below header (HeaderHeight from top).
        float rowsTop = rect.yMax - HeaderHeight;
        float rowsHeight = (rect.yMax - HeaderHeight) - rect.yMin;
        if (rowsHeight <= 0) return;

        if (!CurrentSnapshot.HasDepth)
        {
            // Placeholder mode — single row, no alpha bg (legacy parity).
            return;
        }

        Color askBg = ChartPalette.Bearish(); askBg.a = RowBgAlpha;
        Color bidBg = ChartPalette.Bullish(); bidBg.a = RowBgAlpha;
        Color lastBg = ThemeService.Current.colors.hakoniwa_tile_background;
        float rowH = rowsHeight / TotalRows;
        for (int i = 0; i < TotalRows; i++)
        {
            float yTop = rowsTop - i * rowH;
            float yBot = yTop - rowH;
            Color bg;
            if (i < LadderDepth) bg = askBg;
            else if (i == LadderDepth) bg = lastBg;
            else bg = bidBg;
            EmitQuad(vh, rect.xMin, yBot, rect.xMax, yTop, bg);

            // Highlight tint quad over bg (S9 — D-11 hover / D-12 diff). Same Mesh batch so drawcall=1.
            Color hl = _rowHighlightTints[i];
            if (hl.a > 0f)
                EmitQuad(vh, rect.xMin, yBot, rect.xMax, yTop, hl);
        }
    }

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
}

// DepthSignature — moved from the (retired) signature used by BackcastWorkspaceRoot's DriveDepthLadders
// early-out. Kept here so the per-snapshot rebuild gate (DEPTH-10) lives WITH the new Mesh widget that
// honors it. TimestampMs is EXCLUDED (a venue that bumps ts every poll with unchanged depth must STILL
// short-circuit the rebuild; legacy spec).
public sealed class DepthSignature
{
    public bool HasDepth;
    public double[] BidPrices, BidSizes, AskPrices, AskSizes;
    public double? LastPrice;

    public static DepthSignature Of(DepthSnapshotView s, double? lastPrice)
    {
        var sig = new DepthSignature { HasDepth = s.HasDepth, LastPrice = lastPrice };
        if (s.HasDepth)
        {
            sig.BidPrices = Snapshot(s.Bids, true);
            sig.BidSizes = Snapshot(s.Bids, false);
            sig.AskPrices = Snapshot(s.Asks, true);
            sig.AskSizes = Snapshot(s.Asks, false);
        }
        return sig;
    }

    static double[] Snapshot(IReadOnlyList<DepthLevelView> levels, bool price)
    {
        if (levels == null) return Array.Empty<double>();
        var arr = new double[levels.Count];
        for (int i = 0; i < levels.Count; i++) arr[i] = price ? levels[i].Price : levels[i].Size;
        return arr;
    }

    public override bool Equals(object obj)
    {
        if (!(obj is DepthSignature o)) return false;
        if (HasDepth != o.HasDepth) return false;
        if (LastPrice != o.LastPrice) return false;
        return ArrEq(BidPrices, o.BidPrices) && ArrEq(BidSizes, o.BidSizes)
            && ArrEq(AskPrices, o.AskPrices) && ArrEq(AskSizes, o.AskSizes);
    }
    public override int GetHashCode() => HasDepth.GetHashCode() ^ (LastPrice?.GetHashCode() ?? 0);

    static bool ArrEq(double[] a, double[] b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
