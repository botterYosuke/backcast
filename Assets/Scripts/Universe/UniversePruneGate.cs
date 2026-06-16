// UniversePruneGate.cs — issue #41 "instruments universe prune" (#253 stale-snapshot 回帰防止)
//
// Ports TTWR src/ui/universe.rs (UniverseManager::current_universe + RegistryValidator, issue
// #145) — NOT the thin instruments_universe_prune.rs caller. The load-bearing parity is the
// "R2 asymmetry" (universe.rs:9-17, "must not be merged"): the PICKER's status-facing
// IAvailableInstrumentsProvider.Query() must NOT be reused as the prune allowlist, because a
// destructive prune from a status-only "Ready" reintroduces the #253 stale/fallback wipe. So
// prune gets its OWN resolver here.
//
// Decisions: docs/findings/0041-instruments-universe-prune.md (D1-D5). 方針: ADR-0005 (1:1 表面
// parity — internal gate constants are out of that contract per ADR-0005 Consequences).
// 語彙: CONTEXT.md "universe prune gate（破壊的 prune の live-source 判定・#41）vs picker status / badge band".
//
// Live gate (fail-closed; allowlist returned ONLY when ALL hold, else null):
//   source == LiveVenue (fallback/stale/unknown excluded — #41 本文 prefers LiveVenue-only;
//     TTWR also allows LocalVenueSnapshot but it has no firing path, so excluded here)
//   ∧ venue ∈ {CONNECTED, SUBSCRIBED} ONLY (is_venue_live parity; Reconnecting EXCLUDED — and
//     deliberately NOT VenueConnectionViewModel.IsConnected, which includes RECONNECTING for the
//     badge band: prune owns its own stricter predicate)
//   ∧ status == Ready (Loaded) ∧ non-empty ids (HIGH-1: empty live list must not wipe).
// Replay gate: scenario.end set ∧ catalog Ready ∧ non-empty (no triple-gate — Replay parity).
// No persistent cache: inputs are re-resolved on demand each evaluation, so a venue disconnect
// auto-fails the gate next tick (no invalidate-on-disconnect system needed — findings D3/D4).

using System.Collections.Generic;

// The source axis for the PRUNE gate (ports TTWR TickersSource). Only LiveVenue is a valid
// live-prune source; everything else is fail-closed. Distinct from the picker's UniverseSourceMode
// (Replay/Live) — this discriminates WHERE a Live universe came from.
public enum UniversePruneSourceKind
{
    Unknown,               // no source resolved yet → never prune
    LiveVenue,             // freshly fetched from a connected venue → the ONLY live prune source
    ReplayCatalogFallback, // replay catalog fallback → must NOT prune the live universe (#253)
    LocalVenueSnapshot,    // disk-cached venue snapshot → fallback in backcast #41 (no prune)
}

// Everything the prune gate reads to decide, assembled fresh each evaluation (on-demand
// re-resolve, no cache). The probe builds these directly; BackcastWorkspaceRoot assembles them
// from DisplayMode + the prune source + VenueConnectionViewModel.VenueState + scenario.end.
public struct UniversePruneInputs
{
    public UniverseSourceMode Mode;   // Replay | Live (derived from footer DisplayMode)

    // Live axis
    public UniversePruneSourceKind LiveSource;
    public UniverseStatusKind LiveStatus;        // Ready == loaded; gates the prune
    public string VenueState;                    // raw poll state; prune accepts CONNECTED/SUBSCRIBED only
    public IReadOnlyList<string> LiveIds;

    // Replay axis
    public string ScenarioEnd;                   // null/empty == EndUnset → no prune
    public UniverseStatusKind ReplayStatus;      // Ready == catalog available
    public IReadOnlyList<string> ReplayIds;
}

public static class UniversePruneGate
{
    // Prune-only venue predicate. INTENTIONALLY stricter than the badge band
    // (VenueConnectionViewModel.IsConnected includes RECONNECTING to avoid badge flap). is_venue_live
    // parity: Connected/Subscribed only — pruning the registry while RECONNECTING risks acting on a
    // half-torn-down connection.
    public static bool IsVenueLiveForPrune(string venueState) =>
        venueState == "CONNECTED" || venueState == "SUBSCRIBED";

    // The single prune allowlist producer. Returns the confirmed universe, or null when undetermined
    // (= prune forbidden, fail-closed). Mirrors UniverseManager::current_universe.
    public static HashSet<string> CurrentUniverse(in UniversePruneInputs i)
    {
        switch (i.Mode)
        {
            case UniverseSourceMode.Live:
                if (i.LiveSource != UniversePruneSourceKind.LiveVenue) return null; // fallback/stale/unknown
                if (!IsVenueLiveForPrune(i.VenueState)) return null;                // disconnected/reconnecting/...
                if (i.LiveStatus != UniverseStatusKind.Ready) return null;          // loading/error/notconnected/empty
                return NonEmptyOrNull(i.LiveIds);                                   // empty list → null (HIGH-1)

            case UniverseSourceMode.Replay:
                if (string.IsNullOrEmpty(i.ScenarioEnd)) return null;               // EndUnset
                if (i.ReplayStatus != UniverseStatusKind.Ready) return null;        // loading/error/empty
                return NonEmptyOrNull(i.ReplayIds);

            default:
                return null;
        }
    }

