// InstrumentPickerController.cs — issue #31 "instrument picker / universe sidebar" (picker brain)
//
// The input-agnostic brain of the [+ Add] dropdown picker — a PLAIN C# class (NOT a
// MonoBehaviour) so the AFK probe drives it headlessly (mirrors ScenarioStartupController).
// Ports TTWR instrument_picker.rs logic WITHOUT its #266 backend round-trip: backcast is
// in-proc (ADR-0001) and the C# InstrumentRegistry is the direct SoT (#29), so a row click
// mutates the registry directly (→ UniverseWriteback) instead of sending AddInstrument and
// waiting for a CuratedSetSnapshot mirror (findings 0024 §1).
//
// Owns: visible toggle, search query, the scenario.end snapshot (Replay), and the 100ms
// same-id debounce (TTWR §3.4). Open()/Close() match add_instrument_button_system /
// force_close_picker_on_lock_system; BuildList() reproduces picker_list_rebuild_system's
// status→rows/placeholder mapping; ClickRow() reproduces handle_picker_row_click.
//
// Decisions: docs/findings/0024-instrument-picker-universe-sidebar.md (D3, D5). 方針: ADR-0005.

using System;
using System.Collections.Generic;
using System.Linq;

// One rendered picker entry. A placeholder carries only Label (status/empty/no-matches text)
// and is NOT clickable; a real row carries Id + AlreadyAdded (greyed when in the universe).
public struct PickerRow
{
    public bool IsPlaceholder;
    public string Label;        // display text (id [+ optional name] for a real row, message for a placeholder)
    public string Id;           // null for a placeholder
    public bool AlreadyAdded;   // real row already present in the universe

    public static PickerRow Placeholder(string label) =>
        new PickerRow { IsPlaceholder = true, Label = label, Id = null, AlreadyAdded = false };
    // Issue #46 / review finding A5: an optional human-readable name is appended to the label
    // ("<id> <name>") so the picker shows '7203.TSE トヨタ自動車' instead of '7203.TSE' alone.
    // The `<id> <name>` formatter is now shared with the chart-window chrome title (issue #140 /
    // findings 0112) via InstrumentLabel.Compose so the two surfaces can never drift on the
    // null/empty/equal-to-id collapse rule. CONTEXT.md "銘柄表示ラベル" enforces this single source.
    public static PickerRow Candidate(string id, string name, bool alreadyAdded)
    {
        return new PickerRow { IsPlaceholder = false, Label = InstrumentLabel.Compose(id, name), Id = id, AlreadyAdded = alreadyAdded };
    }
}

public sealed class InstrumentPickerController
{
    // No display cap (owner decision 2026-06-24): the picker exposes the WHOLE listed_info universe
    // (~4400 instruments), NOT TTWR's take(15) — the user opens [+ Add] to browse/scroll every stock,
    // not just the first 15 ordinal codes. The view (UniverseSidebarView) VIRTUALIZES the render
    // (PickerListWindow) so returning thousands of candidates here does not freeze the UI. Divergence
    // from TTWR is justified by the owner ("listed_info の銘柄が一覧に表示される") — see findings 0101.
    public const long DebounceMs = 100;         // TTWR §3.4 same-id debounce

    public bool Visible { get; private set; }
    public string Query { get; private set; } = "";
    // scenario.end snapshot captured on open (Replay only; null in Live) — the picker OWNS
    // this snapshot so a later edit to the panel doesn't re-scope an open dropdown (TTWR R1).
    public string ReplayEndSnapshot { get; private set; }

    string _lastAddedId;
    long _lastAddedAtMs;

    // [+ Add] press: toggle. On open, clear the query and snapshot scenario.end (Replay).
    // Locked registry (instruments_ref) never opens (TTWR add_instrument_button_system guard).
    public void Toggle(InstrumentRegistry registry, UniverseSourceMode mode, string replayEndDate)
    {
        if (registry != null && !registry.Editable) return;
        if (Visible) { Close(); return; }
        Visible = true;
        Query = "";
        ReplayEndSnapshot = mode == UniverseSourceMode.Replay ? replayEndDate : null;
    }

    public void Close()
    {
        Visible = false;
        Query = "";
    }

    // Force-close when the registry locks (instruments_ref) — TTWR force_close_picker_on_lock.
    public void ForceCloseIfLocked(InstrumentRegistry registry)
    {
        if (registry != null && !registry.Editable && Visible) Close();
    }

    // ---- search query editing (driven by the view's keyboard input) ----
    public void SetQuery(string q) { if (Visible) Query = q ?? ""; }
    public void AppendChar(char c) { if (Visible && !char.IsControl(c)) Query += c; }
    public void Backspace() { if (Visible && Query.Length > 0) Query = Query.Substring(0, Query.Length - 1); }
    public void Escape() { if (Visible) Close(); }

