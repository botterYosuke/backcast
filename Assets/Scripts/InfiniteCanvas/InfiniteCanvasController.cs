// InfiniteCanvasController.cs — issue #13 "infinite canvas" (DURABLE tier, Unity boundary)
//
// The thin, input-agnostic boundary between CanvasViewMath (pure) and the live Content
// RectTransform — the analogue of LayoutBinder's Unity-boundary half. A PLAIN C# class
// (NOT a MonoBehaviour) so the AFK probe can drive it against a headless RectTransform,
// just as #12 drove the binder headless. It NEVER reads input (that is
// InfiniteCanvasInputSurface's job) and NEVER sanitizes (that is LayoutStore's job).
//
// CONTRACT (findings 0006 §2/§4): `content` is anchored AND pivoted at the viewport
// centre (anchorMin=anchorMax=pivot=(0.5,0.5)) with no rotation, so anchoredPosition and
// localScale fully express the CanvasView. Cursor coordinates passed to ZoomAtCursor are
// VIEWPORT-CENTRE-RELATIVE pixels (the input surface converts screen->viewport-centred via
// RectTransformUtility; keeping that conversion out of here leaves the controller render-
// free and fully headless-testable).
//
// State of truth is the Content transform itself (stateless controller): every op reads
// the current view from the transform, applies the pure-math result, writes it back — so
// it can never drift from what's on screen.

using UnityEngine;

public class InfiniteCanvasController
{
    readonly RectTransform _content;

    // Optional foreground layer (e.g. the FloatingWindowLayer) given a PARALLAX depth cue: it
    // rides Content like everything else, plus this controller adds CanvasViewMath.ParallaxLayerOffset
    // so it travels `_parallaxFactor`× per unit pan. Null layer / factor 1 == today's coplanar behaviour.
    readonly RectTransform _parallaxLayer;
    readonly float _parallaxFactor;

    public InfiniteCanvasController(RectTransform content, RectTransform parallaxLayer = null, float parallaxFactor = 1f)
    {
        if (content == null) throw new System.ArgumentNullException(nameof(content));
        _content = content;
        _parallaxLayer = parallaxLayer;
        _parallaxFactor = parallaxFactor;
    }

    // Current live view, read back from the Content transform.
    public CanvasView CaptureView()
    {
        return CanvasViewMath.TransformToView(_content.anchoredPosition, _content.localScale.x);
    }

    // Write a view onto the Content transform (canonical anchoredPosition + uniform scale).
    public void ApplyView(CanvasView view)
    {
        if (view == null) return;
        CanvasViewMath.ViewToTransform(view, out Vector2 anchoredPosition, out Vector3 localScale);
        _content.anchoredPosition = anchoredPosition;
        _content.localScale = localScale;

        // Drive the parallax foreground layer from the SAME view. Every pan/zoom funnels through
        // ApplyView, so this single write keeps the depth cue in sync without a per-frame Update.
        if (_parallaxLayer != null)
            _parallaxLayer.anchoredPosition = CanvasViewMath.ParallaxLayerOffset(view, _parallaxFactor);
    }

    // Pan by a screen-pixel drag delta (grab-and-drag).
    public void PanByScreenDelta(Vector2 deltaScreen)
    {
        ApplyView(CanvasViewMath.PanByScreenDelta(CaptureView(), deltaScreen));
    }

    // Cursor-centred zoom by a multiplicative factor; cursor is viewport-centre-relative.
    public void ZoomAtCursor(Vector2 cursorViewportCentered, float factor)
    {
        ApplyView(CanvasViewMath.ZoomAtCursor(CaptureView(), cursorViewportCentered, factor));
    }
}