    static HashSet<string> NonEmptyOrNull(IReadOnlyList<string> ids)
    {
        if (ids == null || ids.Count == 0) return null;   // empty universe → null (no wipe)
        return new HashSet<string>(ids);
    }
}

// On-demand prune source seam. #41 ships a null source (prune dormant) and a stub for the probe;
// the real live fetch (venue fetch_instruments) / Replay catalog (DuckDB listed_info) are a
// SEPARATE issue — same shape-now/supply-later split as #31 (findings 0024 D3).
public interface IUniversePruneSource
{
    // Live universe snapshot, re-resolved each call (source + status + ids). No producer today →
    // returns NotConnected/Unknown → gate null → no prune.
    void LiveSnapshot(out UniversePruneSourceKind source, out UniverseStatusKind status, out IReadOnlyList<string> ids);

    // Replay catalog availability for the given scenario end (reuses the picker's status shape;
    // Replay has no source gate). EndUnset/Loading/Error/Empty all surface as a non-Ready kind.
    AvailableInstrumentsResult ReplayCatalog(string scenarioEnd);
}

// Honest default: no live producer, no catalog producer → prune stays dormant until the real
// sources land (then this is a one-line swap).
public sealed class NullUniversePruneSource : IUniversePruneSource
{
    public void LiveSnapshot(out UniversePruneSourceKind source, out UniverseStatusKind status, out IReadOnlyList<string> ids)
    {
        source = UniversePruneSourceKind.Unknown;
        status = UniverseStatusKind.NotConnected;
        ids = System.Array.Empty<string>();
    }

    public AvailableInstrumentsResult ReplayCatalog(string scenarioEnd) =>
        string.IsNullOrEmpty(scenarioEnd) ? AvailableInstrumentsResult.EndUnset : AvailableInstrumentsResult.NotConnected;
}

// The brain: change-gated, on-demand prune tick. BackcastWorkspaceRoot assembles inputs each poll
// frame and calls Tick; the probe drives Tick directly. Re-evaluates only when the input
// fingerprint changed (inputs_changed() parity) and NEVER subscribes to InstrumentRegistry.Changed
// (prune's own PruneRetain fires Changed → SyncChartTilesToUniverse downstream; subscribing would
// self-reenter — findings D4).
public sealed class UniversePruneDriver
{
    readonly InstrumentRegistry _registry;
    string _fingerprint;

    // Observability for the AFK probe / future HITL.
    public int PruneCount { get; private set; }
    public string LastPruneLog { get; private set; }
    public int EvaluationCount { get; private set; }

    public UniversePruneDriver(InstrumentRegistry registry)
    {
        _registry = registry;
    }

    // Returns true iff a prune actually shrank the registry this tick.
    public bool Tick(in UniversePruneInputs inputs)
    {
        string fp = Fingerprint(inputs);
        if (fp == _fingerprint) return false;   // change-gate: nothing relevant changed
        _fingerprint = fp;
        EvaluationCount++;

        HashSet<string> allow = UniversePruneGate.CurrentUniverse(inputs);
        if (allow == null) return false;        // undetermined → no-op (fail-closed)

        bool shrank = _registry.PruneRetain(allow);
        if (shrank)
        {
            PruneCount++;
            LastPruneLog = "auto-prune: -> " + _registry.Count
                + " (mode=" + inputs.Mode + ", source=" + inputs.LiveSource + ")";
        }
        return shrank;
    }

    static string Fingerprint(in UniversePruneInputs i)
    {
        // Cheap, allocation-light identity of every gate input (inputs_changed parity). Only the
        // ACTIVE mode's id list is joined — the inactive axis can never change the gate result, so
        // joining it would be wasted per-frame allocation (Update is a hot path). Scalars from the
        // inactive axis stay in the key (cheap, and over-capturing only forces a harmless re-eval).
        var ids = i.Mode == UniverseSourceMode.Live ? i.LiveIds : i.ReplayIds;
        string idKey = ids != null ? string.Join(",", ids) : "";
        return (int)i.Mode + "|" + (int)i.LiveSource + "|" + (int)i.LiveStatus + "|" + (i.VenueState ?? "")
            + "|" + (i.ScenarioEnd ?? "") + "|" + (int)i.ReplayStatus + "|" + idKey;
    }
}
