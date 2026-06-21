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
    // #104 (ADR-0019 / findings 0082 §1): each registered window carries its current group membership
    // as a nullable groupId. NEVER re-derived from coordinates — the field IS the source of truth and is
    // round-tripped through Capture/Apply verbatim. Spawn paths leave it null; attach happens at
    // user drag-release (SnapOnRelease, Slice B). groupId becomes non-null in exactly THREE ways, none
    // of them coordinate-derived: (a) user drag-release, (b) restore of a persisted groupId (Apply /
    // RestoreFloating), and (c) #105 the FACTORY first-launch default (FormGroup, owner-requested —
    // see ADR-0019 D8 amendment / findings 0082 §12, findings 0083). The cell coordinator never mints.
    class Entry { public RectTransform rt; public string kind; public string id; public string groupId; }

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
    // #104 (ADR-0019 / findings 0082 §8): the drag-time ghost overlay. Optional — when null, drag still
    // works (the structural slices A–F are independent of the visual preview). DragApplyDelta /
    // ReleaseDrag drive it; the root binds one per plane via AttachGhostLayer.
    DragGhostLayer _ghostLayer;
    // #104 F8 (ADR-0019 / findings 0082 §7): the LAST resolved Hakoniwa swap-target id from the most
    // recent DragApplyDelta frame. Cached so CommitHakoniwaSwap at OnEndDrag commits against the same
    // target the last-painted ghost predicted, even if sibling order or rect positions shifted between
    // the final ghost frame and the release (e.g., another window's SetAsLastSibling on the same plane,
    // or a parallel restore mid-drag). Null whenever the dragged is not currently in HakoniwaSwap mode;
    // CommitHakoniwaSwap falls back to a fresh ResolveHakoniwaSwapTarget when null, so a release without
    // a tracked mid-drag frame (rare; degenerate) still produces a defined outcome.
    string _lastSwapTargetId;

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

    // #104 (ADR-0019 / findings 0082 §8): bind a ghost overlay to this controller. Production root
    // mints one per plane (the back DockLayer plane has its own ghost layer; the front
    // FloatingWindowLayer plane has its own — neither leaks). The controller calls Render() during
    // DragApplyDelta with a mode-specific ghost composition and Clear() at ReleaseDrag (commit-on-
    // release rule from findings 0082 §8 — ghosts vanish at the end of the drag).
    public void AttachGhostLayer(DragGhostLayer ghostLayer) { _ghostLayer = ghostLayer; }
    public DragGhostLayer GhostLayer => _ghostLayer;
    // #104 (ADR-0019 / findings 0082 §1): the group-membership read seam. null = singleton. The math
    // layer (FloatingWindowMath.EvaluateDragMode) and the input layer (drag mode pick) read through this
    // — never reach into the entry struct directly.
    public string GroupIdOf(string id) => _windows.TryGetValue(id, out var e) ? e.groupId : null;
    // #104 (ADR-0019 / findings 0082 §1): the group-membership write seam used by SnapOnRelease's attach
    // commit (Slice B), detach commit / Close cascade (Slice D), and cross-plane restore (Slice F).
    // Null = singleton. No-op for an unknown id. `public` so the AFK gate (Editor assembly) can drive
    // the seam directly; production callers stay disciplined (only the controller's own group lifecycle
    // and BackcastWorkspaceRoot.RestoreFloating write through here, per findings 0082 §10).
    public void SetGroupId(string id, string groupId)
    {
        if (_windows.TryGetValue(id, out var e)) e.groupId = groupId;
    }

    // Spawn a window of `kind` with stable `id` at a canvas-logical top-left (x,y) and size
    // (w,h). Returns null (and spawns nothing) when the kind is UNKNOWN to the catalog (the
    // forward-evolution skip) or the id already exists (id is unique — first wins). The
    // persisted size is clamped UP to the spec's minSize at THIS spawn boundary (findings 0008 §3).
    public RectTransform Spawn(string kind, string id, float x, float y, float w, float h, bool visible)
        => Spawn(kind, id, x, y, w, h, visible, null);

    // #104 (ADR-0019 / findings 0082 §10): the groupId-aware spawn overload exists ONLY so RESTORE can
    // round-trip the persisted membership — every production Spawn caller passes null (attach is a user
    // drag-release event, never a programmatic spawn). Apply() routes the doc's persisted groupId here;
    // BackcastWorkspaceRoot.RestoreFloating does the same after cross-plane split (Slice F).
    public RectTransform Spawn(string kind, string id, float x, float y, float w, float h, bool visible, string groupId)
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

        _windows[id] = new Entry { rt = rt, kind = kind, id = id, groupId = groupId };
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
    // #104 (ADR-0019 / findings 0082 §10): a Close on a group member runs the SHARED dissolve helper —
    // when the surviving visible/live members drop below 2, the lone remnant's groupId is cleared
    // (a singleton is not a group). The helper is identical to the detach-commit path (ReleaseDrag) and
    // the cross-plane restore split (Slice F), so close / detach / restore can never drift on dissolve.
    public bool Close(string id)
    {
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var e)) return false;
        string oldGroup = e.groupId;
        _windows.Remove(id);
        if (_lastUserFocusedId == id) _lastUserFocusedId = null;   // #101: drop a vanished focus target
        if (e.rt != null) _destroy(e.rt.gameObject);
        if (!string.IsNullOrEmpty(oldGroup)) DissolveIfShrunkTo(oldGroup, 2);
        return true;
    }

    // #104 (ADR-0019 / findings 0082 §5, §10): SHARED dissolve helper. When a group's visible/live
    // member count drops below `threshold`, every surviving member's groupId is set to null (the group
    // dissolves — a singleton is not a group, findings 0082 §2). Called on detach commit (ReleaseDrag's
    // NormalGroupDetach / HakoniwaDetach branch), on Close cascade, and on cross-plane restore split
    // (Slice F). Centralising the cascade here is what guarantees parity between those three paths.
    //
    // The implementation walks the live `_windows` to count current members (the source of truth for
    // group membership is the groupId field on each Entry — there is no group registry to maintain).
    // `threshold` is `2` for every production caller (the design uses one rule "singleton dissolves");
    // taking it as a parameter keeps the helper testable without baking the rule into a constant.
    public void DissolveIfShrunkTo(string groupId, int threshold)
    {
        if (string.IsNullOrEmpty(groupId)) return;
        var liveMembers = new List<Entry>();
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != groupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            liveMembers.Add(kv.Value);
        }
        if (liveMembers.Count >= threshold) return;   // group still has enough members; leave it alone
        // Below threshold ⇒ dissolve: clear groupId on every REMAINING (live or hidden) member with this
        // groupId. We dissolve hidden ones too because a Hidden member with no visible siblings will
        // never re-form a group on Show without an explicit attach — the dissolve marker is the right
        // signal that membership is gone (the design's "groupId 温存" rule for Hide applies only while
        // the group is still extant, ADR-0019 D9).
        var toDissolve = new List<Entry>();
        foreach (var kv in _windows)
            if (kv.Value.groupId == groupId) toDissolve.Add(kv.Value);
        foreach (var m in toDissolve) m.groupId = null;
    }

    // #104 (ADR-0019 / findings 0082 §5, §6, §7, §8): the SINGLE production release entry. Called from
    // FloatingWindowTitleInput.OnEndDrag with the dragged's drag-start logical anchor (`restAtDragStart`)
    // and the running cursor (`cursor` = rest + accumulated frameDelta). Classifies the final mode via
    // EvaluateDragMode and commits the variant outcome:
    //
    //   SoloDrag / NormalGroupTranslate: live geometry already tracked the cursor; snap-on-release runs
    //     (SnapOnRelease applies the magnet snap AND the flush-attach commit — Slice B's group merge).
    //   NormalGroupDetach / HakoniwaDetach: live geometry was frozen during drag; at release the dragged
    //     jumps to `cursor` (where the Slice G ghost was painted), drops its current groupId, runs the
    //     magnet snap (ApplySnapOffset), and re-evaluates flush-attach with the OLD group EXCLUDED
    //     (CommitFlushAttachOnRelease(id, excludeGroupId: oldGroup)) — findings 0082 §5: a detach
    //     commit must never silently re-merge the dragged back into the group it just left, but a flush
    //     partner OUTSIDE oldGroup (or a flush singleton) still mints / joins a fresh group. The old
    //     group is then run through DissolveIfShrunkTo (≥2 visible/live members ⇒ group keeps, else the
    //     remnant goes solo). detach + dissolve are commit-on-release per findings 0082 §8.
    //   HakoniwaCoreLock / HakoniwaSnapBack: dragged returns to `restAtDragStart`; groupId untouched.
    //     Live geometry was frozen during drag, so this is just an explicit snap-back.
    //   HakoniwaSwap: Slice E2's `(x,y,w,h)` 4-value swap with the drop target. Until E2 lands the
    //     hasTarget path stays false ⇒ EvaluateDragMode never returns HakoniwaSwap here, so this branch
    //     is unreachable in Slice D — Slice E2 wires the swap commit.
    public FloatingWindowMath.DragMode ReleaseDrag(string id, Vector2 restAtDragStart, Vector2 cursor)
    {
        if (string.IsNullOrEmpty(id)) return FloatingWindowMath.DragMode.SoloDrag;
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null)
            return FloatingWindowMath.DragMode.SoloDrag;

        var ctx = BuildDragContext(dragged, restAtDragStart, cursor);
        var mode = FloatingWindowMath.EvaluateDragMode(ctx);
        string oldGroup = dragged.groupId;

        switch (mode)
        {
            case FloatingWindowMath.DragMode.SoloDrag:
                SnapOnRelease(id);
                break;

            case FloatingWindowMath.DragMode.NormalGroupTranslate:
                // #104 F4 (findings 0082 §3-4): the magnet snap at release must move the WHOLE
                // group, not just the dragged. Mid-drag DragApplyDelta translated every group
                // member by frameDelta so the group's flush adjacency is intact at release; if
                // SnapOnRelease moved only the dragged, that adjacency breaks and the subsequent
                // CommitFlushAttachOnRelease scan could reclassify the dragged against an
                // unrelated flush partner (or leave it singleton), splitting the original group.
                // Fix: apply ApplySnapOffset to the dragged, propagate that same offset to every
                // OTHER visible/live group member, THEN run CommitFlushAttachOnRelease. The flush
                // partners are unchanged (group still flush internally; offset is shared with any
                // external snap target), so the merge cascade just confirms the existing groupId.
                {
                    Vector2 groupOffset = ApplySnapOffset(id, DEFAULT_SNAP_THRESHOLD);
                    if (groupOffset != Vector2.zero && !string.IsNullOrEmpty(dragged.groupId))
                    {
                        foreach (var kv in _windows)
                        {
                            if (kv.Key == id) continue;                                 // dragged already moved by ApplySnapOffset
                            if (kv.Value.groupId != dragged.groupId) continue;          // only this group
                            var rt = kv.Value.rt;
                            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                            rt.anchoredPosition += groupOffset;
                        }
                    }
                    CommitFlushAttachOnRelease(id);
                }
                break;

            case FloatingWindowMath.DragMode.NormalGroupDetach:
            case FloatingWindowMath.DragMode.HakoniwaDetach:
                dragged.rt.anchoredPosition = cursor;
                dragged.groupId = null;
                // findings 0082 §5: detach commit clears groupId, then re-evaluates snap + flush-attach
                // at the new cursor. The attach commit EXCLUDES oldGroup so a dragged that lands flush
                // to a former sibling does NOT silently re-merge (the user's detach gesture wins). A
                // flush partner OUTSIDE oldGroup (or a flush singleton) still forms / joins a new group.
                ApplySnapOffset(id, DEFAULT_SNAP_THRESHOLD);
                CommitFlushAttachOnRelease(id, excludeGroupId: oldGroup);
                if (!string.IsNullOrEmpty(oldGroup)) DissolveIfShrunkTo(oldGroup, 2);
                break;

            case FloatingWindowMath.DragMode.HakoniwaCoreLock:
            case FloatingWindowMath.DragMode.HakoniwaSnapBack:
                dragged.rt.anchoredPosition = restAtDragStart;
                break;

            case FloatingWindowMath.DragMode.HakoniwaSwap:
                CommitHakoniwaSwap(id, restAtDragStart, cursor);
                break;
        }
        // F8 (#104 contract): clear the swap-target cache so the NEXT drag's DragApplyDelta starts
        // from a clean slate. ReleaseDrag is the single end-of-drag boundary, so doing the clear
        // here covers every mode path (including non-swap modes that never wrote the field).
        _lastSwapTargetId = null;
        // #104 Slice G (findings 0082 §8): commit-on-release rule — at release the ghost preview
        // disappears (it was a phantom; the live geometry now reflects the commit). Clear runs whether
        // or not we used the ghost layer this drag, so a probe that never set a ghost layer is fine.
        if (_ghostLayer != null) _ghostLayer.Clear();
        return mode;
    }

    // #104 (ADR-0019 / findings 0082 §7): commit a Hakoniwa swap. The dragged ↔ drop-target exchange
    // four values verbatim — `(x, y, w, h)`. kind / id / groupId / content are UNCHANGED, so the
    // group's footprint stays exactly the same; only the geometry is rotated between the two members.
    // The drop target is resolved at release time via ResolveHakoniwaSwapTarget — the SAME resolver
    // BuildDragContext used during drag, so the mid-drag mode and the commit cannot disagree.
    void CommitHakoniwaSwap(string id, Vector2 restAtDragStart, Vector2 cursor)
    {
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return;

        // F8 (#104 contract): use the LAST-painted-ghost target id (cached by DragApplyDelta) so the
        // commit binds to the same window the user saw highlighted. Fallback to a fresh resolve only
        // when the cache is empty (degenerate: ReleaseDrag invoked without a preceding HakoniwaSwap
        // DragApplyDelta frame). The cache plus fallback together guarantee the same defined outcome
        // as the prior unconditional resolve, without the inter-frame drift risk.
        string targetId = _lastSwapTargetId ?? ResolveHakoniwaSwapTarget(dragged, cursor);
        if (string.IsNullOrEmpty(targetId)
            || !_windows.TryGetValue(targetId, out var target)
            || target.rt == null)
        {
            // Defensive: hasTarget said yes mid-drag but the target vanished by release. Snap back.
            dragged.rt.anchoredPosition = restAtDragStart;
            return;
        }

        // Live geometry was frozen during the drag, so dragged.anchoredPosition == restAtDragStart.
        // Read both sides BEFORE writing to avoid clobbering: simultaneously assign the 4 values.
        Vector2 draggedPos = dragged.rt.anchoredPosition;
        Vector2 draggedSize = dragged.rt.sizeDelta;
        Vector2 targetPos = target.rt.anchoredPosition;
        Vector2 targetSize = target.rt.sizeDelta;
        dragged.rt.anchoredPosition = targetPos;
        dragged.rt.sizeDelta = targetSize;
        target.rt.anchoredPosition = draggedPos;
        target.rt.sizeDelta = draggedSize;
        // kind / id / content / groupId on both Entry rows are untouched — the design's identity-
        // preservation rule (only the rect rotates).
    }

    // Move a window by a CANVAS-LOGICAL delta (the input boundary already divided the viewport
    // delta by zoom). y is up-positive, matching anchoredPosition. No-op for an unknown id.
    public void MoveByLogical(string id, Vector2 logicalDelta)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
            e.rt.anchoredPosition += logicalDelta;
    }

    // #104 (ADR-0019 / findings 0082 §6): the SINGLE per-frame drag entry. Classifies the dragged into
    // one of the 7 DragModes (FloatingWindowMath.EvaluateDragMode) and acts accordingly. Returns the
    // classified mode so the title-input boundary can route additional UI (e.g. Slice G ghost).
    //
    // Live-geometry rules (findings 0082 §8 "commit-on-release"):
    //   SoloDrag             — dragged tracks cursor (existing #15 behaviour).
    //   NormalGroupTranslate — every visible/live group member translates by frameDelta — the ONE mode
    //                          where multi-window live geometry mutates during drag.
    //   NormalGroupDetach    — frozen; release commits detach (Slice D).
    //   HakoniwaSwap / Hak.SnapBack / Hak.Detach / Hak.CoreLock — frozen; release commits the variant
    //                          outcome (Slice E1/E2/D). Slice G's ghost layer preview the outcome.
    //
    // `restAtDragStart` and `cursor` are canvas-LOGICAL coordinates. `cursor` = restAtDragStart +
    // accumulated frameDelta over the drag (the title-input tracks the running cursor — the controller
    // does not re-derive it from rectangles, so a window that snaps mid-drag does not corrupt the
    // detach-threshold metric).
    public FloatingWindowMath.DragMode DragApplyDelta(string id, Vector2 restAtDragStart, Vector2 cursor, Vector2 frameDelta)
    {
        if (string.IsNullOrEmpty(id)) return FloatingWindowMath.DragMode.SoloDrag;
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null)
            return FloatingWindowMath.DragMode.SoloDrag;

        var ctx = BuildDragContext(dragged, restAtDragStart, cursor);
        var mode = FloatingWindowMath.EvaluateDragMode(ctx);

        switch (mode)
        {
            case FloatingWindowMath.DragMode.SoloDrag:
                dragged.rt.anchoredPosition += frameDelta;
                break;
            case FloatingWindowMath.DragMode.NormalGroupTranslate:
                // The ONE multi-window live-geometry path. Iterate group members (the dragged is
                // included by virtue of its own groupId match).
                foreach (var kv in _windows)
                {
                    if (kv.Value.groupId != dragged.groupId) continue;
                    var rt = kv.Value.rt;
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                    rt.anchoredPosition += frameDelta;
                }
                break;
            // All other modes: live geometry frozen during drag. Release commits the outcome (Slices D / E1 / E2);
            // Slice G's ghost layer renders the post-release preview. Intentionally no-op here.
            case FloatingWindowMath.DragMode.NormalGroupDetach:
            case FloatingWindowMath.DragMode.HakoniwaSwap:
            case FloatingWindowMath.DragMode.HakoniwaSnapBack:
            case FloatingWindowMath.DragMode.HakoniwaDetach:
            case FloatingWindowMath.DragMode.HakoniwaCoreLock:
                break;
        }
        // F8 (#104 contract): cache the resolved swap target so CommitHakoniwaSwap at OnEndDrag binds
        // to the same id the last-painted ghost predicted. Clear when not in HakoniwaSwap mode so a
        // mode transition (e.g., cursor moves off the target) does not leave a stale id for release.
        _lastSwapTargetId = (mode == FloatingWindowMath.DragMode.HakoniwaSwap)
            ? ResolveHakoniwaSwapTarget(dragged, cursor)
            : null;
        // #104 Slice G (ADR-0019 / findings 0082 §8): paint the ghost preview for this frame's mode.
        // The composer computes the 0/1/2 ghost specs that match the mode (no allocation for
        // SoloDrag / NormalGroupTranslate). Ghost layer is optional — controllers without one (the
        // AFK gate's bare-stack sections) simply skip the render.
        if (_ghostLayer != null) _ghostLayer.Render(ComposeDragGhosts(dragged, mode, restAtDragStart, cursor));
        return mode;
    }

    // #104 (ADR-0019 / findings 0082 §8): the 7-mode ghost composition. Returns:
    //   SoloDrag / NormalGroupTranslate          → empty (live geometry already shows the outcome)
    //   NormalGroupDetach / HakoniwaDetach       → 1 solid ghost at cursor (the dragged "would land here")
    //   HakoniwaSwap                             → 2 ghosts: dragged-style solid at target rect +
    //                                              target-style dashed at dragged rest rect (the
    //                                              "would-rotate-here" preview)
    //   HakoniwaSnapBack                         → 1 solid ghost at restAtDragStart ("would snap back")
    //   HakoniwaCoreLock                         → 1 solid ghost at restAtDragStart (core can't escape)
    //
    // Pure composition over the controller's current state (no Unity calls beyond reading rects), so
    // the AFK gate drives it headlessly via a bare-RectTransform pool.
    public List<DragGhostLayer.GhostSpec> ComposeDragGhosts(string id, FloatingWindowMath.DragMode mode, Vector2 restAtDragStart, Vector2 cursor)
    {
        if (string.IsNullOrEmpty(id)) return new List<DragGhostLayer.GhostSpec>(0);
        if (!_windows.TryGetValue(id, out var dragged)) return new List<DragGhostLayer.GhostSpec>(0);
        return ComposeDragGhosts(dragged, mode, restAtDragStart, cursor);
    }

    List<DragGhostLayer.GhostSpec> ComposeDragGhosts(Entry dragged, FloatingWindowMath.DragMode mode, Vector2 restAtDragStart, Vector2 cursor)
    {
        var ghosts = new List<DragGhostLayer.GhostSpec>(2);
        if (dragged == null || dragged.rt == null) return ghosts;
        Vector2 draggedSize = dragged.rt.sizeDelta;

        switch (mode)
        {
            case FloatingWindowMath.DragMode.SoloDrag:
            case FloatingWindowMath.DragMode.NormalGroupTranslate:
                return ghosts;   // no ghost

            case FloatingWindowMath.DragMode.NormalGroupDetach:
            case FloatingWindowMath.DragMode.HakoniwaDetach:
                ghosts.Add(new DragGhostLayer.GhostSpec
                {
                    kind = dragged.kind, topLeft = cursor, size = draggedSize,
                    style = DragGhostLayer.GhostStyle.Solid,
                });
                return ghosts;

            case FloatingWindowMath.DragMode.HakoniwaSnapBack:
            case FloatingWindowMath.DragMode.HakoniwaCoreLock:
                ghosts.Add(new DragGhostLayer.GhostSpec
                {
                    kind = dragged.kind, topLeft = restAtDragStart, size = draggedSize,
                    style = DragGhostLayer.GhostStyle.Solid,
                });
                return ghosts;

            case FloatingWindowMath.DragMode.HakoniwaSwap:
            {
                string targetId = ResolveHakoniwaSwapTarget(dragged, cursor);
                if (string.IsNullOrEmpty(targetId) || !_windows.TryGetValue(targetId, out var target) || target.rt == null)
                {
                    // hasTarget said yes but resolver returned nothing this frame (boundary jitter); paint a
                    // snap-back-style single ghost so the user gets a defined visual rather than no ghost.
                    ghosts.Add(new DragGhostLayer.GhostSpec
                    {
                        kind = dragged.kind, topLeft = restAtDragStart, size = draggedSize,
                        style = DragGhostLayer.GhostStyle.Solid,
                    });
                    return ghosts;
                }
                Vector2 targetPos = target.rt.anchoredPosition;
                Vector2 targetSize = target.rt.sizeDelta;
                // Dragged ghost (SOLID) sits AT the target rect (where it would land).
                ghosts.Add(new DragGhostLayer.GhostSpec
                {
                    kind = dragged.kind, topLeft = targetPos, size = targetSize,
                    style = DragGhostLayer.GhostStyle.Solid,
                });
                // Target ghost (DASHED) sits AT the dragged's rest rect (where it would land).
                ghosts.Add(new DragGhostLayer.GhostSpec
                {
                    kind = target.kind, topLeft = restAtDragStart, size = draggedSize,
                    style = DragGhostLayer.GhostStyle.Dashed,
                });
                return ghosts;
            }
        }
        return ghosts;
    }

    // Build the EvaluateDragMode input from the controller's live state. isInGroup requires the
    // dragged's groupId is non-null AND the group has ≥ 2 visible/live members (findings 0082 §2:
    // singleton == not a group). isHakoniwa = isInGroup AND there is at least one visible/live core
    // member (DockShape.IsCoreKind). isCore = dragged itself is a core. hasTarget is wired in Slice E2
    // (ResolveDropTarget) — Slice C/E1 leave it false.
    FloatingWindowMath.DragContext BuildDragContext(Entry dragged, Vector2 restAtDragStart, Vector2 cursor)
    {
        var ctx = new FloatingWindowMath.DragContext
        {
            rest = restAtDragStart,
            cursor = cursor,
            isCore = DockShape.IsCoreKind(dragged.kind),
        };
        if (string.IsNullOrEmpty(dragged.groupId)) return ctx;

        int visibleCount = CountLiveGroupMembers(dragged.groupId, out bool groupHasCore);
        ctx.isInGroup = visibleCount >= 2;
        ctx.isHakoniwa = ctx.isInGroup && groupHasCore;
        // #104 Slice E2 (ADR-0019 / findings 0082 §7): hasTarget = the cursor sits over another
        // visible/live group member. Only Hakoniwa groups distinguish swap vs snap-back, so we skip
        // the resolution work outside that case (saves a per-frame allocation+walk on the common
        // SoloDrag / NormalGroup paths).
        if (ctx.isHakoniwa)
            ctx.hasTarget = !string.IsNullOrEmpty(ResolveHakoniwaSwapTarget(dragged, cursor));
        return ctx;
    }

    // #104 (ADR-0019 / findings 0082 §7): resolve the swap drop-target id for a Hakoniwa drag, or
    // null. The dragged is excluded; hidden members are excluded; multiple overlapping members are
    // broken by the highest live sibling index (front-most). Returns the target id so both
    // BuildDragContext (for hasTarget) and CommitHakoniwaSwap (for the (x,y,w,h) exchange) read the
    // same answer — they will not disagree on a frame where the cursor is exactly on a boundary.
    string ResolveHakoniwaSwapTarget(Entry dragged, Vector2 cursor)
    {
        var members = new List<FloatingWindowMath.GroupMember>();
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != dragged.groupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            members.Add(new FloatingWindowMath.GroupMember
            {
                id = kv.Key,
                rect = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta),
                siblingIndex = rt.GetSiblingIndex(),
            });
        }
        return FloatingWindowMath.ResolveDropTarget(cursor, members, dragged.id);
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
        // #104 (ADR-0019 / findings 0082 §3, §4): SnapOnRelease is the SOLE production attach entry
        // for SoloDrag / NormalGroupTranslate releases: ApplySnapOffset moves the dragged toward the
        // nearest neighbour, then CommitFlushAttachOnRelease evaluates the flush-attach trigger and
        // commits any group merge. Programmatic Spawn / restore / cell coordinator never run this
        // branch, so "the only way a group forms is the user's drag-release" (findings 0082 §10) is
        // enforced by where the call lives, not by a flag. ReleaseDrag's NormalGroupDetach /
        // HakoniwaDetach branch calls ApplySnapOffset + CommitFlushAttachOnRelease(id, oldGroup)
        // directly so the detach commit can never silently re-merge into the just-left group.
        Vector2 offset = ApplySnapOffset(id, threshold);
        CommitFlushAttachOnRelease(id);
        return offset;
    }

    // Magnet-only snap: applies the canvas-logical Δ from FloatingWindowMath.SnapOffset to the
    // dragged's anchoredPosition WITHOUT running the flush-attach commit. Extracted from
    // SnapOnRelease so the ReleaseDrag detach branch can run magnet snap + a FILTERED
    // attach commit (excluding the just-left group) — findings 0082 §5: detach commit must
    // never silently re-form the old group via CommitFlushAttachOnRelease's full partner scan.
    // Returns the applied offset (Vector2.zero if no neighbour was within threshold or the id
    // is unknown). Hidden windows do not pull.
    Vector2 ApplySnapOffset(string id, float threshold)
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

    void CommitFlushAttachOnRelease(string draggedId) => CommitFlushAttachOnRelease(draggedId, null);

    // #104 (ADR-0019 / findings 0082 §3, §4, §5): flush-attach + merge-cascade commit, with an
    // optional `excludeGroupId` for the ReleaseDrag detach branch. After SnapOnRelease has
    // settled the dragged's position, scan every visible/live OTHER window for a flush-adjacent
    // partner (IsFlushAdjacent, eps=DEFAULT_FLUSH_EPS=1px). Each partner contributes its current
    // group to the merge; the dragged's own current group joins too. The cascade winner becomes
    // every contributing member's new groupId. If no partner is flush, groupId is left untouched
    // (the dragged stays in its current group, possibly as a singleton remnant — Slice D handles
    // dissolve on detach commit, not on a non-attach release).
    //
    // `excludeGroupId` (non-null only from ReleaseDrag's NormalGroupDetach / HakoniwaDetach
    // branch, passed as the dragged's pre-detach `oldGroup`) filters out flush partners whose
    // CURRENT groupId == excludeGroupId. The detach commit zeroed `dragged.groupId` and the
    // design (findings 0082 §5) treats the commit as "out of that group"; allowing the very
    // next attach scan to silently re-merge the dragged back into oldGroup (because the magnet
    // snap left it flush to a former sibling) would undo the user's detach gesture. The
    // exclusion preserves the documented re-attach-to-NEW-group affordance: a flush partner
    // outside oldGroup (or a flush singleton) still forms / joins a fresh group.
    void CommitFlushAttachOnRelease(string draggedId, string excludeGroupId)
    {
        if (!_windows.TryGetValue(draggedId, out var dragged) || dragged.rt == null) return;
        if (!dragged.rt.gameObject.activeInHierarchy) return;   // a hidden dragged cannot attach
        var draggedRect = new FloatingWindowMath.DockRect(dragged.rt.anchoredPosition, dragged.rt.sizeDelta);

        // Find every flush-adjacent partner (visible/live, !=self). The partner list defines the merge
        // participants. A dragged with ZERO flush partners → no attach commit (groupId untouched).
        // When excludeGroupId is non-null, partners whose CURRENT groupId == excludeGroupId are
        // skipped (findings 0082 §5: a detach commit must never silently re-form the old group).
        var flushPartnerIds = new List<string>();
        foreach (var kv in _windows)
        {
            if (kv.Key == draggedId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            if (excludeGroupId != null && kv.Value.groupId == excludeGroupId) continue;   // detach: never re-merge into oldGroup
            var partnerRect = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta);
            if (FloatingWindowMath.IsFlushAdjacent(draggedRect, partnerRect, DEFAULT_FLUSH_EPS))
                flushPartnerIds.Add(kv.Key);
        }
        if (flushPartnerIds.Count == 0) return;   // no attach → leave groupId untouched

        // Build MergeCandidate per UNIQUE contributing groupId (plus singleton entries for partners
        // currently groupId=null AND for the dragged when it is groupId=null). The dragged's group, if
        // any, MUST be a candidate (it stays in its group when partner has no other context).
        var seen = new HashSet<string>();
        var cands = new List<FloatingWindowMath.MergeCandidate>(flushPartnerIds.Count + 1);
        AddCandidateFor(cands, seen, dragged.groupId);
        foreach (var pid in flushPartnerIds)
            if (_windows.TryGetValue(pid, out var pe)) AddCandidateFor(cands, seen, pe.groupId);

        string winnerId = FloatingWindowMath.ResolveMergeWinner(cands);
        if (winnerId == null) winnerId = MintGroupId();   // all-singleton attach (ResolveMergeWinner contract)

        // Apply the winning groupId to every contributing member: the dragged, every flush partner, and
        // every EXISTING-group member of the dragged's current group / each partner's current group.
        // Collect the contributing ids first (avoid mutating during enumeration).
        var winners = new HashSet<string>();
        winners.Add(draggedId);
        foreach (var pid in flushPartnerIds) winners.Add(pid);
        var contributingGroupIds = new HashSet<string>();
        if (!string.IsNullOrEmpty(dragged.groupId)) contributingGroupIds.Add(dragged.groupId);
        foreach (var pid in flushPartnerIds)
            if (_windows.TryGetValue(pid, out var pe) && !string.IsNullOrEmpty(pe.groupId))
                contributingGroupIds.Add(pe.groupId);
        if (contributingGroupIds.Count > 0)
            foreach (var kv in _windows)
                if (contributingGroupIds.Contains(kv.Value.groupId)) winners.Add(kv.Key);

        foreach (var wid in winners)
            if (_windows.TryGetValue(wid, out var we)) we.groupId = winnerId;
    }

    // #104 F9 (findings 0082 §13): the scalar "visible/live member count + hasCore" projection
    // shared by BuildDragContext (isInGroup/isHakoniwa decision) and AddCandidateFor (Merge-
    // Candidate row). Both call sites walk `_windows` once and want the same two answers, so
    // pulling them into one helper makes the "what counts as a live group member" rule live in
    // one place (rt != null && rt.gameObject.activeInHierarchy && groupId match). Hot-path
    // sites (DragApplyDelta NormalGroupTranslate, ResolveHakoniwaSwapTarget, ReleaseDrag F4
    // offset propagation, DissolveIfShrunkTo dissolve list, CommitFlushAttachOnRelease winners
    // expansion) intentionally stay inline — each has a heterogeneous projection (mutating
    // anchoredPosition / building GroupMember / clearing groupId / etc.) that cannot share a
    // scalar-return helper without a per-frame allocation (callback delegate or IEnumerable).
    int CountLiveGroupMembers(string groupId, out bool hasCore)
    {
        hasCore = false;
        if (string.IsNullOrEmpty(groupId)) return 0;
        int count = 0;
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != groupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            count++;
            if (DockShape.IsCoreKind(kv.Value.kind)) hasCore = true;
        }
        return count;
    }

    // Project an existing groupId into a MergeCandidate by counting its visible/live members and
    // detecting a visible/live core. Singleton (null/empty groupId) → a single MergeCandidate with
    // id=null, memberCount=1, hasCore=IsCoreKind(dragged_or_partner_kind)? — but the cascade only uses
    // hasCore on NON-null ids (Hakoniwa-priority), so a singleton's hasCore is moot. We pass
    // hasCore=false on the singleton entry. De-dupe by id so the same group is not listed twice.
    void AddCandidateFor(List<FloatingWindowMath.MergeCandidate> cands, HashSet<string> seen, string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            cands.Add(new FloatingWindowMath.MergeCandidate(null, 1, false));
            return;
        }
        if (!seen.Add(groupId)) return;
        int count = CountLiveGroupMembers(groupId, out bool hasCore);
        cands.Add(new FloatingWindowMath.MergeCandidate(groupId, count, hasCore));
    }

    // #104 (ADR-0019 D1 / findings 0082 §1): mint a fresh groupId — "grp_<Guid.NewGuid().ToString("N")>"
    // (hex32, no hyphens). Called only when ResolveMergeWinner returns null AND there is at least one
    // flush partner ⇒ all-singleton attach (the SINGLE GUID-mint trigger).
    static string MintGroupId() => "grp_" + Guid.NewGuid().ToString("N");

    // #105 (ADR-0019 D8 amendment / findings 0082 §12, findings 0083): the FACTORY-GROUP birth path.
    // The owner-requested first-launch default bundles the base dock cluster into ONE group
    // (BackcastWorkspaceRoot.FormFactoryBaseGroup — no-resume boot only). This is the THIRD and only
    // other way a groupId becomes non-null besides (a) user drag-release and (b) restore of a persisted
    // groupId; like restore it is NOT coordinate-derived — the caller names the member ids explicitly.
    // Mints ONE fresh groupId and stamps it on every named id that is CURRENTLY registered (hidden ones
    // INCLUDED — a hidden base window should still join the factory cluster, mirroring the Hide-preserves-
    // groupId rule §5), then returns it. Returns null (and stamps nothing) when fewer than 2 of the ids
    // are registered: a group needs ≥2 members (cf. DissolveIfShrunkTo's threshold, though that helper
    // counts VISIBLE/live members — here we count registered), so a 0/1-member "group" is meaningless.
    public string FormGroup(IReadOnlyList<string> ids)
    {
        if (ids == null) return null;
        int live = 0;
        for (int i = 0; i < ids.Count; i++)
            if (!string.IsNullOrEmpty(ids[i]) && _windows.ContainsKey(ids[i])) live++;
        if (live < 2) return null;
        string g = MintGroupId();
        for (int i = 0; i < ids.Count; i++)
            if (!string.IsNullOrEmpty(ids[i]) && _windows.ContainsKey(ids[i])) SetGroupId(ids[i], g);
        return g;
    }

    // #104 (ADR-0019 / findings 0082 §3): default flush-adjacency epsilon — 1 px in canvas-LOGICAL
    // coordinates (zoom-independent like DEFAULT_SNAP_THRESHOLD). After SnapOnRelease has aligned edges
    // to integer (or near-integer) offsets, 1px is enough to absorb floating-point round-off without
    // letting two windows that LOOK separated attach.
    public const float DEFAULT_FLUSH_EPS = 1f;

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
            // #104 (ADR-0019 / findings 0082 §1, §11): groupId rides every Captured layout entry verbatim.
            // null = singleton. The persistence path makes no judgement on it (no derive-from-coordinates,
            // no merge — those are the SnapOnRelease attach commit's job, Slice B).
            doc.floatingWindows.Add(new FloatingWindowLayout(
                e.id, e.kind,
                e.rt.anchoredPosition.x, e.rt.anchoredPosition.y,
                e.rt.sizeDelta.x, e.rt.sizeDelta.y,
                i, e.rt.gameObject.activeSelf, e.groupId));
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
                    // #104 F1 (ADR-0019 / findings 0082 §1, §11): restore-time groupId pass-through with
                    // legacy-null tolerance. A doc that names a window already present re-applies the saved
                    // groupId — BUT a null in the doc (legacy sidecar lacking the field, or a save predating
                    // any attach) MUST NOT stomp a live non-null groupId. JsonUtility can't distinguish
                    // "field absent" from "explicit null", so we adopt the asymmetric rule: write through
                    // only when the saved value is non-null. Save always re-captures live state, so a saved
                    // null genuinely means "no group at save time" — the round-trip is preserved in practice.
                    // Apply is the SOLE programmatic write to groupId other than the SnapOnRelease attach
                    // commit (Slice B) / detach (Slice D).
                    if (!string.IsNullOrEmpty(w.groupId)) e.groupId = w.groupId;
                }
                else
                {
                    // #104: restore-time spawn carries the saved groupId via the new overload, so a group
                    // member spawned from a saved doc lands with its membership intact (no attach replay).
                    rt = Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible, w.groupId);
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
