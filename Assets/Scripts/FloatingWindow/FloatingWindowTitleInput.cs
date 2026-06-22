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

    // #104 (ADR-0019 / findings 0082 §6): the canvas-LOGICAL drag-start anchor and running cursor.
    // restAtDragStart is the dragged top-left at OnBeginDrag; cursor is restAtDragStart plus the
    // accumulated frameDelta over the drag. ADR-0024's controller resolves the 3-mode drag from
    // |cursor - dragStart| each frame (NOT a re-derive from rectangles — a window that snaps mid-drag
    // does not corrupt the metric).
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
    // window on top while it is being dragged even if OnPointerDown was missed. ADR-0024: snapshot the
    // dragged's logical rest position so subsequent OnDrag frames evaluate |cursor - dragStart| against
    // D_DETACH_PX without re-reading anchoredPosition each frame (which the controller re-positions every
    // frame in Translate/Detach — re-reading would corrupt the metric).
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
        // ADR-0024 §2/§7: open the controller's drag session — snapshot the island + rest rects so the
        // 3-mode dispatcher real-renders absolutely from rest and ESC can revert.
        _windows?.BeginDrag(_windowId, _restAtDragStart);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (_windows == null || _canvas == null || _viewport == null) return;
        if (_escCanceled) return;   // ESC already canceled this drag — feed the controller nothing more

        Vector2 viewportDelta = ScreenDeltaToViewportLocal(eventData);
        float zoom = _canvas.CaptureView().zoom;
        Vector2 frameDelta = FloatingWindowMath.ViewportDeltaToLogical(viewportDelta, zoom);
        _cursorLogical += frameDelta;
        // ADR-0024 §2/§3/§7: DragApplyDelta resolves the 3 modes from cursor position and real-renders
        // the preview (Swap = ghosts, Translate = whole island, Detach = dragged only) with in-drag
        // magnetic snap. frameDelta is passed for signature parity; positioning is absolute from the
        // drag-start snapshot.
        _windows.DragApplyDelta(_windowId, _restAtDragStart, _cursorLogical, frameDelta);
    }

    // ADR-0024 §4 / findings 0088 §4: release commit. ReleaseDrag re-resolves the final mode from the
    // release cursor and commits the universal release-position outcome (swap / translate / detach,
    // with overlap→nearest-flush merge). After an ESC cancel it commits nothing.
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
