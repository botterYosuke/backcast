// WorkspaceFooterView.cs — issue #76 S6b-β-clean U4 (uGUI view; durable surface)
//
// The screen-fixed workspace footer AFTER the transport→reactive UI cutover (findings 0046,
// "S6b-β-clean 設計の木" U4 / ADR-0012). Renamed from ReplayFooterView and STRIPPED of the replay
// transport controls (▶/⏸ play-pause, ⏭ step, ⏹ stop, the [1,2,5,10,50] speed buttons): reactive
// drain runs to completion in ~0.3s/50k, so scrub/pause/step/speed affordances are obsolete — Run now
// lives on the Strategy Editor title bar (U1). What REMAINS is the mode segments (Replay / Manual /
// Auto) — these are for the Live execution mode, NOT transport (Live wiring is a separate epic) — plus
// a status line. A PLAIN C# builder (not a MonoBehaviour): the host owns the GameObject lifecycle and
// forwards the segment click. Colors read ThemeService.Current (#44) — no inline color constants.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class WorkspaceFooterView
{
    // #84 (findings 0053) — the footer is screen-fixed chrome and MUST sit above the sidebar's own
    // override-sorting Canvas (UniverseSidebarView.SIDEBAR_SORT=500) so the status bar can never be
    // overpainted by an overflowing sidebar (the original #84 symptom). MenuBarView.MENU_SORT=600
    // (+ its 599 backdrop) stays in front so dropdowns continue to cover the footer band, matching
    // desktop semantics. Values are DERIVED — Section13_ChromeZOrderLayering asserts the RELATIONS
    // only (findings 0045 D2).
    public const int FOOTER_SORT = 550;

    readonly FooterModeViewModel _modeVm;
    readonly LiveAutoTransportViewModel _autoVm;    // optional: drives the live-mode status line
    readonly Action<string> _onMode;                // segment click → "Replay"|"LiveManual"|"LiveAuto"
    readonly Font _font;

    Text _statusLabel;
    // mode segments (in TTWR order Replay / Manual / Auto).
    readonly List<(Button btn, Text label, string mode)> _modeSegs = new List<(Button, Text, string)>();

    // Retained themed graphics so ApplyTheme() can repaint on a theme switch.
    Image _barBg;
    readonly List<Image> _btnBgs = new List<Image>();
    readonly List<Text> _btnTexts = new List<Text>();

    const float DISABLED_ALPHA = 0.35f;
    const float ENABLED_ALPHA = 1.0f;

    public WorkspaceFooterView(FooterModeViewModel modeVm, LiveAutoTransportViewModel autoVm,
                               Action<string> onMode, Font font)
    {
        _modeVm = modeVm ?? throw new ArgumentNullException(nameof(modeVm));
        _autoVm = autoVm;
        _onMode = onMode;
        _font = font;
    }

    // Build the footer under `bar` (a bottom-anchored RectTransform owned by the host).
    public void Build(RectTransform bar)
    {
        // #84: promote the footer GO to its OWN override-sorting Canvas so it draws above the sidebar
        // (SIDEBAR_SORT=500) and below the menu (MENU_SORT=600). The GraphicRaycaster routes mode-segment
        // clicks through this Canvas's sortingOrder, matching the chrome contract (findings 0045 / 0053).
        ChromeCanvas.Promote(bar.gameObject, FOOTER_SORT);

        var bg = bar.gameObject.GetComponent<Image>();
        if (bg == null) bg = bar.gameObject.AddComponent<Image>();
        _barBg = bg;
        bg.color = ThemeService.Current.colors.panel_background;

        const float gap = 0.005f, segW = 0.065f;
        float x = 0.01f;

        // ── mode segments. TTWR order: Replay / Manual / Auto. ──
        x = AddModeSeg(bar, "Replay", FooterModeViewModel.Replay, x, segW) + gap;
        x = AddModeSeg(bar, "Manual", FooterModeViewModel.LiveManual, x, segW) + gap;
        x = AddModeSeg(bar, "Auto", FooterModeViewModel.LiveAuto, x, segW) + gap * 3;

        // Status line fills the remaining width on the right.
        _statusLabel = MakeLabel(bar, "Replay", x + 0.01f, 0.98f - (x + 0.01f));

        Refresh();
    }

    float AddModeSeg(RectTransform bar, string label, string modeId, float xMin, float width)
    {
        Button b = MakeButton(bar, label, xMin, width, out Text lbl);
        b.onClick.AddListener(() => { _onMode?.Invoke(modeId); Refresh(); });
        _modeSegs.Add((b, lbl, modeId));
        return xMin + width;
    }

    // Reflect VM state: mode segment selection/visibility + the status line.
    public void Refresh()
    {
        string mode = _modeVm.DisplayMode;
        RefreshModeSegments(mode);
        if (_statusLabel != null) _statusLabel.text = StatusText(mode);
    }

    // Segment selection highlight + venue-gated visibility; segments disable while a Live switch is
    // locked (spinner) so a 2nd click can't race the engine answer.
    void RefreshModeSegments(string mode)
    {
        if (_modeSegs.Count == 0) return;
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
        if (_modeVm.Locked) return mode + " (switching…)";
        if (mode == FooterModeViewModel.LiveAuto && _autoVm != null && _autoVm.HasActiveRun)
            return "LiveAuto: " + _autoVm.ActiveRunId;
        return mode;
    }

    public void ApplyTheme()
    {
        var t = ThemeService.Current;
        if (_barBg != null) _barBg.color = t.colors.panel_background;
        foreach (var img in _btnBgs) if (img != null) img.color = t.colors.element_background;
        foreach (var txt in _btnTexts) if (txt != null) txt.color = t.colors.text;
        Refresh(); // re-derive segment highlight under the new theme
    }

    // ---- widget helpers ----
    static void SetActive(Button btn, bool on)
    {
        if (btn != null && btn.gameObject.activeSelf != on) btn.gameObject.SetActive(on);
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
