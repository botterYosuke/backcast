// ReplayFooterView.cs — issue #30 "Replay transport footer" (uGUI view; durable parity surface)
//
// Builds + binds the screen-fixed footer transport bar (TTWR src/ui/footer.rs 1:1 surface parity,
// ADR-0005): a context-sensitive ▶/⏸ button, ⏭ step, ⏹ stop, and the [1,2,5,10,50] speed buttons,
// plus a status line. A PLAIN C# builder (not a MonoBehaviour): the host owns the GameObject
// lifecycle and forwards clicks; this class constructs widgets and reflects ReplayTransportViewModel
// state (glyph / enablement / speed highlight) in Refresh(). Colors read ThemeService.Current
// (issue #44 / findings 0020) — no inline color constants — and ApplyTheme() repaints on a switch.
//
// The view is intent-agnostic: it raises the four click callbacks and the host decides the actual
// transport call from the VM (vm.PlayPauseIntent() etc.) and marshals it over the GIL.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class ReplayFooterView
{
    readonly ReplayTransportViewModel _vm;
    readonly Action _onPlayPause;
    readonly Action _onStep;
    readonly Action _onStop;
    readonly Action<int> _onSpeed;
    readonly Font _font;

    Button _playBtn, _stepBtn, _stopBtn;
    Text _playLabel, _stepLabel, _stopLabel, _statusLabel;
    readonly List<(Button btn, Text label, int mult)> _speedBtns = new List<(Button, Text, int)>();

    // Retained themed graphics so ApplyTheme() can repaint on a theme switch.
    Image _barBg;
    readonly List<Image> _btnBgs = new List<Image>();
    readonly List<Text> _btnTexts = new List<Text>();

    const float DISABLED_ALPHA = 0.35f;
    const float ENABLED_ALPHA = 1.0f;

    public ReplayFooterView(
        ReplayTransportViewModel vm, Action onPlayPause, Action onStep, Action onStop,
        Action<int> onSpeed, Font font)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _onPlayPause = onPlayPause;
        _onStep = onStep;
        _onStop = onStop;
        _onSpeed = onSpeed;
        _font = font;
    }

    // Build the footer under `bar` (a bottom-anchored RectTransform owned by the host).
    public void Build(RectTransform bar)
    {
        var bg = bar.gameObject.GetComponent<Image>();
        if (bg == null) bg = bar.gameObject.AddComponent<Image>();
        _barBg = bg;
        bg.color = ThemeService.Current.colors.panel_background;

        float x = 0.01f;
        const float w = 0.06f, gap = 0.005f;

        _playBtn = MakeButton(bar, "▶", x, w, out _playLabel); x += w + gap;
        _stepBtn = MakeButton(bar, "⏭", x, w, out _stepLabel); x += w + gap;
        _stopBtn = MakeButton(bar, "⏹", x, w, out _stopLabel); x += w + gap * 3;

        _playBtn.onClick.AddListener(() => { _onPlayPause?.Invoke(); Refresh(); });
        _stepBtn.onClick.AddListener(() => { _onStep?.Invoke(); Refresh(); });
        _stopBtn.onClick.AddListener(() => { _onStop?.Invoke(); Refresh(); });

        foreach (int mult in ReplayTransportViewModel.SpeedOptions)
        {
            int m = mult; // capture
            Button b = MakeButton(bar, mult + "x", x, w, out Text lbl); x += w + gap;
            b.onClick.AddListener(() => { _onSpeed?.Invoke(m); Refresh(); });
            _speedBtns.Add((b, lbl, m));
        }

        // Status line fills the remaining width on the right.
        _statusLabel = MakeLabel(bar, "idle", x + 0.01f, 0.98f - (x + 0.01f));

        Refresh();
    }

    // Reflect VM state: ▶/⏸ glyph, button enablement (alpha), speed highlight, status phase.
    public void Refresh()
    {
        if (_playLabel != null) _playLabel.text = _vm.PlayGlyph;
        SetEnabled(_playBtn, _playLabel, _vm.CanPlayPause);
        SetEnabled(_stepBtn, _stepLabel, _vm.CanStep);
        SetEnabled(_stopBtn, _stopLabel, _vm.CanStop);

        var c = ThemeService.Current.colors;
        foreach (var (btn, label, mult) in _speedBtns)
        {
            SetEnabled(btn, label, _vm.CanSpeed);
            var img = btn.GetComponent<Image>();
            if (img != null)
            {
                bool sel = _vm.CurrentSpeed == mult;
                var col = sel ? c.element_selected : c.element_background;
                col.a = _vm.CanSpeed ? ENABLED_ALPHA : DISABLED_ALPHA;
                img.color = col;
            }
        }

        if (_statusLabel != null) _statusLabel.text = StatusText();
    }

    string StatusText()
    {
        switch (_vm.Phase)
        {
            case ReplayPhase.Failed:
                return "FAILED: " + (_vm.Lifecycle.FailureMessage ?? "");
            default:
                return _vm.Phase.ToString();
        }
    }

    public void ApplyTheme()
    {
        var t = ThemeService.Current;
        if (_barBg != null) _barBg.color = t.colors.panel_background;
        foreach (var img in _btnBgs) if (img != null) img.color = t.colors.element_background;
        foreach (var txt in _btnTexts) if (txt != null) txt.color = t.colors.text;
        Refresh(); // re-derive enablement alpha + speed highlight under the new theme
    }

    // ---- widget helpers (mirror ScenarioStartupTile) ----
    void SetEnabled(Button btn, Text label, bool on)
    {
        if (btn != null) btn.interactable = on;
        float a = on ? ENABLED_ALPHA : DISABLED_ALPHA;
        if (label != null) { var c = label.color; c.a = a; label.color = c; }
        var img = btn != null ? btn.GetComponent<Image>() : null;
        if (img != null) { var c = img.color; c.a = a; img.color = c; }
    }

    Button MakeButton(RectTransform parent, string text, float xMin, float width, out Text label)
    {
        var go = new GameObject("btn:" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        AnchorH(rt, xMin, width);
        var img = go.GetComponent<Image>();
        img.color = ThemeService.Current.colors.element_background;
        _btnBgs.Add(img);

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt);
        label = textGo.GetComponent<Text>();
        label.font = _font; label.color = ThemeService.Current.colors.text; label.text = text;
        label.fontSize = 13; label.alignment = TextAnchor.MiddleCenter;
        _btnTexts.Add(label);

        return go.GetComponent<Button>();
    }

    Text MakeLabel(RectTransform parent, string text, float xMin, float width)
    {
        var go = new GameObject("status", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        AnchorH(rt, xMin, width);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.text; t.text = text;
        t.fontSize = 12; t.alignment = TextAnchor.MiddleLeft;
        _btnTexts.Add(t);
        return t;
    }

    // Anchor a control across the full footer height, between normalized x [xMin, xMin+width].
    static void AnchorH(RectTransform rt, float xMin, float width)
    {
        rt.anchorMin = new Vector2(xMin, 0.12f);
        rt.anchorMax = new Vector2(xMin + width, 0.88f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }
}
