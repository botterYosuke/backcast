// StrategyProviderRegistry.cs — issue #16 "Strategy Editor" (DURABLE tier, seam)
//
// The thin durable lookup the future Replay/Live run layer consults to find a
// strategy file provider by floating-window id (findings 0010 §5, owner-locked
// "case A"). #15 made strategy_editor MULTI-INSTANCE (ids like
// "strategy_editor:region_001"), so a single document interface would force the run
// layer to walk the Unity hierarchy; this registry is the one durable seam instead.
//
// RESPONSIBILITIES — ONLY these:
//   * Register / Unregister by window id (duplicate id is REJECTED).
//   * TryGet(windowId, out provider).
//   * Deterministic enumeration of registered ids, ORDERED BY ORDINAL (not insertion
//     order), so the run layer sees a stable list.
// EXPLICIT NON-responsibilities: it does NOT pick an active/current/default strategy,
// does NOT own document/provider lifetime, and does NOT touch the filesystem, save,
// or layout persistence. UnityEngine-FREE so the AFK gate drives it headless.

using System;
using System.Collections.Generic;

public class StrategyProviderRegistry
{
    readonly Dictionary<string, IStrategyFileProvider> _byId =
        new Dictionary<string, IStrategyFileProvider>(StringComparer.Ordinal);

    public int Count => _byId.Count;

    // Register a provider under a window id. Rejects null/empty id, null provider, and a
    // DUPLICATE id (first registration wins until Unregister). Returns whether it was added.
    public bool Register(string windowId, IStrategyFileProvider provider)
    {
        if (string.IsNullOrEmpty(windowId) || provider == null) return false;
        if (_byId.ContainsKey(windowId)) return false;   // duplicate id rejected
        _byId.Add(windowId, provider);
        return true;
    }

    // Remove a registration. Returns whether something was removed.
    public bool Unregister(string windowId)
    {
        if (string.IsNullOrEmpty(windowId)) return false;
        return _byId.Remove(windowId);
    }

    public bool TryGet(string windowId, out IStrategyFileProvider provider)
    {
        provider = null;
        if (string.IsNullOrEmpty(windowId)) return false;
        return _byId.TryGetValue(windowId, out provider);
    }

    public bool Contains(string windowId)
        => !string.IsNullOrEmpty(windowId) && _byId.ContainsKey(windowId);

    // Registered window ids, ORDERED BY ORDINAL (deterministic, not insertion order).
    public List<string> WindowIds()
    {
        var ids = new List<string>(_byId.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }
}
