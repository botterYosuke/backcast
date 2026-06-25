// SettingsModalOverlay.cs — issue #125 (ADR-0026): the screen-fixed Settings modal chrome.
//
// CHROME, mirroring SecretModalOverlay/SaveGuardOverlay: its OWN ScreenSpaceOverlay canvas, OUTSIDE the
// infinite-canvas Content (never pans/zooms), with a full-screen input-blocking backdrop and a centered
// panel. The panel hosts three labeled SECTION containers — Venue / Mode / Scenario — that the host
// (BackcastWorkspaceRoot) builds the section views into (the brains VenueMenuViewModel /
// FooterModeViewModel / ScenarioStartupController are reused unchanged — ADR-0026).
//
// Z-ORDER (ADR-0026 §28 / findings 0102 D2): SETTINGS_SORT sits BELOW the secret/save-guard overlays
// (sortingOrder 1000) and ABOVE the menu bar (MenuBarView.MENU_SORT 600). So a second-password prompt
// raised from the Venue section (secret, 1000) draws ON TOP of Settings, and after submit Settings stays
// open behind it. Only the RELATION matters; 900 just realises "menu < settings < secret".
//
// CLOSE: the [x] button raises CloseClicked; ESC toggle is owned by SettingsModalController (the host
// polls the keyboard and calls Open/Close). SetVisible toggles the canvas + backdrop.

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsModalOverlay : MonoBehaviour
{
    // chrome z-order contract (findings 0045/0102): menu(600) < settings(900) < secret/save-guard(1000).
    public const int SETTINGS_SORT = 900;

    public event Action CloseClicked;

    Canvas _canvas;
    GameObject _panelRoot;
    bool _visible;

    // Section content containers (host builds the reused section views into these).
    public RectTransform VenueSection { get; private set; }
    public RectTransform ModeSection { get; private set; }
    public RectTransform ScenarioSection { get; private set; }

    public bool IsVisible => _visible;

    public void Build(Font font)
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = SETTINGS_SORT;                 // below secret/save-guard, above menu
        if (gameObject.GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        // full-screen input-blocking backdrop (raycast target catches clicks meant for the workspace).
        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var brt = (RectTransform)backdrop.transform;
        brt.SetParent(transform, false);
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        _panelRoot = backdrop;

        // centered panel.
        var panel = new GameObject("SettingsPanel", typeof(RectTransform), typeof(Image));
        var prt = (RectTransform)panel.transform;
        prt.SetParent(brt, false);
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(580f, 600f);
        panel.GetComponent<Image>().color = new Color(0.0667f, 0.0863f, 0.1686f, 1f); // #11162b Neutral.Step3 (parity with secret panel)

        MakeLabel(prt, font, 16f, 12f, 360f, 24f, "<b>Settings</b>").supportRichText = true;

        // [x] close button (top-right).
        MakeButton(prt, font, 580f - 36f, 10f, 26f, 26f, "x", () => CloseClicked?.Invoke());

        // Three labeled sections, stacked. Each header is a label; the content container is exposed for
        // the host to build the reused section view into (Scenario / Mode / Venue populate them). The Venue
        // section is sized for the unpinned/editor max (MOCK + 4 variants + Disconnect = 6 rows × 20px).
        VenueSection    = MakeSection(prt, font, "Venue",    52f,  132f);
        ModeSection     = MakeSection(prt, font, "Mode",     212f,  44f);
        ScenarioSection = MakeSection(prt, font, "Scenario", 284f, 288f);

        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_panelRoot != null) _panelRoot.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
    }

    // ── builders ──

    // A labeled section: a header label at yTop, then a content RectTransform below it. Returns the
    // content container (full-width inside the panel margins) for the host to build a section view into.
    RectTransform MakeSection(RectTransform panel, Font font, string title, float yTop, float height)
    {
        MakeLabel(panel, font, 16f, yTop, 540f, 18f, "<b>" + title + "</b>").supportRichText = true;

        var go = new GameObject("section:" + title, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(panel, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(16f, -(yTop + 20f));
        rt.sizeDelta = new Vector2(548f, height);
        return rt;
    }

    static Text MakeLabel(RectTransform parent, Font font, float x, float yTop, float w, float h, string text)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = 13; t.color = new Color(0.8784f, 0.9059f, 0.9608f, 1f); // #e0e7f5 starlight
        t.alignment = TextAnchor.MiddleLeft; t.text = text; t.raycastTarget = false;
        return t;
    }

    static void MakeButton(RectTransform parent, Font font, float x, float yTop, float w, float h, string text, Action onClick)
    {
        var go = new GameObject("btn_" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        go.GetComponent<Image>().color = new Color(0.0941f, 0.1216f, 0.2275f, 1f); // #181f3a Neutral.Step4
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = font; lt.fontSize = 13; lt.color = new Color(0.8784f, 0.9059f, 0.9608f, 1f);
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
    }
}
