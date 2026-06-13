// LayoutBinder.cs — issue #12 "Replay layout" (DURABLE tier)
//
// The UI<->document conversion layer. CALLED "layout binder", NOT "adapter":
// CONTEXT.md reserves "adapter" for the engine/pythonnet boundary; this is a
// different seam (UI <-> LayoutDocument) and must not collide with that glossary
// term (findings §4). Two entry points: Capture (live -> document) and Apply
// (document -> live).
//
// TWO LAYERS (findings §4, owner-locked):
//   * PURE CORE — float-only math (parent size + anchor + offset <-> normalized
//     display rect). NO RectTransform resolution, NO Canvas/layout pass, so the AFK
//     probe exercises it headless and deterministically.
//   * UNITY BOUNDARY — the thin part that walks RectTransforms, matches panels by
//     id, and reads/writes anchorMin/Max + offsetMin/Max. Parent size is passed in
//     EXPLICITLY (not read from a resolved .rect) so the boundary stays headless-
//     testable too.
//
// NORMALIZED DISPLAY RECT (findings §3): the rect is the panel's displayed box as a
// fraction of its parent — NOT the raw anchor. uGUI corner math:
//     cornerMin = anchorMin * parentSize + offsetMin
//     cornerMax = anchorMax * parentSize + offsetMax
// Capture normalizes those corners by parentSize. Apply uses the CANONICAL inverse:
// anchorMin/Max = the normalized corners, offsets = 0. Many anchor/offset pairs map
// to one display rect; the canonical pure-anchor form reproduces the SAME displayed
// box (capability parity = same UI state, not byte-identical anchors). This also
// makes restore resolution-independent.
//
// id TOLERANCE on Apply (findings §6c): a doc panel whose id has no live target is
// SKIPPED; a live panel absent from the doc is LEFT UNTOUCHED.

using System.Collections.Generic;
using UnityEngine;

public static class LayoutBinder
{
    // A live panel to capture: its stable id + logical slot + visibility + transform.
    public struct PanelBinding
    {
        public string id;
        public int slot;
        public bool visible;
        public RectTransform rt;

        public PanelBinding(string id, int slot, bool visible, RectTransform rt)
        {
            this.id = id;
            this.slot = slot;
            this.visible = visible;
            this.rt = rt;
        }
    }

    // ---------------- PURE CORE (float-only; headless, no layout pass) ----------------

    // live anchor+offset -> normalized display rect, against an explicit parent size.
    public static LayoutRect ToNormalizedRect(
        float parentW, float parentH,
        float anchorMinX, float anchorMinY, float anchorMaxX, float anchorMaxY,
        float offsetMinX, float offsetMinY, float offsetMaxX, float offsetMaxY)
    {
        if (parentW <= 0f || parentH <= 0f)
            throw new System.ArgumentOutOfRangeException(
                nameof(parentW), "parent size must be > 0 (got " + parentW + "x" + parentH + ")");

        return new LayoutRect(
            (anchorMinX * parentW + offsetMinX) / parentW,
            (anchorMinY * parentH + offsetMinY) / parentH,
            (anchorMaxX * parentW + offsetMaxX) / parentW,
            (anchorMaxY * parentH + offsetMaxY) / parentH);
    }

    // ---------------- UNITY BOUNDARY (RectTransform <-> document) ----------------

    // live -> document. Parent size passed explicitly (NOT from a resolved .rect).
    public static LayoutDocument Capture(float parentW, float parentH, IEnumerable<PanelBinding> bindings)
    {
        var doc = new LayoutDocument { version = LayoutDocument.CURRENT_VERSION, panels = new List<PanelLayout>() };
        if (bindings == null) return doc;

        foreach (var b in bindings)
        {
            if (b.rt == null || string.IsNullOrEmpty(b.id)) continue;
            Vector2 aMin = b.rt.anchorMin, aMax = b.rt.anchorMax;
            Vector2 oMin = b.rt.offsetMin, oMax = b.rt.offsetMax;
            LayoutRect rect = ToNormalizedRect(
                parentW, parentH,
                aMin.x, aMin.y, aMax.x, aMax.y,
                oMin.x, oMin.y, oMax.x, oMax.y);
            doc.panels.Add(new PanelLayout(b.id, b.slot, b.visible, rect));
        }
        return doc;
    }

    // document -> live. Matches by id. Applies the SPATIAL state — canonical anchors
    // (offsets zeroed) — and VISIBILITY (SetActive). `slot` is deliberately NOT applied
    // to a transform here: it is LOGICAL-ordering metadata (findings §3) that round-
    // trips at the document level; for the current non-overlapping stacked panels it
    // has no live manifestation, and its UI interpretation (draw/z order, tile order)
    // is a shell-slice concern (#7). Unknown ids skipped; live targets absent from the
    // doc are LEFT UNTOUCHED.
    public static void Apply(LayoutDocument doc, IDictionary<string, RectTransform> liveById)
    {
        if (doc == null || doc.panels == null || liveById == null) return;

        foreach (var p in doc.panels)
        {
            if (p == null || string.IsNullOrEmpty(p.id) || p.rect == null) continue;
            if (!liveById.TryGetValue(p.id, out RectTransform rt) || rt == null) continue; // unknown id -> skip

            // CANONICAL inverse of ToNormalizedRect: anchors = the normalized rect
            // corners, offsets = 0 (reproduces the SAME displayed box; resolution-independent).
            rt.anchorMin = new Vector2(p.rect.minX, p.rect.minY);
            rt.anchorMax = new Vector2(p.rect.maxX, p.rect.maxY);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            rt.gameObject.SetActive(p.visible);
        }
    }
}
