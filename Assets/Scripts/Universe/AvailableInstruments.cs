// AvailableInstruments.cs — issue #31 "instrument picker / universe sidebar" (supply seam, D3)
//
// The picker's candidate-supply seam. Ports the SHAPE of TTWR's UniverseStatus
// (instrument_picker.rs: ReplayEndUnset / ReplayLoading / ReplayError /
// LiveVenueNotConnected / LiveLoading / LiveError / Ready{ids} / empty) — plus backcast's
// `Unsupported` (logged-in venue with no instrument master, kabu MVP) which TTWR lacks; it
// distinguishes that case from NotConnected/not-logged-in (findings 0103) so the picker UI can
// render every placeholder NOW (ADR-0005 surface parity). The SEMANTICS (when each status is
// returned from a REAL source — DuckDB listed_info for Replay, venue fetch_instruments for
// Live) are a SEPARATE issue; #31 ships a mock that returns Ready/Empty, and the probe
// injects the remaining statuses via a stub to pin placeholder rendering (findings 0024 D3).
//
// backcast is in-proc (ADR-0001), so the provider is a plain C# interface — TTWR's
// UniverseManager absorbed Replay/Live behind a Bevy SystemParam; here the implementation
// (engine handoff vs C#-side) is decided by the real-supply issue. #31 fixes only the shape.
//
// Decisions: docs/findings/0024-instrument-picker-universe-sidebar.md (D3, D5). 方針: ADR-0005.

using System;
using System.Collections.Generic;

// Replay draws candidates from a date-scoped local catalog (needs scenario.end); Live draws
// from a connected venue. The source axis the picker branches on for placeholders/messages.
public enum UniverseSourceMode { Replay, Live }

public enum UniverseStatusKind
{
    Ready,         // ids populated (may still be filtered to nothing by the query)
    Empty,         // supply succeeded but the source has 0 instruments
    Loading,       // fetch in flight
    Error,         // fetch failed (Message carries the reason)
    NotConnected,  // Live only: no venue connection (NOT logged in)
    // Live only: the venue IS connected/logged-in but cannot enumerate an instrument master
    // (kabu MVP: fetch_instruments() returns [] → enumerates_instruments=False). DISTINCT from
    // NotConnected so the picker does not contradict the menu badge ("Connected: KABU") with a
    // "Venue not connected" message (bug 2026-06-25: LIVE_UNIVERSE_UNSUPPORTED was collapsed
    // into NotConnected — findings 0103).
    Unsupported,
    EndUnset,      // Replay only: scenario.end not set, cannot date-scope the catalog
}

public struct AvailableInstrumentsResult
{
    public UniverseStatusKind Kind;
    public IReadOnlyList<string> Ids;    // valid (possibly empty) when Kind == Ready
    // Issue #46 / review finding A5: human-readable names (e.g. listed_info CompanyName for
    // kabu/TSE) parallel to Ids — picker filter matches id OR name so users can search by
    // company name (トヨタ) instead of memorising 4-digit codes. May be null/short/empty per
    // entry; picker callers fall back to the id for display when an entry is missing.
    public IReadOnlyList<string> Names;
    public string Message;               // valid when Kind == Error

    public static AvailableInstrumentsResult Ready(IReadOnlyList<string> ids, IReadOnlyList<string> names = null) =>
        new AvailableInstrumentsResult {
            Kind = UniverseStatusKind.Ready,
            Ids = ids ?? Array.Empty<string>(),
            Names = names ?? Array.Empty<string>(),
        };
    public static readonly AvailableInstrumentsResult Empty =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Empty, Ids = Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult Loading =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Loading, Ids = Array.Empty<string>() };
    public static AvailableInstrumentsResult Error(string message) =>
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Error, Ids = Array.Empty<string>(), Message = message };
    public static readonly AvailableInstrumentsResult NotConnected =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.NotConnected, Ids = Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult Unsupported =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Unsupported, Ids = Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult EndUnset =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.EndUnset, Ids = Array.Empty<string>() };
}

public interface IAvailableInstrumentsProvider
{
    // `replayEndDate` is the scenario.end snapshot (YYYY-MM-DD) the picker captured on open;
    // null/empty in Live mode. Empty-in-Replay handling is provider-specific: the Mock returns
    // EndUnset ("Set scenario.end first"); the production Backend falls back to the latest universe
    // (findings 0084, owner request 2026-06-22).
    AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate);

    // #140 / findings 0112: cache-only, mode-agnostic name lookup for the chart-window title.
    // Returns true with the human-readable name iff a previously-cached Ready snapshot (from ANY
    // (mode, end) key the picker has Queried) holds this iid; returns false (out name=null) when
    // no snapshot is cached yet OR this iid is absent from every cached snapshot. The chart-title
    // resolver calls this from BuildDockWindowFrame — it must NOT trigger a fetch (picker stays the
    // sole fetch trigger so layout-restore can't race N concurrent PickerInstrumentFetch threads)
    // and must NOT depend on (mode, end) (the name is a per-instrument property — a Replay→Live
    // mode flip after a picker open must not discard the cached name).
    bool TryGetName(string instrumentId, out string name);
}

// #31's shipped supply: returns Ready{ids} (or Empty when no ids configured). The real
// DuckDB/venue sources — and the Loading/Error/NotConnected transitions — are a separate
// issue; the probe uses a stub to drive those statuses. In Replay, an empty end date still
// yields EndUnset so the picker shows "Set scenario.end first" without a real catalog.
public sealed class MockAvailableInstrumentsProvider : IAvailableInstrumentsProvider
{
    readonly List<string> _ids;

    public MockAvailableInstrumentsProvider(IEnumerable<string> ids = null)
    {
        _ids = ids != null ? new List<string>(ids) : new List<string>();
    }

    public void SetIds(IEnumerable<string> ids)
    {
        _ids.Clear();
        if (ids != null) _ids.AddRange(ids);
    }

    public AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate)
    {
        if (mode == UniverseSourceMode.Replay && string.IsNullOrEmpty(replayEndDate))
            return AvailableInstrumentsResult.EndUnset;
        if (_ids.Count == 0)
            return AvailableInstrumentsResult.Empty;
        return AvailableInstrumentsResult.Ready(_ids);
    }

    // The Mock doesn't carry names — it returns Ready(ids) with empty Names. The chart-title
    // resolver will fall back to id alone, matching the picker's id-only Label for the same row.
    public bool TryGetName(string instrumentId, out string name) { name = null; return false; }
}
