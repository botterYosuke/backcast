// WindowChromeApplier.cs — ADR-0028 (per-window live appearance subscriber)
//
// The per-window theme subscriber HudFrameChrome's header lamented backcast lacked: attached to each
// floating-window root by WindowChrome.Attach, it re-applies the appearance-appropriate chrome + surface
// on every ThemeService.Changed, so a dark↔light switch transforms the windows LIVE (findings 0106 D6).
// Mirrors the ChartView / DepthLadderView self-subscription pattern (OnEnable subscribe, OnDisable drop) —
// OnEnable is play-mode-only, so headless edit-time builds/probes call Apply() explicitly (via Attach).

using UnityEngine;

public sealed class WindowChromeApplier : MonoBehaviour
{
    RectTransform _root;

    // The frame's authored dark body color (incl. alpha), or null to track the theme panel_surface in both
    // appearances (set by WindowChrome.Attach). OnEnable re-applies on re-show so a theme switch that
    // happened while the window was hidden (e.g. the order ticket, hidden until LiveManual) is picked up.
    public Color? DarkSurface;

    void Awake() => _root = (RectTransform)transform;

    void OnEnable()
    {
        ThemeService.Changed += Apply;
        Apply();
    }

    void OnDisable() => ThemeService.Changed -= Apply;

    public void Apply()
    {
        if (_root == null) _root = (RectTransform)transform;
        WindowChrome.Apply(_root, DarkSurface);
    }
}
