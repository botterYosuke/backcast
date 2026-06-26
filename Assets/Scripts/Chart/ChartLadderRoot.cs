// ChartLadderRoot.cs — S9 #163 / ADR-0035 / findings 0120 D-11. The parent Component of the
// chart+ladder複合 widget that hoists CrosshairState ownership out of ChartView so DepthLadderView
// can READ hovered_price (via GetComponentInParent<ChartLadderRoot>()) without ChartView holding a
// direct reference to the ladder (per-window independence + decoupling).
//
// PLACEMENT: BuildChartContent (BackcastWorkspaceRoot) attaches this Component to the chart
// window's `body` RectTransform — the parent of BOTH chartAreaGo AND ladderAreaGo. So when
// ChartView calls `GetComponentInParent<ChartLadderRoot>()` and DepthLadderView does the same,
// they both find the SAME instance and share the same CrosshairState.
//
// PER-WINDOW INDEPENDENCE: opening the same instrument in 2 chart windows creates 2 separate
// `body` RectTransforms with 2 ChartLadderRoot Components — hover in one doesn't bleed into the
// other (findings 0120 D-11).
//
// S4 BACKWARDS COMPAT: ChartView.Crosshair returns the parent ChartLadderRoot's Crosshair when
// the parent exists, otherwise falls back to a local CrosshairState (so standalone ChartViews
// — e.g. the ThemeHitlHarness chart_strip — keep working without needing a ChartLadderRoot).

using UnityEngine;

public class ChartLadderRoot : MonoBehaviour
{
    public CrosshairState Crosshair { get; } = new CrosshairState();
}
