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
// Linear color space (findings 0018 known-risk); revisit only if HITL shows luminance drift.
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
    public Color Step(int step) => _steps[step - 1];

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

    public static ColorScale NeutralDark() => new ColorScale(new[]
    {
        new Color(0.0706f, 0.0784f, 0.0902f), //  1 #121316 app bg
        new Color(0.0941f, 0.1020f, 0.1176f), //  2 #181a1e subtle bg
        new Color(0.1255f, 0.1373f, 0.1569f), //  3 #202328 ui bg
        new Color(0.1529f, 0.1686f, 0.1922f), //  4 #272b31 ui hover
        new Color(0.1843f, 0.2039f, 0.2314f), //  5 #2f343b ui pressed
        new Color(0.2235f, 0.2471f, 0.2784f), //  6 #393f47 subtle border
        new Color(0.2745f, 0.3020f, 0.3373f), //  7 #464d56 ui border
        new Color(0.3608f, 0.3922f, 0.4314f), //  8 #5c646e strong border
        new Color(0.4314f, 0.4627f, 0.5020f), //  9 #6e7680 solid
        new Color(0.4824f, 0.5137f, 0.5529f), // 10 #7b838d solid hover
        new Color(0.7059f, 0.7255f, 0.7529f), // 11 #b4b9c0 low-contrast text
        new Color(0.9255f, 0.9333f, 0.9451f), // 12 #ecedf1 high-contrast text
    });

    public static ColorScale AccentDark() => new ColorScale(new[]
    {
        new Color(0.0745f, 0.0745f, 0.1176f), //  1 #13131e
        new Color(0.0902f, 0.0863f, 0.1451f), //  2 #171625
        new Color(0.1255f, 0.1255f, 0.2392f), //  3 #20203d
        new Color(0.1490f, 0.1490f, 0.3216f), //  4 #262652
        new Color(0.1765f, 0.1725f, 0.4000f), //  5 #2d2c66
        new Color(0.2196f, 0.2118f, 0.4745f), //  6 #383679
        new Color(0.2706f, 0.2627f, 0.5373f), //  7 #454389
        new Color(0.3412f, 0.3255f, 0.7765f), //  8 #5753c6
        new Color(0.3569f, 0.3569f, 0.8392f), //  9 #5b5bd6 brand solid
        new Color(0.4314f, 0.4157f, 0.8706f), // 10 #6e6ade
        new Color(0.6941f, 0.6627f, 1.0000f), // 11 #b1a9ff low-contrast text
        new Color(0.8784f, 0.8745f, 1.0000f), // 12 #e0dfff high-contrast text
    });

    public static ColorScale RedDark() => new ColorScale(new[]
    {
        new Color(0.0980f, 0.0667f, 0.0667f), //  1 #191111
        new Color(0.1255f, 0.0745f, 0.0784f), //  2 #201314
        new Color(0.2314f, 0.0706f, 0.0980f), //  3 #3b1219
        new Color(0.3137f, 0.0588f, 0.1098f), //  4 #500f1c
        new Color(0.3804f, 0.0863f, 0.1373f), //  5 #611623
        new Color(0.4471f, 0.1373f, 0.1765f), //  6 #72232d
        new Color(0.5490f, 0.2000f, 0.2275f), //  7 #8c333a
        new Color(0.7098f, 0.2706f, 0.2824f), //  8 #b54548
        new Color(0.8980f, 0.2824f, 0.3020f), //  9 #e5484d brand solid
        new Color(0.9255f, 0.3647f, 0.3686f), // 10 #ec5d5e
        new Color(1.0000f, 0.5843f, 0.5725f), // 11 #ff9592 low-contrast text
        new Color(1.0000f, 0.8196f, 0.8510f), // 12 #ffd1d9 high-contrast text
    });

    public static ColorScale GreenDark() => new ColorScale(new[]
    {
        new Color(0.0549f, 0.0824f, 0.0667f), //  1 #0e1511
        new Color(0.0784f, 0.1020f, 0.0824f), //  2 #141a15
        new Color(0.1059f, 0.1647f, 0.1176f), //  3 #1b2a1e
        new Color(0.1137f, 0.2275f, 0.1412f), //  4 #1d3a24
        new Color(0.1451f, 0.2824f, 0.1765f), //  5 #25482d
        new Color(0.1765f, 0.3412f, 0.2118f), //  6 #2d5736
        new Color(0.2118f, 0.4039f, 0.2510f), //  7 #366740
        new Color(0.2431f, 0.4745f, 0.2863f), //  8 #3e7949
        new Color(0.2745f, 0.6549f, 0.3451f), //  9 #46a758 brand solid
        new Color(0.3255f, 0.7020f, 0.3961f), // 10 #53b365
        new Color(0.4431f, 0.8157f, 0.5137f), // 11 #71d083 low-contrast text
        new Color(0.7608f, 0.9412f, 0.7608f), // 12 #c2f0c2 high-contrast text
    });

    public static ColorScale YellowDark() => new ColorScale(new[]
    {
        new Color(0.0863f, 0.0706f, 0.0471f), //  1 #16120c
        new Color(0.1137f, 0.0941f, 0.0588f), //  2 #1d180f
        new Color(0.1882f, 0.1255f, 0.0314f), //  3 #302008
        new Color(0.2471f, 0.1529f, 0.0000f), //  4 #3f2700
        new Color(0.3020f, 0.1882f, 0.0000f), //  5 #4d3000
        new Color(0.3608f, 0.2392f, 0.0196f), //  6 #5c3d05
        new Color(0.4431f, 0.3098f, 0.0980f), //  7 #714f19
        new Color(0.5608f, 0.3922f, 0.1412f), //  8 #8f6424
        new Color(1.0000f, 0.7725f, 0.2392f), //  9 #ffc53d brand solid
        new Color(1.0000f, 0.8392f, 0.0392f), // 10 #ffd60a
        new Color(1.0000f, 0.7922f, 0.0863f), // 11 #ffca16 low-contrast text
        new Color(1.0000f, 0.9059f, 0.7020f), // 12 #ffe7b3 high-contrast text
    });

    public static ColorScale BlueDark() => new ColorScale(new[]
    {
        new Color(0.0510f, 0.0824f, 0.1255f), //  1 #0d1520
        new Color(0.0667f, 0.0980f, 0.1529f), //  2 #111927
        new Color(0.0510f, 0.1569f, 0.2784f), //  3 #0d2847
        new Color(0.0000f, 0.2000f, 0.3843f), //  4 #003362
        new Color(0.0000f, 0.2510f, 0.4549f), //  5 #004074
        new Color(0.0627f, 0.3020f, 0.5294f), //  6 #104d87
        new Color(0.1255f, 0.3647f, 0.6196f), //  7 #205d9e
        new Color(0.1569f, 0.4392f, 0.7412f), //  8 #2870bd
        new Color(0.0000f, 0.5647f, 1.0000f), //  9 #0090ff brand solid
        new Color(0.2314f, 0.6196f, 1.0000f), // 10 #3b9eff
        new Color(0.4392f, 0.7216f, 1.0000f), // 11 #70b8ff low-contrast text
        new Color(0.7608f, 0.9020f, 1.0000f), // 12 #c2e6ff high-contrast text
    });
}
