// WindowChrome.cs — ADR-0028 (appearance-aware floating-window chrome seam)
//
// The SINGLE entry point every floating-window frame builder uses to dress its root, REPLACING the direct
// HudFrameChrome.Decorate calls (DockWindowFrame / StrategyEditorWindowFrame / OrderTicketWindowFrame +
// BackcastWorkspaceRoot's adopted editor/order). It picks the chrome by the active Appearance — dark = the
// cyan HUD (corner brackets + edge glow), light = the Miro Card (rounded + drop shadow) — and re-applies
// the card SURFACE color from the theme so a live dark↔light switch re-paints the body too (the frames
// bake their body color at build and are otherwise not theme subscribers — HudFrameChrome's long-standing
// gap, findings 0108 D6). Attach() wires a WindowChromeApplier so the switch is LIVE.

using UnityEngine;
using UnityEngine.UI;

public static class WindowChrome
{
    // A FloatingWindowLayer window rides the 1.2× front plane and reads as FLOATING above the dock back plane —
    // so it carries a deeper drop shadow in BOTH appearances (the dark HUD has none by default; the light card's
    // dock shadow is subtler). Centralized here so the lift can't diverge across the editor / order frames.
    static readonly Color ElevationShadowColor = new Color(0f, 0f, 0f, 0.42f);
    static readonly Vector2 ElevationShadowOffset = new Vector2(5f, -7f);

    // Wire a window root to follow appearance switches AND apply the right chrome NOW. Idempotent.
    // `darkSurface` is the frame's AUTHORED dark body color (incl. alpha) — pass it for windows whose dark
    // fill is NOT the theme panel_surface (editor / order ticket bake bespoke navies). Pass null (default)
    // for windows that track hakoniwa_panel_surface in BOTH appearances (the dock cluster).
    // `elevated` = a front-plane FloatingWindowLayer window (editor / order ticket) — gets the deeper float
    // shadow in both appearances. Dock windows pass false (their depth comes from parallax, not a shadow).
    public static void Attach(RectTransform root, Color? darkSurface = null, bool elevated = false)
    {
        if (root == null) return;
        var applier = root.GetComponent<WindowChromeApplier>();
        if (applier == null) applier = root.gameObject.AddComponent<WindowChromeApplier>();
        applier.DarkSurface = darkSurface;
        applier.Elevated = elevated;
        applier.Apply();   // OnEnable is play-mode-only; apply explicitly so edit-time builds get chrome too
    }

    // Apply the appearance-appropriate chrome to `root`, removing the other variant's so a live switch never
    // stacks HUD brackets under a Card (or vice-versa). Re-paints the card SURFACE so the switch carries the
    // body: Light ⇒ the white Miro panel_surface for EVERY window; Dark ⇒ `darkSurface` if the frame supplied
    // its authored color (editor/order — preserves their exact hue + 0.98 translucency), else the theme
    // panel_surface (the dock cluster, which tracks the theme in both appearances).
    public static void Apply(RectTransform root, Color? darkSurface, bool elevated = false)
    {
        if (root == null) return;

        var img = root.GetComponent<Image>();
        if (img != null)
        {
            bool light = ThemeService.Current.appearance == Appearance.Light;
            img.color = (light || !darkSurface.HasValue)
                ? ThemeService.Current.colors.hakoniwa_panel_surface
                : darkSurface.Value;
        }

        // STRUCTURE: only tear down + rebuild when the wanted chrome isn't already present. ThemeService.Changed
        // fires for ANY SetTheme, and the per-window applier re-applies on each — without this guard a switch
        // that keeps the same appearance would Destroy+recreate the bracket/card GameObjects on every window.
        // The surface repaint above still runs unconditionally (cheap, and it's the only thing that can differ
        // within one appearance). Re-tint within an appearance is a no-op here (Dark/Light accents are fixed).
        if (ThemeService.Current.appearance == Appearance.Light)
        {
            if (!CardFrameChrome.IsDecorated(root))
            {
                HudFrameChrome.Remove(root);
                CardFrameChrome.Decorate(root);
            }
        }
        else
        {
            if (!HudFrameChrome.IsDecorated(root))
            {
                CardFrameChrome.Remove(root);
                HudFrameChrome.Decorate(root);
            }
        }

        // FLOAT LIFT: a front-plane window gets the deeper shadow LAST, so it overrides whatever the chrome
        // variant just set — the light Card's subtle dock shadow is re-tuned up, and the dark HUD (which has no
        // shadow of its own) gains one. Find-or-create keeps it to ONE Shadow (no churn on same-appearance
        // re-apply). Non-elevated dock windows are left with the appearance default (light subtle / dark none).
        ApplyElevationShadow(root, elevated);
    }

    // Ensure the front-plane float shadow on `root` (idempotent). No-op for non-elevated (dock) windows so
    // their appearance-default shadow stands. A live HUD↔Card switch may Destroy the Card's Shadow on the way
    // to dark — this re-creates it, so an elevated window keeps its lift in BOTH appearances.
    static void ApplyElevationShadow(RectTransform root, bool elevated)
    {
        if (!elevated) return;
        if (root.GetComponent<Image>() == null) return;   // Shadow is a mesh effect; needs the root Graphic
        var sh = root.GetComponent<Shadow>();
        if (sh == null) sh = root.gameObject.AddComponent<Shadow>();
        sh.effectColor = ElevationShadowColor;
        sh.effectDistance = ElevationShadowOffset;
        sh.useGraphicAlpha = true;
    }
}
