// DepthLadderView.cs — issue #54 "orderbook: depth ladder 描画を本番コンポーネント（DepthLadderView）に抽出"
// + follow-up: TTWR ladder parity elements (21-row fixed / LAST center / "---" / per-side alpha bg).
//
// The reusable, production bid/ask depth ladder widget. Before #54 the ladder render lived ONLY as
// an OnGUI (IMGUI) block embedded in the throwaway DepthLadderHitlHarness; there was no production
// renderer and no sample-able uGUI Graphic, so the #44 ThemeProbe could not verify the real ladder
// colors. This consolidates that single rendering into one component; the HITL harness and the #44
// montage now feed the SAME part (mirrors #53's ChartView extraction — findings 0024).
//
// PARITY STANCE (memory "TTWR parity first"; findings 0024): TTWR's ladder (overlays_ladder.rs) is
// an IMMEDIATE-mode Bevy system (rows despawn+respawn each changed frame) — there is no "view
// component" to port 1:1. This retained uGUI widget (rows rebuilt on Render) is a backcast-ORIGINAL
// structure forced by the framework gap, exactly like findings 0023's ChartView. Parity is held at
// the VISUAL/SEMANTIC level: pane bg = colors.background (overlays_ladder.rs:206, shared with the
// chart pane), the board is drawn in faithful WIRE order (findings 0012 §2.2 — no re-sort), and the
// LAYOUT now matches TTWR — a fixed 21-row DOM ladder (10 ask + LAST + 10 bid) with "---" fill and
// per-side alpha row backgrounds.
//
// COLOR ROLE DIVERGENCE (findings 0024): bid = status.bid (green.11) / ask = status.ask (red.11).
// TTWR reused status.long/short (green.9/red.9) for the ladder; backcast's #44 theme added a
// DEDICATED bid/ask role (Radix step 11) — a settled #44 divergence, not introduced here. The LAST
// row mirrors TTWR (element_background bg, status.warning text).
//
// LAYOUT (TTWR overlays_ladder.rs parity — supersedes #54's initial A案 extraction, findings 0024):
//   * fixed 21 rows: ask[9..0] reversed (worst ask top → best ask just above LAST), LAST center,
//     bid[0..9] (best just below LAST → worst bottom). Missing levels render as "---" (always 21).
//   * per-side alpha row backgrounds: ask = status.ask @0.22, bid = status.bid @0.22 (ROW_BG_ALPHA).
//   * LAST row shows "LAST {price}" from an optional last-price arg (or "LAST ---"); status.warning.
//   * !HasDepth → a single "(no board)" placeholder (Replay / not-yet-streamed), no alpha bg.
//
// THEME (issue #44 AC②): this view self-subscribes to ThemeService.Changed and re-applies, so it
// follows a runtime theme switch — the same build-once cure ChartView uses (findings 0018 L1).

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

public class DepthLadderView : MonoBehaviour
{
    const int LadderDepth = 10;     // levels per side (TTWR LADDER_DEPTH)
    const int TotalRows = 21;       // 10 ask + 1 LAST + 10 bid (TTWR fixed 21-row ladder)
    const float RowBgAlpha = 0.22f; // per-side row background alpha (TTWR ROW_BG_ALPHA)
    const float FallbackRowHeight = 16f; // used when the container has no laid-out height (headless probe)
    const float MinRowHeight = 10f;
    const float HeaderHeight = 20f;
    const float PadX = 8f;
    const int FontSize = 13;

    RectTransform _rowsRoot;   // rows stack downward from the top of this rect
    Image _bg;
    Text _header;
    Font _font;

    // Retained row graphics + their kind, so ApplyTheme re-colors in place (no re-decode).
    enum RowKind { Ask, Bid, Last, Placeholder }
    struct RowPart { public Image bg; public Text text; public RowKind kind; }
    readonly List<RowPart> _rows = new List<RowPart>();

    // best (= first in wire order) bid/ask Text + the LAST row Text, for the montage / ThemeProbe
    // color sample. In the 21-row layout these always exist when HasDepth (a missing level → "---").
    Text _bestBid, _bestAsk, _last;

