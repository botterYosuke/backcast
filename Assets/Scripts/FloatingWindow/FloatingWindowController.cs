// FloatingWindowController.cs — issue #15 "floating windows" (DURABLE tier, Unity boundary)
//
// The thin, input-agnostic boundary between FloatingWindowMath (pure) and the live window
// RectTransforms under the FloatingWindowLayer — the analogue of #13's InfiniteCanvasController
// and #14's HakoniwaController. A PLAIN C# class (NOT a MonoBehaviour) so the AFK probe can
// drive it against headless RectTransforms. It NEVER reads input (that is FloatingWindowTitleInput)
// and NEVER sanitizes the on-disk document (that is LayoutStore) and NEVER converts screen/render
// coordinates (the input boundary hands it a canvas-LOGICAL delta).
//
// LAYER MODEL (findings 0008 §1/§3, owner-locked): every window is a child of a single
// FloatingWindowLayer (itself a child of the infinite-canvas Content). The layer sits at the
// origin when centred, but the InfiniteCanvasController parallax-shifts its anchoredPosition off
// Content as you pan (the depth cue that floats windows IN FRONT of HakoniwaRoot); this controller
// still owns ONLY each window's position WITHIN the layer, in layer-local coords. So:
//   * pan/zoom follow is FREE — the layer rides Content (proven by #13's child-follow), and the
//     controller only owns each window's position WITHIN the layer.
//   * z-order is the window's SIBLING INDEX within the layer (uGUI draws child 0 = backmost),
//     kept independent of HakoniwaRoot. BringToFront = SetAsLastSibling (the capability-parity
//     analogue of TTWR's WindowManager.max_z bump).
//
// COORDINATE CONTRACT (findings 0008 §2): a window's RectTransform is anchored+pivoted as
// anchorMin=anchorMax=(0.5,0.5), pivot=(0,1) (top-left), so anchoredPosition is the TOP-LEFT
// corner in canvas-logical coords (x right+, y up+) and sizeDelta is its logical px size.
//
// WINDOW CONSTRUCTION is INJECTED (the `factory`): the controller owns placement / z-order /
// capture / apply, but the visual hierarchy (title bar + body + title input wiring, or, in the
// AFK probe, a bare RectTransform) is built by the caller's factory and returned as the root.
// Likewise removal is injected (`destroy`) so the runtime uses Object.Destroy while the edit-
// mode probe uses Object.DestroyImmediate (Destroy is a no-op/warns outside playmode).

using System;
using System.Collections.Generic;
using UnityEngine;

public class FloatingWindowController
{
    class Entry { public RectTransform rt; public string kind; public string id; }

    static readonly Vector2 CENTER = new Vector2(0.5f, 0.5f);
    static readonly Vector2 TOP_LEFT = new Vector2(0f, 1f);

    // #99 Slice 1 (ADR-0017 / findings 0075 §1, owner-locked): the magnet-snap default threshold
    // in canvas-LOGICAL px (NOT screen pixels — drag is already in logical coords, so zoom does
    // not change the felt distance). 12px = a comfortable feel: a slow approach catches, a
    // brisk drag past a neighbour does not surprise-snap (owner-recommended initial value;
    // findings 0075 §8 leaves the final tuning to HITL).
    public const float DEFAULT_SNAP_THRESHOLD = 12f;

    readonly RectTransform _layer;
    readonly FloatingWindowCatalog _catalog;
    readonly Func<FloatingWindowSpec, string, RectTransform> _factory;
    readonly Action<GameObject> _destroy;
    readonly Dictionary<string, Entry> _windows = new Dictionary<string, Entry>();

    // #101 (fix #99 regression; findings 0078): the window the USER last focused via a title-bar
    // press (NoteUserFocus). The dock-spawn path (SpawnDockedToFocus) snaps a new window flush to it.
    // Set ONLY by NoteUserFocus — programmatic Show / BringToFront / Spawn never write it (a layout
    // restore BringToFronts every window and must NOT forge a focus target). May go stale when the
    // focused window is closed/hidden; the resolver re-validates liveness, so staleness is harmless.
    string _lastUserFocusedId;

    public FloatingWindowController(
        RectTransform layer,
        FloatingWindowCatalog catalog,
        Func<FloatingWindowSpec, string, RectTransform> factory,
        Action<GameObject> destroy = null)
    {
        _layer = layer != null ? layer : throw new ArgumentNullException(nameof(layer));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _destroy = destroy ?? (go => { if (go != null) UnityEngine.Object.Destroy(go); });
    }

