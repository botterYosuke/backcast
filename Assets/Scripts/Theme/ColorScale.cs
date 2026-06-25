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
// Light scales: #44 (案A) stubbed *_light to *_dark (TTWR ColorScales::light() returns dark). ADR-0028
// lands the REAL Radix light scales here (the #51-reserved work) so the Miro-風 whiteboard theme can be
// switched live. The light scales are the PUBLISHED Radix LIGHT values (slate/indigo/red/grass/amber/blue)
// — step_1 a near-white app bg, step_12 a near-black high-contrast text, by the same Radix role mapping
// from_scales already consumes (so wiring the light scales needs ZERO downstream re-derivation). Accent =
// Radix INDIGO (step_9 #3e63dd) — an electric Miro-blue, distinct from the status `blue` scale (info).

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

    // Cyan-HUD palette (2026-06-21 owner re-skin — "blue HUD interface" target): near-black
    // navy void base + a single ELECTRIC-CYAN primary, supported by aurora-teal / mars-rust /
    // gold-star kept from the prior space theme for trading semantics. The accent shifts from
    // cosmic violet → glowing cyan so chrome / brackets / focus borders read as a sci-fi heads-
    // up display (corner-bracket frames + grid field, HudFrameChrome / HudGridBackground). The
    // neutral floor drops a touch darker (near-black with a cool cyan-navy tint) so cyan glow
    // and starlight text pop against the void. Linear color space (findings 0020).

    public static ColorScale NeutralDark() => new ColorScale(new[]
    {
        new Color(0.0118f, 0.0275f, 0.0510f), //  1 #03070d app bg (near-black cyan-navy void)
        new Color(0.0235f, 0.0510f, 0.0863f), //  2 #060d16 subtle bg
        new Color(0.0549f, 0.0863f, 0.1490f), //  3 #0e1626 ui bg
        new Color(0.0863f, 0.1255f, 0.2078f), //  4 #162035 ui hover
        new Color(0.1137f, 0.1608f, 0.2706f), //  5 #1d2945 ui pressed
        new Color(0.1490f, 0.2118f, 0.3412f), //  6 #263657 subtle border
        new Color(0.1922f, 0.2745f, 0.4235f), //  7 #31466c ui border
        new Color(0.2549f, 0.3569f, 0.5294f), //  8 #415b87 strong border
        new Color(0.3490f, 0.4627f, 0.6431f), //  9 #5976a4 solid
        new Color(0.4471f, 0.5608f, 0.7294f), // 10 #728fba solid hover
        new Color(0.6471f, 0.7255f, 0.8392f), // 11 #a5b9d6 low-contrast text
        new Color(0.8706f, 0.9216f, 0.9725f), // 12 #deebf8 high-contrast text (starlight)
    });

    // ACCENT = electric cyan (Radix "cyan"/"sky" inspired). Step9 (#22d3ee) is the brand glow —
    // it drives accent, focus borders, HUD corner brackets, text/icon accent, PlayerColors[0]
    // (chart series + window-title accent) and syntax keyword/type.
    public static ColorScale AccentDark() => new ColorScale(new[]
    {
        new Color(0.0157f, 0.0784f, 0.1020f), //  1 #04141a
        new Color(0.0275f, 0.1255f, 0.1647f), //  2 #07202a
        new Color(0.0392f, 0.1922f, 0.2510f), //  3 #0a3140
        new Color(0.0510f, 0.2588f, 0.3294f), //  4 #0d4254
        new Color(0.0627f, 0.3294f, 0.4078f), //  5 #105468
        new Color(0.0824f, 0.4157f, 0.5098f), //  6 #156a82 subtle border
        new Color(0.1098f, 0.5294f, 0.6392f), //  7 #1c87a3 ui border
        new Color(0.1373f, 0.6549f, 0.7843f), //  8 #23a7c8 strong border
        new Color(0.1333f, 0.8275f, 0.9333f), //  9 #22d3ee brand solid (electric cyan glow)
        new Color(0.3059f, 0.8784f, 0.9608f), // 10 #4ee0f5
        new Color(0.5569f, 0.9176f, 0.9725f), // 11 #8eeaf8 low-contrast text
        new Color(0.7843f, 0.9647f, 0.9922f), // 12 #c8f6fd high-contrast text
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

    // -- Light scales (Radix LIGHT: slate / indigo / red / grass / amber / blue) — ADR-0028 ----
    // Published Radix LIGHT hex written as raw sRGB/255 (same流儀 as the dark scales above; Linear
    // color space known-risk per findings 0020 — revisit only if HITL shows luminance drift). These
    // drive the Miro-風 whiteboard variant: near-white app bg (step_1), near-black text (step_12).

    // Hex → Color helper (raw sRGB/255). Light scales transcribe Radix hex, so a helper avoids the
    // per-channel float transcription errors the dark scales risk with hand-divided literals.
    static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);

    // NEUTRAL = Radix Slate (light): the off-white field + grey borders the whiteboard reads on.
    public static ColorScale NeutralLight() => new ColorScale(new[]
    {
        Rgb(0xFB, 0xFC, 0xFD), //  1 #fbfcfd app bg (near-white)
        Rgb(0xF8, 0xF9, 0xFA), //  2 #f8f9fa subtle bg
        Rgb(0xF1, 0xF3, 0xF5), //  3 #f1f3f5 ui bg
        Rgb(0xEC, 0xEE, 0xF0), //  4 #eceef0 ui hover
        Rgb(0xE6, 0xE8, 0xEB), //  5 #e6e8eb ui pressed
        Rgb(0xDF, 0xE3, 0xE6), //  6 #dfe3e6 subtle border
        Rgb(0xD7, 0xDB, 0xDF), //  7 #d7dbdf ui border
        Rgb(0xC1, 0xC8, 0xCD), //  8 #c1c8cd strong border
        Rgb(0x88, 0x90, 0x96), //  9 #889096 solid
        Rgb(0x7E, 0x86, 0x8C), // 10 #7e868c solid hover
        Rgb(0x68, 0x70, 0x76), // 11 #687076 low-contrast text
        Rgb(0x11, 0x18, 0x1C), // 12 #11181c high-contrast text (near-black ink)
    });

    // ACCENT = Radix Indigo (light): step_9 #3e63dd is an electric Miro-blue (drives accent / focus
    // borders / Card-chrome accent / text-icon accent / PlayerColors[0] / syntax keyword+type).
    public static ColorScale AccentLight() => new ColorScale(new[]
    {
        Rgb(0xFD, 0xFD, 0xFE), //  1 #fdfdfe
        Rgb(0xF8, 0xFA, 0xFF), //  2 #f8faff
        Rgb(0xF0, 0xF4, 0xFF), //  3 #f0f4ff
        Rgb(0xE6, 0xED, 0xFE), //  4 #e6edfe
        Rgb(0xD9, 0xE2, 0xFC), //  5 #d9e2fc
        Rgb(0xC6, 0xD4, 0xF9), //  6 #c6d4f9 subtle border
        Rgb(0xAE, 0xC0, 0xF5), //  7 #aec0f5 ui border
        Rgb(0x8D, 0xA4, 0xEF), //  8 #8da4ef strong border
        Rgb(0x3E, 0x63, 0xDD), //  9 #3e63dd brand solid (electric Miro-blue)
        Rgb(0x33, 0x58, 0xD4), // 10 #3358d4 solid hover
        Rgb(0x3A, 0x5B, 0xC7), // 11 #3a5bc7 low-contrast text
        Rgb(0x1F, 0x2D, 0x5C), // 12 #1f2d5c high-contrast text
    });

    public static ColorScale RedLight() => new ColorScale(new[]
    {
        Rgb(0xFF, 0xFC, 0xFC), //  1 #fffcfc
        Rgb(0xFF, 0xF8, 0xF8), //  2 #fff8f8
        Rgb(0xFF, 0xEF, 0xEF), //  3 #ffefef
        Rgb(0xFF, 0xE5, 0xE5), //  4 #ffe5e5
        Rgb(0xFD, 0xD8, 0xD8), //  5 #fdd8d8
        Rgb(0xF9, 0xC6, 0xC6), //  6 #f9c6c6
        Rgb(0xF3, 0xAE, 0xAF), //  7 #f3aeaf
        Rgb(0xEB, 0x90, 0x91), //  8 #eb9091
        Rgb(0xE5, 0x48, 0x4D), //  9 #e5484d brand solid
        Rgb(0xDC, 0x3D, 0x43), // 10 #dc3d43
        Rgb(0xCD, 0x2B, 0x31), // 11 #cd2b31 low-contrast text
        Rgb(0x38, 0x13, 0x16), // 12 #381316 high-contrast text
    });

    public static ColorScale GreenLight() => new ColorScale(new[]
    {
        Rgb(0xFB, 0xFE, 0xFB), //  1 #fbfefb
        Rgb(0xF3, 0xFC, 0xF3), //  2 #f3fcf3
        Rgb(0xEB, 0xF9, 0xEB), //  3 #ebf9eb
        Rgb(0xDF, 0xF3, 0xDF), //  4 #dff3df
        Rgb(0xCE, 0xEB, 0xCF), //  5 #ceebcf
        Rgb(0xB7, 0xDF, 0xBA), //  6 #b7dfba
        Rgb(0x97, 0xCF, 0x9C), //  7 #97cf9c
        Rgb(0x65, 0xBA, 0x75), //  8 #65ba75
        Rgb(0x46, 0xA7, 0x58), //  9 #46a758 brand solid (grass)
        Rgb(0x3D, 0x9A, 0x50), // 10 #3d9a50
        Rgb(0x29, 0x7C, 0x3B), // 11 #297c3b low-contrast text
        Rgb(0x1B, 0x31, 0x1E), // 12 #1b311e high-contrast text
    });

    public static ColorScale YellowLight() => new ColorScale(new[]
    {
        Rgb(0xFE, 0xFD, 0xFB), //  1 #fefdfb
        Rgb(0xFF, 0xF9, 0xED), //  2 #fff9ed
        Rgb(0xFF, 0xF4, 0xD5), //  3 #fff4d5
        Rgb(0xFF, 0xEC, 0xBC), //  4 #ffecbc
        Rgb(0xFF, 0xE3, 0xA2), //  5 #ffe3a2
        Rgb(0xFF, 0xD3, 0x86), //  6 #ffd386
        Rgb(0xF3, 0xBA, 0x63), //  7 #f3ba63
        Rgb(0xEE, 0x9D, 0x2B), //  8 #ee9d2b
        Rgb(0xFF, 0xB2, 0x24), //  9 #ffb224 brand solid (amber)
        Rgb(0xFF, 0xA0, 0x1C), // 10 #ffa01c
        Rgb(0xAD, 0x57, 0x00), // 11 #ad5700 low-contrast text (dark amber for light bg legibility)
        Rgb(0x4E, 0x20, 0x09), // 12 #4e2009 high-contrast text
    });

    public static ColorScale BlueLight() => new ColorScale(new[]
    {
        Rgb(0xFB, 0xFD, 0xFF), //  1 #fbfdff
        Rgb(0xF5, 0xFA, 0xFF), //  2 #f5faff
        Rgb(0xED, 0xF6, 0xFF), //  3 #edf6ff
        Rgb(0xE1, 0xF0, 0xFF), //  4 #e1f0ff
        Rgb(0xCE, 0xE7, 0xFE), //  5 #cee7fe
        Rgb(0xB7, 0xD9, 0xF8), //  6 #b7d9f8
        Rgb(0x96, 0xC7, 0xF2), //  7 #96c7f2
        Rgb(0x5E, 0xB0, 0xEF), //  8 #5eb0ef
        Rgb(0x00, 0x91, 0xFF), //  9 #0091ff brand solid (info blue)
        Rgb(0x00, 0x81, 0xF1), // 10 #0081f1
        Rgb(0x00, 0x6A, 0xDC), // 11 #006adc low-contrast text
        Rgb(0x00, 0x25, 0x4D), // 12 #00254d high-contrast text
    });
}