    // Build the rendered list from the supply status + the universe (for already-added flags).
    // Order: status placeholders first (EndUnset/Loading/Error/NotConnected), then empty-source,
    // then query filter + sort (ALL matches — no take(15) cap; the view virtualizes), then no-matches.
    // Mirrors picker_list_rebuild_system except the cap (owner 2026-06-24 — see the no-cap note above).
    public IReadOnlyList<PickerRow> BuildList(
        IAvailableInstrumentsProvider provider, InstrumentRegistry registry, UniverseSourceMode mode)
    {
        var result = provider != null
            ? provider.Query(mode, ReplayEndSnapshot)
            : AvailableInstrumentsResult.Empty;

        switch (result.Kind)
        {
            case UniverseStatusKind.EndUnset:
                return One("Set scenario.end first");
            case UniverseStatusKind.Loading:
                return One("Loading...");
            case UniverseStatusKind.Error:
                return One("Error: " + (result.Message ?? ""));
            case UniverseStatusKind.NotConnected:
                return One("Venue not connected");
            case UniverseStatusKind.Unsupported:
                // Connected, but the venue exposes no instrument master (kabu MVP). Must NOT say
                // "Venue not connected" — that contradicts the menu badge (findings 0103).
                return One("Venue has no instrument list");
            case UniverseStatusKind.Empty:
                return One(EmptyMessage(mode));
        }

        // Ready: distinguish empty-universe (0 ids) from query-excluded-all (TTWR comment:
        // mixing them yields a wrong "No matches" when nothing was searched).
        IReadOnlyList<string> ids = result.Ids ?? Array.Empty<string>();
        IReadOnlyList<string> names = result.Names ?? Array.Empty<string>();
        if (ids.Count == 0) return One(EmptyMessage(mode));

        // Filter by id OR name (review finding A5): kabu users from kabuStation expect to
        // search by company name (e.g. 'トヨタ'), not just the 4-digit ticker. We keep both
        // the id and name handy for the row build below so the matched indices don't need a
        // re-lookup. Names is parallel to ids and may be shorter / individually empty (legacy
        // schemas, NULL CompanyName); fall back to id-only matching for those entries.
        string q = Query ?? "";
        int n = ids.Count;
        var matchedIds = new List<string>(n);
        var matchedNames = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            string id = ids[i];
            string name = i < names.Count ? (names[i] ?? "") : "";
            bool match = q.Length == 0
                || id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (name.Length > 0 && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!match) continue;
            matchedIds.Add(id);
            matchedNames.Add(name);
        }

        if (matchedIds.Count == 0) return One("No matches");

        // Sort matched rows by id (ordinal) — pair (id,name) so the name follows its id after
        // the sort. Indices into matchedIds/matchedNames are interchangeable up to this point;
        // we materialize a single permutation array to avoid two parallel sorts going out of
        // sync.
        var order = new int[matchedIds.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => string.CompareOrdinal(matchedIds[a], matchedIds[b]));

        // Hoist the already-added lookup OUT of the per-candidate loop. registry.Ids allocates a fresh
        // ReadOnlyCollection wrapper on every get AND .Contains is an O(curated) linear scan — over the
        // now-uncapped ~4400-id universe (findings 0101) that was ~4400 wrappers × O(n) compares PER
        // keystroke. A HashSet built once makes the per-candidate check O(1). (case-insensitive substring
        // is OrdinalIgnoreCase IndexOf — no per-id ToLowerInvariant() allocation over the full universe.)
        var owned = registry != null ? new HashSet<string>(registry.Ids) : null;
        var rows = new List<PickerRow>(order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            int idx = order[i];
            string id = matchedIds[idx];
            string name = matchedNames[idx];
            bool already = owned != null && owned.Contains(id);
            rows.Add(PickerRow.Candidate(id, name, already));
        }
        return rows;
    }

    // Click a candidate row: add to the universe (direct SoT mutation) + mark the writeback
    // dirty. Same-id 100ms debounce (TTWR §3.4). The picker STAYS OPEN for continuous adds
    // (close is Escape / [+ Add] toggle). Locked registry → no-op. Returns true if added.
    public bool ClickRow(string id, InstrumentRegistry registry, UniverseWriteback writeback, long nowMs)
    {
        if (registry == null || !registry.Editable || string.IsNullOrEmpty(id)) return false;
        if (_lastAddedId == id && (nowMs - _lastAddedAtMs) < DebounceMs) return false;

        bool added = registry.Add(id);
        _lastAddedId = id;
        _lastAddedAtMs = nowMs;
        return added;  // caller flushes the writeback (it coalesces on no real change anyway)
    }

    static string EmptyMessage(UniverseSourceMode mode) =>
        mode == UniverseSourceMode.Replay ? "No instruments for this date" : "No instruments in venue";

    static IReadOnlyList<PickerRow> One(string label) => new[] { PickerRow.Placeholder(label) };
}
