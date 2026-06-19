// ChromeCanvas.cs — shared Canvas-promotion helper for screen-fixed chrome views
//
// All chrome views (MenuBarView / UniverseSidebarView / WorkspaceFooterView, plus future toolbars,
// toasts, command palettes) need the SAME 4-line idiom on their root GameObject:
//   1) add a Canvas (idempotent — find-or-add)
//   2) overrideSorting = true   ← without this, sortingOrder is ignored under the parent canvas
//   3) sortingOrder = <layer>   ← chrome z-order contract per CONTEXT.md + findings 0045/0053
//   4) add a GraphicRaycaster  ← without this, clicks fall through to the layer beneath
//
// BackcastWorkspaceProbe.AssertChromeCanvas asserts the same 4-line contract (Canvas exists +
// overrideSorting + GraphicRaycaster), so this helper IS the single source of truth: the contract
// is encoded once, in one place, and the probe gates against the same shape every chrome view uses.
// A new chrome layer added via `ChromeCanvas.Promote(go, FOO_SORT)` is structurally guaranteed to
// satisfy the assertion before any probe pass.

using UnityEngine;
using UnityEngine.UI;

public static class ChromeCanvas
{
    // Promote `go` to a chrome layer at the given sortingOrder. Idempotent: re-promoting the same
    // GameObject reassigns the sortingOrder without spawning duplicate Canvas/GraphicRaycaster
    // components (so re-Build paths in the views don't accumulate components).
    public static Canvas Promote(GameObject go, int sortingOrder)
    {
        if (go == null) return null;
        var canvas = go.GetComponent<Canvas>();
        if (canvas == null) canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
        return canvas;
    }
}
