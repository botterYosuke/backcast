// FloatingWindowTitleInput.cs — issue #15 "floating windows" (DURABLE tier, input boundary)
//
// The thin, DURABLE MonoBehaviour that turns a TITLE-BAR drag into a window MOVE and a title-bar
// press/drag-start into BRING-TO-FRONT — the production input path #15 establishes (mirroring
// #13's InfiniteCanvasInputSurface and #14's HakoniwaTileHeaderInput; the wiring is NOT thrown
// away). It does ONLY translation: it never moves/raises the window itself, it asks the
// controller.
//
// Attach to each window's TITLE BAR (a raycast-target Graphic). The window BODY must NOT be a
// raycast target, so a drag on the body falls through to the InfiniteCanvasInputSurface and PANS
// the canvas — the title bar is the only move/raise handle (findings 0008 §1, owner-locked).
// Because click-to-front fires on the title bar's pointer-down / begin-drag, body-press CANNOT
// raise the window (body press pans); a future real content view raises itself by calling
// controller.BringToFront(id) when IT receives a pointer event.
//
// COORDINATE DISCIPLINE (findings 0008 §2, owner-locked): NEVER feed raw eventData.delta to the
// controller. Under a CanvasScaler, screen pixels != viewport-local pixels, so — exactly like
// #13's pan — we convert the drag to a VIEWPORT-LOCAL delta via RectTransformUtility, then ask
// FloatingWindowMath to divide by the live zoom (read from the InfiniteCanvasController) to get a
// canvas-LOGICAL delta. The controller never sees screen/render coordinates.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;       // ADR-0024 §8: ESC poll (project uses the new Input System, activeInputHandler=1)

