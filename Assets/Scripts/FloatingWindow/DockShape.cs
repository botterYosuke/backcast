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
    // ADR-0026: startup is no longer a dock kind (moved to the Settings modal). ADR-0037: run_result
    // is no longer a dock kind either (cut over to a screen-anchored popup — findings 0125 D4). The
    // dock plane is now chart + the 3 base singletons (buying_power / orders / positions).
    public static bool IsDockKind(string kind) =>
        kind == FloatingWindowCatalog.KIND_CHART ||
        kind == FloatingWindowCatalog.KIND_BUYING_POWER ||
        kind == FloatingWindowCatalog.KIND_ORDERS ||
        kind == FloatingWindowCatalog.KIND_POSITIONS;

    // #104 (ADR-0019 / findings 0082 §2): the Hakoniwa group CORE kind(s). A group containing AT LEAST
    // ONE visible/live core was once promoted to a Hakoniwa group (translate-banned, swap-only). ADR-0024
    // §1 RETIRED the Hakoniwa special (all groups drag identically), so IsCoreKind had no production
    // consumer; ADR-0026 retired `startup` and ADR-0037 cuts `run_result` over to a popup — leaving NO
    // dock core kind. The set is now EMPTY (dead-code simplify — findings 0125 F2/D4). Kept as a stable
    // predicate (always false) so any lingering legacy/diagnostic caller degrades gracefully.
    public static bool IsCoreKind(string kind) => false;
}
