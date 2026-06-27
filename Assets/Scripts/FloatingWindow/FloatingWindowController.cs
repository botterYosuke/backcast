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

    // ADR-0024 §2/§7 / findings 0088 §1, §7 (ADR-0029 §1): the per-drag snapshot that carries the FIXED
    // gesture-channel dispatch and makes ESC-cancel trivial. Captured at BeginDrag: the dragged's drag-start top-left, the dragged's
    // ISLAND (every visible/live member sharing its non-null groupId when ≥2, else just {dragged}), and
    // each island member's REST rect. Every frame re-derives the live position ABSOLUTELY from the rest
    // rect + (cursor - dragStart) + magnetic snap, so a stale/transient frame can never corrupt the
    // geometry, and ESC reverts by writing the rest rects straight back. `canceled` short-circuits the
    // release commit (ESC already reverted; MouseUp must commit nothing).
    class DragSession
    {
        public string draggedId;
        public Vector2 dragStart;
        // ADR-0029 §1 / findings 0106 §1: the gesture channel, fixed at BeginDrag and INVARIANT for the
        // whole drag (the per-frame `ResolveDragMode` is RETIRED — a drag can never morph mid-gesture).
        public FloatingWindowMath.DragChannel channel;
        public List<string> islandIds;                                         // includes draggedId
        public Dictionary<string, FloatingWindowMath.DockRect> restRects;      // per island member
        public bool canceled;
        // ADR-0024 (efficiency): drag-INVARIANTS snapshotted once at BeginDrag so the per-frame
        // DragApplyDelta does not re-allocate them. islandBbox = union of rest rects; islandMembers =
        // GroupMember rects (rest) for the swap scan; nonIslandRects = every visible/live window NOT in
        // the island (the magnetic-snap "others" — external windows do not move during a title drag).
        public FloatingWindowMath.DockRect islandBbox;
        public List<FloatingWindowMath.GroupMember> islandMembers;
        public List<FloatingWindowMath.DockRect> nonIslandRects;
    }

    // ADR-0029 §4 / findings 0106 §3: the committed outcome of a release, returned by ReleaseDrag so the
    // AFK gate (and any caller) can pin which channel + drop outcome fired. IslandMoved is the only
    // IslandMove result (it never detaches); Swapped / Merged / Detached are the SingleWindowPickup
    // drop outcomes.
    public enum ReleaseResult { IslandMoved, Swapped, Merged, Detached }

    // ADR-0030 §3/§6 / findings 0112: the per-RESIZE snapshot — the grip's OWN session, captured at
    // BeginResize and INDEPENDENT of the title-bar DragSession (the grip is NOT a title-bar drag and never
    // enters ResolveChannel, so ADR-0029's gesture-channel invariant is untouched). Holds the resized
    // window's rest rect + every island member's rest rect, so each ResizeApply re-derives the new geometry
    // ABSOLUTELY from rest (a transient frame can't corrupt it) and CancelResize reverts by writing the rest
    // rects straight back. `canceled` short-circuits the release commit (ESC already reverted).
    class ResizeSession
    {
        public string resizedId;
        public Vector2 grabStart;                                            // logical anchor at grab (delta source)
        public List<string> islandIds;                                       // includes resizedId
        public Dictionary<string, FloatingWindowMath.DockRect> restRects;    // per island member (rest); [resizedId] = the resized's rest rect
        public bool canceled;
    }

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
    // ADR-0029 §6 / findings 0106 §3: the LAST resolved swap-target id from the most recent SingleWindowPickup
    // frame whose cursor sat over a sibling. Cached so CommitSwapReflow at OnEndDrag commits against the same
    // target the last-painted reflow ghost predicted, even if sibling order / rects shifted between the final
    // ghost frame and the release. Null whenever the pickup is not currently over a sibling; CommitSwapReflow
    // falls back to the release-frame's resolved target when null, so a degenerate release stays defined.
    string _lastSwapTargetId;

    // #101 (fix #99 regression; findings 0078): the window the USER last focused via a title-bar
    // press (NoteUserFocus). The dock-spawn path (SpawnDockedToFocus) snaps a new window flush to it.
    // Set ONLY by NoteUserFocus — programmatic Show / BringToFront / Spawn never write it (a layout
    // restore BringToFronts every window and must NOT forge a focus target). May go stale when the
    // focused window is closed/hidden; the resolver re-validates liveness, so staleness is harmless.
    string _lastUserFocusedId;

    // ADR-0024 §2/§7: the live drag snapshot (null between drags). Owned by BeginDrag / DragApplyDelta /
    // ReleaseDrag / CancelDrag.
    DragSession _drag;

    // ADR-0030 §6 / findings 0112: the live RESIZE snapshot (null between resizes). Owned by BeginResize /
    // ResizeApply / ReleaseResize / CancelResize. Separate from _drag — a resize and a title-drag are
    // mutually exclusive in practice (one pointer), but the controller keeps them in independent fields so
    // the grip system never touches the gesture-channel drag machinery (ADR-0030 §3).
    ResizeSession _resize;

    // ADR-0024 §3 / findings 0088 §3: the injected spring side-effect ("プルン" rect interpolation). The
    // controller ALWAYS writes the authoritative final geometry directly (so the AFK gate sees the
    // settled rect without a driver); this optional hook lets the production RectSpringDriver animate the
    // visual transition from→to over SPRING_DURATION_MS. Null ⇒ no animation (AFK / headless). Signature:
    // (rt, fromRect, toRect). Injected via SetSpringAnimator; a recorder can be injected by the gate to
    // pin the fire-points (engage / commit / ESC) non-vacuously.
    Action<RectTransform, FloatingWindowMath.DockRect, FloatingWindowMath.DockRect> _springAnim;
    // ADR-0024 §3 (review fix): stop+settle any in-flight spring tween on an rt. BeginDrag calls this
    // for every island member so a re-grab WITHIN the 200ms commit/ESC tween reads a SETTLED rest pose
    // (not a transient overshoot) and the dying tween cannot fight the new drag's per-frame writes. Null
    // ⇒ no driver (AFK / headless — nothing to stop).
    Action<RectTransform> _springStop;

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

    // #125 (ADR-0026 §25): true while a title-bar drag gesture is live (between BeginDrag and the
    // mouse-up ReleaseDrag), INCLUDING the post-ESC-cancel window (CancelDrag sets _drag.canceled but
    // does NOT null _drag — only the mouse-up ReleaseDrag does). So an ESC pressed mid-drag always reads
    // IsDragging==true regardless of MonoBehaviour Update ordering, letting the Settings ESC guard defer
    // to the drag-revert (ADR-0024 §8) instead of toggling Settings.
    public bool IsDragging => _drag != null;

    // ADR-0030 §3 / findings 0112: true between BeginResize and the release ReleaseResize (INCLUDING the
    // post-ESC window — CancelResize sets canceled but does NOT null _resize; only ReleaseResize does).
    // INDEPENDENT of IsDragging — the grip's resize session and the title-bar drag session never coexist
    // on the same gesture, and a resize never flips IsDragging (ADR-0029 channel invariant untouched).
    public bool IsResizing => _resize != null;

    // #139 (ADR-0030 §3 review / simplify): "is ANY geometry gesture (drag OR resize) live on this controller?"
    // — the single predicate the Settings ESC guard consults, so the call site no longer hand-ORs the gesture
    // taxonomy (a future gesture kind extends THIS accessor, not every consumer). IsDragging / IsResizing stay
    // public for callers that must distinguish the two (the AFK gate asserts them separately); this only
    // aggregates the read — it does not couple the sessions (ADR-0030 §3 separation is a field/input concern).
    public bool IsGestureActive => _drag != null || _resize != null;
    public RectTransform RectOf(string id) => _windows.TryGetValue(id, out var e) ? e.rt : null;

    // #138 (findings 0110): mode-conditional visibility for a whole window KIND — used by the root's
    // DriveStrategyEditor (the strategy_editor surface is hidden in LiveManual, the mirror of the order
    // ticket). Allocation-free (struct dictionary enumerator), so it is safe to call every poll frame
    // unlike a List-returning enumerator. The hide/show pair is asymmetric ON PURPOSE: HideKind records
    // the ids it actually hid into `recordHidden`, and ShowHidden re-shows ONLY those — so a window hidden
    // for an INDEPENDENT reason (a dormant region_001 shell after DeleteCell, ADR-0013 D4) is never
    // resurrected by the mode toggle. SetActive only (no BringToFront): geometry and z are preserved.
    public void HideKind(string kind, HashSet<string> recordHidden)
    {
        if (string.IsNullOrEmpty(kind) || recordHidden == null) return;
        foreach (var kv in _windows)
            if (kv.Value.kind == kind && kv.Value.rt != null && kv.Value.rt.gameObject.activeSelf)
            {
                kv.Value.rt.gameObject.SetActive(false);
                recordHidden.Add(kv.Key);
            }
    }

    public void ShowHidden(HashSet<string> hidden)
    {
        if (hidden == null || hidden.Count == 0) return;
        foreach (var id in hidden)
            if (_windows.TryGetValue(id, out var e) && e.rt != null) e.rt.gameObject.SetActive(true);
        hidden.Clear();
    }

    // #104 (ADR-0019 / findings 0082 §8): bind a ghost overlay to this controller. Production root
    // mints one per plane (the back DockLayer plane has its own ghost layer; the front
    // FloatingWindowLayer plane has its own — neither leaks). The controller calls Render() during
    // DragApplyDelta with a mode-specific ghost composition and Clear() at ReleaseDrag (commit-on-
    // release rule from findings 0082 §8 — ghosts vanish at the end of the drag).
    public void AttachGhostLayer(DragGhostLayer ghostLayer) { _ghostLayer = ghostLayer; }
    public DragGhostLayer GhostLayer => _ghostLayer;

    // ADR-0024 §3 / findings 0088 §3: bind the spring animator (production RectSpringDriver.Animate, or a
    // gate recorder) and, optionally, its stop+settle hook (RectSpringDriver.Stop). Null clears them. The
    // controller still writes the final geometry directly regardless of the animator.
    public void SetSpringAnimator(
        Action<RectTransform, FloatingWindowMath.DockRect, FloatingWindowMath.DockRect> springAnim,
        Action<RectTransform> springStop = null)
    {
        _springAnim = springAnim;
        _springStop = springStop;
    }

    // Fire the spring from `from` to the rt's CURRENT (already-settled) rect. Caller writes the final
    // geometry first, then calls this so production animates from the pre-commit pose into it. No-op when
    // no animator is bound or the rects are identical (nothing to animate).
    void FireSpring(RectTransform rt, FloatingWindowMath.DockRect from)
    {
        if (_springAnim == null || rt == null) return;
        var to = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta);
        if (from.topLeft == to.topLeft && from.size == to.size) return;
        _springAnim(rt, from, to);
    }
    // #104 (ADR-0019 / findings 0082 §1): the group-membership read seam. null = singleton. The math
    // layer (FloatingWindowMath drop classifiers) and the input layer (channel pick) read through this
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

    // ADR-0024 §2/§7 / findings 0088 §1, §7: open a drag session — snapshot the dragged's drag-start
    // top-left, its island (visible/live members sharing a non-null groupId when ≥2; else just {dragged}),
    // and every island member's rest rect. The title input calls this from OnBeginDrag; DragApplyDelta /
    // ReleaseDrag self-heal via EnsureDragSession if a caller (or the AFK gate) skips it. Idempotent —
    // re-opening for the same id re-snapshots from the current (rest) geometry.
    public void BeginDrag(string id, Vector2 dragStart)
        => BeginDrag(id, dragStart, FloatingWindowMath.DragChannel.IslandMove);

    // ADR-0029 §1 / findings 0106 §1: open a drag session on a fixed gesture channel. The title input
    // resolves the channel ONCE at OnBeginDrag (Alt → SingleWindowPickup, else IslandMove; ADR-0032)
    // and the channel is INVARIANT for the rest of the drag. A singleton's SingleWindowPickup folds onto
    // IslandMove semantics for the carry render (it is already independent) but still classifies the drop.
    public void BeginDrag(string id, Vector2 dragStart, FloatingWindowMath.DragChannel channel)
    {
        _drag = null;
        _lastSwapTargetId = null;   // a fresh drag has no cached swap target (ghost memoization starts clean)
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return;
        var islandIds = ResolveIslandIds(dragged);

        // Review fix: settle any in-flight commit/ESC spring on the island members FIRST, so the rest
        // snapshot below reads each window's settled target — not a transient overshoot — and no dying
        // tween fights this drag's per-frame writes.
        if (_springStop != null)
            foreach (var mid in islandIds)
                if (_windows.TryGetValue(mid, out var se) && se.rt != null) _springStop(se.rt);

        var session = new DragSession
        {
            draggedId = id,
            dragStart = dragStart,
            channel = channel,
            islandIds = islandIds,
            restRects = new Dictionary<string, FloatingWindowMath.DockRect>(islandIds.Count),
            islandMembers = new List<FloatingWindowMath.GroupMember>(islandIds.Count),
        };
        var islandSet = new HashSet<string>(islandIds);
        float left = float.PositiveInfinity, top = float.NegativeInfinity;
        float right = float.NegativeInfinity, bottom = float.PositiveInfinity;
        foreach (var mid in islandIds)
        {
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            var rect = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
            session.restRects[mid] = rect;
            session.islandMembers.Add(new FloatingWindowMath.GroupMember { id = mid, rect = rect, siblingIndex = e.rt.GetSiblingIndex() });
            if (rect.Left < left) left = rect.Left;
            if (rect.Right > right) right = rect.Right;
            if (rect.Top > top) top = rect.Top;
            if (rect.Bottom < bottom) bottom = rect.Bottom;
        }
        session.islandBbox = new FloatingWindowMath.DockRect(new Vector2(left, top), new Vector2(right - left, top - bottom));

        // The magnetic-snap "others" set: every visible/live window NOT in the island. External windows
        // do not move during a title drag, so this is a drag-invariant snapshot.
        session.nonIslandRects = new List<FloatingWindowMath.DockRect>(_windows.Count);
        foreach (var kv in _windows)
        {
            if (islandSet.Contains(kv.Key)) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            session.nonIslandRects.Add(new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta));
        }
        _drag = session;
    }

    void EnsureDragSession(string id, Vector2 dragStart)
    {
        if (_drag == null || _drag.draggedId != id) BeginDrag(id, dragStart);
    }

    void EndDrag()
    {
        _drag = null;
        _lastSwapTargetId = null;
        if (_ghostLayer != null) _ghostLayer.Clear();
    }

    // ADR-0024 §1 / findings 0088 §1: the dragged's island = every visible/live window sharing its
    // groupId, BUT only when that set has ≥2 members (a singleton-in-name groupId is not a group —
    // findings 0082 §2). Otherwise the island is just {dragged}. Always includes the dragged id.
    List<string> ResolveIslandIds(Entry dragged)
    {
        var ids = new List<string> { dragged.id };
        if (string.IsNullOrEmpty(dragged.groupId)) return ids;
        var members = new List<string>();
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != dragged.groupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            members.Add(kv.Key);
        }
        if (members.Count < 2) return ids;            // singleton: island is just the dragged
        return members;                               // ≥2: the real island (already includes dragged)
    }

    // NOTE: the swap-scan members (`_drag.islandMembers`), the magnetic-snap "others"
    // (`_drag.nonIslandRects`), and the island bbox (`_drag.islandBbox`) are drag-INVARIANTS snapshotted
    // once in BeginDrag (the island moves as a unit; external windows do not move during a title drag),
    // so DragApplyDelta reads them per-frame WITHOUT re-allocating. The swap test runs against REST slots
    // so a mid-drag TRANSLATE cannot make the cursor "fall out of" a sibling slot.

    // ADR-0024 §8 / findings 0088 §6: ESC during a drag — actively revert the live render (TRANSLATE
    // island / DETACH dragged) to the drag-start rest via spring, and mark the session canceled so the
    // following MouseUp commits NOTHING. SWAP needed no live move (geometry was frozen + ghosts), so the
    // revert is a no-op there beyond clearing ghosts. State (groupId / persisted rect) was never touched
    // during the drag, so there is nothing to roll back beyond geometry. No-op when no drag is active or
    // the id does not match the active drag.
    public void CancelDrag(string id)
    {
        if (_drag == null || _drag.draggedId != id) return;
        foreach (var mid in _drag.islandIds)
        {
            if (!_drag.restRects.TryGetValue(mid, out var rest)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            var from = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
            e.rt.anchoredPosition = rest.topLeft;
            e.rt.sizeDelta = rest.size;
            FireSpring(e.rt, from);
        }
        _drag.canceled = true;
        _lastSwapTargetId = null;
        if (_ghostLayer != null) _ghostLayer.Clear();
    }

    // ADR-0024 §1: is `id` a member of the active drag's island? (used to exclude island members from
    // the magnetic-snap / overlap-merge "other window" scans).
    bool IsInDragIsland(string id) => _drag != null && _drag.islandIds.Contains(id);

    // ADR-0024 §4 / findings 0088 §4: find the window the cursor sits over that is NOT in the drag island
    // — the "別 island Y" of the overlap-merge rule. Multiple overlaps break by top sibling (front-most),
    // mirroring the swap resolver. Returns the window's id (out rect + groupId) or null.
    string FindOverlapWindowAtCursor(Vector2 cursor, out FloatingWindowMath.DockRect rect, out string groupId)
    {
        rect = default; groupId = null;
        string best = null; int bestSibling = int.MinValue;
        foreach (var kv in _windows)
        {
            if (IsInDragIsland(kv.Key)) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            var r = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta);
            if (cursor.x < r.Left || cursor.x > r.Right) continue;
            if (cursor.y < r.Bottom || cursor.y > r.Top) continue;
            int sib = rt.GetSiblingIndex();
            if (sib > bestSibling) { best = kv.Key; bestSibling = sib; rect = r; groupId = kv.Value.groupId; }
        }
        return best;
    }

    // ADR-0024 §4 / findings 0088 §5 (review fix): the OVERLAP target's island bounding box. The
    // release-position rule snaps the moving island/window flush to the TARGET ISLAND's outer bbox — NOT
    // the single frontmost member rect under the cursor (yRect), which could be an INTERNAL edge of a
    // multi-window island (dropping onto it would land on an internal seam). Collect every visible/live
    // window sharing Y's groupId; a real island (≥2) → the union bbox, a singleton-in-name (exactly 1 live
    // member) → that member's FRESH rect (it IS Y, read from _windows rather than the captured yRect), and
    // the null/empty-group or unreachable 0-member case → yRect itself (the member is the whole "island").
    FloatingWindowMath.DockRect ResolveTargetIslandBbox(string yGroupId, FloatingWindowMath.DockRect yRect)
    {
        if (string.IsNullOrEmpty(yGroupId)) return yRect;
        var rects = new List<FloatingWindowMath.DockRect>();
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != yGroupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            rects.Add(new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta));
        }
        if (rects.Count < 2) return rects.Count == 1 ? rects[0] : yRect;   // 1 live member ⇒ that member's
        return FloatingWindowMath.UnionBbox(rects);                        // FRESH rect (== Y); 0 ⇒ yRect fallback
    }

    // ADR-0029 §1/§4/§5 / findings 0106 §2, §3: the SINGLE production release entry. Called from
    // FloatingWindowTitleInput.OnEndDrag with the drag-start anchor and the running cursor. It branches on
    // the FIXED gesture channel (not on cursor distance — that trigger is retired):
    //
    //   IslandMove (plain title-bar drag) → CommitTranslate: the whole island shifts by the cursor offset;
    //              on flush/overlap with another island it merges (owner Q3). It NEVER detaches — membership
    //              only shrinks via the pickup channel (root of owner unhappiness 1). Returns IslandMoved.
    //   SingleWindowPickup (Alt+drag; ADR-0032) → CommitPickup: the drop POSITION decides Swap (size-preserving
    //              anchor swap + island reflow) / MergeToIsland (join a flushed other island) / Detach (empty
    //              space → groupId=null, remnant dissolves). Returns Swapped / Merged / Detached.
    //
    // If ESC canceled the drag, NOTHING commits (the geometry was already reverted to rest).
    public ReleaseResult ReleaseDrag(string id, Vector2 dragStart, Vector2 cursor)
    {
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var dragged) || dragged.rt == null)
        {
            EndDrag();
            return ReleaseResult.IslandMoved;
        }
        EnsureDragSession(id, dragStart);

        // ADR-0029 §8 (ADR-0024 §8 maintained): ESC already reverted the geometry and marked the session
        // canceled — commit nothing.
        if (_drag != null && _drag.canceled)
        {
            EndDrag();
            return ReleaseResult.IslandMoved;
        }

        // ADR-0029 §1: the channel was fixed at BeginDrag — the release branches on it, NEVER on cursor
        // distance. IslandMove translates the whole island (and may merge into another island) but NEVER
        // detaches; SingleWindowPickup's outcome is decided by the drop position (ResolveDropOutcome).
        ReleaseResult result;
        if (_drag.channel == FloatingWindowMath.DragChannel.SingleWindowPickup)
        {
            result = CommitPickup(id, dragged.groupId, cursor - dragStart, cursor);
        }
        else
        {
            CommitTranslate(id, cursor - dragStart);
            result = ReleaseResult.IslandMoved;
        }

        EndDrag();
        return result;
    }

    // ADR-0024 §4 / findings 0088 §4: TRANSLATE release commit. Shift the whole island by the cursor
    // offset. If the cursor sits over a window outside the island (overlap with island Y), snap the
    // island's bbox to Y's nearest flush edge and merge; otherwise keep the magnetic-snapped position and
    // run the incidental flush-attach commit (ADR-0019 D4 — a release that happens to land flush attaches).
    void CommitTranslate(string id, Vector2 offset)
    {
        var movingAtOffset = Shift(_drag.islandBbox, offset);
        string yId = FindOverlapWindowAtCursor(_drag.dragStart + offset, out var yRect, out var yGroup);
        if (yId != null)
        {
            // Cursor over a non-island window → snap the moving island bbox flush to the TARGET ISLAND's
            // OUTER bbox (not yRect — the single frontmost member under the cursor, which could be an
            // internal edge of a multi-window target island), then merge DIRECTLY with Y. The merge is
            // driven by the release-position rule (cursor-over-Y intent), NOT by a member-level flush
            // rescan: for a non-rectangular / mixed-size island the bbox edge that ends up flush to Y may
            // belong to a member that does not itself y/x-overlap Y, so a flush rescan would miss it and
            // silently fail to merge (review fix).
            var targetBbox = ResolveTargetIslandBbox(yGroup, yRect);
            Vector2 appliedOffset = offset + FloatingWindowMath.ResolveNearestFlush(movingAtOffset, targetBbox);
            ApplyIslandOffsetWithSpring(appliedOffset);
            CommitMergeWithTarget(id, yId);
        }
        else
        {
            // Empty space → keep the magnetic-snapped position, then run the INCIDENTAL flush-attach
            // commit (ADR-0019 D4 — a release that happens to land flush attaches; island-wide scan).
            Vector2 appliedOffset = offset + FloatingWindowMath.ComputeMagneticSnap(
                movingAtOffset, _drag.nonIslandRects, FloatingWindowMath.R_SNAP_PX);
            ApplyIslandOffsetWithSpring(appliedOffset);
            CommitFlushAttachOnRelease(id);
        }
    }

    // ADR-0029 §4 / findings 0106 §3: SINGLE-WINDOW-PICKUP release commit — the DROP POSITION decides the
    // outcome (owner Q1). The picked window's release rect = its rest + offset + in-drag magnet; the pure
    // ResolveDropOutcome classifies it:
    //   Swap         — cursor over a sibling rect → size-preserving anchor swap + island-scoped reflow.
    //   MergeToIsland— picked rect magnet-flush against another island → A leaves its old island and joins
    //                  the target's island (singleton → merge cascade).
    //   Detach       — empty space → A.groupId=null, remnant chain-dissolves below 2.
    // detach NEVER happens on the IslandMove channel — membership only shrinks here (root of unhappiness 1).
    ReleaseResult CommitPickup(string id, string oldGroup, Vector2 offset, Vector2 cursor)
    {
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return ReleaseResult.Detached;

        var aRest = _drag.restRects.TryGetValue(id, out var r) ? r
                    : new FloatingWindowMath.DockRect(dragged.rt.anchoredPosition, dragged.rt.sizeDelta);
        var aAtOffset = Shift(aRest, offset);
        Vector2 snap = FloatingWindowMath.ComputeMagneticSnap(aAtOffset, _drag.nonIslandRects, FloatingWindowMath.R_SNAP_PX);
        var pickedRect = Shift(aRest, offset + snap);

        var res = FloatingWindowMath.ResolveDropOutcome(
            cursor, pickedRect, _drag.islandMembers, id, CaptureNonIslandMembers(), FloatingWindowMath.R_SNAP_PX);

        if (res.outcome == FloatingWindowMath.DropOutcome.Swap)
        {
            CommitSwapReflow(id, res.swapTargetId);
            return ReleaseResult.Swapped;
        }

        var from = new FloatingWindowMath.DockRect(dragged.rt.anchoredPosition, dragged.rt.sizeDelta);

        if (res.outcome == FloatingWindowMath.DropOutcome.MergeToIsland
            && _windows.TryGetValue(res.mergeTargetId, out var yEntry) && yEntry.rt != null)
        {
            // findings 0106 §3: snap A flush to the TARGET ISLAND's OUTER bbox (ResolveNearestFlush) — NOT
            // the per-window magnet, which could land A flush to an INTERNAL seam of a multi-member island
            // (the same bug CommitTranslate's overlap branch guards against). This also single-sources the
            // landing position and the group target on res.mergeTargetId (no disagreement between them).
            var yRect = new FloatingWindowMath.DockRect(yEntry.rt.anchoredPosition, yEntry.rt.sizeDelta);
            var targetBbox = ResolveTargetIslandBbox(yEntry.groupId, yRect);
            Vector2 appliedOffset = offset + FloatingWindowMath.ResolveNearestFlush(Shift(aRest, offset), targetBbox);
            dragged.groupId = null;   // A leaves its old island (singleton) FIRST so only A moves into Y
            dragged.rt.anchoredPosition = aRest.topLeft + appliedOffset;
            FireSpring(dragged.rt, from);
            CommitMergeWithTarget(id, res.mergeTargetId);   // cascade assigns the surviving groupId
            if (!string.IsNullOrEmpty(oldGroup)) DissolveIfShrunkTo(oldGroup, 2);
            return ReleaseResult.Merged;
        }

        // Detach (empty space — neither sibling nor flush): land at the magnet-flush release rect, leave the
        // island. (A vanished merge target also falls here — degenerate, treat as detach.)
        dragged.groupId = null;
        dragged.rt.anchoredPosition = aRest.topLeft + offset + snap;
        FireSpring(dragged.rt, from);
        if (!string.IsNullOrEmpty(oldGroup)) DissolveIfShrunkTo(oldGroup, 2);
        return ReleaseResult.Detached;
    }

    // ADR-0029 §4 / findings 0106 §3: every visible/live window NOT in the drag island, as GroupMembers
    // (id + rect + siblingIndex) — the merge candidates for ResolveDropOutcome's flush-to-another-island
    // scan (mirrors the swap scan's frontmost-sibling tie-break, hence the siblingIndex).
    List<FloatingWindowMath.GroupMember> CaptureNonIslandMembers()
    {
        var members = new List<FloatingWindowMath.GroupMember>(_windows.Count);
        foreach (var kv in _windows)
        {
            if (IsInDragIsland(kv.Key)) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            members.Add(new FloatingWindowMath.GroupMember
            {
                id = kv.Key,
                rect = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta),
                siblingIndex = rt.GetSiblingIndex(),
            });
        }
        return members;
    }

    // ADR-0024 §4 / findings 0088 §5 (review fix): merge the dragged's island into the OVERLAP target Y's
    // island. Driven by the release-position rule (cursor was over Y), so it does NOT depend on a
    // member-level flush rescan — a non-rectangular island whose bbox edge member is not itself flush to Y
    // still merges. The cascade (size max > dict min > new GUID) picks the surviving groupId; every member
    // of both contributing groups (plus the dragged + Y themselves) is rewritten to it.
    void CommitMergeWithTarget(string draggedId, string targetId)
    {
        if (!_windows.TryGetValue(draggedId, out var dragged)) return;
        if (!_windows.TryGetValue(targetId, out var target)) return;

        var seen = new HashSet<string>();
        var cands = new List<FloatingWindowMath.MergeCandidate>(2);
        AddCandidateFor(cands, seen, dragged.groupId);
        AddCandidateFor(cands, seen, target.groupId);
        string winnerId = FloatingWindowMath.ResolveMergeWinner(cands) ?? MintGroupId();

        var contributingGroupIds = new HashSet<string>();
        if (!string.IsNullOrEmpty(dragged.groupId)) contributingGroupIds.Add(dragged.groupId);
        if (!string.IsNullOrEmpty(target.groupId)) contributingGroupIds.Add(target.groupId);
        var winners = new HashSet<string> { draggedId, targetId };
        if (contributingGroupIds.Count > 0)
            foreach (var kv in _windows)
                if (contributingGroupIds.Contains(kv.Value.groupId)) winners.Add(kv.Key);

        foreach (var wid in winners)
            if (_windows.TryGetValue(wid, out var we)) we.groupId = winnerId;
    }

    // ADR-0029 §6 / findings 0106 §4: SWAP release commit — SIZE-PRESERVING anchor exchange + island-scoped
    // reflow (the ADR-0024 4-value `(x,y,w,h)` exchange is RETIRED, owner Q5). A and B keep their own
    // (w,h); only their anchors swap, then the same-island neighbours re-snap flush ("ぷるん") to close the
    // gaps the size mismatch opens. The pure ReflowIslandAfterSwap returns the post-swap+reflow rect for
    // every member; the commit writes each + springs it from its pre-commit pose. kind/id/groupId untouched
    // — the island footprint stays put, only the rects rotate + settle. The target is the cached
    // last-painted swap target (so the commit matches the preview), falling back to the resolved target.
    void CommitSwapReflow(string id, string swapTargetId)
    {
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return;
        string targetId = swapTargetId ?? _lastSwapTargetId;
        if (string.IsNullOrEmpty(targetId)
            || !_windows.TryGetValue(targetId, out var target) || target.rt == null)
        {
            // Target vanished between the last ghost frame and release — restore the dragged to rest.
            if (_drag != null && _drag.restRects.TryGetValue(id, out var rest))
            {
                var from0 = new FloatingWindowMath.DockRect(dragged.rt.anchoredPosition, dragged.rt.sizeDelta);
                dragged.rt.anchoredPosition = rest.topLeft;
                dragged.rt.sizeDelta = rest.size;
                FireSpring(dragged.rt, from0);
            }
            return;
        }

        // Pure post-swap + island-scoped reflow layout from the captured island REST rects (the stable
        // island — siblings were frozen at rest during the pickup carry). Scope is strictly the island.
        var layout = FloatingWindowMath.ReflowIslandAfterSwap(
            _drag.islandMembers, id, targetId, FloatingWindowMath.R_SNAP_PX);
        foreach (var mid in _drag.islandIds)
        {
            if (!layout.TryGetValue(mid, out var to)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            var from = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
            e.rt.anchoredPosition = to.topLeft;
            e.rt.sizeDelta = to.size;            // size unchanged by reflow, but write for symmetry
            FireSpring(e.rt, from);
        }
    }

    // Shift a DockRect's top-left by an offset (size unchanged).
    static FloatingWindowMath.DockRect Shift(FloatingWindowMath.DockRect r, Vector2 offset)
        => new FloatingWindowMath.DockRect(r.topLeft + offset, r.size);

    // ADR-0024 §4: write every island member to restRect + offset and spring it from its pre-commit pose.
    void ApplyIslandOffsetWithSpring(Vector2 offset)
    {
        foreach (var mid in _drag.islandIds)
        {
            if (!_drag.restRects.TryGetValue(mid, out var rest)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            var from = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
            e.rt.anchoredPosition = rest.topLeft + offset;
            FireSpring(e.rt, from);
        }
    }

    // ============================ ADR-0030 — window resize (grip) ============================
    // The bottom-right grip's resize session. SEPARATE from the title-bar DragSession (ADR-0030 §3): the
    // grip has its OWN raycast target + IDragHandler (FloatingWindowResizeGrip) and drives THIS session
    // directly — it never enters ResolveChannel, so the ADR-0029 gesture-channel invariant is untouched.
    // The math (FloatingWindowMath.ResizeIslandPush) is the AFK-authoritative push-out; this is the thin
    // RectTransform boundary that snapshots rest, clamps to spec.minSize, and writes the live geometry.

    // ADR-0030 §1/§6 / findings 0112: open a resize session — RAISE + record focus (grip grab == a
    // title-bar press, ADR-0030 §6 最前面化), then snapshot the resized window's rest rect AND every island
    // member's rest rect (same ResolveIslandIds the drag uses — visible/live members sharing a non-null
    // groupId when ≥2, else just {resized}). `grabStart` is the logical anchor the grip drag measures its
    // delta from (only the delta matters). Idempotent — re-opening for the same id re-snapshots from rest.
    public void BeginResize(string id, Vector2 grabStart)
    {
        _resize = null;
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var resized) || resized.rt == null) return;
        NoteUserFocus(id);   // ADR-0030 §6: grip grab raises (SetAsLastSibling) + records the focus target

        var islandIds = ResolveIslandIds(resized);
        // Settle any in-flight commit/ESC spring on the island members first, so the rest snapshot reads a
        // settled pose and no dying tween fights this resize's per-frame writes (BeginDrag does the same).
        if (_springStop != null)
            foreach (var mid in islandIds)
                if (_windows.TryGetValue(mid, out var se) && se.rt != null) _springStop(se.rt);

        var session = new ResizeSession
        {
            resizedId = id,
            grabStart = grabStart,
            islandIds = islandIds,
            restRects = new Dictionary<string, FloatingWindowMath.DockRect>(islandIds.Count),
        };
        foreach (var mid in islandIds)
        {
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            session.restRects[mid] = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
        }
        _resize = session;
    }

    // ADR-0030 §1/§4/§6 / findings 0112: per-frame resize apply. The new size is derived ABSOLUTELY from the
    // rest size + the grip's logical delta (cursor - grabStart): the grip lives at the BOTTOM-RIGHT, so
    // dragging it RIGHT grows width (+Δx) and dragging it DOWN grows height (-Δy, y up-positive). The desired
    // size is CLAMPED to spec.minSize (NO max — infinite canvas, ADR-0030 §6); then ResizeIslandPush computes
    // the resized window's new size (TOP-LEFT fixed) + the flush-following members' translations (symmetric,
    // chained, island-scoped), and the controller writes them all. Discrete real-render, NO mid-resize spring
    // (a per-frame tween would fight the absolute writes — mirror RenderTranslate). Self-heals via
    // EnsureResizeSession. Returns the clamped size actually applied to the resized window.
    public Vector2 ResizeApply(string id, Vector2 grabStart, Vector2 cursor)
    {
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var resized) || resized.rt == null)
            return Vector2.zero;
        EnsureResizeSession(id, grabStart);
        // ADR-0030 §6: after ESC the session is canceled — ignore further resize frames until release.
        if (_resize != null && _resize.canceled) return resized.rt.sizeDelta;

        Vector2 delta = cursor - _resize.grabStart;
        Vector2 restSize = _resize.restRects.TryGetValue(id, out var rr) ? rr.size : resized.rt.sizeDelta;
        Vector2 desired = new Vector2(restSize.x + delta.x, restSize.y - delta.y);
        Vector2 clamped = _catalog.TryGet(resized.kind, out FloatingWindowSpec spec)
            ? ClampSize(spec, desired.x, desired.y) : desired;

        // Pure island push-out from the captured REST island (absolute every frame, so no drift). Scope is
        // STRICTLY the captured island (ADR-0030 §5 — other islands / planes are not in restRects).
        var layout = FloatingWindowMath.ResizeIslandPush(id, _resize.restRects, clamped, DEFAULT_FLUSH_EPS);
        foreach (var mid in _resize.islandIds)
        {
            if (!layout.TryGetValue(mid, out var to)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            e.rt.anchoredPosition = to.topLeft;
            e.rt.sizeDelta = to.size;
        }
        return clamped;
    }

    // ADR-0030 §6 / findings 0112: commit the resize. The absolute model means the last ResizeApply already
    // wrote the authoritative geometry (the live render IS the committed state), so the release just closes
    // the session. After an ESC cancel the geometry was already reverted to rest and nothing commits. Persist
    // is the caller's existing Capture (schema-add 0 — ADR-0030 §6). No-op for a mismatched/absent session
    // (a stray release for a different id must not tear down the active resize).
    public void ReleaseResize(string id)
    {
        if (_resize != null && _resize.resizedId != id) return;
        EndResize();
    }

    // ADR-0030 §6 / findings 0112: ESC during a resize — actively revert the resized window's SIZE and every
    // pushed member's POSITION to the rest snapshot (spring the revert), and mark the session canceled so the
    // following release commits nothing. Mirrors CancelDrag (state was never touched during the resize, so
    // there is nothing to roll back beyond geometry). No-op when no resize is active or the id mismatches.
    public void CancelResize(string id)
    {
        if (_resize == null || _resize.resizedId != id) return;
        foreach (var mid in _resize.islandIds)
        {
            if (!_resize.restRects.TryGetValue(mid, out var rest)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            var from = new FloatingWindowMath.DockRect(e.rt.anchoredPosition, e.rt.sizeDelta);
            e.rt.anchoredPosition = rest.topLeft;
            e.rt.sizeDelta = rest.size;
            FireSpring(e.rt, from);
        }
        _resize.canceled = true;
    }

    void EnsureResizeSession(string id, Vector2 grabStart)
    {
        if (_resize == null || _resize.resizedId != id) BeginResize(id, grabStart);
    }

    void EndResize() => _resize = null;
    // ========================== end ADR-0030 — window resize (grip) ==========================

    // Move a window by a CANVAS-LOGICAL delta (the input boundary already divided the viewport
    // delta by zoom). y is up-positive, matching anchoredPosition. No-op for an unknown id.
    public void MoveByLogical(string id, Vector2 logicalDelta)
    {
        if (_windows.TryGetValue(id, out var e) && e.rt != null)
            e.rt.anchoredPosition += logicalDelta;
    }

    // ADR-0029 §1/§2/§3 / findings 0106 §2, §3: the SINGLE per-frame drag entry. The channel is FIXED (no
    // per-frame re-resolve); each frame real-renders the preview ABSOLUTELY from the drag-start snapshot
    // (rest rect + cursor offset + magnetic snap), so a transient frame can never corrupt the geometry.
    // Returns the (invariant) channel. `frameDelta` is unused for positioning (kept for signature parity) —
    // the absolute model supersedes the incremental one.
    //
    //   IslandMove        — whole island real-renders at rest + offset + magnetic snap; UNLIMITED distance;
    //                       no ghost; never detaches.
    //   SingleWindowPickup— ONLY the picked window real-renders at rest + offset + magnetic snap; siblings
    //                       stay at rest. Over a sibling rect, the post-swap+reflow island layout previews
    //                       as a ghost (the ONLY ghost-bearing state).
    public FloatingWindowMath.DragChannel DragApplyDelta(string id, Vector2 dragStart, Vector2 cursor, Vector2 frameDelta)
    {
        if (string.IsNullOrEmpty(id)) return FloatingWindowMath.DragChannel.IslandMove;
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null)
            return FloatingWindowMath.DragChannel.IslandMove;
        EnsureDragSession(id, dragStart);
        // ADR-0029 §8: after ESC the session is canceled — ignore further drag frames until release.
        if (_drag != null && _drag.canceled) return _drag.channel;

        Vector2 offset = cursor - dragStart;

        if (_drag.channel == FloatingWindowMath.DragChannel.SingleWindowPickup)
        {
            // Carry ONLY the picked window (siblings frozen at rest); over a sibling rect, preview the
            // post-swap + island reflow layout as a ghost (the ONLY ghost-bearing state). The reflow layout
            // depends only on (id, swapTarget) — both invariant while the cursor stays over the same sibling —
            // so recompute the ghost ONLY when the swap target CHANGES (no per-frame Dictionary/List churn).
            FreezeIslandToRest();
            RenderDetach(id, offset);
            string swapTarget = FloatingWindowMath.ResolveDropTarget(cursor, _drag.islandMembers, id);
            if (_ghostLayer != null && swapTarget != _lastSwapTargetId)
            {
                if (!string.IsNullOrEmpty(swapTarget)) _ghostLayer.Render(ComposeReflowGhosts(id, swapTarget));
                else _ghostLayer.Clear();
            }
            _lastSwapTargetId = swapTarget;
        }
        else
        {
            // IslandMove: the WHOLE island real-renders at rest+offset+magnet, UNLIMITED distance, no ghost.
            _lastSwapTargetId = null;
            RenderTranslate(offset);
            if (_ghostLayer != null) _ghostLayer.Clear();
        }
        return _drag.channel;
    }

    // ADR-0024 §7 / findings 0088 §2: TRANSLATE real-render — the whole island at rest + offset +
    // magnetic snap (discrete: the snap is a hard-set to the flush position each frame, "離散 snap"). The
    // spring "プルン" is NOT fired mid-drag — a per-frame tween would fight these per-frame absolute
    // writes (and depend on Update order); the spring fires only at the settled commit / ESC points where
    // no further per-frame write follows. The magnetic snap is computed on the island BBOX vs every
    // non-island window.
    void RenderTranslate(Vector2 offset)
    {
        var movingAtOffset = Shift(_drag.islandBbox, offset);
        Vector2 applied = offset + FloatingWindowMath.ComputeMagneticSnap(
            movingAtOffset, _drag.nonIslandRects, FloatingWindowMath.R_SNAP_PX);
        foreach (var mid in _drag.islandIds)
        {
            if (!_drag.restRects.TryGetValue(mid, out var rest)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            e.rt.anchoredPosition = rest.topLeft + applied;
        }
    }

    // ADR-0024 §7 / findings 0088 §2: DETACH real-render — ONLY the dragged at rest + offset + magnetic
    // snap (siblings were already frozen to rest by the caller). Discrete snap, no mid-drag spring (see
    // RenderTranslate).
    void RenderDetach(string id, Vector2 offset)
    {
        if (!_drag.restRects.TryGetValue(id, out var aRest)) return;
        if (!_windows.TryGetValue(id, out var dragged) || dragged.rt == null) return;
        var movingAtOffset = Shift(aRest, offset);
        Vector2 applied = offset + FloatingWindowMath.ComputeMagneticSnap(
            movingAtOffset, _drag.nonIslandRects, FloatingWindowMath.R_SNAP_PX);
        dragged.rt.anchoredPosition = aRest.topLeft + applied;
    }

    // ADR-0024 §7: pin every island member back to its rest rect (SWAP shows ghosts over the rested
    // windows; DETACH keeps siblings at rest). Idempotent — cheap when already at rest.
    void FreezeIslandToRest()
    {
        if (_drag == null) return;
        foreach (var mid in _drag.islandIds)
        {
            if (!_drag.restRects.TryGetValue(mid, out var rest)) continue;
            if (!_windows.TryGetValue(mid, out var e) || e.rt == null) continue;
            e.rt.anchoredPosition = rest.topLeft;
            e.rt.sizeDelta = rest.size;
        }
    }

    // ADR-0029 §5 / findings 0106 §3, §5: the SingleWindowPickup swap candidate is the ONLY ghost-bearing
    // state. The ghost previews the WHOLE post-swap + island-scoped reflow layout (not just a 2-window
    // exchange): every island member whose rect CHANGES under ReflowIslandAfterSwap gets a ghost at its
    // post-reflow rect — the picked window SOLID ("you"), every other moved member DASHED. So the user sees
    // exactly where the picked window lands AND how the neighbours "ぷるん" close the gaps. Empty list when
    // id/target is unknown. Pure composition over the captured island REST rects (siblings are frozen at
    // rest during the carry), so the AFK gate drives it headlessly via a bare-RectTransform pool.
    public List<DragGhostLayer.GhostSpec> ComposeReflowGhosts(string id, string targetId)
    {
        var ghosts = new List<DragGhostLayer.GhostSpec>();
        if (_drag == null) return ghosts;
        if (string.IsNullOrEmpty(id) || !_windows.TryGetValue(id, out var dragged) || dragged.rt == null)
            return ghosts;
        if (string.IsNullOrEmpty(targetId) || !_windows.ContainsKey(targetId))
            return ghosts;

        var layout = FloatingWindowMath.ReflowIslandAfterSwap(
            _drag.islandMembers, id, targetId, FloatingWindowMath.R_SNAP_PX);
        foreach (var m in _drag.islandMembers)
        {
            if (!layout.TryGetValue(m.id, out var to)) continue;
            // Only ghost the members that actually MOVE under post-swap + reflow (predictive preview).
            if (to.topLeft == m.rect.topLeft && to.size == m.rect.size) continue;
            if (!_windows.TryGetValue(m.id, out var e)) continue;
            ghosts.Add(new DragGhostLayer.GhostSpec
            {
                kind = e.kind, topLeft = to.topLeft, size = to.size,
                style = m.id == id ? DragGhostLayer.GhostStyle.Solid : DragGhostLayer.GhostStyle.Dashed,
            });
        }
        return ghosts;
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
        // #99 magnet-snap-on-release (ADR-0017): ApplySnapOffset moves the dragged toward the nearest
        // neighbour (12px DEFAULT_SNAP_THRESHOLD), then CommitFlushAttachOnRelease commits any flush
        // group merge (ADR-0019 D4, retained). ADR-0024's drag dispatch no longer routes through here
        // (CommitTranslate / CommitPickup use the in-drag 96px magnet directly), but SnapOnRelease stays
        // as the standalone release-snap utility the #99 AFK sections (Section10/11) gate.
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
    // `excludeGroupId` (non-null only from ReleaseDrag's Detach commit, passed as the dragged's
    // pre-detach `oldGroup`) filters out flush partners whose
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

        // ADR-0024 §4 (F: island-wide merge): the dragged's island moves as a UNIT, so a flush formed by
        // ANY island member docks the two islands — not just a flush on the dragged's own edge. Source
        // rects = the dragged + every visible/live member of its CURRENT group (singleton ⇒ just the
        // dragged; a detach commit zeroed the groupId, so the source set is again just the dragged).
        var sourceIds = new HashSet<string> { draggedId };
        if (!string.IsNullOrEmpty(dragged.groupId))
            foreach (var kv in _windows)
                if (kv.Key != draggedId && kv.Value.groupId == dragged.groupId
                    && kv.Value.rt != null && kv.Value.rt.gameObject.activeInHierarchy)
                    sourceIds.Add(kv.Key);

        // Find every flush-adjacent partner (visible/live, NOT an island source). A flush on any source
        // member counts. When excludeGroupId is non-null, partners whose CURRENT groupId == excludeGroupId
        // are skipped (findings 0082 §5: a detach commit must never silently re-form the old group).
        var flushPartnerIds = new List<string>();
        var partnerSeen = new HashSet<string>();
        foreach (var sid in sourceIds)
        {
            var srt = _windows[sid].rt;
            if (srt == null || !srt.gameObject.activeInHierarchy) continue;
            var srcRect = new FloatingWindowMath.DockRect(srt.anchoredPosition, srt.sizeDelta);
            foreach (var kv in _windows)
            {
                if (sourceIds.Contains(kv.Key)) continue;
                if (partnerSeen.Contains(kv.Key)) continue;
                var rt = kv.Value.rt;
                if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                if (excludeGroupId != null && kv.Value.groupId == excludeGroupId) continue;   // detach: never re-merge into oldGroup
                var partnerRect = new FloatingWindowMath.DockRect(rt.anchoredPosition, rt.sizeDelta);
                if (FloatingWindowMath.IsFlushAdjacent(srcRect, partnerRect, DEFAULT_FLUSH_EPS))
                { flushPartnerIds.Add(kv.Key); partnerSeen.Add(kv.Key); }
            }
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

    // #104 F9 (findings 0082 §13): visible/live member count for a groupId — the "what counts as a live
    // group member" rule in one place (rt != null && activeInHierarchy && groupId match). ADR-0024 drops
    // the hasCore projection (no Hakoniwa special-casing). Hot-path sites that need a heterogeneous
    // projection (DragApplyDelta translate render, the overlap scan, DissolveIfShrunkTo, CommitFlush-
    // AttachOnRelease winners) stay inline to avoid a per-frame delegate allocation.
    int CountLiveGroupMembers(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return 0;
        int count = 0;
        foreach (var kv in _windows)
        {
            if (kv.Value.groupId != groupId) continue;
            var rt = kv.Value.rt;
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            count++;
        }
        return count;
    }

    // Project an existing groupId into a MergeCandidate by counting its visible/live members. Singleton
    // (null/empty groupId) → a single MergeCandidate(null, 1). De-dupe by id so the same group is not
    // listed twice.
    void AddCandidateFor(List<FloatingWindowMath.MergeCandidate> cands, HashSet<string> seen, string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            cands.Add(new FloatingWindowMath.MergeCandidate(null, 1));
            return;
        }
        if (!seen.Add(groupId)) return;
        cands.Add(new FloatingWindowMath.MergeCandidate(groupId, CountLiveGroupMembers(groupId)));
    }

    // #104 (ADR-0019 D1 / findings 0082 §1): mint a fresh groupId — "grp_<Guid.NewGuid().ToString("N")>"
    // (hex32, no hyphens). Called only when ResolveMergeWinner returns null AND there is at least one
    // flush partner ⇒ all-singleton attach (the SINGLE GUID-mint trigger).
    static string MintGroupId() => "grp_" + Guid.NewGuid().ToString("N");

    // #105 (ADR-0019 D8 amendment / findings 0082 §12, findings 0083): the named-id group birth path.
    // (The first-launch FACTORY-GROUP caller, BackcastWorkspaceRoot.FormFactoryBaseGroup, was retired with
    // ADR-0038 — the base set is empty.) This remains a way a groupId becomes non-null besides (a) user
    // drag-release and (b) restore of a persisted groupId; like restore it is NOT coordinate-derived — the
    // caller names the member ids explicitly.
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
