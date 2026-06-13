// FloatingWindowCatalog.cs — issue #15 "floating windows" (DURABLE tier, spec)
//
// The kind -> FloatingWindowSpec registry that makes window creation SPEC-DRIVEN (AC2). The
// controller resolves a persisted `kind` here at restore; an UNKNOWN kind returns false from
// TryGet so the controller SKIPS its spawn while LayoutStore keeps the entry in the document
// (forward-evolution discipline, findings 0008 §3) — a window kind added by a newer build
// survives a round-trip through an older build that can't render it.
//
// Default() ships the #15 demo kinds (findings 0008 §0/§1, owner-locked): the GENUINE TTWR
// floating windows `strategy_editor` and `order`. CHART IS NOT HERE — chart is a Hakoniwa tile
// (#14); TTWR's dispatcher rejects a Chart floating spawn outright. `order` is conceptually a
// singleton (one Order window), `strategy_editor` is multi-instance (ids like
// "strategy_editor:region_001" share this one kind), but the catalog only maps kind -> spec;
// instance identity is the document's job.

using System.Collections.Generic;
using UnityEngine;

public class FloatingWindowCatalog
{
    public const string KIND_STRATEGY_EDITOR = "strategy_editor";
    public const string KIND_ORDER = "order";

    readonly Dictionary<string, FloatingWindowSpec> _specs;

    public FloatingWindowCatalog(IEnumerable<FloatingWindowSpec> specs)
    {
        _specs = new Dictionary<string, FloatingWindowSpec>();
        if (specs != null)
            foreach (var s in specs)
                if (s != null && !string.IsNullOrEmpty(s.kind)) _specs[s.kind] = s;
    }

    public bool TryGet(string kind, out FloatingWindowSpec spec)
    {
        spec = null;
        if (string.IsNullOrEmpty(kind)) return false;
        return _specs.TryGetValue(kind, out spec);
    }

    public bool Contains(string kind) => !string.IsNullOrEmpty(kind) && _specs.ContainsKey(kind);

    // The #15 demo catalog: the two real TTWR floating windows (NOT chart).
    public static FloatingWindowCatalog Default()
    {
        return new FloatingWindowCatalog(new[]
        {
            new FloatingWindowSpec(
                KIND_STRATEGY_EDITOR, "Strategy Editor",
                defaultSize: new Vector2(520f, 380f), minSize: new Vector2(280f, 180f),
                accent: new Color(0.36f, 0.62f, 0.92f, 1f), closeable: true),
            new FloatingWindowSpec(
                KIND_ORDER, "Order",
                defaultSize: new Vector2(360f, 300f), minSize: new Vector2(280f, 180f),
                accent: new Color(0.92f, 0.55f, 0.30f, 1f), closeable: true),
        });
    }
}
