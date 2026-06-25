// SettingsModalOverlay.cs — #125 (ADR-0026) chrome, redesigned for #137 (findings 0107 S2/S3/D5).
//
// CHROME, mirroring SecretModalOverlay/SaveGuardOverlay: its OWN ScreenSpaceOverlay canvas, OUTSIDE the
// infinite-canvas Content (never pans/zooms), with a full-screen input-blocking backdrop and a centered
// panel. The host (BackcastWorkspaceRoot) builds the reused section views (Venue/Mode/Scenario/Data/
// Appearance) into the exposed section containers; the brains (VenueMenuViewModel / FooterModeViewModel /
// ScenarioStartupController) are reused unchanged — ADR-0026.
//
// #137 REDESIGN (findings 0107):
//   * S2 — TWO TABS「実行 / 外観」. 実行 = Venue+Mode+Scenario+Data, 外観 = Appearance. The tab content is
//     two group containers; SelectTab toggles their activeSelf (the brains' per-frame Refresh / VM Refresh /
//     universe 購読 run harmlessly while inactive — findings 0107 D6). The panel sizes to the taller 実行 tab
//     (owner accepted「実行」being long — D-B), so no scroll chrome is needed.
//   * S3 — each section sits in a themed CARD面 (elevated_surface_background, raised from the panel face);
//     headers are muted/uppercase (text_muted).
//   * D5 — all chrome colors resolve through ThemeService roles (NO inline color constants — findings 0020),
//     and ApplyTheme repaints the chrome (panel + cards + headers + title + tabs + close button) on a LIVE
//     Dark/Light switch (the switch lives in THIS modal's Appearance tab, so the chrome must re-theme in
//     place). Venue/Mode section views own their own rebake (see SettingsVenueSectionView.ApplyTheme /
//     SettingsModeSegmentView.Refresh) and are invoked from BackcastWorkspaceRoot.ApplyViewportTheme.
//     The only theme-independent fill is the modal SCRIM (a dim veil is focus chrome, not a surface).
//
// Z-ORDER (ADR-0026 §28 / findings 0102 D2): SETTINGS_SORT sits BELOW the secret/save-guard overlays
// (sortingOrder 1000) and ABOVE the menu bar (MenuBarView.MENU_SORT 600).
//
// CLOSE: the [x] button raises CloseClicked; ESC toggle is owned by SettingsModalController.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public enum SettingsTab { Run, Appearance }

public sealed class SettingsModalOverlay : MonoBehaviour
{
    // chrome z-order contract (findings 0045/0102): menu(600) < settings(900) < secret/save-guard(1000).
    public const int SETTINGS_SORT = 900;

    // shared spacing constants (findings 0107 D5 — a future #52 spacing-token pass can absorb these).
    const float PanelWidth = 600f, Margin = 16f, CardGap = 10f, CardInsetX = 12f;
    const float TitleH = 24f, TabBarH = 30f, CardHeaderH = 22f, CardPadBottom = 10f, GroupGapTop = 8f;

    public event Action CloseClicked;

    Canvas _canvas;
    GameObject _scrim;          // root toggled by SetVisible (the backdrop GameObject)
    RectTransform _panel;
    Font _font;
    bool _visible;

    // Tab content groups + tab buttons (S2). The host fills the section containers inside the run group's
    // cards; SelectTab toggles which group is active.
    RectTransform _runGroup, _appearanceGroup;
    Button _runTabBtn, _appearanceTabBtn;
    SettingsTab _activeTab = SettingsTab.Run;

    // Retained themed graphics (D5) so ApplyTheme() repaints them on a live Dark/Light switch.
    Image _panelImg;
    readonly List<Image> _cardImgs = new List<Image>();
    readonly List<Text> _headerTexts = new List<Text>();
    Text _titleText;
    Image _closeBtnImg;
    Text _closeBtnText;

    // Section content containers (host builds the reused section views into these).
    public RectTransform VenueSection { get; private set; }
    public RectTransform ModeSection { get; private set; }
    public RectTransform ScenarioSection { get; private set; }
    public RectTransform DataSection { get; private set; }        // #137 S4: DuckDB root
    public RectTransform AppearanceSection { get; private set; }  // ADR-0028: Dark/Light theme switch

