// HakoniwaTileHeaderInput.cs — issue #14 "Hakoniwa split-grid" (DURABLE tier, input boundary)
//
// The thin, DURABLE MonoBehaviour that turns a tile HEADER drag into a tile SWAP — the
// production input path #14 establishes and later shell work reuses (so the gesture wiring
// is NOT thrown away, mirroring #13's InfiniteCanvasInputSurface discipline). It does ONLY
// translation: on drag END it converts the drop point to a root-local normalized point,
// asks the controller which slot it landed in, and swaps. Header-drag SWAPS; it never
// free-floats a tile (findings 0007 §6, capability parity with TTWR ADR 0014).
//
// Attach to each tile's HEADER bar (a raycast-target Graphic). The tile BODY must NOT be a
// raycast target, so a drag on the body falls through to the InfiniteCanvasInputSurface and
// PANS the canvas instead — the header is the only swap handle. Because the header sits on
// top, the EventSystem routes its drag here and the canvas never sees it.
//
// The source slot is looked up by tile id at drag END (NOT cached at Initialize): a prior
// swap may have moved this tile, so the live order is the only correct source (TTWR's swap
// observer likewise reads the current slot from the order, not a stale index).

using UnityEngine;
using UnityEngine.EventSystems;

public class HakoniwaTileHeaderInput : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    HakoniwaController _controller;
    RectTransform _root;   // the HakoniwaRoot whose rect defines the 0..1 cell space
    string _tileId;

    public void Initialize(HakoniwaController controller, RectTransform root, string tileId)
    {
        _controller = controller;
        _root = root;
        _tileId = tileId;
    }

    // OnBeginDrag/OnDrag are required for OnEndDrag to fire and to mark this as the drag
    // target (so the canvas-pan surface doesn't also receive the gesture). No per-step state.
    public void OnBeginDrag(PointerEventData eventData) { }
    public void OnDrag(PointerEventData eventData) { }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (_controller == null || _root == null) return;

        int source = _controller.SlotOf(_tileId);
        if (source < 0) return;

        // Drop point -> root-local, then -> 0..1 across the root's rect (pivot-agnostic).
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _root, eventData.position, eventData.pressEventCamera, out Vector2 local);
        Rect r = _root.rect;
        if (r.width <= 0f || r.height <= 0f) return;
        Vector2 norm = new Vector2((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height);

        int target = _controller.SlotAtNormalized(norm);
        if (target >= 0 && target != source) _controller.Swap(source, target);
    }
}
