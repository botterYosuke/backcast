// InstrumentRegistry.cs — issue #29 "Replay 実行設定パネル" (universe SoT)
//
// The single source of truth for the Replay universe (CONTEXT.md: "universe registry
// (instruments SoT) vs scenario panel"). Ported from TTWR
// src/ui/components/instrument_registry.rs. #29 owns this SoT + the minimal text-list
// editor that mutates it + the sidecar writeback (ScenarioSidecarStore.SetInstruments).
// #31 (instrument picker) plugs a rich search/multi-select UI into the SAME SoT — it does
// NOT replace this seam, so the thin #29 text editor is not reworked away.
//
// editable: true  => user may edit (no instruments_ref); false => locked to an external
// reference. Mutators no-op when not editable. Every mutator returns whether the set
// actually changed, so callers can bump a writeback revision only on real change.
//
// Dedup is order-preserving (first occurrence wins) — the universe is a SET rendered as
// an ordered list; the sidecar instruments array mirrors this order.

using System;
using System.Collections.Generic;
using System.Linq;

public sealed class InstrumentRegistry
{
    readonly List<string> _ids = new List<string>();

    public bool Editable { get; set; } = true;

    public IReadOnlyList<string> Ids => _ids.AsReadOnly();
    public int Count => _ids.Count;

    // Fired AFTER a mutation that ACTUALLY changed the set (the `return true` paths only — a no-op
    // duplicate Add / absent Remove / idempotent ReplaceAll does NOT fire). Lets a SECOND view of
    // the same SoT (#59: the startup tile's text field, held-mode uGUI) re-pull on edits made
    // elsewhere (the #31 sidebar/picker), so "one universe per workspace" stays live across both
    // editors without either polling. Subscribers MUST unsubscribe on teardown (no orphan handler).
    public event Action Changed;

    // Append if absent (case-sensitive exact match — instrument ids are canonical e.g.
    // "1301.TSE"). Returns true if added.
    public bool Add(string id)
    {
        if (!Editable || string.IsNullOrEmpty(id)) return false;
        if (_ids.Contains(id)) return false;
        _ids.Add(id);
        Changed?.Invoke();
        return true;
    }

    // Remove by value. Returns true if removed.
    public bool Remove(string id)
    {
        if (!Editable) return false;
        if (!_ids.Remove(id)) return false;
        Changed?.Invoke();
        return true;
    }

    // Wholesale replace with an order-preserving dedup of the input. Returns true if the
    // resulting set differs from the prior one (so a no-op replace_all does not churn the
    // writeback revision).
    public bool ReplaceAll(IReadOnlyList<string> ids)
    {
        if (!Editable) return false;

        var next = new List<string>();
        var seen = new HashSet<string>();
        if (ids != null)
        {
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (seen.Add(id)) next.Add(id);
            }
        }

        if (_ids.SequenceEqual(next)) return false;
        _ids.Clear();
        _ids.AddRange(next);
        Changed?.Invoke();
        return true;
    }
}
