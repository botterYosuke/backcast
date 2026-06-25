// FloatingWindowEjectHandle.cs — issue #136 Slice 3 / ADR-0029 §3 / findings 0106 §1
//
// The shared builder for the title bar's always-visible "⤴" EJECT HANDLE — the VISIBLE affordance that
// engages the SingleWindowPickup gesture channel (Alt+drag is the keyboard shortcut for the same channel).
// ADR-0029 makes the pickup a first-class, discoverable gesture instead of an invisible distance/modifier
// mode (owner unhappiness 2: "invisible mode" critique). ONE builder so every title bar (dock / editor /
// order / HITL) grows an identical handle; FloatingWindowTitleInput.Awake attaches it, and the AFK gate
// drives Attach directly on a bare title bar to pin the structural contract (named, raycast target, no
// drag handler, top-left) without play mode.
//
// RAYCAST MODEL (the channel discriminator): the handle is a 2nd raycast-target Image with NO IDragHandler,
// so a press/drag on it BUBBLES to the title bar's FloatingWindowTitleInput (the nearest drag handler) while
// eventData.pointerPressRaycast.gameObject still points at THIS handle — OnBeginDrag reads that to pick
// SingleWindowPickup. The title text is raycastTarget=false (it never blocks), and the handle is the LAST
// sibling (drawn on top), so the small top-left region reliably engages pickup while the rest of the title
// bar engages IslandMove.

using UnityEngine;
using UnityEngine.UI;

public static class FloatingWindowEjectHandle
{
    public const string NodeName = "EjectHandle";
    public const string Glyph = "⤴";   // "⤴" arrow-pointing-rightwards-then-curving-upwards
    public const float Size = 22f;
    // Inset from the title bar's LEFT edge. The handle lives at the LEFT (like a window grip) — NOT the right —
    // because the right edge is a crowded RAYCAST cluster on editor/order windows (close "✕" at -3, cell run
    // "▶" at ~-28, both raycast Buttons appended AFTER this Awake-created handle), where a right-anchored chip
    // would be buried under a later sibling and the press would fall through to IslandMove. The left edge holds
    // only the title Text, which is raycastTarget=false (no raycast contest — at worst a cosmetic overlap of
    // the first glyph, which owner HITL tunes — ADR-0029 §自己保護).
    public const float LeftInset = 4f;

    // Attach an eject handle to `titleBar` (top-left) and return its GameObject — find-or-create by NodeName
    // (idempotent: a title bar that already carries an EjectHandle child is not given a second one). `font`
    // may be null (a builtin legacy font is used). The handle is subtle but always legible — a faint chip
    // with the glyph.
    public static GameObject Attach(RectTransform titleBar, Font font)
    {
        if (titleBar == null) return null;
        var existing = titleBar.Find(NodeName);
        if (existing != null) return existing.gameObject;   // already attached — do not duplicate

        var go = new GameObject(NodeName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(titleBar, false);
        // Anchor to the title bar's LEFT edge, vertically centred — independent of the title bar height
        // (dock = 28px, HITL differs), so the handle sits consistently at the top-left corner.
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(LeftInset, 0f);
        rt.sizeDelta = new Vector2(Size, Size);

        var img = go.GetComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.16f);   // faint chip behind the glyph
        img.raycastTarget = true;                    // THE 2nd raycast target — its press → SingleWindowPickup

        var glyphGo = new GameObject("EjectGlyph", typeof(RectTransform), typeof(Text));
        var grt = (RectTransform)glyphGo.transform;
        grt.SetParent(rt, false);
        grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one;
        grt.offsetMin = Vector2.zero; grt.offsetMax = Vector2.zero;
        var t = glyphGo.GetComponent<Text>();
        t.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = Glyph;
        t.fontSize = 15;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.raycastTarget = false;   // the glyph never blocks — the chip Image is the single raycast target

        go.transform.SetAsLastSibling();   // drawn on top of the title text, so its press wins the raycast
        return go;
    }
}