    public int Count => _windows.Count;
    public bool Has(string id) => id != null && _windows.ContainsKey(id);
    public RectTransform RectOf(string id) => _windows.TryGetValue(id, out var e) ? e.rt : null;

    // Spawn a window of `kind` with stable `id` at a canvas-logical top-left (x,y) and size
    // (w,h). Returns null (and spawns nothing) when the kind is UNKNOWN to the catalog (the
    // forward-evolution skip) or the id already exists (id is unique — first wins). The
    // persisted size is clamped UP to the spec's minSize at THIS spawn boundary (findings 0008 §3).
    public RectTransform Spawn(string kind, string id, float x, float y, float w, float h, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_windows.ContainsKey(id)) return _windows[id].rt;     // duplicate id -> keep first
        if (!_catalog.TryGet(kind, out FloatingWindowSpec spec)) return null;  // unknown kind -> skip

        RectTransform rt = _factory(spec, id);
        if (rt == null) return null;
        rt.SetParent(_layer, false);

        Vector2 size = ClampSize(spec, w, h);
        Place(rt, x, y, size.x, size.y);
        rt.gameObject.SetActive(visible);
        rt.SetAsLastSibling();   // newest on top

        _windows[id] = new Entry { rt = rt, kind = kind, id = id };
        return rt;
    }

    // Spawn a window at an AUTO-PLACED top-left (#81 cell-as-floating-window): the caller hands a
    // canvas-logical anchor (the viewport centre), and SpawnPlacement cascades it diagonally off
    // EVERY live window's top-left so a new cell never lands directly under an existing window
    // (marimo calcSpawnPosition). The collision母集合 is `_windows` (cell AND non-cell), and the
    // anchor is used verbatim as the top-left (no half-size centring — see SpawnPlacement). Size is
    // the default cell window size; resize is a later slice so w/h are not persisted (findings 0050).
    public RectTransform SpawnAuto(string kind, string id, float w, float h, Vector2 anchorTopLeft, bool visible)
    {
        Vector2 p = SpawnPlacement.Next(CaptureTopLefts(), anchorTopLeft, SpawnPlacement.DefaultOffset);
        return Spawn(kind, id, p.x, p.y, w, h, visible);
    }

    // #101 (fix #99 regression; findings 0078): spawn a dock window at its SPEC-DEFAULT size — so the
    // size is INDEPENDENT of how many windows already exist (the #99 bug sized each chart to a
    // DockDefaultPlacement.ComputeRects(N) grid cell, so it shrank as N grew) — and SNAP it flush to a
    // target window's edge (DockSnapPlacement, right→down→left→up, non-overlapping). The target is the
    // last USER-focused window when still live+visible, else the visible window nearest the
    // `anchorTopLeft` gaze point (TryResolveDockTarget). With no other window at all, the anchor is used
    // verbatim. Size = the catalog spec default (Spawn clamps UP to minSize — a no-op since default ≥ min).
    public RectTransform SpawnDockedToFocus(string kind, string id, Vector2 anchorTopLeft, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_windows.ContainsKey(id)) return _windows[id].rt;               // duplicate id -> keep first
        if (!_catalog.TryGet(kind, out FloatingWindowSpec spec)) return null;  // unknown kind -> skip

        Vector2 size = spec.defaultSize;
        Vector2 topLeft;
        if (TryResolveDockTarget(anchorTopLeft, id, out var target))
        {
            var others = CaptureVisibleRects(id);
            topLeft = DockSnapPlacement.PlaceAdjacent(target, size, others, 0f);   // flush (gap 0)
        }
        else
        {
            topLeft = anchorTopLeft;   // nothing to snap to (empty cluster) -> land at the gaze point
        }
        return Spawn(kind, id, topLeft.x, topLeft.y, size.x, size.y, visible);
    }

    // #101 (findings 0078): resolve the window a new dock window should snap to:
    //   1. the last USER-focused window (NoteUserFocus = a title-bar press, NOT a programmatic
    //      BringToFront/Show), IF still registered, live, visible, and not `excludeId`; else
    //   2. the visible window whose CENTRE is nearest the `anchorTopLeft` gaze point (viewport centre),
    //      ties broken by id ordinal so the pick is deterministic regardless of dictionary order.
    // Returns false ONLY when no other visible window exists (the caller then uses the anchor verbatim).
    public bool TryResolveDockTarget(Vector2 anchorTopLeft, string excludeId, out FloatingWindowMath.DockRect target)
    {
        target = default;

        // 1. last user-focused, if it still qualifies (the resolver re-validates liveness, so a stale
        //    _lastUserFocusedId from a closed/hidden window simply falls through to the nearest pick).
        if (!string.IsNullOrEmpty(_lastUserFocusedId) && _lastUserFocusedId != excludeId
            && _windows.TryGetValue(_lastUserFocusedId, out var fe)
            && fe.rt != null && fe.rt.gameObject.activeInHierarchy)
        {
            target = new FloatingWindowMath.DockRect(fe.rt.anchoredPosition, fe.rt.sizeDelta);
            return true;
        }

        // 2. nearest visible window centre to the anchor.
        string bestId = null;
        float bestSqr = float.PositiveInfinity;
        foreach (var kv in _windows)
        {
            if (kv.Key == excludeId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            Vector2 centre = rt.anchoredPosition + new Vector2(rt.sizeDelta.x * 0.5f, -rt.sizeDelta.y * 0.5f);
            float d = (centre - anchorTopLeft).sqrMagnitude;
            bool better = d < bestSqr - 1e-4f
                       || (Mathf.Abs(d - bestSqr) <= 1e-4f && (bestId == null || string.CompareOrdinal(kv.Key, bestId) < 0));
            if (better) { bestSqr = d; bestId = kv.Key; }
        }
        if (bestId == null) return false;
        var best = _windows[bestId].rt;
        target = new FloatingWindowMath.DockRect(best.anchoredPosition, best.sizeDelta);
        return true;
    }

    // #101 (findings 0078): every VISIBLE window's (top-left, size) as a DockRect, excluding `excludeId`
    // — the non-overlap 母集合 for DockSnapPlacement (the dock analogue of CaptureTopLefts, but carrying
    // SIZE for full-rect overlap tests, and skipping hidden/dormant windows so they don't block a slot).
    public List<FloatingWindowMath.DockRect> CaptureVisibleRects(string excludeId)
    {
        var rects = new List<FloatingWindowMath.DockRect>(_windows.Count);
        foreach (var kv in _windows)
        {
            if (kv.Key == excludeId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            rects.Add(new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta));
        }
        return rects;
    }

    // Every live window's top-left (anchoredPosition), the collision母集合 for SpawnPlacement —
    // used by SpawnAuto AND by the coordinator when it re-places a REUSED dormant window (which
    // SpawnAuto can't handle, since the id already exists). Order is incidental (a set of points).
    public List<Vector2> CaptureTopLefts()
    {
        var tops = new List<Vector2>(_windows.Count);
        foreach (var kv in _windows)
            if (kv.Value.rt != null) tops.Add(kv.Value.rt.anchoredPosition);
        return tops;
    }

    // Hide a window WITHOUT destroying or deregistering it (#81 dormant region_001): the adopted
    // scene-authored cell window survives a delete via SetActive(false) and is reused by the next
    // AddCell (ADR-0013 Decision 4 — adopt = hide-not-destroy). The inverse of Show. No-op for an
    // unknown id; returns whether the window was found.
    public bool Hide(string id)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
        {
            e.rt.gameObject.SetActive(false);
            return true;
        }
        return false;
    }

    // Adopt a PRE-EXISTING window RectTransform (e.g. a scene-authored Strategy Editor) as a
    // managed window WITHOUT the factory. The Backcast workspace root (findings 0025 §8) registers
    // scene-authored editor windows this way and restores them IN PLACE — they are never
    // destroyed+respawned. Re-parents under the layer and re-asserts the canonical top-left-pivot
    // anchors so the authored anchoredPosition/sizeDelta carry the canvas-logical contract. Returns
    // the existing rt (first wins) on a duplicate id; null for a null/empty id or null rt.
    public RectTransform Adopt(string kind, string id, RectTransform rt)
    {
        if (string.IsNullOrEmpty(id) || rt == null) return null;
        if (_windows.TryGetValue(id, out var existing)) return existing.rt;   // duplicate id -> keep first
        rt.SetParent(_layer, false);
        Vector2 pos = rt.anchoredPosition;
        Vector2 size = rt.sizeDelta;
        Place(rt, pos.x, pos.y, size.x, size.y);   // idempotent when already canonical-authored
        rt.SetAsLastSibling();
        _windows[id] = new Entry { rt = rt, kind = kind, id = id };
        return rt;
    }

    // Apply position/size/visibility to an ALREADY-REGISTERED window from a persisted layout,
    // WITHOUT the full-replacement remove/spawn pass of Apply(). The workspace root uses this to
    // restore adopted (scene-authored) windows in place and to reposition windows it spawns,
    // because the mainline restore must NEVER destroy the scene-authored editor (findings 0025 §8).
    // No-op (returns false) for a null layout or an unknown id.
    public bool ApplyGeometry(FloatingWindowLayout w)
    {
        if (w == null || string.IsNullOrEmpty(w.id)) return false;
        if (!_windows.TryGetValue(w.id, out var e) || e.rt == null) return false;
        if (_catalog.TryGet(e.kind, out FloatingWindowSpec spec))
        {
            Vector2 size = ClampSize(spec, w.w, w.h);
            Place(e.rt, w.x, w.y, size.x, size.y);
        }
        e.rt.gameObject.SetActive(w.visible);
        return true;
    }

    // Destroy + deregister a SINGLE window by id (returns false for an unknown id). Unlike Apply()'s
    // full-replacement pass, this targets ONE window so the caller can drop an ADDITIONAL window while
    // leaving the scene-authored adopted window untouched (File→New, findings 0027 D3 / findings 0025 §8).
    public bool Close(string id)
    {
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var e)) return false;
        _windows.Remove(id);
        if (_lastUserFocusedId == id) _lastUserFocusedId = null;   // #101: drop a vanished focus target
        if (e.rt != null) _destroy(e.rt.gameObject);
        return true;
    }

    // Move a window by a CANVAS-LOGICAL delta (the input boundary already divided the viewport
    // delta by zoom). y is up-positive, matching anchoredPosition. No-op for an unknown id.
    public void MoveByLogical(string id, Vector2 logicalDelta)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
            e.rt.anchoredPosition += logicalDelta;
    }

    // #99 Slice 1 (ADR-0017 / findings 0075 §1): magnet snap on drag release. The title-bar
    // input boundary calls this from OnEndDrag with the window's id; the controller reads every
    // OTHER live window's (top-left, size) into DockRects, asks FloatingWindowMath.SnapOffset
    // for the canvas-logical Δ (x and y INDEPENDENT — one window may snap horizontally to A and
    // vertically to B), and applies it via MoveByLogical so the snap goes through the same
    // anchoredPosition write path the drag uses. The dragged window is EXCLUDED from `others`
    // (a window never snaps to itself). Returns the applied offset (Vector2.zero if there was
    // nothing to snap to or the nearest edge was beyond threshold). No-op for an unknown id.
    //
    // The default threshold is DEFAULT_SNAP_THRESHOLD (12px logical). A live HITL tuner could
    // pass a different value; the production input path uses the default.
    public Vector2 SnapOnRelease(string id, float threshold)
    {
        if (string.IsNullOrEmpty(id)) return Vector2.zero;
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return Vector2.zero;

        var draggedRect = new FloatingWindowMath.DockRect(dragged.rt.anchoredPosition, dragged.rt.sizeDelta);
        var others = new List<FloatingWindowMath.DockRect>(_windows.Count > 0 ? _windows.Count - 1 : 0);
        foreach (var kv in _windows)
        {
            if (kv.Key == id) continue;                                          // never snap to self
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;        // hidden windows do not pull
            others.Add(new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta));
        }

        Vector2 offset = FloatingWindowMath.SnapOffset(draggedRect, others, threshold);
        if (offset != Vector2.zero) dragged.rt.anchoredPosition += offset;
        return offset;
    }

    // Convenience: production path (the title-bar input) snaps with the default threshold.
    public Vector2 SnapOnRelease(string id) => SnapOnRelease(id, DEFAULT_SNAP_THRESHOLD);

    // Raise a window to the front (last sibling = topmost draw). No-op for an unknown id. Deliberately
    // does NOT record user focus (see NoteUserFocus): a layout restore BringToFronts every window.
    public void BringToFront(string id)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
            e.rt.SetAsLastSibling();
    }

    // #101 (fix #99 regression; findings 0078): record that the USER focused this window via a title-bar
    // press/drag (FloatingWindowTitleInput), so the next dock spawn snaps flush to it (SpawnDockedToFocus).
    // This is the ONLY writer of the focus target — programmatic Show / BringToFront / Spawn never record
    // focus, so a layout restore (which BringToFronts every restored window) cannot forge a target. Also
    // raises the window (a press raises), keeping the SetAsLastSibling semantics in BringToFront. No-op for
    // an unknown id (the focus target is left as-is rather than cleared to a vanished window).
    public void NoteUserFocus(string id)
    {
        if (string.IsNullOrEmpty(id) || !_windows.ContainsKey(id)) return;
        _lastUserFocusedId = id;
        BringToFront(id);
    }

    // Reveal a hidden window AND raise it to the front (#81 reveal-on-insert). BringToFront only
    // re-orders siblings; a window hidden via SetActive(false) needs SetActive(true) as well, so a
    // cell added into a hidden editor becomes visible. No-op for an unknown id.
    public void Show(string id)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
            e.rt.gameObject.SetActive(true);
        BringToFront(id);   // keep the raise semantics in ONE place
    }

    // live -> document. Each window becomes a FloatingWindowLayout in SIBLING order, with
    // zOrder = its 0-based rank among windows (contiguous, 0 = backmost): we read the live
    // sibling index and re-rank, so the captured z is always canonical even if the layer also
    // held non-window children (it doesn't, but the rank is robust). x,y = anchoredPosition
    // (top-left), w,h = sizeDelta. Produces an empty panels list + null canvasView — this
    // controller owns ONLY the floatingWindows dimension; the caller merges panels/canvasView.
    public LayoutDocument Capture()
    {
        var ranked = new List<Entry>(_windows.Count);
        foreach (var kv in _windows) ranked.Add(kv.Value);
        ranked.Sort((a, b) => a.rt.GetSiblingIndex().CompareTo(b.rt.GetSiblingIndex()));

        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            floatingWindows = new List<FloatingWindowLayout>(ranked.Count),
        };
        for (int i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            doc.floatingWindows.Add(new FloatingWindowLayout(
                e.id, e.kind,
                e.rt.anchoredPosition.x, e.rt.anchoredPosition.y,
                e.rt.sizeDelta.x, e.rt.sizeDelta.y,
                i, e.rt.gameObject.activeSelf));
        }
        return doc;
    }

    // document -> live, FULL REPLACEMENT (findings 0008 §3, owner-locked): windows present live
    // but absent from the document are REMOVED (destroyed); document windows are spawned (or, if
    // already live, repositioned/resized/re-shown); visible=false stays REGISTERED but hidden.
    // The document's own zOrder is NOT mutated — only the stable-normalized 0..n-1 result is
    // applied as sibling indices. Unknown-kind entries don't spawn (Spawn returns null) so they
    // simply don't appear live, while LayoutStore keeps them in the document.
    public void Apply(LayoutDocument doc)
    {
        var desired = doc?.floatingWindows;

        // 1. Remove live windows not named in the document.
        var desiredIds = new HashSet<string>();
        if (desired != null)
            foreach (var w in desired)
                if (w != null && !string.IsNullOrEmpty(w.id)) desiredIds.Add(w.id);

        var toRemove = new List<string>();
        foreach (var id in _windows.Keys) if (!desiredIds.Contains(id)) toRemove.Add(id);
        foreach (var id in toRemove)
        {
            var e = _windows[id];
            _windows.Remove(id);
            if (e.rt != null) _destroy(e.rt.gameObject);
        }

        // 2. Spawn-or-update each document window; collect the ones that became live (in doc
        //    order) with their persisted zOrder for the normalization step.
        var liveIds = new List<string>();
        var liveZ = new List<int>();
        if (desired != null)
        {
            foreach (var w in desired)
            {
                if (w == null || string.IsNullOrEmpty(w.id)) continue;
                RectTransform rt;
                if (_windows.TryGetValue(w.id, out var e))
                {
                    rt = e.rt;
                    if (_catalog.TryGet(e.kind, out FloatingWindowSpec spec))
                    {
                        Vector2 size = ClampSize(spec, w.w, w.h);
                        Place(rt, w.x, w.y, size.x, size.y);
                    }
                    rt.gameObject.SetActive(w.visible);
                }
                else
                {
                    rt = Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible);
                    if (rt == null) continue;   // unknown kind -> skipped (kept only in the doc)
                }
                liveIds.Add(w.id);
                liveZ.Add(w.zOrder);
            }
        }

        // 3. Normalize zOrder -> contiguous sibling indices (stable: zOrder asc, ties by doc order).
        int[] order = FloatingWindowMath.SiblingOrder(liveZ);
        for (int slot = 0; slot < order.Length; slot++)
        {
            string id = liveIds[order[slot]];
            if (_windows.TryGetValue(id, out var e) && e.rt != null) e.rt.SetSiblingIndex(slot);
        }
    }

    // ---- helpers ----

    // Canonical placement: centre anchors + top-left pivot so anchoredPosition IS the top-left
    // corner in canvas-logical coords and sizeDelta IS the logical size (findings 0008 §2).
    static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = CENTER;
        rt.anchorMax = CENTER;
        rt.pivot = TOP_LEFT;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Vector2 ClampSize(FloatingWindowSpec spec, float w, float h)
    {
        float cw = Mathf.Max(w, spec.minSize.x);
        float ch = Mathf.Max(h, spec.minSize.y);
        return new Vector2(cw, ch);
    }
}
