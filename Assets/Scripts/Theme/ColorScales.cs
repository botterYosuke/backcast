// ColorScales.cs — issue #44 "theme（配色）システム"
//
// PORT of TTWR ColorScales (theme/mod.rs): the six Radix 12-step scales the theme derives
// every semantic role from. Swapping Dark() ↔ Light() is the ONLY switch needed for
// appearance — from_scales is appearance-agnostic by Radix design (step_12 is always the
// high-contrast text step, step_1 always app bg).
//
// 案A (findings 0020): Light() stubs to Dark() exactly like TTWR ColorScales::light(). Real
// Radix light scales land in #51; filling Light() needs ZERO re-wiring downstream.

public sealed class ColorScales
{
    public readonly ColorScale neutral;
    public readonly ColorScale accent;
    public readonly ColorScale red;
    public readonly ColorScale green;
    public readonly ColorScale yellow;
    public readonly ColorScale blue;

    public ColorScales(ColorScale neutral, ColorScale accent, ColorScale red,
                       ColorScale green, ColorScale yellow, ColorScale blue)
    {
        this.neutral = neutral;
        this.accent = accent;
        this.red = red;
        this.green = green;
        this.yellow = yellow;
        this.blue = blue;
    }

    public static ColorScales Dark() => new ColorScales(
        ColorScale.NeutralDark(),
        ColorScale.AccentDark(),
        ColorScale.RedDark(),
        ColorScale.GreenDark(),
        ColorScale.YellowDark(),
        ColorScale.BlueDark());

    // Real Radix light scales (ADR-0028, the #51-reserved work) for the Miro-風 whiteboard variant.
    // from_scales is appearance-agnostic, so swapping in the light scales re-derives every semantic role
    // with no downstream change. The canvas-isolated owner literals (workspace_background / hakoniwa_*)
    // are switched per-Appearance inside ThemeColors.FromScales, NOT here (they are not scale-derived).
    public static ColorScales Light() => new ColorScales(
        ColorScale.NeutralLight(),
        ColorScale.AccentLight(),
        ColorScale.RedLight(),
        ColorScale.GreenLight(),
        ColorScale.YellowLight(),
        ColorScale.BlueLight());
}
