// StrategyEditorRunButton.cs — issue #76 S6b-β-clean U1 (uGUI view; durable surface)
//
// The single Run entry: a small ▶ Run button on the ADOPTED Strategy Editor floating-window title bar
// (findings 0046, "S6b-β-clean 設計の木" U1). Marimo frontend's RunButton principle — Run targets WHAT
// THE EDITOR SHOWS (always WINDOW_ID's .py, RegistryStrategyFileProvider) and the block reason sits
// next to it. A PLAIN C# builder (not a MonoBehaviour): the host owns the GameObject lifecycle, forwards
// the click to OnRun(), and feeds readiness each frame via Refresh(vm). Colors read ThemeService.Current
// (#44) — no inline color constants.
//
// Only the adopted editor gets this button (the run target is always WINDOW_ID); secondary spawned
// editors get no Run (per-window run targets = a later slice). The reason label shows the disabled
// cause (not-owner / running… / scenario invalid / unsaved) so a greyed Run is never mute.

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class StrategyEditorRunButton
{
    const float BtnWidth = 56f;      // right-aligned Run button width (px)
    const float Pad = 4f;
    const float DisabledAlpha = 0.35f;
    const float EnabledAlpha = 1.0f;

    readonly Action _onRun;
    Button _btn;
    Text _label, _status;
    Image _btnBg;

    public StrategyEditorRunButton(Action onRun) { _onRun = onRun; }

    // Build the Run button + reason label under the title bar (a full-width RectTransform owned by the
    // host — the adopted editor's FloatingWindowTitleInput GameObject). The reason label is NOT a raycast
    // target so title-bar drag still falls through everywhere except the button itself.
    public void Build(RectTransform titleBar, Font font)
    {
        var c = ThemeService.Current.colors;

        var btnGo = new GameObject("RunButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var brt = (RectTransform)btnGo.transform;
        brt.SetParent(titleBar, false);
        brt.anchorMin = new Vector2(1f, 0f); brt.anchorMax = new Vector2(1f, 1f); brt.pivot = new Vector2(1f, 0.5f);
        brt.sizeDelta = new Vector2(BtnWidth, -6f);
        brt.anchoredPosition = new Vector2(-Pad, 0f);
        _btnBg = btnGo.GetComponent<Image>();
        _btnBg.color = c.element_background;

        var labelGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(brt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        _label = labelGo.GetComponent<Text>();
        _label.font = font; _label.color = c.text; _label.text = "▶ Run"; _label.fontSize = 12;
        _label.alignment = TextAnchor.MiddleCenter; _label.raycastTarget = false;

        _btn = btnGo.GetComponent<Button>();
        _btn.onClick.AddListener(() => _onRun?.Invoke());

        // Reason label fills the left of the bar (right-inset clears the button). raycast off → drag
        // falls through. Wrap + Truncate keeps a long reason INSIDE its rect (uGUI Overflow would spill
        // left over the title field and off the window edge); the cramped one-line rect truncates it.
        var stGo = new GameObject("RunStatus", typeof(RectTransform), typeof(Text));
        var srt = (RectTransform)stGo.transform;
        srt.SetParent(titleBar, false);
        srt.anchorMin = new Vector2(0.42f, 0f); srt.anchorMax = new Vector2(1f, 1f);
        srt.offsetMin = new Vector2(4f, 0f); srt.offsetMax = new Vector2(-(BtnWidth + Pad * 2f), 0f);
        _status = stGo.GetComponent<Text>();
        _status.font = font; _status.color = ThemeService.Current.status.error; _status.fontSize = 10;
        _status.alignment = TextAnchor.MiddleRight;
        _status.horizontalOverflow = HorizontalWrapMode.Wrap;
        _status.verticalOverflow = VerticalWrapMode.Truncate;
        _status.raycastTarget = false; _status.enabled = false;
    }

    // Reflect readiness: enable/grey the button, show/clear the block reason. Cheap (no allocation when
    // unchanged via the Text early-out); the host may call it every frame.
    public void Refresh(RunReadinessViewModel vm)
    {
        if (_btn == null) return;
        bool can = vm != null && vm.CanRun;
        _btn.interactable = can;
        float a = can ? EnabledAlpha : DisabledAlpha;
        if (_label != null) { var lc = _label.color; lc.a = a; _label.color = lc; }
        if (_btnBg != null) { var bc = _btnBg.color; bc.a = a; _btnBg.color = bc; }
        if (_status != null)
        {
            string reason = vm != null ? vm.BlockReason : null;
            _status.text = reason ?? "";
            _status.enabled = !string.IsNullOrEmpty(reason);
        }
    }
}
