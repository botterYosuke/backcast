// FloatingWindowResizeHandle.cs — issue #139 / ADR-0030 §1/§6 / findings 0112
//
// The shared builder for the window root's always-visible "◢" RESIZE GRIP — the VISIBLE affordance that
// drives the bottom-right resize gesture. Mirrors FloatingWindowEjectHandle.Attach (the eject "⤴" chip):
// ONE find-or-create builder so EVERY floating window (editor / order / dock / HITL) grows an identical
// grip, and the AFK gate drives Attach directly on a bare window root to pin the structural contract
// (named, raycast target, OWN drag handler, last sibling, bottom-right) without play mode.
//
// WHY A SEPARATE SYSTEM (ADR-0030 §3): the grip is NOT a title-bar drag, so — UNLIKE the eject handle,
// which deliberately has NO drag handler and BUBBLES to the title bar's FloatingWindowTitleInput — the
// grip carries its OWN IDragHandler (FloatingWindowResizeGrip). Its press/drag is SWALLOWED by the grip
// and routed to the controller's resize session, so it NEVER enters ResolveChannel and the ADR-0029
// gesture-channel invariant is untouched. The chip is the LAST sibling (drawn on top, wins the raycast)
// and a raycast target, so the small bottom-right region reliably engages resize while the body/title and
// the canvas pan are unaffected.

using UnityEngine;
using UnityEngine.UI;

public static class FloatingWindowResizeHandle
{
    public const string NodeName = "ResizeGrip";
    public const string Glyph = "◢";   // "◢" black lower-right triangle — a corner resize affordance
    public const float Size = 18f;
    // Inset from the window root's BOTTOM-RIGHT corner. The grip lives at the corner the window grows from
    // (right + bottom edges move, ADR-0030 §1), so it sits exactly where the user expects to "pull" the size.
    public const float Inset = 2f;

    // Attach a resize grip to `windowRoot` (bottom-right) and return its GameObject — find-or-create by
    // NodeName (idempotent: a root that already carries a ResizeGrip child is not given a second one). The
    // chip carries a FloatingWindowResizeGrip (its OWN drag handler) + a raycast-target Image; the caller
    // (FloatingWindowTitleInput.Initialize) Initialize()s the grip with the controller/canvas/viewport/id.
    // `font` may be null (a builtin legacy font is used).
    public static GameObject Attach(RectTransform windowRoot, Font font)
    {
        if (windowRoot == null) return null;
        var existing = windowRoot.Find(NodeName);
        if (existing != null) return existing.gameObject;   // already attached — do not duplicate

        var go = new GameObject(NodeName, typeof(RectTransform), typeof(Image), typeof(FloatingWindowResizeGrip));
        var rt = (RectTransform)go.transform;
        rt.SetParent(windowRoot, false);
        // Anchor to the window root's BOTTOM-RIGHT corner, independent of the window size, so the grip
        // tracks the corner as the window grows/shrinks (the body stretch-anchors; this corner-anchors).
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-Inset, Inset);
        rt.sizeDelta = new Vector2(Size, Size);

        var img = go.GetComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.16f);   // faint chip behind the glyph (eject-handle parity)
        img.raycastTarget = true;                    // THE grip raycast target — its press drives the resize session

        var glyphGo = new GameObject("ResizeGlyph", typeof(RectTransform), typeof(Text));
        var grt = (RectTransform)glyphGo.transform;
        grt.SetParent(rt, false);
        grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one;
        grt.offsetMin = Vector2.zero; grt.offsetMax = Vector2.zero;
        var t = glyphGo.GetComponent<Text>();
        t.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = Glyph;
        t.fontSize = 13;
        t.color = Color.white;
        t.alignment = TextAnchor.LowerRight;
        t.raycastTarget = false;   // the glyph never blocks — the chip Image is the single raycast target

        go.transform.SetAsLastSibling();   // drawn on top of the body/chrome, so its press wins the raycast
        return go;
    }
}
