// SecretModalOverlay.cs — issue #23 re-home slice (DURABLE tier, Unity boundary)
//
// The screen-fixed, input-blocking uGUI overlay for the second-password (secret) modal (findings
// 0014 RH4). It is CHROME: its own ScreenSpaceOverlay canvas at a high sort order, OUTSIDE the
// infinite-canvas Content, so it never pans/zooms and always draws topmost. A full-screen backdrop
// raycast target blocks input to the workspace beneath while the modal is open.
//
// SECRET DISCIPLINE (mirrors the retired IMGUI path, but uGUI): the plaintext NEVER becomes a
// managed string. Keystrokes arrive ONE char at a time via the New Input System
// Keyboard.current.onTextInput (Action<char>) — NOT Input.inputString (an immutable secret string)
// and NOT a uGUI InputField. Printable chars are forwarded as CharTyped; Backspace is the keyboard
// key poll (control chars from onTextInput are filtered out so backspace isn't double-handled). The
// owner (BackcastWorkspaceRoot) routes CharTyped/Backspace into SecretModalController.AppendChar/
// Backspace and reads MaskedDisplay back — this view holds no buffer.
//
// SUBSCRIPTION SAFETY (owner-requested): onTextInput is subscribed only while visible and is
// unsubscribed on Hide, OnDisable AND OnDestroy — a missed unsubscribe would keep draining keys into
// a closed modal. _subscribed guards against double subscribe/unsubscribe.

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public sealed class SecretModalOverlay : MonoBehaviour
{
    public event Action<char> CharTyped;
    public event Action BackspacePressed;
    public event Action SubmitClicked;
    public event Action CancelClicked;

    Canvas _canvas;
    GameObject _panelRoot;
    Text _masked;
    Keyboard _subscribedKb;   // the EXACT device we hooked, so a device re-enumeration can't leak the handler
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
        var panel = new GameObject("SecretPanel", typeof(RectTransform), typeof(Image));
        var prt = (RectTransform)panel.transform;
        prt.SetParent(brt, false);
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(380f, 150f);
        panel.GetComponent<Image>().color = new Color(0.0745f, 0.0941f, 0.2510f, 1f); // #131840 Neutral.Step3 (cyberpunk re-skin)

        MakeLabel(prt, font, 12f, 10f, 356f, 22f, "<b>Second password</b> (typed; masked, never stored as text)").supportRichText = true;
        _masked = MakeLabel(prt, font, 12f, 40f, 356f, 24f, "secret: ");

        MakeButton(prt, font, 12f, 100f, 100f, 30f, "Submit", () => SubmitClicked?.Invoke());
        MakeButton(prt, font, 120f, 100f, 100f, 30f, "Cancel", () => CancelClicked?.Invoke());

        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_panelRoot != null) _panelRoot.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
        if (visible) Subscribe(); else Unsubscribe();
    }

    public void SetMasked(string maskedDots)
    {
        if (_masked != null) _masked.text = "secret: " + (maskedDots ?? "");
    }

    void Update()
    {
        if (!_visible) return;
        var kb = Keyboard.current;
        if (kb != null && kb.backspaceKey.wasPressedThisFrame) BackspacePressed?.Invoke();
    }

    void Subscribe()
    {
        if (_subscribedKb != null) return;
        var kb = Keyboard.current;
        if (kb == null) return;            // no keyboard device (headless): nothing to drain
        kb.onTextInput += OnTextInput;
        _subscribedKb = kb;
    }

    void Unsubscribe()
    {
        if (_subscribedKb == null) return;
        // Detach from the SAME device instance we hooked — Keyboard.current may have changed (a keyboard
        // re-enumerated/reconnected while the modal was open), and detaching from the new one would leak.
        _subscribedKb.onTextInput -= OnTextInput;
        _subscribedKb = null;
    }

    // Forward only PRINTABLE chars; control chars (backspace '\b', enter '\r'/'\n', etc.) are handled
    // by the key poll / ignored, so backspace is never double-counted as an appended char.
    void OnTextInput(char c)
    {
        if (c < ' ' || c == (char)0x7F) return;
        CharTyped?.Invoke(c);
    }

    void OnDisable() => Unsubscribe();
    void OnDestroy() => Unsubscribe();

    // ── tiny uGUI builders (top-left-anchored absolute placement) ──
    static Text MakeLabel(RectTransform parent, Font font, float x, float yTop, float w, float h, string text)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = 12; t.color = new Color(0.8784f, 0.9176f, 1.0000f, 1f); // #e0eaff Neutral.Step12
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
        go.GetComponent<Image>().color = new Color(0.1020f, 0.1255f, 0.3137f, 1f); // #1a2050 Neutral.Step4
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = font; lt.fontSize = 12; lt.color = new Color(0.8784f, 0.9176f, 1.0000f, 1f); // #e0eaff Neutral.Step12
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
    }
}
