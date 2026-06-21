// HudGridBackground.cs — cyan-HUD re-skin 2026-06-21 (workspace grid field)
//
// The faint cyan GRID that overlays the infinite-canvas field (the Viewport background), giving the
// workspace the "HUD screen" backdrop from the owner's reference art. It is a fixed screen-space
// RawImage child of the Viewport, placed as the FIRST sibling so it sits ABOVE the solid
// workspace_background fill but BEHIND Content (and every floating window, which live under
// Content). It does NOT pan with the infinite canvas — the grid is a static instrument-panel field
// behind the moving windows, matching the reference.
//
// The grid texture is generated once (a single tiled cell: 1px cyan lines on two edges, the rest
// transparent), tinted via RawImage.color and tiled by HudGridTiler so cells stay a fixed pixel
// size regardless of viewport size. Idempotent (find-or-create by child name) and null-safe.

using UnityEngine;
using UnityEngine.UI;

public static class HudGridBackground
{
    const string GridName = "HudGrid";
    const int CellPixels = 36;   // grid cell size in screen pixels

    static Texture2D _cellTex;   // shared, generated once (domain-reload reset between Plays)

    // Ensure a grid field exists on `viewport`, tinted from the active theme accent (faint cyan).
    public static void Ensure(RectTransform viewport)
    {
        if (viewport == null) return;
        var a = ThemeService.Current.colors.accent;
        Ensure(viewport, new Color(a.r, a.g, a.b, 0.07f));   // very faint — a backdrop, not a foreground
    }

    public static void Ensure(RectTransform viewport, Color lineColor)
    {
        if (viewport == null) return;

        var existing = FindChild(viewport, GridName);
        RawImage raw;
        if (existing != null)
        {
            raw = existing.GetComponent<RawImage>();
            if (raw == null) return;
        }
        else
        {
            var go = new GameObject(GridName, typeof(RectTransform), typeof(RawImage), typeof(HudGridTiler));
            var rt = (RectTransform)go.transform;
            rt.SetParent(viewport, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.SetAsFirstSibling();   // above the viewport fill, behind Content + windows
            raw = go.GetComponent<RawImage>();
            raw.raycastTarget = false;
            go.GetComponent<HudGridTiler>().cellPixels = CellPixels;
        }

        raw.texture = CellTexture();
        raw.color = lineColor;
        raw.GetComponent<HudGridTiler>()?.Apply();
    }

    static Texture2D CellTexture()
    {
        if (_cellTex != null) return _cellTex;

        const int s = CellPixels;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            name = "HudGridCell",
        };
        var clear = new Color(1f, 1f, 1f, 0f);
        var line = Color.white;   // white in the texture; RawImage.color does the cyan tint
        var px = new Color[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                px[y * s + x] = (x == 0 || y == 0) ? line : clear;   // 1px lines on left + bottom edges
        tex.SetPixels(px);
        tex.Apply(false, false);
        _cellTex = tex;
        return tex;
    }

    static RectTransform FindChild(RectTransform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c.name == name) return c as RectTransform;
        }
        return null;
    }
}

// Keeps the grid cells a FIXED pixel size by re-deriving the RawImage.uvRect tiling from the live
// rect size whenever the Viewport resizes (window / canvas-scaler changes). Without this the cells
// would stretch with the viewport.
[RequireComponent(typeof(RawImage))]
public sealed class HudGridTiler : MonoBehaviour
{
    public int cellPixels = 36;
    RawImage _raw;
    RectTransform _rt;

    void OnEnable() { Apply(); }
    void OnRectTransformDimensionsChange() { Apply(); }

    public void Apply()
    {
        if (_raw == null) _raw = GetComponent<RawImage>();
        if (_rt == null) _rt = (RectTransform)transform;
        if (_raw == null || _rt == null || cellPixels <= 0) return;
        var size = _rt.rect.size;
        _raw.uvRect = new Rect(0f, 0f, size.x / cellPixels, size.y / cellPixels);
    }
}
