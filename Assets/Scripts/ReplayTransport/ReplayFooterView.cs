// ReplayFooterView.cs — issue #30 "Replay transport footer" + #39 Slice 3 "mode segment + LiveAuto ▶"
// (uGUI view; durable parity surface).
//
// Builds + binds the screen-fixed footer (TTWR src/ui/footer.rs 1:1 surface parity, ADR-0005):
// the Replay/LiveManual/LiveAuto mode segments (left), one context-sensitive ▶/⏸ button shared
// between Replay and LiveAuto (TTWR's single PauseResume entity), ⏭ step, ⏹ stop, the
// [1,2,5,10,50] speed buttons, and a status line. A PLAIN C# builder (not a MonoBehaviour): the
// host owns the GameObject lifecycle and forwards clicks; this class constructs widgets and
// reflects VM state (glyph / enablement / visibility / speed highlight / mode selection) in
// Refresh(). Colors read ThemeService.Current (#44 / findings 0020) — no inline color constants —
// and ApplyTheme() repaints on a switch.
//
// The view is intent-agnostic: it raises the click callbacks and the host routes them to the right
// VM (Replay ▶ → ReplayTransportViewModel, LiveAuto ▶ → LiveAutoTransportViewModel, segment →
// FooterModeViewModel.RequestMode) and marshals the resulting RPC over the GIL.
//
// #39 mode-awareness is OPT-IN: when modeVm is null this view renders EXACTLY the #30 Replay-only
// footer (no segments, ▶/step/stop/speed always shown) so the existing ScenarioStartupHitlHarness
// is unaffected. The production footer (ProductionLiveShell) passes all three VMs and gets the full
// surface with mode-driven visibility (findings 0025 §4).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class ReplayFooterView
{
    readonly ReplayTransportViewModel _vm;
    readonly FooterModeViewModel _modeVm;          // #39 (null → #30 Replay-only legacy render)
    readonly LiveAutoTransportViewModel _autoVm;   // #39 (null → no LiveAuto ▶ routing)
    readonly Action _onPlayPause;
    readonly Action _onStep;
    readonly Action _onStop;
    readonly Action<int> _onSpeed;
    readonly Action<string> _onMode;               // #39 segment click → "Replay"|"LiveManual"|"LiveAuto"
    readonly Font _font;

    Button _playBtn, _stepBtn, _stopBtn;
    Text _playLabel, _stepLabel, _stopLabel, _statusLabel;
    readonly List<(Button btn, Text label, int mult)> _speedBtns = new List<(Button, Text, int)>();
    // #39 mode segments (in TTWR order Replay / Manual / Auto).
    readonly List<(Button btn, Text label, string mode)> _modeSegs = new List<(Button, Text, string)>();

    // Retained themed graphics so ApplyTheme() can repaint on a theme switch.
    Image _barBg;
    readonly List<Image> _btnBgs = new List<Image>();
    readonly List<Text> _btnTexts = new List<Text>();

    const float DISABLED_ALPHA = 0.35f;
    const float ENABLED_ALPHA = 1.0f;

    public ReplayFooterView(
        ReplayTransportViewModel vm, Action onPlayPause, Action onStep, Action onStop,
        Action<int> onSpeed, Font font,
        FooterModeViewModel modeVm = null, LiveAutoTransportViewModel autoVm = null,
        Action<string> onMode = null)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _onPlayPause = onPlayPause;
        _onStep = onStep;
        _onStop = onStop;
        _onSpeed = onSpeed;
        _font = font;
        _modeVm = modeVm;
        _autoVm = autoVm;
        _onMode = onMode;
    }

    // Build the footer under `bar` (a bottom-anchored RectTransform owned by the host).
    public void Build(RectTransform bar)
    {
        var bg = bar.gameObject.GetComponent<Image>();
        if (bg == null) bg = bar.gameObject.AddComponent<Image>();
        _barBg = bg;
        bg.color = ThemeService.Current.colors.panel_background;

        const float w = 0.06f, gap = 0.005f;
        float x = 0.01f;

        // ── #39 mode segments (only when mode-aware). TTWR order: Replay / Manual / Auto. ──
        if (_modeVm != null)
        {
            const float segW = 0.065f;
            x = AddModeSeg(bar, "Replay", FooterModeViewModel.Replay, x, segW) + gap;
            x = AddModeSeg(bar, "Manual", FooterModeViewModel.LiveManual, x, segW) + gap;
            x = AddModeSeg(bar, "Auto", FooterModeViewModel.LiveAuto, x, segW) + gap * 3;
        }

        // ── transport: ▶/⏸ (shared Replay/LiveAuto), ⏭ step, ⏹ stop ──
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

    float AddModeSeg(RectTransform bar, string label, string modeId, float xMin, float width)
    {
        Button b = MakeButton(bar, label, xMin, width, out Text lbl);
        b.onClick.AddListener(() => { _onMode?.Invoke(modeId); Refresh(); });
        _modeSegs.Add((b, lbl, modeId));
        return xMin + width;
    }

    // Reflect VM state: mode segment selection/visibility, the mode-routed ▶/⏸ glyph + enablement,
    // mode-driven transport visibility, speed highlight, status.
    public void Refresh()
    {
        string mode = _modeVm != null ? _modeVm.DisplayMode : FooterModeViewModel.Replay;
        bool replay = mode == FooterModeViewModel.Replay;
        bool liveAuto = mode == FooterModeViewModel.LiveAuto;

        RefreshModeSegments(mode);

        // ── ▶/⏸ — shared button; glyph + enablement come from the active mode's VM (TTWR's
        // footer_pause_resume_system branch). Visible in Replay and LiveAuto; hidden in LiveManual
        // (manual order entry is a separate ticket, not the footer). ──
        bool playVisible = replay || (liveAuto && _autoVm != null);
        SetActive(_playBtn, playVisible);
        if (playVisible)
        {
            if (liveAuto && _autoVm != null)
            {
                if (_playLabel != null) _playLabel.text = _autoVm.PlayGlyph;
                SetEnabled(_playBtn, _playLabel, _autoVm.CanPlayPause);
            }
            else
            {
                if (_playLabel != null) _playLabel.text = _vm.PlayGlyph;
                SetEnabled(_playBtn, _playLabel, _vm.CanPlayPause);
            }
        }

        // ── step / stop / speed — Replay only (TTWR apply_execution_mode_visibility_system). ──
        SetActive(_stepBtn, replay);
        SetActive(_stopBtn, replay);
        if (replay)
        {
            SetEnabled(_stepBtn, _stepLabel, _vm.CanStep);
            SetEnabled(_stopBtn, _stopLabel, _vm.CanStop);
        }

        var c = ThemeService.Current.colors;
        foreach (var (btn, label, mult) in _speedBtns)
        {
            SetActive(btn, replay);
            if (!replay) continue;
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

        if (_statusLabel != null) _statusLabel.text = StatusText(mode);
    }

    // Segment selection highlight + venue-gated visibility; segments disable while a Live switch is
    // locked (spinner) so a 2nd click can't race the engine answer.
    void RefreshModeSegments(string mode)
    {
        if (_modeVm == null || _modeSegs.Count == 0) return;
        var c = ThemeService.Current.colors;
        bool locked = _modeVm.Locked;
        foreach (var (btn, label, segMode) in _modeSegs)
        {
            // Replay segment always shown; Manual/Auto only while the venue is live.
            bool visible = segMode == FooterModeViewModel.Replay || _modeVm.ShowManualAutoSegments;
            SetActive(btn, visible);
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

    string StatusText(string mode)
    {
        if (_modeVm != null && _modeVm.Locked) return mode + " (switching…)";
        if (_modeVm != null && (mode == FooterModeViewModel.LiveAuto || mode == FooterModeViewModel.LiveManual))
        {
            // In a live mode the LiveAuto run lifecycle is the meaningful status (when wired).
            if (mode == FooterModeViewModel.LiveAuto && _autoVm != null && _autoVm.HasActiveRun)
                return "LiveAuto: " + _autoVm.ActiveRunId;
            return mode;
        }
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
        Refresh(); // re-derive enablement alpha + speed/segment highlight under the new theme
    }

    // ---- widget helpers (mirror ScenarioStartupTile) ----
    static void SetActive(Button btn, bool on)
    {
        if (btn != null && btn.gameObject.activeSelf != on) btn.gameObject.SetActive(on);
    }

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
