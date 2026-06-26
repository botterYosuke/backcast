// CrosshairState.cs — S4 #159 / findings 0119 D-6 + S9 #163 / findings 0120 D-11.
//
// Pure data carrying the chart hover state: cursor position in widget-local coords (Vector2?),
// the derived hovered_price (main_area only), hovered_time_ms, and hovered_volume (volume_area
// only, S5 #160). flowsurface `chart_crosshair.rs::CrosshairState` Unity 翻訳。
//
// OWNERSHIP MIGRATION (findings 0120 D-11): in S4 the state lives ON ChartView (one widget = one
// hover state). In S9 the ownership is hoisted to a shared `ChartLadderRoot` Component sitting on
// the chart-window's body (sibling parent of both chartArea and ladderArea), so DepthLadderView
// can read `hovered_price` and highlight the nearest level. ChartView's accessor then delegates to
// `GetComponentInParent<ChartLadderRoot>().Crosshair` (per-window independent; multiple chart
// windows on the same iid each get an independent CrosshairState because they have independent
// ChartLadderRoot parents).
//
// hovered_price is COMPUTED in the main_area only — the volume sub-pane (S5) carries hovered_volume
// instead. Crossing the main↔volume divide while hovering toggles which derived value is non-null
// (the other goes to null), so a readout badge can render unambiguously.

using UnityEngine;

public class CrosshairState
{
    // null = pointer not over the chart (or exited). Coords are widget-local (rectTransform.rect space).
    public Vector2? cursor_world;
    // Derived from cursor_world.x ↔ ViewState.translation_ms (price/time conversion).
    public long? hovered_time_ms;
    // Derived from cursor_world.y when cursor is in main_area. Null when in volume_area (S5) or out of bounds.
    public double? hovered_price;
    // Derived from cursor_world.y when cursor is in volume_area (S5). Null in main_area.
    public double? hovered_volume;

    public void Clear()
    {
        cursor_world = null;
        hovered_time_ms = null;
        hovered_price = null;
        hovered_volume = null;
    }
}
