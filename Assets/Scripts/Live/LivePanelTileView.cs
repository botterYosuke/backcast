// LivePanelTileView.cs — issue #23 re-home slice (DURABLE tier, Unity boundary)
//
// The reusable uGUI content for a live data Hakoniwa tile (Orders / Positions / Run Result).
// findings 0014 RH2/RH4: ONE view class is Built three times — once per tile — each with its own
// formatter delegate, so the three tiles share construction/refresh but render their own slice of
// the LivePanelViewModel. The authority is _host.Panel (LivePanelViewModel, findings 0011 D2); this
// view NEVER decodes wire events or holds live state — it only renders a string the formatter
// derives from the VM.
//
// REFRESH (parity with WorkspaceFooterView): the caller may Refresh(panel) every frame, but the Text is
// only rewritten when the formatter output actually changes (cheap string compare), so a steady VM
// costs one format + one compare per frame and no uGUI mesh rebuild.

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class LivePanelTileView
{
    readonly Func<LivePanelViewModel, string> _format;
    Text _content;
    string _last;          // null sentinel: the first Refresh always writes (null != any formatted string)

    public LivePanelTileView(Func<LivePanelViewModel, string> formatter)
    {
        _format = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    // Build a single full-body Text into the tile body (the tile header/chrome is the caller's —
    // BackcastWorkspaceRoot.BuildTileChrome already supplies the panel bg + header label).
    public void Build(RectTransform body, Font font)
    {
        if (body == null) return;
        var go = new GameObject("content", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(body, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(8f, 6f); rt.offsetMax = new Vector2(-8f, -6f);
        _content = go.GetComponent<Text>();
        _content.font = font;
        _content.fontSize = 12;
        _content.color = new Color(0.90f, 0.92f, 0.94f, 1f);
        _content.alignment = TextAnchor.UpperLeft;
        _content.horizontalOverflow = HorizontalWrapMode.Wrap;
        _content.verticalOverflow = VerticalWrapMode.Truncate;
        _content.raycastTarget = false;   // data display only; body-drag falls through to canvas pan
    }

    // Rewrite the tile text from the VM ONLY when the formatted output changed. Returns true on a
    // real change (lets the caller fold this into a footer-style signature if desired).
    public bool Refresh(LivePanelViewModel panel)
    {
        if (_content == null) return false;
        string next = _format(panel) ?? "";
        if (next == _last) return false;
        _last = next;
        _content.text = next;
        return true;
    }

    // #61: honest empty state for the Replay shape. The base panels are mode-independent residents now,
    // but LivePanelViewModel (_host.Panel) is monotonic — it is NEVER cleared, so a Live→Replay flip would
    // otherwise leave the LAST live account/orders/fills on screen, misrepresenting Replay as carrying live
    // figures. In Replay the caller paints this fixed "(no data — Replay)" instead of the live formatter.
    public const string ReplayEmpty = "(no data — Replay)";
    public void ShowReplayEmpty()
    {
        if (_content == null || _last == ReplayEmpty) return;
        _last = ReplayEmpty;
        _content.text = ReplayEmpty;
    }

    // #65: render a Replay-derived string (the caller formats it from the decoded get_portfolio_json /
    // summary_json). Same dedup gate as Refresh/ShowReplayEmpty so a steady snapshot costs one compare
    // per frame and no uGUI rebuild. Keeps the view a dumb string sink — Replay formatting lives in the
    // caller, exactly as the Live path's per-tile formatter delegates do.
    public void ShowText(string text)
    {
        if (_content == null) return;
        string next = text ?? "";
        if (next == _last) return;
        _last = next;
        _content.text = next;
    }
}
