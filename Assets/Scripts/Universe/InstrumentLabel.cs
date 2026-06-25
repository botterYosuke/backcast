// InstrumentLabel.cs — issue #140 / findings 0112 (instrument display label `<id> <name>`)
//
// The SHARED `<id> <name>` formatter for the TWO user-facing surfaces that currently name a
// single instrument with id-plus-human-name:
//   * sidebar `+Add` picker rows   (PickerRow.Candidate — findings 0024)
//   * chart window chrome title    (BackcastWorkspaceRoot.BuildDockWindowFrame — findings 0112)
//
// NOT (yet) consumed by `OrderTicketView` (shows id alone) or `ChartView._titleLabel` (hardcoded
// "CHART"); migrating those is a follow-up — list this header (and CONTEXT.md "銘柄表示ラベル") if
// you do, so the "single source of truth" claim stays accurate. Until then this helper covers the
// picker + chart-window pair, and that's a deliberate scope cap.
//
// Centralising the rule here is what structurally PREVENTS drift between picker and chart-window
// titles. Without the shared helper, the two collapse rules (name null / empty / equal-to-id)
// would inevitably diverge over time and a future regression would only show in one surface — the
// bug class CONTEXT.md "銘柄表示ラベル" explicitly forbids.
//
// COLLAPSE RULES — `<id> <name>` when name is meaningful, else id alone:
//   * name is null            → id alone (no source name available)
//   * name is empty/whitespace → id alone (treat blanks as "no name")
//   * name == id              → id alone (de-dup defence: _snapshot_to_list_result substitutes id
//                                          when listed_info CompanyName is missing, so this avoids
//                                          the "7203.TSE 7203.TSE" hazard at the display boundary)
//
// id is the venue-suffixed FULL id (e.g. "7203.TSE"), never shortened. Picker and chart-window
// titles MUST show the same form so a user can read them across surfaces without translation.
//
// CONTRACT FOR null/empty id (review finding #8): a null or empty id is a PASSTHROUGH —
// `Compose(null, _)` returns null and `Compose("", _)` returns "". This is intentional: id is the
// system's source of truth for what to display; we don't synthesize a placeholder ("unknown") nor
// throw, so a caller that hasn't resolved an id yet gets the falsy value back and can decide what
// to render. `ResolveChartTitleForId` (BackcastWorkspaceRoot) pre-validates id before calling
// Compose so the null path is unreachable there; `PickerRow.Candidate` does NOT pre-validate —
// it forwards whatever the picker's supply chain produced. The supply chain today never yields a
// null id (an empty backend response is mapped to Result.Empty, not Ready+null entries), so the
// null-id path is unreachable in shipped code, but a future supply that emits a null id would
// land a null Label on the row — a guard at PickerRow.Candidate (or a tightening of the supply
// invariant) would be needed before this becomes load-bearing.

public static class InstrumentLabel
{
    // The single source of `<id> <name>` truth. Returns id alone when name carries no extra signal.
    // A null/empty id is a passthrough (the caller validates id; see CONTRACT note in header).
    public static string Compose(string id, string name)
    {
        if (string.IsNullOrEmpty(id)) return id;
        if (string.IsNullOrWhiteSpace(name)) return id;
        if (name == id) return id;
        return id + " " + name;
    }
}