    // ---- probe / montage seams (ThemeProbe samples the PRODUCTION graphics, findings 0024) ----
    public Image Background => _bg;
    public Text BestBid() => _bestBid;   // best bid row text (status.bid) — for ThemeProbe value-assert
    public Text BestAsk() => _bestAsk;   // best ask row text (status.ask) — for ThemeProbe value-assert
    public Text LastRow() => _last;      // LAST center row text (status.warning) — for ThemeProbe value-assert

    // Build the subtree under `parent`. Each consumer keeps ONLY its own parent-rect placement; the
    // ladder internals are identical here.
    public void Build(RectTransform parent)
    {
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // background fills the whole parent (colors.background — TTWR pane bg parity).
        var bgGo = new GameObject("LadderBg", typeof(RectTransform), typeof(Image));
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.SetParent(parent, false);
        Stretch(bgRt);
        _bg = bgGo.GetComponent<Image>();

        // column header (muted) pinned to the top strip.
        var headerGo = new GameObject("header", typeof(RectTransform), typeof(Text));
        var headerRt = headerGo.GetComponent<RectTransform>();
        headerRt.SetParent(parent, false);
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(-PadX * 2f, HeaderHeight);
        headerRt.anchoredPosition = Vector2.zero;
        _header = headerGo.GetComponent<Text>();
        _header.font = _font; _header.fontSize = FontSize; _header.text = "price          size";
        _header.alignment = TextAnchor.MiddleLeft; _header.supportRichText = false;
        _header.horizontalOverflow = HorizontalWrapMode.Overflow; _header.verticalOverflow = VerticalWrapMode.Overflow;

        // rows root = the area below the header; rows are anchored from its top edge. A RectMask2D
        // clips the fixed 21-row ladder to the pane so a pane shorter than 21*minRow never spills rows
        // over adjacent UI (the layout is fixed-count, unlike the pre-port dynamic-count ladder).
        var rootGo = new GameObject("Rows", typeof(RectTransform), typeof(RectMask2D));
        _rowsRoot = rootGo.GetComponent<RectTransform>();
        _rowsRoot.SetParent(parent, false);
        _rowsRoot.anchorMin = new Vector2(0f, 0f);
        _rowsRoot.anchorMax = new Vector2(1f, 1f);
        _rowsRoot.offsetMin = new Vector2(0f, 0f);
        _rowsRoot.offsetMax = new Vector2(0f, -HeaderHeight);

        ThemeService.Changed += ApplyTheme;
        ApplyTheme();
    }

    // Render the ladder. TTWR overlays_ladder.rs parity: a fixed 21-row DOM ladder — ask[9..0]
    // reversed (worst ask top, best ask just above LAST), LAST center, bid[0..9] (best just below
    // LAST, worst bottom). Missing levels render as "---". When !HasDepth, a single "no board"
    // placeholder (Replay / not-yet-streamed). `lastPrice` feeds the LAST row ("LAST ---" if null).
    public void Render(DepthSnapshotView snapshot, double? lastPrice = null)
    {
        for (int i = _rowsRoot.childCount - 1; i >= 0; i--)
            DestroyChild(_rowsRoot.GetChild(i).gameObject);
        _rows.Clear();
        _bestBid = _bestAsk = _last = null;

        if (!snapshot.HasDepth)
        {
            AddRow("(no board — Replay/None or not yet streamed)", RowKind.Placeholder, TextAnchor.MiddleLeft);
            LayoutRows();
            ApplyTheme();
            return;
        }

        var asks = snapshot.Asks;
        var bids = snapshot.Bids;

        // Ask block: index 9 (worst) at top → index 0 (best) just above the LAST row. Always 10 rows;
        // a missing level is "---". The BEST ask is asks[0], tracked regardless of display position.
        for (int i = LadderDepth - 1; i >= 0; i--)
        {
            var lvl = LevelAt(asks, i);
            var row = AddRow(FormatLevel("ASK", lvl), RowKind.Ask, TextAnchor.MiddleLeft);
            if (i == 0) _bestAsk = row;
        }

        // LAST row (center).
        _last = AddRow(lastPrice.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "LAST {0:0.00}", lastPrice.Value)
                : "LAST ---",
            RowKind.Last, TextAnchor.MiddleCenter);

