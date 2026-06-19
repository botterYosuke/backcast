// ColorScale.cs — issue #44 "theme（配色）システム" (color layer port)
//
// Radix-inspired 12-step color scale. PORT of TTWR src/ui/theme/scale.rs (dark variant).
// Each step has a fixed semantic role (background → border → solid → text):
//   1 app bg · 2 subtle bg · 3 ui bg · 4 ui hover · 5 ui pressed · 6 subtle border ·
//   7 ui border · 8 strong border · 9 solid · 10 solid hover · 11 low-contrast text ·
//   12 high-contrast text.
//
// sRGB floats are the published Radix hex values divided by 255 (~4 sig figs), written
// RAW into new Color(r,g,b,1) — same流儀 as existing backcast inline colors. Project is in
// Linear color space (findings 0020 known-risk); revisit only if HITL shows luminance drift.
//
// Light scales are NOT shipped in #44 (案A): the *_light factories stub to *_dark, mirroring
// TTWR ColorScales::light() which returns dark. Real Radix light values land in #51.

using UnityEngine;

public sealed class ColorScale
{
    readonly Color[] _steps; // 12 entries, index 0 == step 1

    public ColorScale(Color[] steps)
    {
        if (steps == null || steps.Length != 12)
            throw new System.ArgumentException("ColorScale needs exactly 12 steps");
        _steps = steps;
    }

    // 1-based step access (matches Radix / TTWR naming).
    public Color Step1 => _steps[0];
    public Color Step2 => _steps[1];
    public Color Step3 => _steps[2];
    public Color Step4 => _steps[3];
    public Color Step5 => _steps[4];
    public Color Step6 => _steps[5];
    public Color Step7 => _steps[6];
    public Color Step8 => _steps[7];
    public Color Step9 => _steps[8];
    public Color Step10 => _steps[9];
    public Color Step11 => _steps[10];
    public Color Step12 => _steps[11];

    // -- Dark scales (Radix: slate / iris / red / grass / amber / blue) --------------------

    // Cyberpunk neon palette (2026-06-20 owner re-skin): deep-space navy base + cyan(Blue.Step9)
    // / magenta(Accent.Step9) / purple(Accent.Step8) neons. Step 9 anchors the user-specified
    // brand hues; Step 8 is intentionally a sibling neon (purple) rather than a monotonic
    // luminance step, so panel_focused_border / strong_border read as a second-accent rather
    // than a dimmed brand. Step 11/12 stay desaturated for text legibility on the dark base.

    public static ColorScale NeutralDark() => new ColorScale(new[]
    {
        new Color(0.0392f, 0.0549f, 0.1529f), //  1 #0a0e27 app bg (owner brand bg)
        new Color(0.0510f, 0.0667f, 0.1882f), //  2 #0d1130 subtle bg
        new Color(0.0745f, 0.0941f, 0.2510f), //  3 #131840 ui bg
        new Color(0.1020f, 0.1255f, 0.3137f), //  4 #1a2050 ui hover
        new Color(0.1333f, 0.1608f, 0.3765f), //  5 #222960 ui pressed
        new Color(0.1725f, 0.2039f, 0.4392f), //  6 #2c3470 subtle border
        new Color(0.2275f, 0.2588f, 0.5294f), //  7 #3a4287 ui border
        new Color(0.3020f, 0.3529f, 0.6392f), //  8 #4d5aa3 strong border
        new Color(0.4000f, 0.4706f, 0.7216f), //  9 #6678b8 solid
        new Color(0.4784f, 0.5490f, 0.7804f), // 10 #7a8cc7 solid hover
        new Color(0.6588f, 0.7216f, 0.8784f), // 11 #a8b8e0 low-contrast text
        new Color(0.8784f, 0.9176f, 1.0000f), // 12 #e0eaff high-contrast text
    });

    public static ColorScale AccentDark() => new ColorScale(new[]
    {
        new Color(0.1020f, 0.0157f, 0.1255f), //  1 #1a0420
        new Color(0.1333f, 0.0235f, 0.1569f), //  2 #220628
        new Color(0.2196f, 0.0392f, 0.2431f), //  3 #380a3e
        new Color(0.3020f, 0.0549f, 0.3216f), //  4 #4d0e52
        new Color(0.3686f, 0.0627f, 0.3882f), //  5 #5e1063
        new Color(0.4627f, 0.0784f, 0.4706f), //  6 #761478
        new Color(0.5725f, 0.1020f, 0.5569f), //  7 #921a8e
        new Color(0.6941f, 0.2902f, 0.9294f), //  8 #b14aed strong border / focus (owner purple)
        new Color(1.0000f, 0.0000f, 0.6667f), //  9 #ff00aa brand solid (owner magenta)
        new Color(1.0000f, 0.2667f, 0.7373f), // 10 #ff44bc solid hover
        new Color(1.0000f, 0.5608f, 0.8314f), // 11 #ff8fd4 low-contrast text
        new Color(1.0000f, 0.8157f, 0.9216f), // 12 #ffd0eb high-contrast text
    });

