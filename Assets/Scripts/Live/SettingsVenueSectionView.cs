// SettingsVenueSectionView.cs — issue #128 (ADR-0026): Venue 接続/切断, re-homed from the menu-bar
// Venue dropdown into the Settings modal's Venue section.
//
// The brain (VenueMenuViewModel) is UNCHANGED — VisibleConnectItems' LIVE_VENUE filter / prod grey-out
// / connecting-or-authenticating disable / _host.Conn-Coord login-logout all stay; this just rebuilds
// the view against the same VM (ADR-0026). Mirrors MenuBarView.BuildVenueMenu's per-item interactable
// gating, but as a flat list of buttons in the section instead of a dropdown. The menu bar's top-level
// Venue button + dropdown are retired (#128).
//
// When a Connect requires a second password, the host raises the secret modal (sortingOrder 1000) which
// draws OVER Settings (SETTINGS_SORT 900); after submit Settings stays open and the badge/venue state
// updates (S1 z-order contract).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsVenueSectionView
{
    readonly VenueMenuViewModel _venue;
    readonly string _filterVenue;                 // ADR-0021: pinned LIVE_VENUE or null = show all
    readonly Action<string, string> _onConnect;   // (venue, env)
    readonly Action _onDisconnect;
    readonly Func<bool> _connectReady;            // server ready && !teardown
    readonly Font _font;

    // (button, enabled-predicate, face Image, label Text) — interactable refreshed each frame to follow
    // the async venue poll; face/label retained so ApplyTheme repaints them on a LIVE Dark/Light switch
    // (build-time baked colors otherwise persist until the modal is reopened — #137 review HIGH 1).
    readonly List<(Button btn, Func<bool> enabled, Image face, Text label)> _items =
        new List<(Button, Func<bool>, Image, Text)>();

    public SettingsVenueSectionView(VenueMenuViewModel venue, string filterVenue,
                                    Action<string, string> onConnect, Action onDisconnect,
                                    Func<bool> connectReady, Font font)
    {
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _filterVenue = filterVenue;
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        _connectReady = connectReady;
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    bool Ready() => _connectReady == null || _connectReady();

    public void Build(RectTransform container)
    {
        // ADR-0021: the venue list is computed by the pure VenueMenuViewModel.VisibleConnectItems — same
        // call the retired dropdown used, so the AFK gate drives the filter without building uGUI.
        var connectItems = VenueMenuViewModel.VisibleConnectItems(_filterVenue, Application.isEditor);

        const float rowH = 18f, gap = 2f;
        float y = 0f;
        foreach (var (label, v, env) in connectItems)
        {
            string venueId = v, envId = env;
            var (btn, face, lbl) = MakeButton(container, label, ref y, rowH, gap,
                () => { _onConnect?.Invoke(venueId, envId); });
            // ADR-0027: prod 解禁の env ゲートは廃止。MOCK も prod も含め全 variant は同一述語で gate される
            // （切断中は enable・接続中/認証中は disable）。CanConnectEnv は per-(venue,env) enablement の唯一の
            // seam＝env はもう結果を変えないが、将来 per-env ゲートが戻るならここ（PRODGATE-07 が pin する場所）。
            // 旧 MOCK 特例分岐は CanConnectEnv("MOCK","")⇒CanConnect と恒等なので撤去（dead branch / #130 simplify）。
            _items.Add((btn, () => Ready() && _venue.CanConnectEnv(venueId, envId), face, lbl));
        }
        var (disBtn, disFace, disLbl) = MakeButton(container, "Disconnect", ref y, rowH, gap, () => { _onDisconnect?.Invoke(); });
        _items.Add((disBtn, () => Ready() && _venue.CanDisconnect, disFace, disLbl));

        Refresh();
    }

    // Reflect the live connection state into each button's interactable (cheap; no GameObject churn).
    public void Refresh()
    {
        foreach (var (btn, enabled, _, _) in _items)
            if (btn != null) btn.interactable = enabled();
    }

    // #137 review HIGH 1: repaint the venue button face + label on a LIVE Dark/Light switch (build-time
    // baked colors otherwise persist). Called from BackcastWorkspaceRoot.ApplyViewportTheme alongside
    // SettingsModalOverlay.ApplyTheme / SettingsModeSegmentView.Refresh so the whole Settings chrome
    // re-themes in place while the modal is open.
    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        foreach (var it in _items)
        {
            if (it.face != null) it.face.color = c.element_background;
            if (it.label != null) it.label.color = c.text;
        }
        Refresh();
    }

    (Button btn, Image face, Text label) MakeButton(RectTransform parent, string text, ref float y, float h, float gap, Action onClick)
    {
        var go = new GameObject("venue:" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(0f, h);
        rt.anchoredPosition = new Vector2(0f, y);
        var face = go.GetComponent<Image>();
        face.color = ThemeService.Current.colors.element_background;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => onClick());

        var labelGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-4f, 0f);
        var lt = labelGo.GetComponent<Text>();
        lt.font = _font; lt.fontSize = 12; lt.color = ThemeService.Current.colors.text;
        lt.alignment = TextAnchor.MiddleLeft; lt.text = text; lt.raycastTarget = false;

        y -= h + gap;
        return (btn, face, lt);
    }
}
