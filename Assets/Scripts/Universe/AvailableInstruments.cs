// AvailableInstruments.cs — issue #31 "instrument picker / universe sidebar" (supply seam, D3)
//
// The picker's candidate-supply seam. Ports the SHAPE of TTWR's UniverseStatus
// (instrument_picker.rs: ReplayEndUnset / ReplayLoading / ReplayError /
// LiveVenueNotConnected / LiveLoading / LiveError / Ready{ids} / empty) so the picker UI can
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
    NotConnected,  // Live only: no venue connection
    EndUnset,      // Replay only: scenario.end not set, cannot date-scope the catalog
}

public struct AvailableInstrumentsResult
{
    public UniverseStatusKind Kind;
    public IReadOnlyList<string> Ids;  // valid (possibly empty) when Kind == Ready
    public string Message;             // valid when Kind == Error

    public static AvailableInstrumentsResult Ready(IReadOnlyList<string> ids) =>
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Ready, Ids = ids ?? Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult Empty =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Empty, Ids = Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult Loading =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Loading, Ids = Array.Empty<string>() };
    public static AvailableInstrumentsResult Error(string message) =>
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.Error, Ids = Array.Empty<string>(), Message = message };
    public static readonly AvailableInstrumentsResult NotConnected =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.NotConnected, Ids = Array.Empty<string>() };
    public static readonly AvailableInstrumentsResult EndUnset =
        new AvailableInstrumentsResult { Kind = UniverseStatusKind.EndUnset, Ids = Array.Empty<string>() };
}

public interface IAvailableInstrumentsProvider
{
    // `replayEndDate` is the scenario.end snapshot (YYYY-MM-DD) the picker captured on open;
    // null/empty in Live mode (and the provider returns EndUnset in Replay when it is empty).
    AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate);
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
}
