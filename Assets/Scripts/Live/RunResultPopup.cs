// RunResultPopup.cs — #172 / #173 (ADR-0037 / findings 0125)
//
// The screen-anchored top-right Run Result popup card. run_result USED to be a back-plane dock
// base singleton (DockLayer, 1.0× parallax) that panned with the infinite canvas, reserved a
// gutter, and rode CaptureLayout. ADR-0037 cuts it over to a popup that lives DIRECTLY under a
// ScreenSpaceOverlay Canvas (NOT the infinite-canvas Content), so it does NOT move when the canvas
// is panned (it is excluded from the parallax "3D space"). Fixed size, no drag/resize — only a
// title bar and a × close.
//
// This view owns ONLY the chrome + visibility + close affordance. The body Text is produced by the
// reused LivePanelTileView (FormatRunResult / FormatReplayRunResult* are unchanged — ADR-0037 D6 /
// findings 0125 D5/F9): the caller Builds that view into `Body`. The content-derived visibility,
// the LiveAuto-scoped hasContent gate (D3), and the dismiss latch + symmetric re-arm (#173, D7/D8)
// all live in the caller (BackcastWorkspaceRoot) where the poll + mode authority are — this view
// just exposes SetVisible / OnClose so it stays a dumb, theme-aware shell (mirrors LivePanelTileView).

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class RunResultPopup
{
    // Fixed card geometry (screen px). Sized for the two-line run summary (running counts /
    // full stats / LiveAuto telemetry); never resized — the popup is not a draggable window.
    const float CardWidth = 360f;
    // #185 (findings 0134): 136 (was 112) to fit the added time line — three body lines now
    // (time / running|run / pnl|telemetry) instead of two.
    const float CardHeight = 136f;
    const float Margin = 16f;            // inset from the screen's top-right corner
    const float MenuClearance = 48f;     // clear the top menu bar (~40px) + an 8px gap
    const float TitleHeight = DockWindowFrame.TitleHeight;   // share the dock title-bar height
    const float CloseSize = 20f;

    // sortingOrder: above the dock plane (Content @0), BELOW the add-cell overlay (200) and the
    // secret/settings modals (900/1000+), so a modal always covers the popup. The menu bar (600) sits
    // higher but never overlaps — MenuClearance keeps the popup below it. The popup (top-right) and
    // add-cell (bottom-right) never overlap.
    const int SortingOrder = 190;

    GameObject _root;            // the card root — SetActive toggles the whole popup's visibility
    RectTransform _body;         // content region under the title bar (LivePanelTileView Builds here)
    Image _cardBg;
    Image _titleBg;
    Text _titleText;
    Image _closeBg;
    Text _closeLabel;

    // Raised when the user clicks ×. The caller latches dismissal (findings 0125 D7).
    public Action OnClose;

    // The body RectTransform the caller injects the reused LivePanelTileView into.
    public RectTransform Body => _body;

    // True iff the card is currently shown (the caller drives this via SetVisible).
    public bool IsVisible => _root != null && _root.activeSelf;

    public void Build(Transform parent, Font font)
    {
        var overlayGo = new GameObject("RunResultPopupOverlay",
            typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        overlayGo.transform.SetParent(parent, false);   // parent = BackcastWorkspaceRoot (NOT Content)
        var canvas = overlayGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;

        var colors = ThemeService.Current.colors;

        var rootGo = new GameObject("RunResultCard", typeof(RectTransform), typeof(Image));
        _root = rootGo;
        var rt = (RectTransform)rootGo.transform;
        rt.SetParent(overlayGo.transform, false);
        rt.anchorMin = new Vector2(1f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(CardWidth, CardHeight);
        rt.anchoredPosition = new Vector2(-Margin, -MenuClearance);   // top-right, below the menu bar
        _cardBg = rootGo.GetComponent<Image>();
        _cardBg.color = colors.hakoniwa_panel_surface;
        _cardBg.raycastTarget = false;   // click/drag falls through to canvas pan (mirrors LivePanelTileView body); only × is interactive

        // Title bar (accent fill, white caption) — mirrors DockWindowFrame so the popup reads as
        // the same family of card the run_result tile was. Accent = the old run_result slot (6).
        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image));
        var titleRt = (RectTransform)titleGo.transform;
        titleRt.SetParent(rt, false);
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TitleHeight); titleRt.offsetMax = Vector2.zero;
        _titleBg = titleGo.GetComponent<Image>();
        _titleBg.color = ThemeService.Current.players.Get(6);
        _titleBg.raycastTarget = false;   // popup is fixed (no title-drag); let pan fall through — only the × button consumes input

        var labelGo = new GameObject("Title", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(titleRt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-(CloseSize + 8f), 0f);
        _titleText = labelGo.GetComponent<Text>();
        _titleText.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _titleText.fontSize = 12;
        _titleText.color = Color.white;
        _titleText.alignment = TextAnchor.MiddleLeft;
        _titleText.text = "Run Result";
        _titleText.raycastTarget = false;

        // × close button (top-right of the title bar). Unlike the dock kinds (closeable=false /
        // workspace-owned), the popup IS user-closable — that is the whole point of #173.
        var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
        var crt = (RectTransform)closeGo.transform;
        crt.SetParent(titleRt, false);
        crt.anchorMin = new Vector2(1f, 0.5f); crt.anchorMax = new Vector2(1f, 0.5f); crt.pivot = new Vector2(1f, 0.5f);
        crt.sizeDelta = new Vector2(CloseSize, CloseSize);
        crt.anchoredPosition = new Vector2(-4f, 0f);
        _closeBg = closeGo.GetComponent<Image>();
        _closeBg.color = new Color(0f, 0f, 0f, 0.18f);

        var clblGo = new GameObject("X", typeof(RectTransform), typeof(Text));
        var clrt = (RectTransform)clblGo.transform;
        clrt.SetParent(crt, false);
        clrt.anchorMin = Vector2.zero; clrt.anchorMax = Vector2.one; clrt.offsetMin = Vector2.zero; clrt.offsetMax = Vector2.zero;
        _closeLabel = clblGo.GetComponent<Text>();
        _closeLabel.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _closeLabel.text = "×";   // ×
        _closeLabel.fontSize = 16;
        _closeLabel.color = Color.white;
        _closeLabel.alignment = TextAnchor.MiddleCenter;
        _closeLabel.raycastTarget = false;
        closeGo.GetComponent<Button>().onClick.AddListener(() => OnClose?.Invoke());

        // Body region under the title bar — the caller Builds the reused LivePanelTileView here.
        var bodyGo = new GameObject("Body", typeof(RectTransform));
        _body = (RectTransform)bodyGo.transform;
        _body.SetParent(rt, false);
        _body.anchorMin = Vector2.zero; _body.anchorMax = Vector2.one;
        _body.offsetMin = new Vector2(4f, 4f); _body.offsetMax = new Vector2(-4f, -(TitleHeight + 2f));

        WindowChrome.Attach(rt);   // appearance-aware chrome (dark HUD brackets ⇔ light Miro card, ADR-0028)

        _root.SetActive(false);    // content-derived: starts hidden, the caller shows it when a run has content
    }

    // Toggle the whole card (content-derived visibility ∧ !dismissed — computed by the caller).
    public void SetVisible(bool visible)
    {
        if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
    }

    // ADR-0028 live theme switch: re-read the ACCENT title bar so a Dark↔Light flip repaints it.
    // The card surface (_cardBg) is repainted automatically by the WindowChromeApplier that
    // WindowChrome.Attach wired (it tracks hakoniwa_panel_surface on ThemeService.Changed), and the
    // body Text is the reused LivePanelTileView's own ApplyTheme — so only the accent needs this hook.
    public void ApplyTheme()
    {
        if (_titleBg != null) _titleBg.color = ThemeService.Current.players.Get(6);
    }
}
