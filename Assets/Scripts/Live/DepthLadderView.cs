// DepthLadderView.cs — issue #54 "orderbook: depth ladder 描画を本番コンポーネント（DepthLadderView）に抽出"
//
// The reusable, production bid/ask depth ladder widget. Before #54 the ladder render lived ONLY as
// an OnGUI (IMGUI) block embedded in the throwaway DepthLadderHitlHarness; there was no production
// renderer and no sample-able uGUI Graphic, so the #44 ThemeProbe could not verify the real ladder
// colors. This consolidates that single rendering into one component; the HITL harness and the #44
// montage now feed the SAME part (mirrors #53's ChartView extraction — findings 0024).
//
// PARITY STANCE (memory "TTWR parity first"; findings 0024): TTWR's ladder (overlays_ladder.rs) is
// an IMMEDIATE-mode Bevy system (rows despawn+respawn each changed frame) — there is no "view
// component" to port 1:1. This retained uGUI widget (Text rows rebuilt on Render) is a backcast-
// ORIGINAL structure forced by the framework gap, exactly like findings 0023's ChartView. Parity is
// held at the VISUAL/SEMANTIC level: pane bg = colors.background (overlays_ladder.rs:206, shared with
// the chart pane), and the board is drawn in faithful WIRE order (findings 0012 §2.2 — no re-sort).
//
// COLOR ROLE DIVERGENCE (findings 0024): bid = status.bid (green.11) / ask = status.ask (red.11).
// TTWR reused status.long/short (green.9/red.9) for the ladder; backcast's #44 theme added a
// DEDICATED bid/ask role (Radix step 11) — a settled #44 divergence, not introduced here.
//
// SCOPE (A案 — extraction, findings 0024): the CURRENT OnGUI look (asks reversed for display, a
// "spread" separator, bids in wire order, a single "no board" placeholder when !HasDepth). TTWR's
// richer ladder — fixed 21 rows, a LAST center row, "---" placeholders, per-side alpha backgrounds —
// is NOT ported here and is split to a follow-up, keeping this an extraction, not a feature add.
//
// THEME (issue #44 AC②): this view self-subscribes to ThemeService.Changed and re-applies, so it
// follows a runtime theme switch — the same build-once cure ChartView uses (findings 0018 L1).

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

public class DepthLadderView : MonoBehaviour
{
    const float RowHeight = 18f;
    const float HeaderHeight = 20f;
    const float PadX = 8f;
    const int FontSize = 14;

    RectTransform _rowsRoot;   // rows stack downward from the top of this rect
    Image _bg;
    Text _header;
    Font _font;

    // Retained row graphics + their kind, so ApplyTheme re-colors in place (no re-decode).
    enum RowKind { Ask, Bid, Separator, Placeholder }
    struct RowPart { public Text text; public RowKind kind; }
    readonly List<RowPart> _rows = new List<RowPart>();

    // best (= first in wire order) bid/ask Text, for the montage / ThemeProbe color sample.
    Text _bestBid, _bestAsk;

    // ---- probe / montage seams (ThemeProbe samples the PRODUCTION graphics, findings 0024) ----
    public Image Background => _bg;
    public Text BestBid() => _bestBid;   // best bid row text (status.bid) — for ThemeProbe value-assert
    public Text BestAsk() => _bestAsk;   // best ask row text (status.ask) — for ThemeProbe value-assert

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
        _header = MakeRow(parent, "price          size", 0, isHeader: true);
        _header.alignment = TextAnchor.MiddleLeft;

        // rows root = the area below the header; rows are anchored from its top edge.
        var rootGo = new GameObject("Rows", typeof(RectTransform));
        _rowsRoot = rootGo.GetComponent<RectTransform>();
        _rowsRoot.SetParent(parent, false);
        _rowsRoot.anchorMin = new Vector2(0f, 0f);
        _rowsRoot.anchorMax = new Vector2(1f, 1f);
        _rowsRoot.offsetMin = new Vector2(0f, 0f);
        _rowsRoot.offsetMax = new Vector2(0f, -HeaderHeight);

        ThemeService.Changed += ApplyTheme;
        ApplyTheme();
    }

    // Render the ladder. Same look the OnGUI harness shared: asks reversed for display (highest ask
    // top → lowest just above the spread), a "spread" separator, bids in wire order (highest just
    // below the spread). When !HasDepth, a single "no board" placeholder (Replay / not-yet-streamed).
    public void Render(DepthSnapshotView snapshot)
    {
        for (int i = _rowsRoot.childCount - 1; i >= 0; i--)
            DestroyChild(_rowsRoot.GetChild(i).gameObject);
        _rows.Clear();
        _bestBid = _bestAsk = null;

        if (!snapshot.HasDepth)
        {
            AddRow("(no board — Replay/None or not yet streamed)", RowKind.Placeholder);
            ApplyTheme();
            return;
        }

        var asks = snapshot.Asks;
        var bids = snapshot.Bids;

        // asks: reversed for display (highest ask top → best ask just above the spread). The BEST ask
        // is asks[0] (wire order ascending) — tracked regardless of its display position.
        for (int k = (asks != null ? asks.Count : 0) - 1; k >= 0; k--)
        {
            var row = AddRow(FormatLevel("ASK", asks[k]), RowKind.Ask);
            if (k == 0) _bestAsk = row;
        }

        AddRow("————— spread —————", RowKind.Separator);

        // bids: wire order (highest bid just below the spread). The BEST bid is bids[0] (descending).
        for (int k = 0; k < (bids != null ? bids.Count : 0); k++)
        {
            var row = AddRow(FormatLevel("BID", bids[k]), RowKind.Bid);
            if (k == 0) _bestBid = row;
        }

        ApplyTheme();
    }

    static string FormatLevel(string side, DepthLevelView lv) =>
        string.Format(CultureInfo.InvariantCulture, "{0}   {1,10:0.0####}   {2,10:0.###}", side, lv.Price, lv.Size);

    Text AddRow(string label, RowKind kind)
    {
        int index = _rows.Count;
        var t = MakeRow(_rowsRoot, label, index, isHeader: false);
        _rows.Add(new RowPart { text = t, kind = kind });
        return t;
    }

    // One row Text anchored from the top edge of `parent`, stacked by `index` * RowHeight.
    Text MakeRow(RectTransform parent, string label, int index, bool isHeader)
    {
        var go = new GameObject(isHeader ? "header" : "row", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        float h = isHeader ? HeaderHeight : RowHeight;
        rt.sizeDelta = new Vector2(-PadX * 2f, h);
        rt.anchoredPosition = new Vector2(0f, isHeader ? 0f : -index * RowHeight);
        var t = go.GetComponent<Text>();
        t.font = _font;
        t.fontSize = FontSize;
        t.text = label;
        t.alignment = TextAnchor.MiddleLeft;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
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
            if (r.text == null) continue;
            r.text.color = r.kind switch
            {
                RowKind.Ask => th.status.ask,
                RowKind.Bid => th.status.bid,
                _ => th.colors.text_muted,   // Separator / Placeholder
            };
        }
    }

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
