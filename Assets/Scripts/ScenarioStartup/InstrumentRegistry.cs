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

using System.Collections.Generic;

public sealed class InstrumentRegistry
{
    readonly List<string> _ids = new List<string>();

    public bool Editable { get; set; } = true;

    public IReadOnlyList<string> Ids => _ids;
    public int Count => _ids.Count;

    // Append if absent (case-sensitive exact match — instrument ids are canonical e.g.
    // "1301.TSE"). Returns true if added.
    public bool Add(string id)
    {
        if (!Editable || string.IsNullOrEmpty(id)) return false;
        if (_ids.Contains(id)) return false;
        _ids.Add(id);
        return true;
    }

    // Remove by value. Returns true if removed.
    public bool Remove(string id)
    {
        if (!Editable) return false;
        return _ids.Remove(id);
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

        if (SameSequence(_ids, next)) return false;
        _ids.Clear();
        _ids.AddRange(next);
        return true;
    }

    static bool SameSequence(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
