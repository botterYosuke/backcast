// UniverseSidebarController.cs — issue #31 "instrument picker / universe sidebar" (sidebar brain)
//
// The input-agnostic brain of the screen-fixed left sidebar's Instruments section (TTWR
// sidebar.rs update_sidebar_system + instrument_row_click_system + instrument_remove_button_
// system). A PLAIN C# class (NOT a MonoBehaviour) so the AFK probe drives it headlessly; the
// IMGUI/uGUI view is a thin layer (findings 0024 §1, brain/view split per #29/#42).
//
// COMPOSES the seams without owning the universe SoT: it holds a REFERENCE to the SAME
// InstrumentRegistry #29's ScenarioStartupController owns (the host wires controller.Universe
// in) — one universe per workspace, edited by both #29's text input and #31's sidebar/picker
// (CONTEXT.md "#31 は同じ SoT/writeback に差し込む"). Also holds SelectedSymbol (focus, D2),
// the picker, the writeback, and the available-instruments provider.
//
// MODE: the host supplies the current UniverseSourceMode each call. #31 is Replay-centric;
// in Live, row click still updates the focus but SubscribeMarketData is a DEFERRED seam
// (LiveSubscribeHook, null in #31) and the writeback is a no-op (Replay-gated, D5).
//
// Decisions: docs/findings/0024-instrument-picker-universe-sidebar.md (D1, D2, D4, D5). 方針: ADR-0005.

using System;
using System.Collections.Generic;

// One rendered sidebar instrument row. Price is supplied by the host (LastPrices) — #31 keeps
// the column for TTWR parity but the price feed wiring is the host's existing concern.
public struct SidebarRow
{
    public string Id;
    public bool Selected;   // == SelectedSymbol.Value (the focused row)
}

public sealed class UniverseSidebarController
{
    readonly InstrumentRegistry _registry;
    readonly SelectedSymbol _selected;
    readonly UniverseWriteback _writeback;
    readonly IAvailableInstrumentsProvider _provider;

    public InstrumentPickerController Picker { get; } = new InstrumentPickerController();

    // Live subscribe seam (D5): in Live mode a row select / [+ Add] subscribes market data
    // (TTWR instrument_row_click_system Live branch). #31 left this null (DEFERRED); #107 wires it to
    // LiveSubscriptionCoordinator.OnLiveRowSelected (方針 ADR-0022). Fired only in Live; never touches
    // the universe registry (membership 不可侵 — the subscribe is subordinate to the add/select).
    public Action<string> LiveSubscribeHook { get; set; }

    public UniverseSidebarController(
        InstrumentRegistry registry,
        SelectedSymbol selected,
        UniverseWriteback writeback,
        IAvailableInstrumentsProvider provider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _writeback = writeback ?? throw new ArgumentNullException(nameof(writeback));
        _provider = provider;
    }

    public InstrumentRegistry Registry => _registry;
    public SelectedSymbol Selected => _selected;
    public UniverseWriteback Writeback => _writeback;
    public bool Editable => _registry.Editable;

    // The universe rows for rendering, in registry (set) order, flagged with the focus.
    public IReadOnlyList<SidebarRow> Rows()
    {
        var ids = _registry.Ids;
        var rows = new List<SidebarRow>(ids.Count);
        foreach (string id in ids)
            rows.Add(new SidebarRow { Id = id, Selected = id == _selected.Value });
        return rows;
    }

    // ── [+ Add] / picker ──────────────────────────────────────────────────────
    public void TogglePicker(UniverseSourceMode mode, string replayEndDate) =>
        Picker.Toggle(_registry, mode, replayEndDate);

    public IReadOnlyList<PickerRow> PickerList(UniverseSourceMode mode) =>
        Picker.BuildList(_provider, _registry, mode);

    // Cheap async-supply revision for the view's per-frame poll: the supply STATUS (kind + id count)
    // WITHOUT the filter/sort/already-added work PickerList does. The view polls this while the picker
    // is open to catch a Loading→Ready/Empty transition that has no discrete event (the production
    // BackendAvailableInstrumentsProvider resolves on a background thread) — WITHOUT re-sorting the
    // whole listed_info universe (~4400 ids) every frame now that the take(15) cap is gone (findings 0101).
    // A ValueTuple key so the per-frame compare is allocation-free and value-equal out of the box
    // (no hand-rolled equality boilerplate). NOTE: this gate is strictly WEAKER than the per-row
    // signature it replaced — a supply that resolves to a DIFFERENT id set with the SAME (Kind, Count)
    // under a stable key would not repaint. The shipping BackendAvailableInstrumentsProvider caches
    // per (mode, end) and only transitions Loading→Ready/Empty/Error (always changing Kind or Count),
    // so this holds today; an in-place equal-count universe swap would need a richer revision.
    public (UniverseStatusKind Kind, int Count) CurrentSupplyRevision(UniverseSourceMode mode)
    {
        var r = _provider != null
            ? _provider.Query(mode, Picker.ReplayEndSnapshot)
            : AvailableInstrumentsResult.Empty;
        return (r.Kind, r.Ids != null ? r.Ids.Count : 0);
    }

    // Click a picker candidate → add to the universe + flush the writeback (Replay-gated).
    // #107: in Live, a newly added instrument must also be market-data subscribed (AC#2 [+ Add]).
    // Same DEFERRED seam as row-select (LiveSubscribeHook) so the production trigger is one place;
    // membership add is unchanged (the subscribe is subordinate, never gates the add — ADR-0022 D3).
    public bool AddFromPicker(string id, UniverseSourceMode mode, IStrategyFileProvider strategyProvider, long nowMs)
    {
        bool added = Picker.ClickRow(id, _registry, _writeback, nowMs);
        if (added)
        {
            _writeback.Flush(_registry, strategyProvider, mode);
            if (mode == UniverseSourceMode.Live) LiveSubscribeHook?.Invoke(id);  // null in #31 (seam) — wired by #107
        }
        return added;
    }

    // ── row × remove ──────────────────────────────────────────────────────────
    public bool Remove(string id, UniverseSourceMode mode, IStrategyFileProvider strategyProvider)
    {
        if (!_registry.Editable) return false;       // TTWR: locked registry × is a no-op
        bool removed = _registry.Remove(id);
        if (removed) _writeback.Flush(_registry, strategyProvider, mode);
        // Note: a removed id that was the focus is left as-is (TTWR does not clear SelectedSymbol).
        return removed;
    }

    // ── row label click → focus (chart/depth target) ──────────────────────────
    // Replay: focus only. Live: focus + (deferred) subscribe. Returns true if focus moved.
    public bool SelectRow(string id, UniverseSourceMode mode)
    {
        bool moved = _selected.Set(id);
        if (mode == UniverseSourceMode.Live) LiveSubscribeHook?.Invoke(id);  // null in #31 (seam)
        return moved;
    }

    // Restore hook: after #29's Populate filled the registry from the sidecar, prime the
    // writeback so the restored set isn't redundantly re-written on the next flush (D4).
    public void PrimeWritebackFromCurrent() => _writeback.Prime(_registry.Ids);
}
