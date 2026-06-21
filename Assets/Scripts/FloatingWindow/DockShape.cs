// DockShape.cs — #99 Slice 3 (ADR-0017 / findings 0075 §3/§5)
//
// Tiny stateless helpers for the dock cluster: map a footer mode string to the 2-valued shape
// the workspace cares about (Replay vs Live — the `startup` window's visibility is the only
// mode-conditional surface left after ADR-0017 retires per-mode tile profiles), and form/test
// the `chart:<instrument>` window-id convention. The OLD HakoniwaBaseTiles.IsLiveShape /
// .IsChartId live here now — `Kinds(live)` and `PanelOrder` are retired along with the split
// grid (ADR-0017 §3).
//
// Pure (no UnityEngine) so probes can drive it headlessly.

public static class DockShape
{
    public const string ChartIdPrefix = "chart:";

    // True for the LiveManual / LiveAuto modes; Replay (and an unknown garbage string) → false.
    // The seam the dock uses to toggle `startup` show/hide (findings 0075 §3): Replay shows it,
    // Live hides it (NEVER destroyed — visibility toggle, owner-locked).
    public static bool IsLiveShape(string displayMode) =>
        displayMode == FooterModeViewModel.LiveManual || displayMode == FooterModeViewModel.LiveAuto;

    // True iff `id` belongs to the chart family (prefix `chart:`), i.e. the dynamic universe-driven
    // window family that the chart-window sync spawns/closes. Replaces HakoniwaBaseTiles.IsChartId.
    public static bool IsChartId(string id) => id != null && id.StartsWith(ChartIdPrefix);

    // Compose a chart window id for an instrument: "chart:<instrument>". The SINGLE place this
    // convention lives so the spawn/close/render lookups can't drift apart.
    public static string ChartId(string instrument) =>
        string.IsNullOrEmpty(instrument) ? null : ChartIdPrefix + instrument;

    // Recover the instrument id from a "chart:<instrument>" window id. Returns null for a non-chart id.
    public static string InstrumentOfChartId(string id) =>
        IsChartId(id) ? id.Substring(ChartIdPrefix.Length) : null;

    // #103 (ADR-0018 / findings 0075 §10): true iff `kind` belongs to the BACK (dock) plane — the
    // 6 former Hakoniwa kinds that live on the 1.0× DockLayer (chart family + the 5 base singletons).
    // strategy_editor / order live on the 1.2× FloatingWindowLayer and return false. The SINGLE
    // predicate that routes a kind to its depth plane: BackcastWorkspaceRoot uses it on layout
    // restore, and the AFK gate uses it to prove the round-trip plane routing — so the two cannot
    // drift. Pure (the catalog consts are compile-time strings) so probes drive it headlessly.
    public static bool IsDockKind(string kind) =>
        kind == FloatingWindowCatalog.KIND_CHART ||
        kind == FloatingWindowCatalog.KIND_STARTUP ||
        kind == FloatingWindowCatalog.KIND_BUYING_POWER ||
        kind == FloatingWindowCatalog.KIND_ORDERS ||
        kind == FloatingWindowCatalog.KIND_POSITIONS ||
        kind == FloatingWindowCatalog.KIND_RUN_RESULT;
}
