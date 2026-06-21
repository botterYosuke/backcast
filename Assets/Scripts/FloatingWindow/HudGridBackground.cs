// HudGridBackground.cs — cyan-HUD re-skin 2026-06-21 (workspace grid field)
//
// The faint cyan GRID that backs the infinite-canvas workspace, giving it the "HUD screen" look
// from the owner's reference art. It is a child of CONTENT (the infinite-canvas transform), placed
// as the FIRST sibling so it sits BEHIND the dock layer + every floating window. Because Content
// owns the pan (anchoredPosition) and zoom (localScale), a grid parented under Content pans AND
// zooms by EXACTLY the same amount as the dock windows ride it (1.0× plane) — drag the canvas and
// the grid slides with the windows; zoom and the cells grow/shrink with them. (The earlier build
// parented it to the Viewport, which pinned it to the screen — owner-rejected 2026-06-21.)
//
// It is a single large quad (GridExtent²) tiled by the texture's Repeat wrap, so the whole
// "infinite" field is one draw call — only the part inside the Viewport's RectMask2D rasterises.
// The grid texture is generated once (a single cell: 1px cyan lines on two edges, the rest
// transparent) and tinted via RawImage.color. Idempotent (find-or-create) and null-safe.

using UnityEngine;
using UnityEngine.UI;

public static class HudGridBackground
{
    const string GridName = "HudGrid";
    const int CellPixels = 36;       // grid cell size in CONTENT-local units (scales with zoom)
    const float GridExtent = 20000f; // quad side — large enough to cover any realistic pan/zoom-out

    static Texture2D _cellTex;   // shared, generated once (domain-reload reset between Plays)

    // Ensure a grid field exists under `content`, tinted from the active theme accent (faint cyan).
    public static void Ensure(RectTransform content)
    {
        if (content == null) return;
        var a = ThemeService.Current.colors.accent;
        Ensure(content, new Color(a.r, a.g, a.b, 0.07f));   // very faint — a backdrop, not a foreground
    }

    public static void Ensure(RectTransform content, Color lineColor)
    {
        if (content == null) return;

        var existing = FindChild(content, GridName);
        RawImage raw;
        if (existing != null)
        {
            raw = existing.GetComponent<RawImage>();
            if (raw == null) return;
        }
        else
        {
            var go = new GameObject(GridName, typeof(RectTransform), typeof(RawImage));
            var rt = (RectTransform)go.transform;
            rt.SetParent(content, false);
            // centered on Content's origin so it pans/zooms with the canvas (Content is pivot-centred).
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(GridExtent, GridExtent);
            rt.SetAsFirstSibling();   // behind the dock layer + floating windows
            raw = go.GetComponent<RawImage>();
            raw.raycastTarget = false;
            raw.uvRect = new Rect(0f, 0f, GridExtent / CellPixels, GridExtent / CellPixels);  // world-fixed tiling
        }

        raw.texture = CellTexture();
        raw.color = lineColor;
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
