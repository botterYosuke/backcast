// CardFrameChrome.cs — ADR-0028 (Miro-風 whiteboard card chrome)
//
// The LIGHT-theme counterpart to HudFrameChrome: instead of the dark cyan corner-bracket / edge-glow HUD,
// a floating window reads as a soft Miro CARD — rounded corners + a drop shadow. Applied/removed by
// WindowChrome.Apply on the appearance switch (HUD ⇔ Card is a STRUCTURAL swap, not a recolor — findings
// 0103 D5). Purely cosmetic. Idempotent and null-safe so a re-apply (live theme switch) never stacks.
//
// Rounded corners: the window root Image gets the shared RoundedRectSprite (9-slice). The title bar gets it
// too so the visible top edge is rounded; the body is inset a few px, so the root's rounded bottom corners
// show through as the card's rounded bottom. NO RectMask2D / stencil Mask (would clip chart/ladder meshes
// and fight the canvas parallax) — the rounded silhouette comes from the sprite alpha alone.
// Drop shadow: a UnityEngine.UI.Shadow effect on the root Image (soft offset, follows the rounded mesh).

using UnityEngine;
using UnityEngine.UI;

public static class CardFrameChrome
{
    static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.20f);
    static readonly Vector2 ShadowOffset = new Vector2(2f, -3f);

    // True when `root` is already wearing the Card chrome (rounded root sprite). Lets WindowChrome.Apply
    // skip a redundant teardown+rebuild when the appearance hasn't actually changed (no per-Changed churn).
    public static bool IsDecorated(RectTransform root)
    {
        var img = root != null ? root.GetComponent<Image>() : null;
        return img != null && img.sprite != null;
    }

    // Make `root` read as a Miro card: rounded root + rounded title bar + drop shadow.
    public static void Decorate(RectTransform root)
    {
        if (root == null) return;

        Round(root.GetComponent<Image>());
        var titleBar = root.Find("TitleBar") as RectTransform;
        if (titleBar != null) Round(titleBar.GetComponent<Image>());

        // drop shadow on the root Image (soft offset copy of the rounded mesh).
        var rootImg = root.GetComponent<Image>();
        if (rootImg != null)
        {
            var sh = root.GetComponent<Shadow>();
            if (sh == null) sh = root.gameObject.AddComponent<Shadow>();
            sh.effectColor = ShadowColor;
            sh.effectDistance = ShadowOffset;
            sh.useGraphicAlpha = true;
        }
    }

    // Revert `root` back to a flat sharp-cornered panel (the dark HUD theme paints over this).
    public static void Remove(RectTransform root)
    {
        if (root == null) return;

        Unround(root.GetComponent<Image>());
        var titleBar = root.Find("TitleBar") as RectTransform;
        if (titleBar != null) Unround(titleBar.GetComponent<Image>());

        var sh = root.GetComponent<Shadow>();
        if (sh != null) DestroyCompat(sh);
    }

    static void Round(Image img)
    {
        if (img == null) return;
        img.sprite = RoundedRectSprite.Get();
        img.type = Image.Type.Sliced;
    }

    static void Unround(Image img)
    {
        if (img == null) return;
        img.sprite = null;
        img.type = Image.Type.Simple;
    }

    static void DestroyCompat(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }
}
