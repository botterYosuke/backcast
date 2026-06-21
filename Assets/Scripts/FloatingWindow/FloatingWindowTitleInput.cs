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

public class FloatingWindowTitleInput : MonoBehaviour,
    IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    FloatingWindowController _windows;
    InfiniteCanvasController _canvas;   // live zoom source (render-free controller stays clean)
    RectTransform _viewport;            // screen -> viewport-local conversion space (CanvasScaler-safe)
    string _windowId;

    // #104 (ADR-0019 / findings 0082 §6): the canvas-LOGICAL drag-start anchor and running cursor.
    // restAtDragStart is the dragged top-left at OnBeginDrag; cursor is restAtDragStart plus the
    // accumulated frameDelta over the drag. The controller's DragApplyDelta uses |cursor - rest| as
    // the detach-threshold metric (NOT a re-derive from rectangles each frame — a window that snaps
    // mid-drag does not corrupt the metric this way).
    Vector2 _restAtDragStart;
    Vector2 _cursorLogical;

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
    // window on top while it is being dragged even if OnPointerDown was missed. #104: snapshot the
    // dragged's logical rest position so subsequent OnDrag frames can evaluate |cursor - rest| against
    // D_DETACH without re-reading anchoredPosition each frame (which may have been snapped or, in
    // NormalGroupTranslate mode, mutated by the controller — both would break the metric).
    public void OnBeginDrag(PointerEventData eventData)
    {
        _windows?.NoteUserFocus(_windowId);
        var rt = _windows?.RectOf(_windowId);
        if (rt != null)
        {
            _restAtDragStart = rt.anchoredPosition;
            _cursorLogical = _restAtDragStart;
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (_windows == null || _canvas == null || _viewport == null) return;

        Vector2 viewportDelta = ScreenDeltaToViewportLocal(eventData);
        float zoom = _canvas.CaptureView().zoom;
        Vector2 frameDelta = FloatingWindowMath.ViewportDeltaToLogical(viewportDelta, zoom);
        _cursorLogical += frameDelta;
        // #104: DragApplyDelta classifies the 7-mode drag state and applies live geometry per mode
        // (SoloDrag = dragged tracks cursor, NormalGroupTranslate = every group member tracks cursor,
        // all other modes = geometry frozen; Slice G ghost-previews them). MoveByLogical is no longer
        // called from production — Section3/Section11 still drive it directly for the pure solo-move test.
        _windows.DragApplyDelta(_windowId, _restAtDragStart, _cursorLogical, frameDelta);
    }

    // #104 (ADR-0019 / findings 0082 §5, §6, §7, §8): release commit. ReleaseDrag classifies the
    // final drag mode using the dragged's drag-start rest position + the running cursor, and commits
    // the variant outcome — magnet snap + flush-attach commit for solo / normal-translate, jump-to-
    // cursor + detach + dissolve for the detach modes, snap-back to rest for Hakoniwa core-lock /
    // snap-back, swap (x,y,w,h) for Hakoniwa swap (Slice E2). Replaces the bare SnapOnRelease call —
    // SnapOnRelease still runs INSIDE ReleaseDrag for the solo/translate/detach branches so the
    // existing magnet snap + Slice B flush-attach commit semantics are preserved unchanged.
    public void OnEndDrag(PointerEventData eventData)
    {
        _windows?.ReleaseDrag(_windowId, _restAtDragStart, _cursorLogical);
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
