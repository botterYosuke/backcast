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
    // issues #166-168 / findings 0123 §1.1: ウィンドウフレーミングのデフォルト余白（窓の左右上下に 6%）と
    // dock plane の parallax 係数 (1.0×, findings 0075 §10) を **唯一の数値ソース** としてここに持つ。
    // FloatingWindowTitleInput と BackcastWorkspaceRoot.FrameChartWindowForInstrument が同じ値を別々に
    // ハードコードすると S1/S3 経路で異なる zoom になる（AC#1 "all paths converge" を構造的に保てない）
    // のを防ぐ。MIN_ZOOM / MAX_ZOOM が CanvasView 側に居るのと同型の置き場。
    public const float FRAME_MARGIN_DEFAULT = 0.06f;
    public const float DOCK_PLANE_PARALLAX = 1.0f;

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

    // Parallax offset (Content-LOCAL units) for a foreground layer that should track pan at
    // `factor`× the base (Content) plane — the depth cue for floating windows sitting "above"
    // HakoniwaRoot. Both planes ride Content (move 1× for free); this extra offset on the
    // foreground layer adds the remaining (factor−1)× so its NET screen travel is factor× per
    // unit pan. Derivation (findings 0006 §2 coordinate model): a foreground point at layer-local
    // w renders at viewport = zoom·((w + O) − pan); to make d(viewport)/d(pan) = −factor·zoom
    // (factor× the base plane's −zoom) we need O = (1 − factor)·pan. So at pan=0 (centred) O=0 —
    // the layer is identity and nothing existing shifts; the offset only grows as you pan away.
    //   factor = 1  -> 0 (coplanar, today's behaviour)
    //   factor > 1  -> foreground (moves MORE than Hakoniwa -> feels in front)
    //   0 < factor < 1 -> background (moves less)
    // Returned in Content-LOCAL units (the layer is a Content child), so it is zoom-independent:
    // O = (1 − factor)·pan, and the live offset must NOT be persisted (CaptureView reads Content only).
    public static Vector2 ParallaxLayerOffset(CanvasView v, float factor)
    {
        // Defend non-finite factor -> coplanar (no offset), mirroring SafeZoom's discipline so a
        // bad live/serialized value can't push NaN/Inf into the layer's anchoredPosition.
        if (float.IsNaN(factor) || float.IsInfinity(factor)) factor = 1f;
        return new Vector2((1f - factor) * v.panX, (1f - factor) * v.panY);
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

    // FrameWindow — issues #166-168 / findings 0123: pure "centre this window in the viewport at the
    // largest contain-fit zoom" math. Returns the CanvasView that, when applied to the controller, puts
    // `topLeft`+`size`'s centre at viewport (0,0) and scales it to occupy (1-marginFraction) of the
    // viewport's smaller axis (uniform zoom). Plane-agnostic via `parallaxFactor` (floating plane=1.2,
    // dock plane=1.0) — the parallax pan compensation `pan = centre / factor` is LOAD-BEARING; omitting it
    // pushes a floating-plane window off-centre by (1-factor)·zoom·centre on screen (findings 0123 §1.4,
    // derived from findings 0006 §2's `viewport(L) = zoom·(L - factor·pan)` on the parallax plane).
    //
    // Inputs are in CANVAS LOGICAL coordinates (the window's RectTransform anchoredPosition+sizeDelta on
    // the floating layer; pivot=(0,1) top-left, Y up — findings 0008 §2). `viewportSize` is the live
    // viewport.rect.size (logical px). `marginFraction` is the symmetric padding around the framed window
    // expressed as a fraction of viewport size (0.06 = 6% on each axis). Result zoom is clamped to
    // [MIN_ZOOM, MAX_ZOOM]; pan is computed from the UNCLAMPED centre formula so a clamp degenerates
    // gracefully ("centre still hits viewport origin, zoom hits the bound"), the AFK gate locks this.
    //
    // Safety regime mirrors SafeZoom / ParallaxLayerOffset: non-finite factor → 1, non-positive size →
    // MAX_ZOOM (no /0), non-positive viewport → return `fallbackView` (caller's current view if supplied,
    // else CanvasView.Identity()). Returning identity on degenerate input was a S1-era safety; under the S2
    // glide seam that animates from the current view TO the returned view, identity becomes a 200ms
    // 'reset to origin' jolt rather than a no-op (review finding). Hand the caller's CURRENT view back so
    // BeginGlide treats it as a zero-distance tween (effectively no-op) — preserves both call shapes.
    public static CanvasView FrameWindow(
        Vector2 topLeft, Vector2 size, Vector2 viewportSize,
        float parallaxFactor, float marginFraction,
        CanvasView fallbackView = null)
    {
        // SafeFactor: NaN/Inf or non-positive parallax factor → coplanar (1f). 0/negative `factor` would
        // /0 in `pan = centre / factor`; we treat both as "no parallax compensation needed" rather than
        // throw — the AFK gate covers the well-formed 1.0/1.2 paths.
        if (parallaxFactor <= 0f || float.IsNaN(parallaxFactor) || float.IsInfinity(parallaxFactor))
            parallaxFactor = 1f;
        if (float.IsNaN(marginFraction) || float.IsInfinity(marginFraction)) marginFraction = 0f;
        marginFraction = Mathf.Clamp(marginFraction, 0f, 0.95f);

        float vw = viewportSize.x, vh = viewportSize.y;
        if (vw <= 0f || vh <= 0f || float.IsNaN(vw) || float.IsNaN(vh) ||
            float.IsInfinity(vw) || float.IsInfinity(vh))
        {
            // Headless / pre-init / mid-reflow: hand back the caller's current view (no zoom/pan change)
            // so a glide tween becomes zero-distance. Identity fallback was a S1-era no-op assumption that
            // the S2 glide path turns into a 200ms reset — review finding.
            return fallbackView != null ? fallbackView.Clone() : CanvasView.Identity();
        }

        // Window centre in canvas-logical coords (pivot top-left, Y up):
        //   centre = topLeft + (w/2, -h/2)
        Vector2 centre = new Vector2(
            topLeft.x + size.x * 0.5f,
            topLeft.y - size.y * 0.5f);

        // Contain-fit zoom with margin: window occupies at most (1-m) of each viewport axis on screen.
        // screen_w = zoom·size.x, want screen_w ≤ (1-m)·vw  ⇒  zoom ≤ (1-m)·vw/size.x. Same on y.
        float zoomFit;
        if (size.x <= 0f || size.y <= 0f)
        {
            zoomFit = CanvasView.MAX_ZOOM;     // degenerate window → max zoom (probe corner)
        }
        else
        {
            float oneMinusM = 1f - marginFraction;
            float zx = oneMinusM * vw / size.x;
            float zy = oneMinusM * vh / size.y;
            zoomFit = Mathf.Min(zx, zy);
        }
        float zoom = Mathf.Clamp(zoomFit, CanvasView.MIN_ZOOM, CanvasView.MAX_ZOOM);

        // Parallax-corrected pan: `viewport(centre) = zoom·(centre - factor·pan) = 0` ⇒ pan = centre/factor.
        // factor=1 (dock plane) collapses to pan = centre (the base plane case).
        Vector2 pan = centre / parallaxFactor;

        return new CanvasView(pan.x, pan.y, zoom);
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
