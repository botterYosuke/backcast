// FloatingWindowResizeGrip.cs — issue #139 / ADR-0030 §1/§3/§6 / findings 0112
//
// The thin, DURABLE MonoBehaviour that turns a BOTTOM-RIGHT GRIP drag into a window RESIZE — the
// production input path #139 establishes (mirroring FloatingWindowTitleInput's move/raise path; the
// wiring is NOT thrown away). It does ONLY translation of input → controller: it never resizes the window
// itself, it asks the controller's resize session (BeginResize / ResizeApply / ReleaseResize / CancelResize).
//
// SEPARATE SYSTEM (ADR-0030 §3): UNLIKE the eject handle (no drag handler — its press BUBBLES to the title
// bar so ResolveChannel can read it), this grip IS its OWN drag handler, so its drag is SWALLOWED here and
// routed straight to the resize session. It NEVER enters ResolveChannel — the ADR-0029 gesture-channel
// invariant is untouched, and IsDragging stays false during a resize (only IsResizing flips).
//
// COORDINATE DISCIPLINE (findings 0008 §2, owner-locked — identical to the title input): NEVER feed raw
// eventData.delta to the controller. Convert the drag to a VIEWPORT-LOCAL delta via RectTransformUtility,
// then divide by the live zoom (FloatingWindowMath.ViewportDeltaToLogical) to get a canvas-LOGICAL delta,
// so the grip tracks the cursor 1:1 at any zoom. The controller never sees screen/render coordinates.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;       // ADR-0030 §6: ESC poll (project uses the new Input System, activeInputHandler=1)

public class FloatingWindowResizeGrip : MonoBehaviour,
    IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    FloatingWindowController _windows;
    InfiniteCanvasController _canvas;   // live zoom source (render-free controller stays clean)
    RectTransform _viewport;            // screen -> viewport-local conversion space (CanvasScaler-safe)
    string _windowId;

    // The canvas-LOGICAL accumulated cursor since OnBeginDrag. Only the DELTA from the grab anchor matters
    // (the new size = rest size + (Δx, -Δy)), so the anchor is a fixed Vector2.zero and the cursor accrues
    // the per-frame logical delta. The controller re-derives the size ABSOLUTELY from rest + this each frame.
    static readonly Vector2 GRAB_ANCHOR = Vector2.zero;
    Vector2 _cursorLogical;

    // ADR-0030 §6: true between OnBeginDrag and OnEndDrag, so Update() only polls ESC during an active
    // resize. _escCanceled latches the ESC so a second ESC is a no-op and OnDrag stops feeding the controller.
    bool _resizing;
    bool _escCanceled;

    public void Initialize(FloatingWindowController windows, InfiniteCanvasController canvas, RectTransform viewport, string windowId)
    {
        _windows = windows;
        _canvas = canvas;
        _viewport = viewport;
        _windowId = windowId;
    }

    // Press on the grip -> raise + record USER focus (ADR-0030 §6 最前面化, title-bar-press parity). A press
    // that does NOT become a drag still raises the window. BeginResize also raises, so this keeps the raise
    // consistent even when OnBeginDrag is missed.
    public void OnPointerDown(PointerEventData eventData)
    {
        _windows?.NoteUserFocus(_windowId);
    }

    // ADR-0030 §1/§6: begin the resize — open the controller's resize session (BeginResize raises + records
    // focus and snapshots the resized window's rest rect + its island members' rest rects for the absolute
    // per-frame re-render and the ESC revert).
    public void OnBeginDrag(PointerEventData eventData)
    {
        _cursorLogical = GRAB_ANCHOR;
        _escCanceled = false;
        _resizing = true;
        _windows?.BeginResize(_windowId, GRAB_ANCHOR);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (_windows == null || _canvas == null || _viewport == null) return;
        if (_escCanceled) return;   // ESC already canceled this resize — feed the controller nothing more

        Vector2 viewportDelta = ScreenDeltaToViewportLocal(eventData);
        float zoom = _canvas.CaptureView().zoom;
        Vector2 frameDelta = FloatingWindowMath.ViewportDeltaToLogical(viewportDelta, zoom);
        _cursorLogical += frameDelta;
        // ADR-0030 §4: ResizeApply re-renders ABSOLUTELY from the rest snapshot — new size = rest size +
        // (Δx, -Δy), clamped to spec.minSize, + the island flush-following push-out. The grip lives at the
        // bottom-right, so a rightward/downward drag grows the window.
        _windows.ResizeApply(_windowId, GRAB_ANCHOR, _cursorLogical);
    }

    // ADR-0030 §6: release commit. The absolute model already wrote the final geometry; ReleaseResize just
    // closes the session (the existing Capture persists w/h + x/y — schema-add 0). After an ESC cancel it
    // commits nothing.
    public void OnEndDrag(PointerEventData eventData)
    {
        _windows?.ReleaseResize(_windowId);
        _resizing = false;
        _escCanceled = false;
    }

    // ADR-0030 §6: poll ESC during an active resize. A press reverts the size + every pushed member to rest
    // (CancelResize springs the revert and latches commit-skip). Null-safe when no keyboard device is present
    // (headless / AFK never reaches Update with _resizing true).
    void Update()
    {
        if (!_resizing || _escCanceled) return;
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            _windows?.CancelResize(_windowId);
            _escCanceled = true;
        }
    }

    // Screen-pixel drag delta -> viewport-local delta (the same mechanism #13's pan and the title input use,
    // so the grip and the canvas agree under any CanvasScaler scaleFactor; a delta is translation-invariant
    // so the pivot/centre offset cancels).
    Vector2 ScreenDeltaToViewportLocal(PointerEventData eventData)
    {
        Camera cam = eventData.pressEventCamera;   // valid during a drag (a press happened)
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position, cam, out Vector2 cur);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position - eventData.delta, cam, out Vector2 prev);
        return cur - prev;
    }
}
