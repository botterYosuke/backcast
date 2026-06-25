// WorkspaceFooterView.cs — issue #76 S6b-β-clean U4 (uGUI view; durable surface), reduced to a
// mode-STATUS-display surface by #127 (ADR-0026).
//
// HISTORY: this footer once owned the replay transport (▶/⏸/step/stop/speed — retired in #76 by the
// reactive cutover, ADR-0012) and then the execution-mode segments (Replay / Manual / Auto). #127
// (ADR-0026) moves the mode segments into the Settings modal's Mode section. What REMAINS here is the
// mode STATUS row only — current mode / "<mode> (switching…)" / "LiveAuto: <runId>" — mirrored from the
// SAME FooterModeViewModel. The footer holds NO switch control ("今どのモードか" is also always visible
// on the menu-bar `mode: <X>` badge). A PLAIN C# builder (not a MonoBehaviour); the host owns the
// GameObject lifecycle. Colors read ThemeService.Current (#44).

using UnityEngine;
using UnityEngine.UI;

public sealed class WorkspaceFooterView
{
    // #84 (findings 0053) — the footer is screen-fixed chrome and MUST sit above the sidebar's own
    // override-sorting Canvas (UniverseSidebarView.SIDEBAR_SORT=500) so the status bar can never be
    // overpainted by an overflowing sidebar (the original #84 symptom). MenuBarView.MENU_SORT=600
    // stays in front. Values are DERIVED — the chrome z-order section asserts the RELATIONS only.
    public const int FOOTER_SORT = 550;

    readonly FooterModeViewModel _modeVm;
    readonly LiveAutoTransportViewModel _autoVm;    // optional: drives the live-mode status line
    readonly Font _font;

    Text _statusLabel;
    Image _barBg;

    public WorkspaceFooterView(FooterModeViewModel modeVm, LiveAutoTransportViewModel autoVm, Font font)
    {
        _modeVm = modeVm ?? throw new System.ArgumentNullException(nameof(modeVm));
        _autoVm = autoVm;
        _font = font;
    }

    // Build the footer under `bar` (a bottom-anchored RectTransform owned by the host).
    public void Build(RectTransform bar)
    {
        // #84: promote the footer GO to its OWN override-sorting Canvas so it draws above the sidebar
        // (SIDEBAR_SORT=500) and below the menu (MENU_SORT=600) (findings 0045 / 0053).
        ChromeCanvas.Promote(bar.gameObject, FOOTER_SORT);

        var bg = bar.gameObject.GetComponent<Image>();
        if (bg == null) bg = bar.gameObject.AddComponent<Image>();
        _barBg = bg;
        bg.color = ThemeService.Current.colors.panel_background;

        // #127: no mode segments — the status line fills the footer width.
        _statusLabel = MakeLabel(bar, "Replay", 0.01f, 0.98f);

        Refresh();
    }

    // Reflect VM state: the mode status line only (#127 — segments moved to Settings).
    public void Refresh()
    {
        if (_statusLabel != null) _statusLabel.text = StatusText(_modeVm.DisplayMode);
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
        if (_statusLabel != null) _statusLabel.color = t.colors.text;
        Refresh();
    }

    Text MakeLabel(RectTransform parent, string text, float xMin, float width)
    {
        var go = new GameObject("status", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(xMin, 0.12f);
        rt.anchorMax = new Vector2(xMin + width, 0.88f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.text; t.text = text;
        t.fontSize = 12; t.alignment = TextAnchor.MiddleLeft;
        return t;
    }
}
