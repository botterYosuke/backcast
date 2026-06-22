// DragGhostLayer.cs — issue #104 Slice G / ADR-0024 §7 (puzzle-feel drag)
//
// The drag-time PREVIEW overlay. Under ADR-0024 it is used by the SWAP mode ONLY: 2 ghosts (the dragged
// at the target's slot + the target at the dragged's slot) show the would-be (x,y,w,h) exchange. Translate
// and Detach REAL-RENDER the live island / dragged window (no ghost — the live geometry IS the preview).
// Commit-on-release discipline: the ghost is a PREVIEW only; groupId is untouched until release.
//
// LAYER MODEL: the ghost container is a child RectTransform on the same plane as the windows it
// previews (the controller owns the layer; the root creates one DragGhostLayer per plane and binds
// it to that plane's controller via `AttachGhostLayer`). During a drag the layer is brought to the
// last sibling slot so ghosts draw in FRONT of the real windows; ghosts never intercept input
// (CanvasGroup.blocksRaycasts = false), so dragging a ghost feels exactly like dragging the
// real window beneath it.
//
// POOLING: ghost GameObjects are re-used across frames (pool grows monotonically). Inactive ghosts
// stay deactivated; ActiveCount is the live ghost prefix. The pool is small in practice — at most
// 2 ghosts per drag (Swap), 0 for Translate / Detach (real-render, no ghost).
//
// VISUAL CONTRACT (findings 0082 §8): alpha = 0.45, kind accent border. Dragged ghost = SOLID border
// (1 px); target ghost = DASHED border (1 px). The dashed/solid distinction is part of the spec —
// it tells the user which ghost belongs to their pointer (solid = "you") vs which belongs to the
// drop target (dashed = "the other"). uGUI has no native dashed renderer; the production visual is
// a future refinement (HITL covers the look). For the AFK gate the structural distinction is
// encoded on the ghost's GameObject name (`GhostWindow_Solid` / `GhostWindow_Dashed`) AND in the
// per-spec `style` field, so a probe can pin "2 ghosts, one solid one dashed" without rendering.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DragGhostLayer
{
    // #104 (ADR-0019 / findings 0082 §8 owner-locked): the half-transparent feel for a "would-go-here"
    // preview. Lower values disappear into the background; higher values feel too solid against the
    // live window below. 0.45 reads as "this is a phantom" without losing edge legibility (HITL tuned).
    public const float ALPHA = 0.45f;

    // #104 (ADR-0019 / findings 0082 §8): the dragged ghost wears a SOLID border (it's "you") and the
    // target ghost wears a DASHED border (it's "the other window that would receive the swap").
    public enum GhostStyle { Solid, Dashed }

    // A single ghost rendering: a window-sized phantom at a canvas-logical top-left position with the
    // catalog accent for `kind` and the style flag. Pure POCO — assembled by the controller's
    // ComposeSwapGhosts and handed to Render(). Swap paints 2 ghosts per frame.
    public struct GhostSpec
    {
        public string kind;          // accent color source (FloatingWindowCatalog.TryGet)
        public Vector2 topLeft;      // canvas-logical top-left (matches FloatingWindowController layer convention)
        public Vector2 size;
        public GhostStyle style;     // Solid = dragged ghost; Dashed = target ghost
    }

    readonly RectTransform _container;
    readonly FloatingWindowCatalog _catalog;
    readonly Func<RectTransform> _factory;             // bare-RectTransform mint for the AFK probe; production uses CreateUguiGhost
    readonly Action<GameObject> _destroy;
    readonly List<RectTransform> _pool = new List<RectTransform>();

    public int ActiveCount { get; private set; }
    public RectTransform Container => _container;
    public RectTransform GhostAt(int i) => (i >= 0 && i < ActiveCount) ? _pool[i] : null;

    // Production constructor — pool ghosts are real uGUI Images with a CanvasGroup that disables
    // raycasting (blocksRaycasts=false). The container should be a child of the plane's layer.
    public DragGhostLayer(RectTransform container, FloatingWindowCatalog catalog)
        : this(container, catalog, null, null) { }

    // AFK-friendly overload: the probe injects a bare-RectTransform factory + DestroyImmediate destroy
    // (no Canvas / no Image), so the structural gate (count / rect / style flag / sibling order /
    // blocksRaycasts) runs headlessly. `factory` is the per-ghost mint; null defaults to a production
    // uGUI ghost (Image + CanvasGroup + raycast off).
    public DragGhostLayer(RectTransform container, FloatingWindowCatalog catalog, Func<RectTransform> factory, Action<GameObject> destroy)
    {
        _container = container != null ? container : throw new ArgumentNullException(nameof(container));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _factory = factory ?? CreateUguiGhost;
        _destroy = destroy ?? (go => { if (go != null) UnityEngine.Object.Destroy(go); });
    }

    // Materialise the given ghost specs as live RectTransforms under the container. Pool grows as
    // needed; surplus ghosts are deactivated (never destroyed mid-drag). The container is raised to
    // the LAST sibling so ghosts draw in FRONT of the real windows on this plane (findings 0082 §8).
    // Pass null / empty to clear the previous frame's ghosts (Clear() is the explicit shortcut).
    public void Render(IList<GhostSpec> ghosts)
    {
        int n = ghosts?.Count ?? 0;
        while (_pool.Count < n) _pool.Add(MintGhost());
        // F5 (#104 defensive): MintGhost may return null when an injected factory returns null
        // (the AFK probe corner case). The deactivate loop below already guards; do the same here
        // so a null pool slot does not NRE in ApplyGhost.
        for (int i = 0; i < n; i++) if (_pool[i] != null) ApplyGhost(_pool[i], ghosts[i]);
        for (int i = n; i < _pool.Count; i++)
            if (_pool[i] != null) _pool[i].gameObject.SetActive(false);
        ActiveCount = n;
        if (n > 0 && _container.parent != null) _container.SetAsLastSibling();
    }

    public void Clear() => Render(null);

    RectTransform MintGhost()
    {
        var rt = _factory();
        if (rt == null) return null;
        if (rt.parent != _container) rt.SetParent(_container, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, 1f);     // top-left — matches FloatingWindowController's canonical
        return rt;
    }

    void ApplyGhost(RectTransform rt, GhostSpec spec)
    {
        rt.anchoredPosition = spec.topLeft;
        rt.sizeDelta = spec.size;
        rt.gameObject.SetActive(true);
        // Style flag encoded on the name so the AFK gate can structurally pin "solid / dashed without rendering".
        rt.gameObject.name = spec.style == GhostStyle.Solid ? "GhostWindow_Solid" : "GhostWindow_Dashed";
        var img = rt.GetComponent<Image>();
        if (img != null && _catalog.TryGet(spec.kind, out var s))
        {
            var c = s.accent; c.a = ALPHA;
            img.color = c;
        }
    }

    // Production ghost mint: an Image (rim accent) + CanvasGroup (alpha + raycast disable). The
    // dashed-vs-solid border distinction is intentionally NOT rendered here — HITL #104 G-13 covers
    // the visual; AFK pins the structural intent via GhostStyle on the spec + the encoded name.
    static RectTransform CreateUguiGhost()
    {
        var go = new GameObject("GhostWindow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        var rt = (RectTransform)go.transform;
        var cg = go.GetComponent<CanvasGroup>();
        cg.alpha = ALPHA;
        cg.blocksRaycasts = false;
        cg.interactable = false;
        return rt;
    }
}
