// UniverseWriteback.cs — issue #31 "instrument picker / universe sidebar" (persistence, D4)
//
// The registry → sidecar writeback the picker/sidebar edits flow through. Mirrors TTWR
// writeback_scenario_instruments_system (scenario_registry_sync.rs): gate on `editable` +
// Replay mode + a content change, then persist via the seam #29 RESERVED for exactly this —
// ScenarioSidecarStore.SetInstruments ("the deferred registry-only writeback (#31 picker)").
// Restore is already #29's job (ScenarioStartupController.Populate → Universe.ReplaceAll).
//
// REVISION via CONTENT DIFF (not a counter): TTWR's sibling sync_scenario_metadata_from_
// registry_system flushes on `scenario.instruments != new_ids` — a content comparison. Using
// the same here means the SAME registry can be edited by BOTH #29's startup-tile text input
// AND #31's sidebar/picker without either having to bump a shared counter (CONTEXT.md "#29 の
// 薄い入力を剥がして置換するリワークは出さない" — we touch nothing in #29). Coalesces naturally:
// N rapid edits that net to the same set flush once.
//
// PATH via PROVIDER, re-resolved each flush (CONTEXT.md "active strategy 選択": supplyable is
// re-queried at call time, never cached). When the provider can't supply a path (no saved
// strategy yet), Flush SKIPS WITHOUT recording the content as flushed — so the edit persists
// later (next flush once a strategy is saved, or #29's Run Commit). No data loss on the
// normal path; in-memory authority until a path exists.
//
// Decisions: docs/findings/0024-instrument-picker-universe-sidebar.md (D4). 方針: ADR-0005.

using System.Collections.Generic;
using System.Linq;

public sealed class UniverseWriteback
{
    // The content last SUCCESSFULLY written to disk. null = nothing flushed/primed yet.
    List<string> _lastFlushed;

    // Surfaced for the sidebar's writeback-error label (TTWR ScenarioInstrumentsWritebackState
    // .last_error parity). Cleared on a successful flush.
    public string LastError { get; private set; }

    // Mark the current on-disk content as already in sync WITHOUT writing — called after
    // #29's Populate/restore so a freshly restored universe does not trigger a redundant
    // (idempotent but churny) re-write on the first flush.
    public void Prime(IReadOnlyList<string> ids)
    {
        _lastFlushed = ids != null ? new List<string>(ids) : new List<string>();
    }

    // Flush the registry to the sidecar if (and only if) it is editable, we are in Replay,
    // the content changed since the last successful flush, AND a strategy path resolves.
    // Returns true iff a write actually happened. Safe to call every frame (coalesces).
    public bool Flush(InstrumentRegistry registry, IStrategyFileProvider provider, UniverseSourceMode mode)
    {
        if (registry == null || !registry.Editable) return false;
        if (mode != UniverseSourceMode.Replay) return false;  // Live universe is venue-driven (D5)

        IReadOnlyList<string> cur = registry.Ids;
        if (_lastFlushed != null && _lastFlushed.SequenceEqual(cur)) return false;  // no content change

        // Re-resolve at call time; skip (retry later) when not supplyable — do NOT advance
        // _lastFlushed, so the pending edit is not silently dropped.
        if (provider == null || !provider.TryGetStrategyFile(out string path) || string.IsNullOrEmpty(path))
            return false;

        try
        {
            // #67: SetInstruments is mutate-existing-only. null = no complete sidecar yet, so it
            // wrote NOTHING (an instruments-only sidecar would shadow the inline .py SCENARIO and
            // break live register). Skip WITHOUT advancing _lastFlushed — the edit persists later
            // when Run-commit writes the full sidecar. Not surfaced as LastError (deferred, not a
            // failure), same as the unresolvable-path skip above.
            WritebackOutcome? outcome = ScenarioSidecarStore.SetInstruments(path, cur);
            if (outcome == null)
            {
                // No complete sidecar yet → deferred, not a failure. Clear any stale error so a
                // prior corrupt-file error (since fixed by deleting the file) does not linger on
                // the sidebar label while we simply wait for Run-commit.
                LastError = null;
                return false;
            }
            _lastFlushed = new List<string>(cur);
            LastError = null;
            return true;
        }
        catch (ScenarioSidecarException e)
        {
            // A corrupt user sidecar is a real error to surface, not a silent default — keep
            // _lastFlushed unchanged so a subsequent fixed file retries.
            LastError = e.Message;
            return false;
        }
    }
}
