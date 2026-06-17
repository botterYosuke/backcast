// UniverseSidebarProbe.cs — issue #31 "instrument picker / universe sidebar" (AFK regression gate)
//
// Headless, Python-FREE gate for the #31 brain seams. Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod UniverseSidebarProbe.Run -logFile <log>
//   # expect: [UNIVERSE SIDEBAR PASS] ... / exit=0
//
// Covers (findings 0024): picker open/lock/force-close, status→rows/placeholder for EVERY
// UniverseStatus (stub-injected, D3), query filter/sort/take15/no-matches/already-added,
// click→registry add + 100ms debounce + lock no-op, × remove + lock no-op, row→SelectedSymbol
// (+ Live deferred-subscribe hook, Replay does not fire it, D5), revision/content-diff
// writeback with Replay gate + editable gate + path-unresolved skip + Prime (D4), and the
// real consumer: DepthDecoder follows SelectedSymbol (D2). The view (IMGUI) is HITL-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class UniverseSidebarProbe
{
    static string TempDir => Path.Combine(Application.temporaryCachePath, "universe_sidebar_probe");
    static string StrategyPath => Path.Combine(TempDir, "my_strategy.py");
    static string SidecarPath => Path.Combine(TempDir, "my_strategy.json");

    // ---- stubs -------------------------------------------------------------------------
    sealed class StubProvider : IAvailableInstrumentsProvider
    {
        public AvailableInstrumentsResult Next = AvailableInstrumentsResult.Empty;
        public AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate) => Next;
    }

    sealed class StubStrategyProvider : IStrategyFileProvider
    {
        public string Path;  // null/empty = not supplyable (no saved strategy)
        public bool TryGetStrategyFile(out string path) { path = Path; return !string.IsNullOrEmpty(Path); }
    }

    public static void Run()
    {
        string fail = null;
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            fail = Section1_PickerOpenLockForceClose()
                ?? Section2_StatusPlaceholders()
                ?? Section3_QueryFilterAndRows()
                ?? Section4_ClickAddDebounceAndLock()
                ?? Section5_RemoveAndLock()
                ?? Section6_SelectFocusAndLiveHook()
                ?? Section7_Writeback()
                ?? Section8_DepthFollowsSelection();
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[UNIVERSE SIDEBAR PASS] picker + status + select + writeback + depth-follow verified");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[UNIVERSE SIDEBAR FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static UniverseSidebarController NewController(out InstrumentRegistry reg, out SelectedSymbol sel, StubProvider provider)
    {
        reg = new InstrumentRegistry();
        sel = new SelectedSymbol();
        return new UniverseSidebarController(reg, sel, new UniverseWriteback(), provider);
    }

    // ---- 1. picker open / lock guard / force-close ----
    static string Section1_PickerOpenLockForceClose()
    {
        var ctrl = NewController(out var reg, out _, new StubProvider());
        var p = ctrl.Picker;

        if (p.Visible) return "picker starts visible";
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");
        if (!p.Visible) return "toggle did not open picker";
        if (p.ReplayEndSnapshot != "2024-12-31") return "open did not snapshot scenario.end";
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");
        if (p.Visible) return "second toggle did not close picker";

        // Live open: no end snapshot.
        ctrl.TogglePicker(UniverseSourceMode.Live, "2024-12-31");
        if (!p.Visible || p.ReplayEndSnapshot != null) return "Live open should not snapshot end";
        p.Close();

        // Locked registry never opens.
        reg.Editable = false;
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");
        if (p.Visible) return "locked registry opened picker";

        // Force-close when locked mid-open.
        reg.Editable = true;
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");
        p.SetQuery("abc");
        reg.Editable = false;
        p.ForceCloseIfLocked(reg);
        if (p.Visible) return "lock did not force-close picker";
        if (p.Query.Length != 0) return "force-close did not clear query";
        return null;
    }

    // ---- 2. every UniverseStatus → its placeholder (D3) ----
    static string Section2_StatusPlaceholders()
    {
        var provider = new StubProvider();
        var ctrl = NewController(out _, out _, provider);
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");

        string OnePlaceholder(UniverseSourceMode mode, string expect)
        {
            var rows = ctrl.PickerList(mode);
            if (rows.Count != 1 || !rows[0].IsPlaceholder) return "expected single placeholder for " + expect;
            if (rows[0].Label != expect) return $"placeholder label '{rows[0].Label}' != '{expect}'";
            return null;
        }

        provider.Next = AvailableInstrumentsResult.EndUnset;
        var r = OnePlaceholder(UniverseSourceMode.Replay, "Set scenario.end first"); if (r != null) return r;
        provider.Next = AvailableInstrumentsResult.Loading;
        r = OnePlaceholder(UniverseSourceMode.Replay, "Loading..."); if (r != null) return r;
        provider.Next = AvailableInstrumentsResult.Error("timeout");
        r = OnePlaceholder(UniverseSourceMode.Replay, "Error: timeout"); if (r != null) return r;
        provider.Next = AvailableInstrumentsResult.NotConnected;
        r = OnePlaceholder(UniverseSourceMode.Live, "Venue not connected"); if (r != null) return r;
        provider.Next = AvailableInstrumentsResult.Empty;
        r = OnePlaceholder(UniverseSourceMode.Replay, "No instruments for this date"); if (r != null) return r;
        r = OnePlaceholder(UniverseSourceMode.Live, "No instruments in venue"); if (r != null) return r;
        // Ready but zero ids behaves like Empty (mode-specific message).
        provider.Next = AvailableInstrumentsResult.Ready(Array.Empty<string>());
        r = OnePlaceholder(UniverseSourceMode.Replay, "No instruments for this date"); if (r != null) return r;
        return null;
    }

    // ---- 3. Ready → filter + sort + take(15) + no-matches + already-added ----
    static string Section3_QueryFilterAndRows()
    {
        var provider = new StubProvider();
        var ctrl = NewController(out var reg, out _, provider);
        ctrl.TogglePicker(UniverseSourceMode.Replay, "2024-12-31");
        reg.Add("9984.TSE");  // pre-existing in universe → AlreadyAdded flag

        provider.Next = AvailableInstrumentsResult.Ready(new[] { "9984.TSE", "1301.TSE", "7203.TSE" });
        var rows = ctrl.PickerList(UniverseSourceMode.Replay);
        if (rows.Any(x => x.IsPlaceholder)) return "Ready produced a placeholder";
        // sorted ordinal: 1301, 7203, 9984
        if (rows.Count != 3 || rows[0].Id != "1301.TSE" || rows[1].Id != "7203.TSE" || rows[2].Id != "9984.TSE")
            return "rows not sorted ordinal";
        if (!rows[2].AlreadyAdded) return "9984.TSE should be flagged already-added";
        if (rows[0].AlreadyAdded) return "1301.TSE should not be already-added";

        // query filter (case-insensitive contains).
        ctrl.Picker.SetQuery("72");
        rows = ctrl.PickerList(UniverseSourceMode.Replay);
        if (rows.Count != 1 || rows[0].Id != "7203.TSE") return "query filter wrong";

        // no-matches placeholder (distinct from empty-source).
        ctrl.Picker.SetQuery("zzz");
        rows = ctrl.PickerList(UniverseSourceMode.Replay);
        if (rows.Count != 1 || !rows[0].IsPlaceholder || rows[0].Label != "No matches") return "no-matches placeholder wrong";

        // take(15) cap.
        ctrl.Picker.SetQuery("");
        provider.Next = AvailableInstrumentsResult.Ready(
            Enumerable.Range(0, 30).Select(i => $"{1000 + i}.TSE").ToArray());
        rows = ctrl.PickerList(UniverseSourceMode.Replay);
        if (rows.Count != InstrumentPickerController.MaxRows) return "take(15) cap not applied";
        return null;
    }

    // ---- 4. click → registry add + 100ms same-id debounce + lock no-op ----
    static string Section4_ClickAddDebounceAndLock()
    {
        var ctrl = NewController(out var reg, out _, new StubProvider());
        var sp = new StubStrategyProvider { Path = null };  // no path → in-memory only

        long t0 = 1_000_000;
        if (!ctrl.AddFromPicker("7203.TSE", UniverseSourceMode.Replay, sp, t0)) return "first add did not return true";
        if (!reg.Ids.Contains("7203.TSE")) return "registry not mutated on add";

        // same id within 100ms → debounced (no-op, registry unchanged count).
        ctrl.AddFromPicker("7203.TSE", UniverseSourceMode.Replay, sp, t0 + 50);
        if (reg.Count != 1) return "debounce failed: duplicate within 100ms";

        // different id within 100ms → allowed.
        if (!ctrl.AddFromPicker("9984.TSE", UniverseSourceMode.Replay, sp, t0 + 50)) return "different id within 100ms blocked";
        if (reg.Count != 2) return "second distinct add missing";

        // same id after 100ms → registry already has it so Add returns false, but no crash.
        ctrl.AddFromPicker("7203.TSE", UniverseSourceMode.Replay, sp, t0 + 200);
        if (reg.Count != 2) return "post-debounce re-add changed set (dedup expected)";

        // locked registry → no-op.
        reg.Editable = false;
        ctrl.AddFromPicker("1301.TSE", UniverseSourceMode.Replay, sp, t0 + 1000);
        if (reg.Ids.Contains("1301.TSE")) return "locked registry accepted add";
        return null;
    }

    // ---- 5. × remove + lock no-op ----
    static string Section5_RemoveAndLock()
    {
        var ctrl = NewController(out var reg, out _, new StubProvider());
        var sp = new StubStrategyProvider { Path = null };
        reg.ReplaceAll(new[] { "7203.TSE", "9984.TSE" });

        if (!ctrl.Remove("7203.TSE", UniverseSourceMode.Replay, sp)) return "remove returned false";
        if (reg.Ids.Contains("7203.TSE")) return "remove did not mutate registry";

        reg.Editable = false;
        if (ctrl.Remove("9984.TSE", UniverseSourceMode.Replay, sp)) return "locked registry allowed remove";
        if (!reg.Ids.Contains("9984.TSE")) return "locked remove still mutated registry";
        return null;
    }

    // ---- 6. row → SelectedSymbol (+ Live deferred hook, Replay does not fire) ----
    static string Section6_SelectFocusAndLiveHook()
    {
        var ctrl = NewController(out var reg, out var sel, new StubProvider());
        reg.ReplaceAll(new[] { "7203.TSE", "9984.TSE" });

        int changed = 0; string lastChanged = null;
        sel.Changed += v => { changed++; lastChanged = v; };

        if (!ctrl.SelectRow("7203.TSE", UniverseSourceMode.Replay)) return "select did not move focus";
        if (sel.Value != "7203.TSE" || changed != 1 || lastChanged != "7203.TSE") return "SelectedSymbol/event wrong";

        // re-select same → no event.
        if (ctrl.SelectRow("7203.TSE", UniverseSourceMode.Replay)) return "re-select same fired move";
        if (changed != 1) return "no-op select fired Changed";

        // Replay must NOT fire the Live subscribe hook.
        bool hookFired = false;
        ctrl.LiveSubscribeHook = _ => hookFired = true;
        ctrl.SelectRow("9984.TSE", UniverseSourceMode.Replay);
        if (hookFired) return "Replay fired Live subscribe hook";

        // Live fires the hook (deferred seam present).
        ctrl.SelectRow("7203.TSE", UniverseSourceMode.Live);
        if (!hookFired) return "Live did not fire subscribe hook";

        // sidebar rows reflect the focus.
        var rows = ctrl.Rows();
        if (rows.Count != 2) return "rows count wrong";
        if (!rows.Single(x => x.Id == "7203.TSE").Selected) return "focused row not flagged";
        if (rows.Single(x => x.Id == "9984.TSE").Selected) return "non-focused row flagged";
        return null;
    }

    // ---- 7. writeback: content-diff + Replay gate + editable gate + path skip + Prime (D4) ----
    // #67: universe-only writeback is MUTATE-EXISTING-ONLY (TTWR set_instruments parity). It must
    // NOT create a fresh sidecar — a {schema_version, instruments}-only sidecar would shadow the
    // inline .py SCENARIO (load_scenario prefers the sidecar) and break register_live_strategy with
    // STRATEGY_LOAD_FAILED. So a flush before a complete sidecar exists SKIPS (edit stays in the
    // in-memory registry, persisted later by #29's Run-commit which writes the full sidecar).
    static string Section7_Writeback()
    {
        var wb = new UniverseWriteback();
        var reg = new InstrumentRegistry();
        reg.ReplaceAll(new[] { "7203.TSE", "9984.TSE" });
        var sp = new StubStrategyProvider { Path = StrategyPath };

        // #67 RED: no sidecar yet → flush SKIPS without creating an incomplete sidecar (the kill:
        // the old create-on-absent wrote {schema_version, instruments} and broke live load).
        if (wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "flush created a sidecar before one existed (incomplete-sidecar regression #67)";
        if (File.Exists(SidecarPath)) return "universe-only flush created a sidecar file from nothing (#67)";

        // Run-commit seeds the COMPLETE sidecar (the only creator). After it exists, the pending
        // universe edit flushes by MUTATING it, preserving start/end/granularity/initial_cash.
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            StrategyPath,
            new StartupParamsForWrite("2024-01-01", "2024-03-01", "Daily", "1000000"),
            new[] { "7203.TSE", "9984.TSE" });

        // a real universe change now mutates-existing; content-diff still coalesces no-ops.
        reg.Add("8035.TSE");
        if (!wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "flush of pending change did not write into existing sidecar";
        var snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (snap == null || snap.Instruments.Count != 3 || snap.Instruments[0] != "7203.TSE" || snap.Instruments[2] != "8035.TSE")
            return "sidecar instruments not persisted in order";
        // mutate-existing PRESERVED the startup window (the whole point of #67).
        if (snap.Start != "2024-01-01" || snap.End != "2024-03-01" || snap.Granularity != "Daily" || snap.InitialCash != 1000000)
            return "universe writeback clobbered the startup window (merge not preserved, #67)";

        // content unchanged → coalesce (no write).
        if (wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "unchanged content re-wrote";

        // Live → no-op (Replay-gated), even with a content change.
        reg.Add("1301.TSE");
        if (wb.Flush(reg, sp, UniverseSourceMode.Live)) return "Live mode wrote (should be gated)";
        snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (snap.Instruments.Contains("1301.TSE")) return "Live flush leaked to sidecar";

        // back to Replay → the pending 1301 change now flushes.
        if (!wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "Replay flush of pending change missed";
        snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (!snap.Instruments.Contains("1301.TSE")) return "pending change not persisted on Replay flush";

        // editable=false → no write.
        reg.Add("6758.TSE");
        reg.Editable = false;
        if (wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "locked registry wrote";
        reg.Editable = true;

        // path unresolved → SKIP without recording flushed (retry later succeeds).
        var noPath = new StubStrategyProvider { Path = null };
        if (wb.Flush(reg, noPath, UniverseSourceMode.Replay)) return "unresolved path wrote";
        snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (snap.Instruments.Contains("6758.TSE")) return "unresolved-path flush leaked";
        // now a path resolves → the still-pending 6758 must flush (proves we didn't drop it).
        if (!wb.Flush(reg, sp, UniverseSourceMode.Replay)) return "retry after path resolved did not write";
        snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (!snap.Instruments.Contains("6758.TSE")) return "pending edit dropped after path skip";

        // Prime → marks current as in-sync without writing (restore path, D4).
        var wb2 = new UniverseWriteback();
        wb2.Prime(reg.Ids);
        if (wb2.Flush(reg, sp, UniverseSourceMode.Replay)) return "Prime did not suppress redundant write";
        return null;
    }

    // ---- 8. the real consumer: DepthDecoder follows SelectedSymbol (D2) ----
    static string Section8_DepthFollowsSelection()
    {
        var sel = new SelectedSymbol();
        // get_state_json-shaped: per_instrument keyed by id, each with a depth object.
        const string state = @"{""per_instrument"":{
          ""7203.TSE"":{""depth"":{""bids"":[{""price"":100.0,""size"":5.0}],""asks"":[{""price"":101.0,""size"":3.0}],""timestamp_ms"":111}},
          ""9984.TSE"":{""depth"":{""bids"":[{""price"":200.0,""size"":9.0}],""asks"":[{""price"":201.0,""size"":7.0}],""timestamp_ms"":222}}
        }}";

        // No focus → no target → empty.
        var d0 = DepthDecoder.Decode(state, sel.Value);
        if (d0.HasDepth) return "empty focus produced depth";

        sel.Set("7203.TSE");
        var d1 = DepthDecoder.Decode(state, sel.Value);
        if (!d1.HasDepth || d1.Bids.Count != 1 || Math.Abs(d1.Bids[0].Price - 100.0) > 1e-9 || d1.TimestampMs != 111)
            return "depth did not follow focus to 7203.TSE";

        sel.Set("9984.TSE");
        var d2 = DepthDecoder.Decode(state, sel.Value);
        if (!d2.HasDepth || Math.Abs(d2.Bids[0].Price - 200.0) > 1e-9 || d2.TimestampMs != 222)
            return "depth did not re-point to 9984.TSE on focus change";
        return null;
    }
}
