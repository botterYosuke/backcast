// FloatingWindowSpec.cs — issue #15 "floating windows" (DURABLE tier, spec)
//
// The spec-driven DEFINITION of a window KIND (AC: "window は spec 駆動で生成される"). The
// capability-parity analogue of TTWR's FloatingWindowSpec (src/ui/floating_window/mod.rs:
// title / size / accent / closeable), MINUS the fields out of #15's scope (findings 0008 §0):
// no `resizable` (resize is a future slice), no `position` (the catalog is kind-keyed; the
// instance carries its own position), no content factory (the body is a Python-free
// placeholder; real Strategy Editor / Order content is a later slice — the catalog never owns
// content, findings 0008 §6).
//
// `minSize` is the SPAWN-boundary clamp (findings 0008 §3): LayoutStore drops a non-finite/<=0
// size, but a small-but-positive persisted size is clamped UP to minSize here at spawn, not by
// the persistence layer. UnityEngine types (Vector2/Color) are fine — this is code config, not
// a serialized POCO (it never round-trips to disk; only `kind` does, on the instance).

using UnityEngine;

public class FloatingWindowSpec
{
    public readonly string kind;        // the persisted key the catalog re-spawns from
    public readonly string title;       // title-bar caption
    public readonly Vector2 defaultSize; // spawn size when no persisted size is supplied
    public readonly Vector2 minSize;    // spawn-boundary minimum (clamp persisted size UP to this)
    public readonly Color accent;       // rim/title accent (HITL visual only)
    public readonly bool closeable;     // whether a close button (-> visible=false) is shown

    public FloatingWindowSpec(string kind, string title, Vector2 defaultSize, Vector2 minSize, Color accent, bool closeable)
    {
        this.kind = kind;
        this.title = title;
        this.defaultSize = defaultSize;
        this.minSize = minSize;
        this.accent = accent;
        this.closeable = closeable;
    }
}
