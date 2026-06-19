// QuitConfirmOverlay.cs — issue #89「終了時の確認ダイアログ」(durable tier, Unity boundary)
//
// The screen-fixed, input-blocking uGUI overlay for the quit-confirm modal (findings 0068). It is
// CHROME: its own ScreenSpaceOverlay canvas at a high sort order, OUTSIDE the infinite-canvas Content,
// so it never pans/zooms and always draws topmost. A full-screen backdrop raycast target blocks input
// to the workspace beneath while the modal is open. (Mirrors SecretModalOverlay's chrome shape.)
//
// Unlike SecretModalOverlay this view drains NO keystrokes — it has no text entry. It exposes only the
// three button events Save / Don't Save / Cancel. The owner (BackcastWorkspaceRoot) routes those into
// QuitConfirmController.ChooseSave / ChooseDiscard / ChooseCancel and decides quit continuation; this
// view holds no logic and no buffer.

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class QuitConfirmOverlay : MonoBehaviour
{
    public event Action SaveClicked;
    public event Action DiscardClicked;
    public event Action CancelClicked;

    Canvas _canvas;
    GameObject _panelRoot;
    bool _visible;

    public bool IsVisible => _visible;

    public void Build(Font font)
    {
        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1000;                     // topmost chrome
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
        var panel = new GameObject("QuitConfirmPanel", typeof(RectTransform), typeof(Image));
        var prt = (RectTransform)panel.transform;
        prt.SetParent(brt, false);
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(400f, 140f);
        panel.GetComponent<Image>().color = new Color(0.14f, 0.15f, 0.18f, 1f);

        MakeLabel(prt, font, 12f, 12f, 376f, 22f, "<b>Unsaved changes</b>").supportRichText = true;
        MakeLabel(prt, font, 12f, 40f, 376f, 36f, "Save the open document before quitting?");

        MakeButton(prt, font, 12f, 96f, 120f, 30f, "Save", () => SaveClicked?.Invoke());
        MakeButton(prt, font, 140f, 96f, 120f, 30f, "Don't Save", () => DiscardClicked?.Invoke());
        MakeButton(prt, font, 268f, 96f, 120f, 30f, "Cancel", () => CancelClicked?.Invoke());

        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_panelRoot != null) _panelRoot.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
    }

    // ── tiny uGUI builders (top-left-anchored absolute placement) ──
    static Text MakeLabel(RectTransform parent, Font font, float x, float yTop, float w, float h, string text)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = 12; t.color = new Color(0.92f, 0.93f, 0.95f, 1f);
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
        go.GetComponent<Image>().color = new Color(0.22f, 0.25f, 0.31f, 1f);
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = font; lt.fontSize = 12; lt.color = new Color(0.92f, 0.93f, 0.95f, 1f);
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
    }
}
