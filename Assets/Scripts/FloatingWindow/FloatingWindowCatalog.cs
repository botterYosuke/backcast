// FloatingWindowCatalog.cs — issue #15 "floating windows" (DURABLE tier, spec)
//
// The kind -> FloatingWindowSpec registry that makes window creation SPEC-DRIVEN (AC2). The
// controller resolves a persisted `kind` here at restore; an UNKNOWN kind returns false from
// TryGet so the controller SKIPS its spawn while LayoutStore keeps the entry in the document
// (forward-evolution discipline, findings 0008 §3) — a window kind added by a newer build
// survives a round-trip through an older build that can't render it.
//
// Default() ships:
//   * `strategy_editor` and `order` — the GENUINE TTWR floating windows (findings 0008 §0/§1).
//   * #99 Slice 2 / ADR-0017 / findings 0075 §2 — `chart` / `buying_power` / `orders` /
//     `positions` (`run_result` retired by ADR-0037, `startup` by ADR-0026). The Hakoniwa surface
//     was ported FROM split-grid tiles TO the floating-window seam (ADR-0017 Decision 1), so the
//     former tile KINDS land here. `chart` is MULTI-INSTANCE (ids "chart:<instrument-id>" share this
//     one kind, same shape as `strategy_editor:<region>`); the other 3 are conceptually singletons
//     (one BuyingPower etc.). The catalog only maps kind -> spec; instance identity is the
//     document's job. Per-kind accents come from PlayerColors so the dock cluster stays
//     visually distinguishable without inline literals (findings 0020).

using System.Collections.Generic;
using UnityEngine;

public class FloatingWindowCatalog
{
    public const string KIND_STRATEGY_EDITOR = "strategy_editor";
    public const string KIND_ORDER = "order";

    // #99 Slice 2 (ADR-0017 / findings 0075 §2, owner-locked): the kinds that succeed
    // `Hakoniwa` (the dock cluster). Names preserve the existing tile ids so an old persisted
    // doc that mentions e.g. "orders" is forward-compatible when read as a floating-window kind.
    public const string KIND_CHART = "chart";
    public const string KIND_BUYING_POWER = "buying_power";
    public const string KIND_ORDERS = "orders";
    public const string KIND_POSITIONS = "positions";
    // KIND_STARTUP ("startup") RETIRED — ADR-0026: Scenario Startup moved to the Settings modal's
    // Scenario section; the dock no longer hosts a startup window.
    // KIND_RUN_RESULT ("run_result") RETIRED — ADR-0037 (findings 0125 D4): run_result is cut over from
    // a dock base singleton to a screen-anchored popup (RunResultPopup), so it is no longer a catalog
    // kind. Both are forward-compat: a pre-retirement saved layout naming "startup"/"run_result" gets
    // TryGet=false → spawn skipped, layout entry kept (forward-evolution discipline, findings 0008 §3).

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

    // The default catalog: the 2 floating-window seam kinds (strategy_editor / order) + the
    // #99 dock kinds (chart / buying_power / orders / positions; startup retired by ADR-0026,
    // run_result by ADR-0037) that replaced the Hakoniwa split-grid tile system (ADR-0017). Per-kind
    // accents come from the theme PlayerColors palette (findings 0020) so the cluster stays
    // distinguishable without inline literals; PlayerColors cycles modulo 8.
    // closeable=false on the dock kinds: they are WORKSPACE-OWNED (the View menu / mode poll
    // governs visibility), so an in-title-bar X is suppressed — the user does not close them
    // accidentally and lose them (the strategy_editor / order frames keep their X as before).
    public static FloatingWindowCatalog Default()
    {
        var players = ThemeService.Current.players;
        return new FloatingWindowCatalog(new[]
        {
            new FloatingWindowSpec(
                KIND_STRATEGY_EDITOR, "Strategy Editor",
                defaultSize: new Vector2(520f, 380f), minSize: new Vector2(280f, 180f),
                accent: players.Get(0), closeable: true),
            new FloatingWindowSpec(
                KIND_ORDER, "Order",
                defaultSize: new Vector2(360f, 300f), minSize: new Vector2(280f, 180f),
                accent: players.Get(2), closeable: true),
            // #99 dock kinds (findings 0075 §2): the 6 former Hakoniwa tile kinds, now floating
            // windows. defaultSize is reasonable for first-launch read; minSize keeps a tile from
            // collapsing to unreadable; accents spread across the remaining PlayerColors slots.
            new FloatingWindowSpec(
                KIND_CHART, "Chart",
                defaultSize: new Vector2(520f, 360f), minSize: new Vector2(280f, 200f),
                accent: players.Get(1), closeable: false),
            new FloatingWindowSpec(
                KIND_BUYING_POWER, "Buying Power",
                defaultSize: new Vector2(340f, 140f), minSize: new Vector2(220f, 100f),
                accent: players.Get(3), closeable: false),
            new FloatingWindowSpec(
                KIND_ORDERS, "Orders",
                defaultSize: new Vector2(380f, 220f), minSize: new Vector2(240f, 140f),
                accent: players.Get(4), closeable: false),
            new FloatingWindowSpec(
                KIND_POSITIONS, "Positions",
                defaultSize: new Vector2(380f, 220f), minSize: new Vector2(240f, 140f),
                accent: players.Get(5), closeable: false),
            // KIND_STARTUP spec RETIRED — ADR-0026 (Scenario Startup → Settings modal).
            // KIND_RUN_RESULT spec RETIRED — ADR-0037 (run_result → RunResultPopup, findings 0125 D4).
            // Dropping a kind from Default() is what makes a saved window of that kind skip on restore
            // (catalog TryGet=false) while LayoutStore keeps the entry — forward-compat without migration.
        });
    }
}
