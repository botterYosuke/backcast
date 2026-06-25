// SettingsAppearanceSegmentView.cs — ADR-0028 (Settings → Appearance segment)
//
// The Dark / Light theme switch, homed in the Settings modal's Appearance section (the ADR-0026 集約口).
// Mirrors SettingsModeSegmentView: two segments across the section, selection highlight follows the active
// Appearance, a click invokes onSelect (the host applies the theme LIVE + persists). View layer only — the
// host owns ThemeService.SetTheme + AppearanceStore.Save so this stays a thin, headless-testable widget.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsAppearanceSegmentView
{
    readonly Action<Appearance> _onSelect;
    readonly Font _font;

    readonly List<(Button btn, Text label, Appearance mode)> _segs = new List<(Button, Text, Appearance)>();

    public SettingsAppearanceSegmentView(Action<Appearance> onSelect, Font font)
    {
        _onSelect = onSelect;
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // Build the 2 segments (Dark / Light) across `container`.
    public void Build(RectTransform container)
    {
        const float gap = 0.02f, segW = 0.30f;
        float x = 0.0f;
        x = AddSeg(container, "Dark",  Appearance.Dark,  x, segW) + gap;
        AddSeg(container, "Light", Appearance.Light, x, segW);
        Refresh();
    }

    float AddSeg(RectTransform parent, string label, Appearance mode, float xMin, float width)
    {
        var go = new GameObject("seg:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(xMin, 0.1f);
        rt.anchorMax = new Vector2(xMin + width, 0.9f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => { _onSelect?.Invoke(mode); Refresh(); });

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = (RectTransform)textGo.transform;
        trt.SetParent(rt, false);
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        var lbl = textGo.GetComponent<Text>();
        lbl.font = _font; lbl.color = ThemeService.Current.colors.text; lbl.text = label;
        lbl.fontSize = 13; lbl.alignment = TextAnchor.MiddleCenter; lbl.raycastTarget = false;

        _segs.Add((btn, lbl, mode));
        return xMin + width;
    }

    // Highlight the segment matching the active appearance (re-read from ThemeService each call so a live
    // switch from elsewhere stays in sync).
    public void Refresh()
    {
        if (_segs.Count == 0) return;
        var c = ThemeService.Current.colors;
        var active = ThemeService.Current.appearance;
        foreach (var (btn, label, mode) in _segs)
        {
            var img = btn != null ? btn.GetComponent<Image>() : null;
            if (img != null) img.color = mode == active ? c.element_selected : c.element_background;
            if (label != null) label.color = c.text;
        }
    }
}
