// UniverseBridge.cs — ADR-0031 S1 (#141): apply drained bt.universe.* edits to the registry SoT.
//
// The Python half (bt.universe.add/remove/clear) enqueues edit ops on the engine; the Unity host
// drains them every frame on the MAIN THREAD (BackcastWorkspaceRoot.DriveUniverseBridge) and routes
// them here to mutate the C# InstrumentRegistry SoT (ADR-0031 D2 — "programmatic user edit"). Each
// applied edit fires InstrumentRegistry.Changed, so chart spawn/despawn (SyncChartWindowsToUniverse)
// follows with ZERO extra wiring (D2 — chart 反映はタダ). In LiveAuto the same Changed cascade also
// drives subscribe/unsubscribe (S4/S5).
//
// PURE C# (no MonoBehaviour, no pythonnet): the AFK runner UniverseBridgeE2ERunner drives this
// directly with hand-built edit JSON (Python-FREE), exercising the REAL registry + REAL chart
// cascade — only the SOURCE of the edits (a Python run) is faked.
//
// clear → ReplaceAll(empty): InstrumentRegistry has no Clear(); a wholesale replace with an empty
// list is the registry's idempotent "empty the set" primitive (fires Changed only when non-empty).
// add/remove/clear all respect the registry's Editable gate (a locked registry no-ops the edit,
// exactly like a UI edit being rejected) — the bridge never bypasses it (PruneRetain is the only
// system-prune backdoor and is NOT used here; ADR-0031 D7 — this is populate, not autonomous prune).

using System;
using System.Collections.Generic;
using UnityEngine;

public static class UniverseBridge
{
    [Serializable]
    public struct Edit
    {
        public string op;   // "add" | "remove" | "clear"
        public string id;   // instrument id for add/remove; "" for clear
    }

    [Serializable]
    class EditList { public List<Edit> items = new List<Edit>(); }

    // Parse the JSON array engine.inproc_server.drain_universe_edits() returns
    // (`[{"op":"add","id":"7203.TSE"}, ...]`). JsonUtility cannot parse a top-level array, so wrap
    // it in `{"items":[...]}`. A blank/whitespace payload (no pending edits) → empty list. A
    // malformed payload is logged and treated as empty (never throws into the Update loop).
    public static List<Edit> ParseEdits(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new List<Edit>();
        try
        {
            EditList wrapped = JsonUtility.FromJson<EditList>("{\"items\":" + json + "}");
            return wrapped != null && wrapped.items != null ? wrapped.items : new List<Edit>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UniverseBridge] failed to parse universe edits: " + e.Message + " json=" + json);
            return new List<Edit>();
        }
    }

    // Apply a drained batch of edits to the registry, in order. Returns the number of edits that
    // ACTUALLY changed the set. The production mirror push is gated by InstrumentRegistry.Changed
    // (BackcastWorkspaceRoot._universeMirrorDirty), not this count — the count is for callers/gates that
    // want to assert how many edits took effect (the AFK runner asserts changed==1 per add/remove/clear).
    public static int Apply(IReadOnlyList<Edit> edits, InstrumentRegistry registry)
    {
        if (edits == null || registry == null) return 0;
        int changed = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            Edit e = edits[i];
            bool didChange;
            switch (e.op)
            {
                case "add":
                    didChange = registry.Add(e.id);
                    break;
                case "remove":
                    didChange = registry.Remove(e.id);
                    break;
                case "clear":
                    didChange = registry.ReplaceAll(Array.Empty<string>());
                    break;
                default:
                    Debug.LogWarning("[UniverseBridge] unknown universe edit op '" + e.op + "' (ignored)");
                    didChange = false;
                    break;
            }
            if (didChange) changed++;
        }
        return changed;
    }

    // Convenience for the production caller: parse + apply in one step. Returns the change count.
    public static int ApplyJson(string json, InstrumentRegistry registry) =>
        Apply(ParseEdits(json), registry);
}
