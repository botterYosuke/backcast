// SettingsModeSegmentView.cs — issue #127 (ADR-0026): the execution-mode segment, re-homed from the
// footer into the Settings modal's Mode section.
//
// The brain (FooterModeViewModel) and switch path (OnFooterMode: optimistic display + poll confirm /
// Live lock / LiveAuto leave stop-then-switch) are UNCHANGED — this is just the view layer rebuilt
// against the same VM (ADR-0026). Behaviour preserved from WorkspaceFooterView.RefreshModeSegments:
//   * Replay segment always shown; Manual/Auto only while the venue is live (VM.ShowManualAutoSegments).
//   * While a Live switch is locked, every visible segment dims (alpha 0.35) and is non-interactable so
//     a 2nd click can't race the engine answer (VM.Locked).
//   * Selection highlight follows VM.DisplayMode.
// The footer keeps the mode STATUS row only (WorkspaceFooterView after #127).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsModeSegmentView
{
    readonly FooterModeViewModel _modeVm;
    readonly Action<string> _onMode;   // segment click → "Replay"|"LiveManual"|"LiveAuto"
    readonly Font _font;

    readonly List<(Button btn, Text label, string mode)> _modeSegs = new List<(Button, Text, string)>();

    const float DISABLED_ALPHA = 0.35f;
    const float ENABLED_ALPHA = 1.0f;

    public SettingsModeSegmentView(FooterModeViewModel modeVm, Action<string> onMode, Font font)
    {
        _modeVm = modeVm ?? throw new ArgumentNullException(nameof(modeVm));
        _onMode = onMode;
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // Build the 3 segments (Replay / Manual / Auto, TTWR order) across `container`.
    public void Build(RectTransform container)
    {
        const float gap = 0.01f, segW = 0.22f;
        float x = 0.0f;
        x = AddSeg(container, "Replay", FooterModeViewModel.Replay, x, segW) + gap;
        x = AddSeg(container, "Manual", FooterModeViewModel.LiveManual, x, segW) + gap;
        AddSeg(container, "Auto", FooterModeViewModel.LiveAuto, x, segW);
        Refresh();
    }

    float AddSeg(RectTransform parent, string label, string modeId, float xMin, float width)
    {
        var go = new GameObject("seg:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(xMin, 0.1f);
        rt.anchorMax = new Vector2(xMin + width, 0.9f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => { _onMode?.Invoke(modeId); Refresh(); });

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = (RectTransform)textGo.transform;
        trt.SetParent(rt, false);
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        var lbl = textGo.GetComponent<Text>();
        lbl.font = _font; lbl.color = ThemeService.Current.colors.text; lbl.text = label;
        lbl.fontSize = 13; lbl.alignment = TextAnchor.MiddleCenter; lbl.raycastTarget = false;

        _modeSegs.Add((btn, lbl, modeId));
        return xMin + width;
    }

    // Reflect VM state: selection highlight + venue-gated visibility + lock-dim. Identical predicate to
    // the retired WorkspaceFooterView.RefreshModeSegments so behaviour is preserved across the re-home.
    public void Refresh()
    {
        if (_modeSegs.Count == 0) return;
        var c = ThemeService.Current.colors;
        string mode = _modeVm.DisplayMode;
        bool locked = _modeVm.Locked;
        foreach (var (btn, label, segMode) in _modeSegs)
        {
            bool visible = segMode == FooterModeViewModel.Replay || _modeVm.ShowManualAutoSegments;
            if (btn != null && btn.gameObject.activeSelf != visible) btn.gameObject.SetActive(visible);
            if (!visible) continue;
            bool selected = segMode == mode;
            var img = btn.GetComponent<Image>();
            if (img != null)
            {
                var col = selected ? c.element_selected : c.element_background;
                col.a = locked ? DISABLED_ALPHA : ENABLED_ALPHA;
                img.color = col;
            }
            if (label != null) { var lc = label.color; lc.a = locked ? DISABLED_ALPHA : ENABLED_ALPHA; label.color = lc; }
            if (btn != null) btn.interactable = !locked;
        }
    }
}
