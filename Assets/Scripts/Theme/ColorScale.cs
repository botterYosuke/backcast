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

    // Deep-space palette (2026-06-20 owner re-skin): cosmos-void base + single primary
    // (cosmic violet) supported by muted aurora-teal / mars-rust / gold-star / steel-cyan.
    // Hue diversity is intentionally narrow and saturation dialed back ~40% vs Radix dark, so
    // the chrome reads "spacecraft instrument" rather than "arcade neon" (replaces the
    // cyberpunk experiment which was too psychedelic). Linear color space (findings 0020).

    public static ColorScale NeutralDark() => new ColorScale(new[]
    {
        new Color(0.0196f, 0.0314f, 0.0784f), //  1 #050814 app bg (deepest cosmos)
        new Color(0.0392f, 0.0549f, 0.1216f), //  2 #0a0e1f subtle bg
        new Color(0.0667f, 0.0863f, 0.1686f), //  3 #11162b ui bg
        new Color(0.0941f, 0.1216f, 0.2275f), //  4 #181f3a ui hover
        new Color(0.1216f, 0.1529f, 0.2902f), //  5 #1f274a ui pressed
        new Color(0.1569f, 0.2000f, 0.3608f), //  6 #28335c subtle border
        new Color(0.2078f, 0.2588f, 0.4471f), //  7 #354272 ui border
        new Color(0.2745f, 0.3373f, 0.5451f), //  8 #46568b strong border
        new Color(0.3725f, 0.4392f, 0.6588f), //  9 #5f70a8 solid
        new Color(0.4588f, 0.5294f, 0.7412f), // 10 #7587bd solid hover
        new Color(0.6588f, 0.7059f, 0.8314f), // 11 #a8b4d4 low-contrast text
        new Color(0.8784f, 0.9059f, 0.9608f), // 12 #e0e7f5 high-contrast text (starlight)
    });

    public static ColorScale AccentDark() => new ColorScale(new[]
    {
        new Color(0.0588f, 0.0431f, 0.1333f), //  1 #0f0b22
        new Color(0.0863f, 0.0627f, 0.1922f), //  2 #161031
        new Color(0.1333f, 0.0980f, 0.2784f), //  3 #221947
        new Color(0.1725f, 0.1255f, 0.3490f), //  4 #2c2059
        new Color(0.2196f, 0.1608f, 0.4275f), //  5 #38296d
        new Color(0.2745f, 0.2000f, 0.5020f), //  6 #463380
        new Color(0.3373f, 0.2471f, 0.5922f), //  7 #563f97
        new Color(0.4157f, 0.3059f, 0.7020f), //  8 #6a4eb3 strong border / Neptune storm
        new Color(0.5059f, 0.3490f, 0.8118f), //  9 #8159cf brand solid (cosmic violet)
        new Color(0.5843f, 0.4471f, 0.8627f), // 10 #9572dc
        new Color(0.7216f, 0.6235f, 0.9098f), // 11 #b89fe8 low-contrast text
        new Color(0.8667f, 0.8157f, 0.9608f), // 12 #ddd0f5 high-contrast text
    });

    public static ColorScale RedDark() => new ColorScale(new[]
    {
        new Color(0.1098f, 0.0510f, 0.0392f), //  1 #1c0d0a
        new Color(0.1451f, 0.0627f, 0.0627f), //  2 #251010
        new Color(0.2392f, 0.0941f, 0.0824f), //  3 #3d1815
        new Color(0.3176f, 0.1255f, 0.1020f), //  4 #51201a
        new Color(0.3882f, 0.1569f, 0.1255f), //  5 #632820
        new Color(0.4706f, 0.1961f, 0.1490f), //  6 #783226
        new Color(0.5765f, 0.2549f, 0.1804f), //  7 #93412e
        new Color(0.7020f, 0.3216f, 0.2196f), //  8 #b35238
        new Color(0.8510f, 0.3882f, 0.2627f), //  9 #d96343 brand solid (mars rust)
        new Color(0.8863f, 0.4706f, 0.3569f), // 10 #e2785b
        new Color(0.9255f, 0.6039f, 0.5137f), // 11 #ec9a83 low-contrast text
        new Color(0.9647f, 0.8039f, 0.7333f), // 12 #f6cdbb high-contrast text
    });

    public static ColorScale GreenDark() => new ColorScale(new[]
    {
        new Color(0.0235f, 0.0941f, 0.0706f), //  1 #061812
        new Color(0.0314f, 0.1333f, 0.0980f), //  2 #082219
        new Color(0.0510f, 0.2196f, 0.1608f), //  3 #0d3829
        new Color(0.0667f, 0.2902f, 0.2118f), //  4 #114a36
        new Color(0.0824f, 0.3686f, 0.2667f), //  5 #155e44
        new Color(0.1020f, 0.4471f, 0.3255f), //  6 #1a7253
        new Color(0.1294f, 0.5333f, 0.4039f), //  7 #218867
        new Color(0.1647f, 0.6392f, 0.4824f), //  8 #2aa37b
        new Color(0.2314f, 0.7686f, 0.5961f), //  9 #3bc498 brand solid (aurora teal)
        new Color(0.3451f, 0.8235f, 0.6588f), // 10 #58d2a8
        new Color(0.5451f, 0.8784f, 0.7608f), // 11 #8be0c2 low-contrast text
        new Color(0.7843f, 0.9373f, 0.8706f), // 12 #c8efde high-contrast text
    });

    public static ColorScale YellowDark() => new ColorScale(new[]
    {
        new Color(0.1098f, 0.0863f, 0.0196f), //  1 #1c1605
        new Color(0.1490f, 0.1137f, 0.0314f), //  2 #261d08
        new Color(0.2392f, 0.1804f, 0.0471f), //  3 #3d2e0c
        new Color(0.3137f, 0.2353f, 0.0627f), //  4 #503c10
        new Color(0.3882f, 0.2941f, 0.0745f), //  5 #634b13
        new Color(0.4784f, 0.3647f, 0.0941f), //  6 #7a5d18
        new Color(0.5843f, 0.4510f, 0.1216f), //  7 #95731f
        new Color(0.7098f, 0.5490f, 0.1569f), //  8 #b58c28
        new Color(0.8471f, 0.6588f, 0.2314f), //  9 #d8a83b brand solid (gold star)
        new Color(0.8902f, 0.7216f, 0.3333f), // 10 #e3b855
        new Color(0.9294f, 0.8157f, 0.5098f), // 11 #edd082 low-contrast text
        new Color(0.9686f, 0.9098f, 0.7529f), // 12 #f7e8c0 high-contrast text
    });

    public static ColorScale BlueDark() => new ColorScale(new[]
    {
        new Color(0.0196f, 0.0784f, 0.1255f), //  1 #051420
        new Color(0.0314f, 0.1098f, 0.1725f), //  2 #081c2c
        new Color(0.0549f, 0.1765f, 0.2784f), //  3 #0e2d47
        new Color(0.0784f, 0.2314f, 0.3647f), //  4 #143b5d
        new Color(0.0980f, 0.2863f, 0.4471f), //  5 #194972
        new Color(0.1255f, 0.3569f, 0.5412f), //  6 #205b8a
        new Color(0.1569f, 0.4392f, 0.6588f), //  7 #2870a8
        new Color(0.1961f, 0.5294f, 0.7686f), //  8 #3287c4
        new Color(0.2941f, 0.6275f, 0.8588f), //  9 #4ba0db brand solid (cosmic steel-cyan)
        new Color(0.4039f, 0.6941f, 0.8902f), // 10 #67b1e3
        new Color(0.5804f, 0.7922f, 0.9333f), // 11 #94caee low-contrast text
        new Color(0.7843f, 0.8824f, 0.9608f), // 12 #c8e1f5 high-contrast text
    });
}
