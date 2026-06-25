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

    static Texture2D _lineTex;   // dark HUD: 1px cell lines (generated once; domain-reload reset)
    static Texture2D _dotTex;    // light Miro: a dot per cell corner (generated once)

    // Ensure a grid field exists under `content`, styled by the active Appearance (ADR-0028): the dark HUD
    // uses faint cyan LINES; the light Miro whiteboard uses faint grey DOTS. Re-callable on a theme switch —
    // it swaps both the texture and the tint, so the field morphs live with the rest of the canvas.
    public static void Ensure(RectTransform content)
    {
        if (content == null) return;
        if (ThemeService.Current.appearance == Appearance.Light)
        {
            // grey dots on the off-white field — Miro's signature dotted board.
            Ensure(content, new Color(0.45f, 0.48f, 0.52f, 0.55f), dotted: true);
        }
        else
        {
            var a = ThemeService.Current.colors.accent;
            Ensure(content, new Color(a.r, a.g, a.b, 0.07f), dotted: false);   // very faint cyan backdrop
        }
    }

    public static void Ensure(RectTransform content, Color tint, bool dotted)
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

        raw.texture = CellTexture(dotted);   // swap on a live style switch (lines ⇔ dots)
        raw.color = tint;
    }

    static Texture2D CellTexture(bool dotted)
    {
        if (dotted)
        {
            if (_dotTex != null) return _dotTex;
            _dotTex = BuildCell(dotted: true, name: "MiroGridDot");
            return _dotTex;
        }
        if (_lineTex != null) return _lineTex;
        _lineTex = BuildCell(dotted: false, name: "HudGridCell");
        return _lineTex;
    }

    static Texture2D BuildCell(bool dotted, string name)
    {
        const int s = CellPixels;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = dotted ? FilterMode.Bilinear : FilterMode.Point,
            name = name,
        };
        var clear = new Color(1f, 1f, 1f, 0f);
        var mark = Color.white;   // white in the texture; RawImage.color tints it
        var px = new Color[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                bool on = dotted
                    ? (x <= 1 && y <= 1)            // a 2x2 dot at the cell corner (Miro dotted board)
                    : (x == 0 || y == 0);           // 1px lines on left + bottom edges (HUD grid)
                px[y * s + x] = on ? mark : clear;
            }
        tex.SetPixels(px);
        tex.Apply(false, false);
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
