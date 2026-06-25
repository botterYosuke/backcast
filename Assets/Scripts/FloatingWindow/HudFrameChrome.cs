// HudFrameChrome.cs — cyan-HUD re-skin 2026-06-21 (shared window-frame decorator)
//
// The SINGLE source of the "heads-up display" chrome that wraps every floating window: a thin
// glowing cyan EDGE outline + four L-shaped CORNER BRACKETS (the signature sci-fi frame from the
// owner's reference HUD). One decorator so the editor / order / dock frames can't diverge — same
// discipline as the *WindowFrame builders. It is purely cosmetic (every Image is
// raycastTarget=false) and idempotent (find-or-create by child name), so it can be re-applied to
// an adopted scene window or a runtime spawn without stacking duplicate chrome.
//
// Colors are READ from the active ThemeService theme at decorate time (accent = cyan brand glow),
// matching how the *WindowFrame title literals are baked at build — a theme swap re-tints on the
// next frame build, not live (the windows are not per-window theme subscribers yet).

using UnityEngine;
using UnityEngine.UI;

public static class HudFrameChrome
{
    const string ChromeName = "HudChrome";

    const float BracketLen = 16f;   // arm length of each corner bracket
    const float Thick = 2f;         // bracket arm + edge line thickness
    const float EdgeInset = 0f;     // edges run flush to the window rect

    // Decorate using the active theme: bright cyan accent brackets + a dimmer cyan edge glow.
    public static void Decorate(RectTransform root)
    {
        var c = ThemeService.Current.colors;
        var bracket = c.accent;                                  // electric-cyan brand glow (Accent.Step9)
        var edge = new Color(c.accent.r, c.accent.g, c.accent.b, 0.45f);  // same hue, faint glow
        Decorate(root, edge, bracket);
    }

    // Decorate `root` with HUD chrome. Idempotent: an existing "HudChrome" child is cleared and
    // rebuilt so a re-tint (theme swap) lands without duplicating GameObjects. Null-safe.
    public static void Decorate(RectTransform root, Color edgeColor, Color bracketColor)
    {
        if (root == null) return;

        // find-or-create the chrome container (stretched to the whole window, drawn ON TOP of the
        // frame's own background / title bar / body since it is the last sibling).
        var existing = FindChild(root, ChromeName);
        RectTransform chrome;
        if (existing != null)
        {
            chrome = existing;
            for (int i = chrome.childCount - 1; i >= 0; i--)
            {
                var child = chrome.GetChild(i).gameObject;
                if (Application.isPlaying) Object.Destroy(child);
                else Object.DestroyImmediate(child);   // edit-time re-author can't use Destroy
            }
        }
        else
        {
            var go = new GameObject(ChromeName, typeof(RectTransform));
            chrome = (RectTransform)go.transform;
            chrome.SetParent(root, false);
        }
        chrome.anchorMin = Vector2.zero; chrome.anchorMax = Vector2.one;
        chrome.offsetMin = Vector2.zero; chrome.offsetMax = Vector2.zero;
        chrome.SetAsLastSibling();   // frame sits above title bar + body

        // ── glowing edge outline (4 faint cyan lines flush to the rect) ──
        Edge(chrome, "EdgeTop",    new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(EdgeInset, -Thick),  new Vector2(-EdgeInset, 0f), edgeColor);
        Edge(chrome, "EdgeBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(EdgeInset, 0f),      new Vector2(-EdgeInset, Thick), edgeColor);
        Edge(chrome, "EdgeLeft",   new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(0f, EdgeInset),      new Vector2(Thick, -EdgeInset), edgeColor);
        Edge(chrome, "EdgeRight",  new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-Thick, EdgeInset),  new Vector2(0f, -EdgeInset), edgeColor);

        // ── four corner brackets (each = horizontal arm + vertical arm, anchored to its corner) ──
        Corner(chrome, "TL", new Vector2(0f, 1f), new Vector2(0f, 1f), bracketColor);
        Corner(chrome, "TR", new Vector2(1f, 1f), new Vector2(1f, 1f), bracketColor);
        Corner(chrome, "BL", new Vector2(0f, 0f), new Vector2(0f, 0f), bracketColor);
        Corner(chrome, "BR", new Vector2(1f, 0f), new Vector2(1f, 0f), bracketColor);
    }

    // True when `root` already wears the HUD chrome ("HudChrome" child). Lets WindowChrome.Apply skip a
    // redundant teardown+rebuild when the appearance hasn't actually changed (no per-Changed churn).
    public static bool IsDecorated(RectTransform root) => root != null && FindChild(root, ChromeName) != null;

    // Strip the HUD chrome (the "HudChrome" child) so the light Card chrome can take over on an appearance
    // switch (ADR-0028 / WindowChrome.Apply). Null-safe; a no-op if no HUD chrome is present.
    public static void Remove(RectTransform root)
    {
        if (root == null) return;
        var existing = FindChild(root, ChromeName);
        if (existing == null) return;
        if (Application.isPlaying) Object.Destroy(existing.gameObject);
        else Object.DestroyImmediate(existing.gameObject);
    }

    // One stretched edge line. anchor min/max + pivot place it on a side; offsetMin/offsetMax give
    // its thickness (the side that doesn't stretch).
    static void Edge(RectTransform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 pivot,
                     Vector2 offMin, Vector2 offMax, Color color)
    {
        var rt = NewImage(parent, name, color);
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        rt.offsetMin = offMin; rt.offsetMax = offMax;
    }

    // One corner bracket: an L made of a horizontal arm (BracketLen × Thick) and a vertical arm
    // (Thick × BracketLen), both pinned to the same corner anchor so they meet at the corner.
    static void Corner(RectTransform parent, string name, Vector2 anchor, Vector2 pivot, Color color)
    {
        var groupGo = new GameObject("Bracket_" + name, typeof(RectTransform));
        var group = (RectTransform)groupGo.transform;
        group.SetParent(parent, false);
        group.anchorMin = anchor; group.anchorMax = anchor; group.pivot = pivot;
        group.anchoredPosition = Vector2.zero;
        group.sizeDelta = new Vector2(BracketLen, BracketLen);

        var h = NewImage(group, "ArmH", color);
        h.anchorMin = anchor; h.anchorMax = anchor; h.pivot = pivot;
        h.anchoredPosition = Vector2.zero;
        h.sizeDelta = new Vector2(BracketLen, Thick);

        var v = NewImage(group, "ArmV", color);
        v.anchorMin = anchor; v.anchorMax = anchor; v.pivot = pivot;
        v.anchoredPosition = Vector2.zero;
        v.sizeDelta = new Vector2(Thick, BracketLen);
    }

    static RectTransform NewImage(RectTransform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;   // chrome never eats input (drag/click pass through to the frame)
        return rt;
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