        // Bid block: index 0 (best) just below LAST → index 9 (worst) at bottom. The BEST bid is bids[0].
        for (int i = 0; i < LadderDepth; i++)
        {
            var lvl = LevelAt(bids, i);
            var row = AddRow(FormatLevel("BID", lvl), RowKind.Bid, TextAnchor.MiddleLeft);
            if (i == 0) _bestBid = row;
        }

        LayoutRows();
        ApplyTheme();
    }

    static DepthLevelView? LevelAt(IReadOnlyList<DepthLevelView> levels, int i) =>
        (levels != null && i < levels.Count) ? levels[i] : (DepthLevelView?)null;

    // "SIDE  price  size", or "SIDE  ---" for a missing level (TTWR format_level_label).
    static string FormatLevel(string side, DepthLevelView? lv) =>
        lv.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0}   {1,10:0.0####}   {2,10:0.###}", side, lv.Value.Price, lv.Value.Size)
            : side + "   ---";

    // Append a row: a bg Image (per-side alpha / LAST element bg / transparent placeholder) carrying a
    // Text child. Returns the Text so Render can track best bid/ask. Coloring is done in ApplyTheme.
    Text AddRow(string label, RowKind kind, TextAnchor anchor)
    {
        var rowGo = new GameObject("row", typeof(RectTransform), typeof(Image));
        var rowRt = rowGo.GetComponent<RectTransform>();
        rowRt.SetParent(_rowsRoot, false);
        rowRt.anchorMin = new Vector2(0f, 1f);
        rowRt.anchorMax = new Vector2(1f, 1f);
        rowRt.pivot = new Vector2(0.5f, 1f);
        var bg = rowGo.GetComponent<Image>();

        var txtGo = new GameObject("t", typeof(RectTransform), typeof(Text));
        var txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.SetParent(rowRt, false);
        txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(PadX, 0f); txtRt.offsetMax = new Vector2(-PadX, 0f);
        var t = txtGo.GetComponent<Text>();
        t.font = _font; t.fontSize = FontSize; t.text = label; t.alignment = anchor;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;

        _rows.Add(new RowPart { bg = bg, text = t, kind = kind });
        return t;
    }

    // Size + stack the rows from the top. Row height fills the container across all rows (TTWR
    // ladder_height/21); falls back to a fixed height when the container has no laid-out height yet
    // (the headless ThemeProbe, which only samples colors).
    void LayoutRows()
    {
        float available = _rowsRoot != null ? _rowsRoot.rect.height : 0f;
        float rowH = available > 1f ? Mathf.Max(MinRowHeight, available / TotalRows) : FallbackRowHeight;
        for (int i = 0; i < _rows.Count; i++)
        {
            var rt = (RectTransform)_rows[i].bg.transform;
            rt.sizeDelta = new Vector2(0f, rowH);
            rt.anchoredPosition = new Vector2(0f, -i * rowH);
        }
    }

    // Re-paint bg + rows from the active theme. Subscribed to ThemeService.Changed.
    public void ApplyTheme()
    {
        var th = ThemeService.Current;
        if (_bg != null) _bg.color = th.colors.background;
        if (_header != null) _header.color = th.colors.text_muted;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            switch (r.kind)
            {
                case RowKind.Ask:
                    if (r.text != null) r.text.color = th.status.ask;
                    if (r.bg != null) r.bg.color = WithAlpha(th.status.ask, RowBgAlpha);
                    break;
                case RowKind.Bid:
                    if (r.text != null) r.text.color = th.status.bid;
                    if (r.bg != null) r.bg.color = WithAlpha(th.status.bid, RowBgAlpha);
                    break;
                case RowKind.Last:
                    if (r.text != null) r.text.color = th.status.warning;
                    if (r.bg != null) r.bg.color = th.colors.element_background;
                    break;
                default: // Placeholder
                    if (r.text != null) r.text.color = th.colors.text_muted;
                    if (r.bg != null) r.bg.color = Color.clear;
                    break;
            }
        }
    }

    static Color WithAlpha(Color c, float a) { c.a = a; return c; }

    void OnDestroy() => ThemeService.Changed -= ApplyTheme;

    // Destroy() is illegal in edit mode (the ThemeProbe AFK gate drives this widget headless).
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
