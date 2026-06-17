// LayoutSidecarStore.cs — issue #69 "multi-document layout surface" (DURABLE tier, persistence seam)
//
// The MIRROR of ScenarioSidecarStore (#29): that store merge-writes the engine-owned
// "scenario" key into <strategy>.json; THIS store merge-writes the Unity-owned "layout"
// key into the SAME <strategy>.json, preserving "scenario" and every other sibling
// verbatim (CONTEXT.md: "同一 <strategy>.json に scenario キーと layout キーが共存").
// Together they realise the 2-file document model (#69 grill, findings 0048): a document
// is the (<strategy>.py, <strategy>.json) pair; the .json carries both keys, each owned
// and written by its own seam, neither clobbering the other.
//
// WHY NEWTONSOFT (ADR-0005 decision 2): a whole-file JsonUtility overwrite (LayoutStore's
// root write) would DROP the engine's strict-validated "scenario" key. Newtonsoft JObject
// is the C# equivalent of TTWR's serde_json::Value DOM (atomic_mutate_scenario_object):
// read the whole JSON, set ONE key, preserve the rest, atomic-write. Newtonsoft is
// CONTAINED in this store (callers see WriteLayout / TryReadLayout, never a JObject) —
// the same containment ScenarioSidecarStore uses.
//
// LAYOUT SHAPE STAYS ON JsonUtility: the LayoutDocument <-> JSON shape is still owned by
// LayoutStore (JsonUtility + Sanitize). This store only bridges that shape into/out of the
// "layout" key: WriteLayout serialises the doc with JsonUtility then splices it in as a
// JObject; TryReadLayout extracts the "layout" sub-object and hands its JSON to
// LayoutStore.LoadFromJson so the SAME normalisation runs (no parser fork).
//
// READ STRICTNESS (findings 0048 D4): TryReadLayout is the OPEN path's strict load —
// false (abort, keep the current workspace) on a missing file, malformed JSON, or no
// "layout" object. This is distinct from boot's fail-soft LayoutStore.Load (-> Default()),
// which is correct only because boot starts from an empty workspace.

using System;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class LayoutSidecarStore
{
    // foo.py -> foo.json ; foo.bar.py -> foo.bar.json — the SAME stem rule as
    // ScenarioSidecarStore / the engine's scenario._sidecar_path, so both keys land in
    // the one sidecar that sits next to the .py.
    public static string SidecarPathFor(string strategyPath) =>
        ScenarioSidecarStore.SidecarPathFor(strategyPath);   // one canonical resolver, shared with the scenario key

    // ---- WRITE: merge the "layout" key into <strategy>.json, preserving "scenario" and
    // every sibling. Unlike scenario's mutate-existing-only guard, a layout-only sidecar is
    // VALID (load_scenario tolerates a layout-only file — CONTEXT.md L380), so this always
    // creates/updates. The LayoutDocument shape is serialised by JsonUtility (LayoutStore's
    // parser) and spliced in as a JObject so the merge is lossless. ----
    public static void WriteLayout(string strategyPath, LayoutDocument doc)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        string path = SidecarPathFor(strategyPath);

        JObject root = File.Exists(path) ? ParseFile(path) : new JObject();
        // JsonUtility owns the LayoutDocument <-> JSON shape; bridge it into the DOM so the
        // sibling "scenario" key (and any other) survives the write verbatim.
        root["layout"] = JObject.Parse(JsonUtility.ToJson(doc));

        AtomicFile.WriteAllText(path, root.ToString(Formatting.Indented));
    }

    // ---- READ (strict, OPEN path): true + a sanitized LayoutDocument when the sidecar
    // exists and carries a "layout" object; false on missing file / malformed JSON / no
    // "layout" key (the caller aborts Open and keeps the current workspace, findings 0048
    // D4). The extracted "layout" JSON runs through LayoutStore.LoadFromJson so panels /
    // windows / canvasView / hakoniwaProfiles get the SAME normalisation as boot. ----
    public static bool TryReadLayout(string strategyPath, out LayoutDocument doc)
    {
        doc = null;
        string path = SidecarPathFor(strategyPath);
        if (!File.Exists(path)) return false;

        JObject root;
        try { root = ParseFile(path); }
        catch { return false; }   // malformed JSON / unreadable -> abort (keep workspace, findings 0048 D4)

        if (!(root["layout"] is JObject layout)) return false;   // no layout key -> abort

        // STRICT (not fail-soft): a present-but-invalid layout key (empty / version<=0 / malformed
        // sub-object) must ABORT the Open and keep the current workspace — NOT degrade to Default()
        // and wipe it (findings 0048 D4). LoadFromJson would have returned Default()+true here.
        return LayoutStore.TryLoadFromJson(layout.ToString(), out doc);
    }

    static JObject ParseFile(string path)
    {
        try
        {
            return JObject.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new LayoutSidecarException($"invalid JSON in sidecar '{path}': {e.Message}", e);
        }
    }
}

public sealed class LayoutSidecarException : Exception
{
    public LayoutSidecarException(string message, Exception inner) : base(message, inner) { }
}