    public static ColorScale RedDark() => new ColorScale(new[]
    {
        new Color(0.1255f, 0.0314f, 0.0314f), //  1 #200808
        new Color(0.1647f, 0.0392f, 0.0392f), //  2 #2a0a0a
        new Color(0.2902f, 0.0471f, 0.0706f), //  3 #4a0c12
        new Color(0.3882f, 0.0627f, 0.0941f), //  4 #631018
        new Color(0.4784f, 0.0784f, 0.1098f), //  5 #7a141c
        new Color(0.5843f, 0.0941f, 0.1333f), //  6 #951822
        new Color(0.7216f, 0.1176f, 0.1725f), //  7 #b81e2c
        new Color(0.8510f, 0.1569f, 0.2510f), //  8 #d92840
        new Color(1.0000f, 0.1569f, 0.3333f), //  9 #ff2855 brand solid (hot pink-red)
        new Color(1.0000f, 0.3451f, 0.4706f), // 10 #ff5878
        new Color(1.0000f, 0.5608f, 0.6392f), // 11 #ff8fa3 low-contrast text
        new Color(1.0000f, 0.8157f, 0.8471f), // 12 #ffd0d8 high-contrast text
    });

    public static ColorScale GreenDark() => new ColorScale(new[]
    {
        new Color(0.0000f, 0.1020f, 0.0588f), //  1 #001a0f
        new Color(0.0078f, 0.1412f, 0.0784f), //  2 #022414
        new Color(0.0196f, 0.2275f, 0.1216f), //  3 #053a1f
        new Color(0.0235f, 0.3020f, 0.1569f), //  4 #064d28
        new Color(0.0314f, 0.3765f, 0.1882f), //  5 #086030
        new Color(0.0392f, 0.4588f, 0.2275f), //  6 #0a753a
        new Color(0.0510f, 0.5647f, 0.2824f), //  7 #0d9048
        new Color(0.0627f, 0.7098f, 0.3451f), //  8 #10b558
        new Color(0.0000f, 1.0000f, 0.5333f), //  9 #00ff88 brand solid (neon lime)
        new Color(0.3020f, 1.0000f, 0.6275f), // 10 #4dffa0
        new Color(0.5608f, 1.0000f, 0.7490f), // 11 #8fffbf low-contrast text
        new Color(0.8157f, 1.0000f, 0.8784f), // 12 #d0ffe0 high-contrast text
    });

    public static ColorScale YellowDark() => new ColorScale(new[]
    {
        new Color(0.1216f, 0.1020f, 0.0078f), //  1 #1f1a02
        new Color(0.1647f, 0.1373f, 0.0196f), //  2 #2a2305
        new Color(0.2706f, 0.2235f, 0.0392f), //  3 #45390a
        new Color(0.3608f, 0.2902f, 0.0510f), //  4 #5c4a0d
        new Color(0.4510f, 0.3608f, 0.0627f), //  5 #735c10
        new Color(0.5490f, 0.4392f, 0.0784f), //  6 #8c7014
        new Color(0.6784f, 0.5412f, 0.1020f), //  7 #ad8a1a
        new Color(0.8392f, 0.6588f, 0.1255f), //  8 #d6a820
        new Color(1.0000f, 0.8431f, 0.0000f), //  9 #ffd700 brand solid (neon amber)
        new Color(1.0000f, 0.8784f, 0.3020f), // 10 #ffe04d
        new Color(1.0000f, 0.9176f, 0.5608f), // 11 #ffea8f low-contrast text
        new Color(1.0000f, 0.9608f, 0.8157f), // 12 #fff5d0 high-contrast text
    });

    public static ColorScale BlueDark() => new ColorScale(new[]
    {
        new Color(0.0000f, 0.1020f, 0.1255f), //  1 #001a20
        new Color(0.0078f, 0.1373f, 0.1647f), //  2 #02232a
        new Color(0.0157f, 0.2196f, 0.2706f), //  3 #043845
        new Color(0.0196f, 0.2980f, 0.3686f), //  4 #054c5e
        new Color(0.0235f, 0.3765f, 0.4588f), //  5 #066075
        new Color(0.0314f, 0.4588f, 0.5608f), //  6 #08758f
        new Color(0.0392f, 0.5647f, 0.6902f), //  7 #0a90b0
        new Color(0.0510f, 0.7098f, 0.8549f), //  8 #0db5da
        new Color(0.0000f, 0.9412f, 1.0000f), //  9 #00f0ff brand solid (owner cyan)
        new Color(0.3020f, 0.9608f, 1.0000f), // 10 #4df5ff
        new Color(0.5608f, 0.9725f, 1.0000f), // 11 #8ff8ff low-contrast text
        new Color(0.8157f, 0.9882f, 1.0000f), // 12 #d0fcff high-contrast text
    });
}
