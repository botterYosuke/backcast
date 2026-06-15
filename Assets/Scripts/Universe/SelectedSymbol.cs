// SelectedSymbol.cs — issue #31 "instrument picker / universe sidebar" (focus seam, D2)
//
// The single source of truth for the FOCUSED instrument — the one whose board/chart the
// panels display. This is DISTINCT from the universe (CONTEXT.md "universe registry vs
// scenario panel"): the universe (InstrumentRegistry, #29) is the SET that is the execution
// target (→ scenario.instruments sidecar); SelectedSymbol is the SINGLE id the sidebar row
// click focuses for chart/depth. TTWR splits these the same way (sidebar.rs
// instrument_row_click_system updates `SelectedSymbol`, separate from `InstrumentRegistry`).
//
// backcast is in-proc (ADR-0001), so this is a plain C# observable — NOT a backend
// round-trip. #31 wires the depth panel as the real consumer (DepthDecoder takes an explicit
// instrument id; SelectedSymbol becomes that id's default source — findings 0024 D2). The
// chart consumer is left as a seam for the cutover shell (chart renders the run universe
// implicitly today).
//
// Decisions: docs/findings/0024-instrument-picker-universe-sidebar.md (D2). 方針: ADR-0005.

using System;

public sealed class SelectedSymbol
{
    string _value = "";

    // Empty string = nothing focused (no chart/depth target). Never null.
    public string Value => _value;
    public bool HasValue => !string.IsNullOrEmpty(_value);

    // Fired only on a real change, with the new value — depth/chart subscribers re-point.
    public event Action<string> Changed;

    // Returns true if the focus actually moved (so callers don't churn on a no-op click).
    public bool Set(string id)
    {
        string next = id ?? "";
        if (_value == next) return false;
        _value = next;
        Changed?.Invoke(_value);
        return true;
    }

    public bool Clear() => Set("");
}
