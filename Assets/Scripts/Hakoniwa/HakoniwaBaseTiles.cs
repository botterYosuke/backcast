// HakoniwaBaseTiles.cs — issue #61 "mode-conditional base tiles" (DURABLE tier, pure logic)
//
// The backcast port of TTWR hakoniwa_tile_kinds(mode): the canonical BASE tile id list for a mode
// shape. PURE (no UnityEngine) so the AFK probe asserts the kinds headlessly (findings 0028 §1/§7).
//
// base tile ids are the non-chart, mode-owned residents (chart tiles are the dynamic "chart:<id>"
// family owned by the universe, #60/findings 0027). The ONLY mode-conditional base tile is `startup`
// (Replay-only — TTWR ADR 0013 "Startup is a Replay-only Tile"); BuyingPower/Orders/Positions/
// RunResult are present in BOTH shapes, so a base retile is, in practice, the startup toggle.
//
// SHAPE, not the 3-way mode: LiveManual and LiveAuto share the SAME 4-tile Live shape (TTWR parity,
// HakoniwaLayoutProfile::from_mode), so the base set is keyed on a 2-valued shape (Replay vs Live),
// NOT on the full FooterModeViewModel.DisplayMode string. IsLiveShape() folds the string to the bool.

using System.Collections.Generic;

public static class HakoniwaBaseTiles
{
    public const string Startup     = "startup";
    public const string BuyingPower = "buying_power";
    public const string Orders      = "orders";
    public const string Positions   = "positions";
    public const string RunResult   = "run_result";

    // The 4 panels present in EVERY shape (mode-independent), in canonical order. base(Live) is
    // exactly this; base(Replay) is this with `startup` prepended at index 0.
    public static readonly string[] PanelOrder = { BuyingPower, Orders, Positions, RunResult };

    // Canonical base order for a mode shape. live == false → Replay (startup index 0, TTWR
    // hakoniwa_tile_kinds(Replay)); live == true → Live (no startup, hakoniwa_tile_kinds(Live)).
    public static string[] Kinds(bool live) =>
        live
            ? new[] { BuyingPower, Orders, Positions, RunResult }
            : new[] { Startup, BuyingPower, Orders, Positions, RunResult };

    // Map a footer DisplayMode string to the base SHAPE: any live mode → true; "Replay"/unknown →
    // false (the engine default). LiveManual and LiveAuto collapse to the same Live shape.
    public static bool IsLiveShape(string displayMode) =>
        displayMode == FooterModeViewModel.LiveManual || displayMode == FooterModeViewModel.LiveAuto;

    // True if `id` is a chart tile (the dynamic "chart:<id>" family), i.e. NOT a base tile. The
    // orchestrator and base retile distinguish base from chart by this prefix — the backcast
    // equivalent of TTWR filtering on `PanelKind::Chart` (findings 0028 §1).
    public static bool IsChartId(string id) => id != null && id.StartsWith("chart:");
}
