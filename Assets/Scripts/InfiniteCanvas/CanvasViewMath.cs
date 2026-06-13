// CanvasViewMath.cs — issue #13 "infinite canvas" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless float arithmetic for the infinite-canvas pan/zoom
// surface — the AFK gate proves THIS (no playmode, no Canvas update), exactly as #12's
// LayoutBinder.ToNormalizedRect is the authoritative rect math. NOTHING here touches a
// RectTransform, a render, or input; InfiniteCanvasController is the thin Unity boundary
// that reads/writes Content from these results.
//
// COORDINATE MODEL (findings 0006 §2, owner-locked): all "viewport" coordinates are
// VIEWPORT-CENTRE-RELATIVE pixels (origin at the centre of the fixed Viewport, Y up).
// A CanvasView is { pan = the canvas LOGICAL point sitting at the viewport centre,
// zoom = uniform scalar }. Content is anchored/pivoted at the viewport centre with no
// rotation, so a Content-local point L maps to the viewport as:
//
//     viewport(L) = zoom * (L - pan)            // LogicalToViewport
//
// from which the whole family follows (Content.anchoredPosition = -zoom*pan, since
// L=0 must land at viewport(0) = -zoom*pan):
//
//     ViewToTransform : anchoredPosition = -zoom*pan ; localScale = (zoom,zoom,1)
//     TransformToView : zoom = localScale.x ; pan = -anchoredPosition / zoom
//     logicalUnderCursor(c) = pan + c/zoom      // inverse of viewport(L)=c
//
// CURSOR-CENTRED ZOOM keeps the logical point under the cursor fixed across the step,
// computed with the CLAMPED new zoom (findings 0006 §2, the gate's non-vacuous invariant):
//     newZoom = clamp(zoom*factor, MIN, MAX)
//     newPan  = logicalUnderCursor(c) - c/newZoom
//
// PAN by a screen drag delta d: anchoredPosition += d  =>  pan -= d/zoom (grab-and-drag).
//
// Zoom bounds live on CanvasView (MIN_ZOOM/MAX_ZOOM). Inputs are assumed already finite
// (LayoutStore.Sanitize is the normaliser at the persistence boundary); a degenerate
// zoom<=0 is defended here too so a bad live state can't divide-by-zero.

using UnityEngine;

public static class CanvasViewMath
{
    static float SafeZoom(float zoom) =>
        (zoom > 0f && !float.IsNaN(zoom) && !float.IsInfinity(zoom)) ? zoom : 1f;

    // Content-local point L -> viewport-centre-relative position.
    public static Vector2 LogicalToViewport(Vector2 logical, CanvasView v)
    {
        float zoom = SafeZoom(v.zoom);
        return new Vector2(
            zoom * (logical.x - v.panX),
            zoom * (logical.y - v.panY));
    }

    // The canvas logical point currently under a viewport-centre-relative cursor c.
    public static Vector2 LogicalUnderCursor(CanvasView v, Vector2 cursorViewportCentered)
    {
        float zoom = SafeZoom(v.zoom);
        return new Vector2(
            v.panX + cursorViewportCentered.x / zoom,
            v.panY + cursorViewportCentered.y / zoom);
    }

    // Cursor-centred zoom by a multiplicative factor (scroll up -> factor>1 -> zoom in).
    // The logical point under the cursor is invariant across the (clamped) step.
    public static CanvasView ZoomAtCursor(CanvasView v, Vector2 cursorViewportCentered, float factor)
    {
        float zoom = SafeZoom(v.zoom);
        if (factor <= 0f || float.IsNaN(factor) || float.IsInfinity(factor)) factor = 1f;

        float newZoom = Mathf.Clamp(zoom * factor, CanvasView.MIN_ZOOM, CanvasView.MAX_ZOOM);

        Vector2 underCursor = LogicalUnderCursor(v, cursorViewportCentered);
        return new CanvasView(
            underCursor.x - cursorViewportCentered.x / newZoom,
            underCursor.y - cursorViewportCentered.y / newZoom,
            newZoom);
    }

    // Pan by a screen-pixel drag delta: grab-and-drag, so pan moves -d/zoom.
    public static CanvasView PanByScreenDelta(CanvasView v, Vector2 deltaScreen)
    {
        float zoom = SafeZoom(v.zoom);
        return new CanvasView(
            v.panX - deltaScreen.x / zoom,
            v.panY - deltaScreen.y / zoom,
            v.zoom);
    }

    // CanvasView -> Content transform (anchoredPosition, localScale). Content must be
    // anchored/pivoted at the viewport centre with no rotation (the boundary's contract).
    public static void ViewToTransform(CanvasView v, out Vector2 anchoredPosition, out Vector3 localScale)
    {
        float zoom = SafeZoom(v.zoom);
        anchoredPosition = new Vector2(-zoom * v.panX, -zoom * v.panY);
        localScale = new Vector3(zoom, zoom, 1f);
    }

    // Content transform -> CanvasView (inverse of ViewToTransform).
    public static CanvasView TransformToView(Vector2 anchoredPosition, float localScaleX)
    {
        float zoom = SafeZoom(localScaleX);
        return new CanvasView(-anchoredPosition.x / zoom, -anchoredPosition.y / zoom, zoom);
    }
}
