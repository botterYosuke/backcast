// InfiniteCanvasInputSurface.cs — issue #13 "infinite canvas" (DURABLE tier, input boundary)
//
// The thin, DURABLE MonoBehaviour that turns uGUI pointer events into controller calls —
// the production input path #14/#15 reuse (so AC1's wiring is NOT thrown away). It does
// ONLY translation: it never references mouse/touchpad devices directly and never mixes
// Input-System polling into the controller. The route is:
//
//   InputSystemUIInputModule -> InfiniteCanvasInputSurface -> InfiniteCanvasController
//                            -> CanvasViewMath -> Content RectTransform
//
// It implements the EventSystem drag + scroll interfaces, so it works the same for mouse
// and touchpad without device-specific code. Attach it to the VIEWPORT GameObject, which
// must carry a raycast-target Graphic (e.g. the grid Image) so the EventSystem routes
// pointer events here. Wire it with Initialize(controller, viewport) — the harness (and,
// later, the production scene) owns construction.
//
// SCREEN -> VIEWPORT-CENTRE conversion lives HERE (the controller stays render-free): a
// pointer's screen position becomes a viewport-centre-relative point via
// RectTransformUtility, so cursor-centred zoom keeps the point under the cursor fixed.

using UnityEngine;
using UnityEngine.EventSystems;

public class InfiniteCanvasInputSurface : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IScrollHandler
{
    // Multiplicative zoom per unit of scroll. scrollDelta.y>0 (scroll up) -> factor>1 ->
    // zoom in. Out-of-range results are clamped to [MIN_ZOOM,MAX_ZOOM] by CanvasViewMath.
    const float ZOOM_STEP_BASE = 1.1f;

    // Per-event scroll magnitude cap (notches): bounds a single wheel event so a large
    // platform-reported scrollDelta.y can't jump straight to a zoom bound.
    const float MAX_SCROLL_TICKS = 4f;

    InfiniteCanvasController _controller;
    RectTransform _viewport;

    public void Initialize(InfiniteCanvasController controller, RectTransform viewport)
    {
        _controller = controller;
        _viewport = viewport;
    }

    // OnBeginDrag is required for OnDrag to fire; no per-begin state needed (the controller
    // reads the live view each step), so this is intentionally empty.
    public void OnBeginDrag(PointerEventData eventData) { }

    public void OnDrag(PointerEventData eventData)
    {
        if (_controller == null || _viewport == null) return;
        // Convert the screen-pixel drag delta into VIEWPORT-LOCAL pixels through the SAME
        // mechanism the zoom path uses, so pan and zoom share one coordinate space. Feeding
        // raw eventData.delta would disagree with the (canvas-local) zoom cursor under any
        // CanvasScaler scaleFactor != 1, and the grabbed point would slide. A delta is
        // translation-invariant, so the pivot/centre offset cancels (no rect.center term).
        _controller.PanByScreenDelta(ScreenDeltaToViewportLocal(eventData));
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (_controller == null || _viewport == null) return;

        // Clamp the per-event scroll magnitude: some platforms / input configs report large
        // scrollDelta.y per notch (e.g. raw wheel ~120), and 1.1^120 would saturate to a zoom
        // bound in a single notch. Clamping keeps small/trackpad deltas smooth and caps runaway.
        float ticks = Mathf.Clamp(eventData.scrollDelta.y, -MAX_SCROLL_TICKS, MAX_SCROLL_TICKS);
        float factor = Mathf.Pow(ZOOM_STEP_BASE, ticks);
        Vector2 cursor = ScreenToViewportCentered(eventData.position, eventData.pressEventCamera);
        _controller.ZoomAtCursor(cursor, factor);
    }

    // Screen pixel -> viewport-centre-relative pixel. cam is null for ScreenSpaceOverlay.
    // Subtracting rect.center makes it pivot-agnostic (centre-relative regardless of pivot).
    Vector2 ScreenToViewportCentered(Vector2 screenPoint, Camera cam)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _viewport, screenPoint, cam, out Vector2 local);
        return local - _viewport.rect.center;
    }

    // Screen-pixel drag delta -> viewport-local delta (same units as the zoom cursor).
    Vector2 ScreenDeltaToViewportLocal(PointerEventData eventData)
    {
        Camera cam = eventData.pressEventCamera;   // valid during a drag (a press happened)
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position, cam, out Vector2 cur);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, eventData.position - eventData.delta, cam, out Vector2 prev);
        return cur - prev;
    }
}
