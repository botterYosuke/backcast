// RoundedRectSprite.cs — ADR-0028 (Miro-風 Card chrome)
//
// A runtime-generated rounded-rectangle 9-slice sprite, used by CardFrameChrome to give floating-window
// cards their Miro rounded corners under the Light theme. Generated ONCE and cached (domain reload resets
// it between Plays, like HudGridBackground._cellTex). White RGBA with an anti-aliased rounded-rect alpha;
// the consuming Image tints it via .color, and Image.type = Sliced + the sprite's border keeps the corner
// radius constant at any window size. Cosmetic only.

using UnityEngine;

public static class RoundedRectSprite
{
    const int Size = 24;     // texture side (px)
    const int Radius = 6;    // corner radius (px)

    static Sprite _sprite;

    // The shared rounded-rect sprite (9-slice border = Radius+2 so Sliced scaling never distorts the arc).
    public static Sprite Get()
    {
        if (_sprite != null) return _sprite;

        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "RoundedRectCard",
        };
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[y * Size + x] = new Color(1f, 1f, 1f, CornerAlpha(x, y));
        tex.SetPixels(px);
        tex.Apply(false, false);

        const float b = Radius + 2f;
        _sprite = Sprite.Create(tex, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f),
                                100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        return _sprite;
    }

    // Rounded-rect signed-distance alpha: a pixel is fully opaque inside the inset rect, fades over ~1px at
    // the rounded corners. cx/cy is the nearest point on the rect inset by Radius; the distance to it is the
    // corner arc radius for corner pixels and 0 for interior pixels.
    static float CornerAlpha(int x, int y)
    {
        float px = x + 0.5f, py = y + 0.5f;
        float cx = Mathf.Clamp(px, Radius, Size - Radius);
        float cy = Mathf.Clamp(py, Radius, Size - Radius);
        float dist = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        return Mathf.Clamp01(Radius - dist + 0.5f);
    }
}