    public RectTransform RunTabContent => _runGroup;
    public RectTransform AppearanceTabContent => _appearanceGroup;
    public SettingsTab ActiveTab => _activeTab;
    public bool IsVisible => _visible;

    public void Build(Font font)
    {
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = SETTINGS_SORT;                 // below secret/save-guard, above menu
        if (gameObject.GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        // full-screen input-blocking backdrop. The dim SCRIM is theme-independent focus chrome (not a themed
        // surface — D5 exempts it): a translucent veil over the workspace, raycast target to catch stray clicks.
        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var brt = (RectTransform)backdrop.transform;
        brt.SetParent(transform, false);
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);   // dim scrim (focus chrome, theme-independent)
        _scrim = backdrop;

        // centered panel (panel_background role — D5). Height is set after measuring the taller tab.
        var panel = new GameObject("SettingsPanel", typeof(RectTransform), typeof(Image));
        _panel = (RectTransform)panel.transform;
        _panel.SetParent(brt, false);
        _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f); _panel.pivot = new Vector2(0.5f, 0.5f);
        _panelImg = panel.GetComponent<Image>();
        _panelImg.color = ThemeService.Current.colors.panel_background;

        _titleText = MakeLabel(_panel, Margin, 10f, 360f, TitleH, "<b>Settings</b>", ThemeService.Current.colors.text);
        _titleText.supportRichText = true;

        // [x] close button (top-right) — name "btn_x" is pinned by SETTINGS-05/07. Retain the Image/Text
        // so ApplyTheme() repaints them on a LIVE Dark/Light switch (else the baked colors persist).
        MakeButton(_panel, PanelWidth - 36f, 10f, 26f, 26f, "x", () => CloseClicked?.Invoke(),
                   out _closeBtnImg, out _closeBtnText);

        // tab bar (S2): two tabs under the title.
        float tabY = TitleH + 12f;
        float tabW = (PanelWidth - 2f * Margin - CardGap) / 2f;
        _runTabBtn = MakeTabButton("tab_run", "実行", Margin, tabY, tabW, () => SelectTab(SettingsTab.Run));
        _appearanceTabBtn = MakeTabButton("tab_appearance", "外観", Margin + tabW + CardGap, tabY, tabW, () => SelectTab(SettingsTab.Appearance));

        // tab content groups. Cards stack top-down inside each; the host fills the section containers.
        float groupTop = tabY + TabBarH + GroupGapTop;
        _runGroup = MakeGroup("tab:run", groupTop);
        _appearanceGroup = MakeGroup("tab:appearance", groupTop);

        float runH = 0f;
        VenueSection    = MakeCard(_runGroup, "VENUE",    132f, ref runH);
        ModeSection     = MakeCard(_runGroup, "MODE",      30f, ref runH);
        ScenarioSection = MakeCard(_runGroup, "SCENARIO", 236f, ref runH);
        DataSection     = MakeCard(_runGroup, "DATA",      72f, ref runH);

        float appH = 0f;
        AppearanceSection = MakeCard(_appearanceGroup, "APPEARANCE", 30f, ref appH);

        // panel height = chrome + the taller tab's stacked cards (owner accepted 実行 being长 — D-B).
        float contentH = Mathf.Max(runH, appH);
        float panelH = groupTop + contentH + Margin;
        _panel.sizeDelta = new Vector2(PanelWidth, panelH);

