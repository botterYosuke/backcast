// HakoniwaStageInputSurface.cs — issue #93 R3 (findings 0068 §17, DURABLE tier, input boundary)
//
// The single live-input seam for the perspective Hakoniwa stage. The board tiles are baked into
// a RenderTexture on a World-Space Stage canvas (findings 0068 §15), so they never receive
// EventSystem pointer events directly — ALL live drags land on the Content-side RawImage (the
// diorama "photo"). This MonoBehaviour, attached to that RawImage, re-dispatches a drag through
// the math-pick route (findings 0068 §12/§14, production==gate):
//
//   screen-press -> RawImage-local -> RT pixel -> HakoniwaStageMath.UnprojectToSlot
//                -> HakoniwaController.RouteNormalized -> (slot, inHeader)
//
//   inHeader  -> header swap-drag: capture the source slot at BEGIN; on END route the drop point
//                to a target slot and HakoniwaController.Swap (header is the only swap handle, §6).
//   body/盤外 -> pan fall-through: forward the drag to the InfiniteCanvas pan path (no Swap).
//
// Real EventSystem routing (live screen-press) is HITL (HAKONIWA-11b); the AFK gate
// (HakoniwaStageInputProbe / HAKONIWA-15) injects synthetic PointerEventData straight into these
// handlers. GraphicRaycaster is NOT used (preflight RAYCAST-DEAD, findings 0068 §11).

using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class HakoniwaStageInputSurface : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    HakoniwaController _controller;
    RectTransform _rawImageRect;
    HakoniwaStageMath.StageParams _stage;
    float _headerFrac;
    Action<PointerEventData> _onPanBegin;
    Action<PointerEventData> _onPanDrag;

    int _dragSourceSlot = -1;   // >=0 while a header swap-drag is active; -1 = pan fall-through / no gesture

    // The slot captured at BEGIN of the active header swap-drag, or -1 if the current gesture is a
    // pan fall-through / no gesture. Exposed for the AFK gate (HakoniwaStageInputProbe section 1).
    public int ActiveDragSourceSlot => _dragSourceSlot;

    public void Initialize(HakoniwaController controller, RectTransform rawImageRect,
        HakoniwaStageMath.StageParams stage, float headerFrac,
        Action<PointerEventData> onPanBegin, Action<PointerEventData> onPanDrag)
    {
        _controller = controller;
        _rawImageRect = rawImageRect;
        _stage = stage;
        _headerFrac = headerFrac;
        _onPanBegin = onPanBegin;
        _onPanDrag = onPanDrag;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        // header band -> start a header swap-drag: capture the source slot and forward NOTHING to pan
        // (OnDrag forwards nothing while active, OnEnd resolves the drop). body/盤外 (or a degenerate
        // route) -> pan fall-through. Mirrors OnEndDrag's TryRoute usage (findings 0068 §12/§14).
        _dragSourceSlot = -1;
        if (TryRoute(eventData, out int slot, out bool inHeader) && inHeader && slot >= 0)
        {
            _dragSourceSlot = slot;
            return;
        }
        _onPanBegin?.Invoke(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        // Pan fall-through forwards every step; a header swap-drag resolves at END (drop point),
        // like HakoniwaTileHeaderInput — so an active swap-drag forwards nothing here.
        if (_dragSourceSlot < 0) _onPanDrag?.Invoke(eventData);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        int source = _dragSourceSlot;
        _dragSourceSlot = -1;
        if (source < 0) return;                        // was a pan fall-through (or no header capture)
        if (_controller == null) return;
        if (!TryRoute(eventData, out int target, out _)) return;
        if (target >= 0 && target != source) _controller.Swap(source, target);
    }

    // screen-press -> RawImage-local -> normalized (0..1, Y up) -> RT pixel -> UnprojectToSlot ->
    // RouteNormalized. Returns false if the RawImage rect is degenerate. (slot, inHeader) via out.
    bool TryRoute(PointerEventData eventData, out int slot, out bool inHeader)
    {
        slot = -1; inHeader = false;
        if (_controller == null || _rawImageRect == null) return false;
        Vector2 rtPixel = ScreenToRtPixel(eventData);
        if (float.IsNaN(rtPixel.x)) return false;
        Vector2 boardNorm = HakoniwaStageMath.UnprojectToSlot(rtPixel, _stage);
        slot = _controller.RouteNormalized(boardNorm, _headerFrac, out inHeader);
        return true;
    }

    // screen pixel -> RT pixel (origin bottom-left, Y up — UnprojectToSlot's input space). The
    // RawImage displays the whole RT, so RawImage-local normalized maps 1:1 onto RT pixels.
    // Returns NaN.x if the rect is degenerate. The AFK gate asserts this conversion (section 4).
    public Vector2 ScreenToRtPixel(PointerEventData eventData)
    {
        // A perspective press camera can fail to project the screen point onto the RawImage plane
        // (ray misses / behind the plane). The sibling overlay surface ignores this bool, but THIS
        // surface's route mutates order state (Swap), so a garbage `local` could fire a spurious
        // swap — guard it into a clean no-route (TryRoute returns false on NaN). findings 0068 §17.
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rawImageRect, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return new Vector2(float.NaN, float.NaN);
        Rect r = _rawImageRect.rect;
        if (r.width <= 0f || r.height <= 0f) return new Vector2(float.NaN, float.NaN);
        float nx = (local.x - r.xMin) / r.width;
        float ny = (local.y - r.yMin) / r.height;
        return new Vector2(nx * _stage.rtW, ny * _stage.rtH);
    }
}