public class FloatingWindowTitleInput : MonoBehaviour,
    IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    FloatingWindowController _windows;
    InfiniteCanvasController _canvas;   // live zoom source (render-free controller stays clean)
    RectTransform _viewport;            // screen -> viewport-local conversion space (CanvasScaler-safe)
    string _windowId;
    // ADR-0029 §3 / findings 0106 §1: the title bar's eject handle (the "⤴" affordance, a 2nd raycast
    // target with NO drag handler so its press/drag bubbles here). When the press landed on it, the drag
    // is a SingleWindowPickup; otherwise a plain title-bar grab is an IslandMove. Null on a bare title bar
    // (no handle wired) → every drag is IslandMove unless Alt is held.
    GameObject _ejectHandle;

    // #104 (ADR-0019 / findings 0082 §6): the canvas-LOGICAL drag-start anchor and running cursor.
    // restAtDragStart is the dragged top-left at OnBeginDrag; cursor is restAtDragStart plus the
    // accumulated frameDelta over the drag. ADR-0029: the gesture CHANNEL is fixed once at OnBeginDrag
    // (the per-frame `|cursor - dragStart|` distance metric is RETIRED); the controller re-renders the
    // fixed channel ABSOLUTELY from rest + this running cursor each frame, so a window that snaps
    // mid-drag does not corrupt the geometry and the channel can never morph.
    Vector2 _restAtDragStart;
    Vector2 _cursorLogical;

    // ADR-0024 §8: true between OnBeginDrag and OnEndDrag, so Update() only polls ESC during an active
    // drag. _escCanceled latches the ESC so a second ESC in the same drag is a no-op and OnDrag stops
    // feeding the controller (the controller also guards, but skipping the per-frame work is cleaner).
    bool _dragging;
    bool _escCanceled;

    public void Initialize(FloatingWindowController windows, InfiniteCanvasController canvas, RectTransform viewport, string windowId)
    {
        _windows = windows;
        _canvas = canvas;
        _viewport = viewport;
        _windowId = windowId;
        AttachResizeGrip();
    }

    // #139 / ADR-0030 §1/§2 / findings 0112: every window grows the always-visible "◢" RESIZE GRIP here,
    // uniformly (dock / editor / order / HITL), with no per-factory wiring — the same "attach via the title
    // input" discipline the eject handle uses (Awake). The grip targets the window ROOT (this title bar's
    // parent), which — unlike the eject handle (attached to the title bar SELF in Awake) — is not available
    // at Awake time (the title bar is parented AFTER `new GameObject`), so the grip attaches from Initialize,
    // where the hierarchy is wired and the controller/canvas/viewport/id deps the grip needs are in hand.
    // Idempotent find-or-create (FloatingWindowResizeHandle.Attach), so a re-Initialize is a no-op.
    void AttachResizeGrip()
    {
        var root = transform.parent as RectTransform;
        if (root == null) return;   // bare title bar (AFK drives the grip builder directly) — nothing to attach to
        var gripGo = FloatingWindowResizeHandle.Attach(root, null);
        var grip = gripGo != null ? gripGo.GetComponent<FloatingWindowResizeGrip>() : null;
        grip?.Initialize(_windows, _canvas, _viewport, _windowId);
    }

    // ADR-0029 §3 / findings 0106 §1: every title bar grows the always-visible "⤴" eject handle here, ONCE,
    // so the SingleWindowPickup affordance appears uniformly on dock / editor / order / HITL windows with no
    // per-factory wiring. The handle is a 2nd raycast target with NO drag handler, so its press/drag bubbles
    // to this component while pointerPressRaycast still names it (the channel discriminator in OnBeginDrag).
    void Awake()
    {
        if (_ejectHandle == null)
            _ejectHandle = FloatingWindowEjectHandle.Attach((RectTransform)transform, null);
    }

    // Press on the title bar -> raise (click-to-front, title-bar only) AND record USER focus (#101 /
    // findings 0078): NoteUserFocus raises like BringToFront but ALSO marks this as the focus target so
    // the next dock spawn snaps adjacent to it. Only a genuine title-bar press records focus — a
    // programmatic BringToFront/Show (e.g. a layout restore) must not forge a target.
    public void OnPointerDown(PointerEventData eventData)
    {
        _windows?.NoteUserFocus(_windowId);
    }

    // Begin-drag also raises+focuses (a drag is a press too), then OnDrag moves. Raising here keeps the
    // window on top while it is being dragged even if OnPointerDown was missed. ADR-0029 §1/§3: the gesture
    // CHANNEL is fixed HERE, once — a press on the eject handle (pointerPressRaycast) OR a held Alt makes it
    // a SingleWindowPickup; otherwise a plain title-bar grab is an IslandMove. The channel never changes
    // mid-drag (the distance metric is retired). Snapshot the dragged's logical rest position for the
    // absolute per-frame re-render.
    public void OnBeginDrag(PointerEventData eventData)
    {
        _windows?.NoteUserFocus(_windowId);
        var rt = _windows?.RectOf(_windowId);
        if (rt != null)
        {
            _restAtDragStart = rt.anchoredPosition;
            _cursorLogical = _restAtDragStart;
        }
        _escCanceled = false;
        _dragging = true;

        bool hitEject = _ejectHandle != null && eventData.pointerPressRaycast.gameObject == _ejectHandle;
        var kb = Keyboard.current;
        bool altHeld = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
        var channel = FloatingWindowMath.ResolveChannel(hitEject, altHeld);
        // ADR-0029 §1: open the controller's drag session on the fixed channel — snapshot the island + rest
        // rects so the render is absolute from rest and ESC can revert.
        _windows?.BeginDrag(_windowId, _restAtDragStart, channel);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (_windows == null || _canvas == null || _viewport == null) return;
        if (_escCanceled) return;   // ESC already canceled this drag — feed the controller nothing more

        Vector2 viewportDelta = ScreenDeltaToViewportLocal(eventData);
        float zoom = _canvas.CaptureView().zoom;
        Vector2 frameDelta = FloatingWindowMath.ViewportDeltaToLogical(viewportDelta, zoom);
        _cursorLogical += frameDelta;
        // ADR-0029 §2/§3: DragApplyDelta real-renders the preview for the FIXED channel (IslandMove = whole
        // island, SingleWindowPickup = picked window only + swap-candidate reflow ghost) with in-drag magnetic
        // snap. frameDelta is passed for signature parity; positioning is absolute from the drag-start snapshot.
        _windows.DragApplyDelta(_windowId, _restAtDragStart, _cursorLogical, frameDelta);
    }

    // ADR-0029 §4 / findings 0106 §3: release commit. ReleaseDrag commits the channel's outcome — IslandMove
    // translates (+ possible merge), SingleWindowPickup resolves swap / merge / detach by the drop position.
    // After an ESC cancel it commits nothing.
    public void OnEndDrag(PointerEventData eventData)
    {
        _windows?.ReleaseDrag(_windowId, _restAtDragStart, _cursorLogical);
        _dragging = false;
        _escCanceled = false;
    }

    // ADR-0024 §8 / findings 0088 §6: poll ESC during an active drag. The new Input System
    // (activeInputHandler=1) exposes the keyboard via Keyboard.current; a press cancels the drag
    // (CancelDrag springs the live render back to rest and latches commit-skip). Null-safe when no
    // keyboard device is present (headless / AFK never reaches Update with _dragging true).
    void Update()
    {
        if (!_dragging || _escCanceled) return;
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            _windows?.CancelDrag(_windowId);
            _escCanceled = true;
        }
    }

    // Screen-pixel drag delta -> viewport-local delta (same mechanism #13's pan uses, so a
    // window and the canvas agree under any CanvasScaler scaleFactor; a delta is translation-
    // invariant so the pivot/centre offset cancels).
    Vector2 ScreenDeltaToViewportLocal(PointerEventData eventData)
    {
        Camera cam = eventData.pressEventCamera;   // valid during a drag (a press happened)
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position, cam, out Vector2 cur);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position - eventData.delta, cam, out Vector2 prev);
        return cur - prev;
    }
}