        SelectTab(SettingsTab.Run);
        SetVisible(false);
    }

    // S2: show the chosen tab's cards, hide the other; reflect the active tab in the tab-button colors.
    public void SelectTab(SettingsTab tab)
    {
        _activeTab = tab;
        if (_runGroup != null) _runGroup.gameObject.SetActive(tab == SettingsTab.Run);
        if (_appearanceGroup != null) _appearanceGroup.gameObject.SetActive(tab == SettingsTab.Appearance);
        PaintTabs();
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_scrim != null) _scrim.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
    }

    // D5: repaint the chrome (panel + cards + headers + title + tabs) on a LIVE Dark/Light switch. The
    // section views' own bodies (venue buttons / mode segments / tile fields / data field / appearance
    // segments) are repainted by their owners' Refresh/ApplyTheme — the host calls those alongside this.
    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        if (_panelImg != null) _panelImg.color = c.panel_background;
        foreach (var img in _cardImgs) if (img != null) img.color = c.elevated_surface_background;
        foreach (var t in _headerTexts) if (t != null) t.color = c.text_muted;
        if (_titleText != null) _titleText.color = c.text;
        if (_closeBtnImg != null) _closeBtnImg.color = c.element_background;
        if (_closeBtnText != null) _closeBtnText.color = c.text;
        PaintTabs();
    }

    // ── builders ──

    void PaintTabs()
    {
        var c = ThemeService.Current.colors;
        Paint(_runTabBtn, _activeTab == SettingsTab.Run, c);
        Paint(_appearanceTabBtn, _activeTab == SettingsTab.Appearance, c);

        void Paint(Button b, bool active, ThemeColors col)
        {
            if (b == null) return;
            var img = b.GetComponent<Image>();
            if (img != null) img.color = active ? col.tab_active_background : col.tab_inactive_background;
            var lbl = b.GetComponentInChildren<Text>();
            if (lbl != null) lbl.color = active ? col.text : col.text_muted;
        }
    }

    // A tab content group: a top-anchored full-width container the cards stack into.
    RectTransform MakeGroup(string name, float yTop)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_panel, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(0f, -yTop);
        rt.sizeDelta = new Vector2(0f, 0f);
        return rt;
    }

    // A themed CARD面 (S3 / D5): an elevated surface holding a muted header and a content RectTransform the
    // host builds the section view into. Stacks top-down via the `y` cursor; returns the content container.
    RectTransform MakeCard(RectTransform group, string title, float contentH, ref float y)
    {
        float cardH = CardHeaderH + contentH + CardPadBottom;

        var cardGo = new GameObject("card:" + title, typeof(RectTransform), typeof(Image));
        var crt = (RectTransform)cardGo.transform;
        crt.SetParent(group, false);
        // horizontal-stretch (Margin inset) + vertical-fixed (top at -y, height cardH). Encode BOTH via
        // offsetMin/offsetMax — setting sizeDelta on a stretched axis would overwrite the Margin inset to 0.
        crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f);
        crt.offsetMin = new Vector2(Margin, -y - cardH);
        crt.offsetMax = new Vector2(-Margin, -y);
        var cardImg = cardGo.GetComponent<Image>();
        cardImg.color = ThemeService.Current.colors.elevated_surface_background;   // raised from the panel face
        _cardImgs.Add(cardImg);

        // muted/uppercase header (D5).
        var header = MakeLabel(crt, CardInsetX, 5f, PanelWidth - 2f * (Margin + CardInsetX), 14f, title,
                               ThemeService.Current.colors.text_muted);
        header.fontSize = 11;
        _headerTexts.Add(header);

        // content container below the header (full card width minus CardInsetX, height contentH). Same
        // mixed-axis idiom: offsets encode the horizontal inset AND the vertical top(-CardHeaderH)/height.
        var content = new GameObject("section:" + title, typeof(RectTransform));
        var rt = (RectTransform)content.transform;
        rt.SetParent(crt, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(CardInsetX, -CardHeaderH - contentH);
        rt.offsetMax = new Vector2(-CardInsetX, -CardHeaderH);

        y += cardH + CardGap;
        return rt;
    }

    Text MakeLabel(RectTransform parent, float x, float yTop, float w, float h, string text, Color color)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 13; t.color = color;
        t.alignment = TextAnchor.MiddleLeft; t.text = text; t.raycastTarget = false;
        return t;
    }

    Button MakeTabButton(string name, string text, float x, float yTop, float w, Action onClick)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_panel, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, TabBarH);
        go.GetComponent<Image>().color = ThemeService.Current.colors.tab_inactive_background;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = _font; lt.fontSize = 13; lt.color = ThemeService.Current.colors.text_muted;
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
        return go.GetComponent<Button>();
    }

    void MakeButton(RectTransform parent, float x, float yTop, float w, float h, string text, Action onClick,
                    out Image faceImg, out Text label)
    {
        var go = new GameObject("btn_" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        faceImg = go.GetComponent<Image>();
        faceImg.color = ThemeService.Current.colors.element_background;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        label = labelGo.GetComponent<Text>();
        label.font = _font; label.fontSize = 13; label.color = ThemeService.Current.colors.text;
        label.alignment = TextAnchor.MiddleCenter; label.text = text; label.raycastTarget = false;
    }
}
