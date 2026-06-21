// UniverseSidebarE2ERunner.cs — ユニバース sidebar サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// UniverseSidebarE2ERunner.md）。第二波で `UniverseSidebarProbe`（throwaway AFK gate, Assets/Editor）から昇格・改名
// （ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=findings 0054 / FooterMode=findings 0055）。実証済み
// `Section1`〜`Section8`（picker open/lock、status placeholder、filter/sort/take15、add/debounce、remove、focus/Live-hook、
// writeback、depth-follow）を assert 1 行も削らず移送し SIDEBAR-01〜10 を Covers 化、台本の `要新規自動化` 2 行
// （SIDEBAR-11 view 反映 / SIDEBAR-14 空ラベル）を view 反射 section として追加した。Python-FREE（brain は pure C#、
// view section は実 root 不要で `UniverseSidebarView` を bare RectTransform へ Bind するだけ）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod UniverseSidebarE2ERunner.Run -logFile <log>
//   # expect: [E2E UNIVERSE SIDEBAR PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep/Bash grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。共有の自然な
// 検証単位（picker BuildList / writeback 不変条件）は Action ID ごとに人工分割しない（E2E-CONVENTIONS.md
// 「runner section ↔ Action ID 対応方針」）。SIDEBAR-11 の SoT 側回帰（`PruneRetain`→`Changed`→downstream mirror）は
// 別 probe `UniversePruneProbe` 所有のまま据え置き — 本 runner は sidebar VIEW の `Registry.Changed`→`Rebuild` 反映だけを補う。
//
// 観測対象の重要前提（grill で確認済み）: 実 `IAvailableInstrumentsProvider`（DuckDB/venue universe）は未配線で、
// production は `MockAvailableInstrumentsProvider`（6 銘柄ハードコード）のみ。本 runner は stub provider 経由で
// status→行マッピングの固定までを観測し、「実 DuckDB を assert」は対象外（別 issue 所有）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class UniverseSidebarE2ERunner
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

    // Records the (mode, endDate) the picker queried with, so Section11 can prove the view honors the
    // root's SetContext push instead of the Bind-time default (findings 0084). Returns a non-empty
    // Ready so BuildList takes the real-rows path (not a placeholder).
    sealed class RecordingProvider : IAvailableInstrumentsProvider
    {
        public bool Queried;
        public UniverseSourceMode LastMode;
        public string LastEnd = "<never>";
        public AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate)
        {
            Queried = true; LastMode = mode; LastEnd = replayEndDate;
            return AvailableInstrumentsResult.Ready(new[] { "7203.TSE" });
        }
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
                ?? Section8_DepthFollowsSelection()
                ?? Section9_ViewReflectsExternalRegistryChange()
                ?? Section10_ViewEmptyUniverseLabel()
                ?? Section11_PickerContextDrivenByScenarioEnd()
                ?? Section12_PickerListGuiRendersCandidatesAndPlaceholder()
                ?? Section13_PickerAutoRefreshesWhenAsyncSupplyResolves();
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[E2E UNIVERSE SIDEBAR PASS] picker + status + select + writeback + depth-follow + view-reflect + context-driven-end + picker-list-gui + async-refresh verified");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E UNIVERSE SIDEBAR FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static UniverseSidebarController NewController(out InstrumentRegistry reg, out SelectedSymbol sel, StubProvider provider)
    {
        reg = new InstrumentRegistry();
        sel = new SelectedSymbol();
        return new UniverseSidebarController(reg, sel, new UniverseWriteback(), provider);
    }

    // ---- 1. picker open / lock guard / force-close ----
    // Covers: SIDEBAR-05 (＋Add 開閉・Replay は scenario.end snapshot / Live は null), SIDEBAR-10 (ロック中は開かず・開放中ロックで force-close)
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
    // Covers: SIDEBAR-09 (供給ステータス別 placeholder。stub provider 経由＝実 DuckDB/venue 配線は別 issue 所有・対象外)
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
    // Covers: SIDEBAR-06 (検索フィルタ・ordinal sort・take15・no-matches), SIDEBAR-08 (追加済み ✓ AlreadyAdded フラグ)
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
    // Covers: SIDEBAR-07 (候補追加・100ms debounce・ロック no-op), SIDEBAR-08 (dedup no-op で SoT 不変)
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
    // Covers: SIDEBAR-03 (× で SoT 削除), SIDEBAR-04 (ロック registry で × no-op・TTWR parity)
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
    // Covers: SIDEBAR-01 (行クリックで focus 移動・Selected フラグ), SIDEBAR-02 (Live のみ LiveSubscribeHook 発火・Replay は focus のみ)
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
    // Covers: SIDEBAR-03 (× remove → Replay-gated flush・既存 sidecar mutate-existing), SIDEBAR-07 (候補追加 → flush)
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
    // Covers: SIDEBAR-01 (focus→depth：DepthDecoder が SelectedSymbol を追従＝行クリック観測点の実消費者)
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

    // ---- view reflection helpers (SIDEBAR-11/14) -----------------------------------------
    // Build the real UniverseSidebarView (a MonoBehaviour) under a bare RectTransform and Bind it to a
    // headless controller. Bind self-promotes a ChromeCanvas (idempotent, no scene needed) and reads
    // ThemeService.Current (lazy-dark default), so no real BackcastWorkspaceRoot is required — the rows
    // are rebuilt into _rowsContent during Rebuild() BEFORE Relayout (which early-returns at rect.height==0
    // headless), so child reflection is layout-independent.
    static UniverseSidebarView BuildView(out GameObject go, out UniverseSidebarController ctrl, out InstrumentRegistry reg)
    {
        go = new GameObject("universe_sidebar_view_e2e", typeof(RectTransform), typeof(UniverseSidebarView));
        reg = new InstrumentRegistry();
        ctrl = new UniverseSidebarController(reg, new SelectedSymbol(), new UniverseWriteback(), new StubProvider());
        var view = go.GetComponent<UniverseSidebarView>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        view.Bind(ctrl, new StubStrategyProvider { Path = null }, font, "2024-12-31");
        return view;
    }

    static RectTransform RowsContent(UniverseSidebarView view)
    {
        var f = typeof(UniverseSidebarView).GetField("_rowsContent", BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(view) as RectTransform;
    }

    static Button AddButton(UniverseSidebarView view)
    {
        var f = typeof(UniverseSidebarView).GetField("_addBtn", BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(view) as Button;
    }

    static RectTransform PickerListContent(UniverseSidebarView view)
    {
        var f = typeof(UniverseSidebarView).GetField("_pickerListContent", BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(view) as RectTransform;
    }

    // true iff _pickerListContent has a direct child GameObject named "cand:<id>" (PopulatePickerListContent
    // names each rendered candidate row that way).
    static bool HasCandRow(RectTransform pickerContent, string id)
    {
        if (pickerContent == null) return false;
        for (int i = 0; i < pickerContent.childCount; i++)
            if (pickerContent.GetChild(i).name == "cand:" + id) return true;
        return false;
    }

    // true iff any Text under _pickerListContent (candidate label or placeholder) contains `substr`.
    static bool PickerHasText(RectTransform pickerContent, string substr)
    {
        if (pickerContent == null) return false;
        foreach (var txt in pickerContent.GetComponentsInChildren<Text>(true))
            if (txt != null && txt.text != null && txt.text.Contains(substr)) return true;
        return false;
    }

    // Drive one frame tick (the private MonoBehaviour Update) so the open-picker async poll runs headless.
    static void InvokeUpdate(UniverseSidebarView view)
    {
        var m = typeof(UniverseSidebarView).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
        m?.Invoke(view, null);
    }

    // true iff _rowsContent has a child GameObject named "row:<id>" (BuildRow names each row that way).
    static bool HasRowFor(RectTransform rowsContent, string id)
    {
        if (rowsContent == null) return false;
        for (int i = 0; i < rowsContent.childCount; i++)
            if (rowsContent.GetChild(i).name == "row:" + id) return true;
        return false;
    }

    // true iff _rowsContent carries a Text child reading exactly `label` (the empty-universe placeholder).
    static bool HasLabel(RectTransform rowsContent, string label)
    {
        if (rowsContent == null) return false;
        for (int i = 0; i < rowsContent.childCount; i++)
        {
            var txt = rowsContent.GetChild(i).GetComponent<Text>();
            if (txt != null && txt.text == label) return true;
        }
        return false;
    }

    // ---- 9. sidebar VIEW reflects an EXTERNAL edit of the shared universe SoT (#29 text field / system
    // prune both mutate the same InstrumentRegistry → Registry.Changed → Rebuild, polling-free). The SoT
    // 側回帰 (PruneRetain→Changed→downstream) is UniversePruneProbe's; here we pin only that the uGUI view
    // RE-MATERIALIZES rows from the Changed event. Non-vacuous: assert the row is ABSENT first, present
    // after the external Add, and gone again after the external Remove (so a dead/never-subscribed
    // Rebuild can't false-green a static "row exists" snapshot). ----
    // Covers: SIDEBAR-11 (外部編集→sidebar view 反映。SoT 側回帰は UniversePruneProbe 所有)
    static string Section9_ViewReflectsExternalRegistryChange()
    {
        var view = BuildView(out var go, out _, out var reg);
        try
        {
            var rows = RowsContent(view);
            if (rows == null) return "view: _rowsContent not reflectable (field renamed?)";

            // presence guard: the id is NOT rendered before any edit (vacuous-negative kill).
            if (HasRowFor(rows, "7203.TSE")) return "view: row:7203.TSE present before any external add";

            // an EXTERNAL edit into the SHARED SoT (mirrors #29 text field / system prune).
            reg.Add("7203.TSE");
            if (!HasRowFor(rows, "7203.TSE"))
                return "view: external registry Add did NOT rebuild the sidebar row (Registry.Changed→Rebuild gap)";

            // a second external add also materializes (the subscription stays live across rebuilds).
            reg.Add("9984.TSE");
            if (!HasRowFor(rows, "9984.TSE")) return "view: second external add not reflected";

            // and an external remove tears the row down (Changed fires on real removes too).
            reg.Remove("7203.TSE");
            if (HasRowFor(rows, "7203.TSE")) return "view: external Remove did not rebuild (stale row survived)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 10. empty universe renders the "No instruments" placeholder (UniverseSidebarView.Rebuild adds
    // it only when Registry.Count==0 and reserves one ROW_H so the rows pane keeps its height). Non-vacuous:
    // the placeholder must be GONE once a real row exists, proving it is gated by Count==0 and not a static
    // label that would also "pass" if the empty-check were deleted. ----
    // Covers: SIDEBAR-14 (空ユニバースの "No instruments" 表示)
    static string Section10_ViewEmptyUniverseLabel()
    {
        var view = BuildView(out var go, out _, out var reg);
        try
        {
            var rows = RowsContent(view);
            if (rows == null) return "view: _rowsContent not reflectable (field renamed?)";

            // fresh registry is empty → exactly the placeholder, no real rows.
            if (!HasLabel(rows, "No instruments")) return "view: empty universe did not render 'No instruments' label";
            if (HasRowFor(rows, "1301.TSE")) return "view: phantom row present on empty universe";

            // once a real instrument exists the placeholder must disappear (gated by Count==0).
            reg.Add("1301.TSE");
            if (HasLabel(rows, "No instruments")) return "view: 'No instruments' label survived a non-empty universe (empty-check not gating)";
            if (!HasRowFor(rows, "1301.TSE")) return "view: real row not rendered after add";

            // emptying again brings the placeholder back.
            reg.Remove("1301.TSE");
            if (!HasLabel(rows, "No instruments")) return "view: 'No instruments' label not restored after universe emptied";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 11. [+ Add] queries the picker supply with the ROOT-PUSHED scenario.end + mode, not the
    // Bind-time default. The report "sidebar の [+ Add] で銘柄一覧が出てこない" was this: the view kept
    // _mode=Replay / _replayEnd="2024-12-31" forever (BackcastWorkspaceRoot.Bind never passed the real
    // scenario.end and nothing pushed it after), so the picker queried list_instruments("local",
    // "2024-12-31") — a date BEFORE every listed_info snapshot (the owner's DB only has 2025-12 rows) →
    // empty list. Fix: DriveSidebarContext() pushes (mode, scenario.end) each tick via SetContext.
    // We drive the REAL [+ Add] button handler (reflected _addBtn.onClick) so the view's own
    // _mode/_replayEnd fields are exercised, and a RecordingProvider captures what was queried.
    // Non-vacuous RED→GREEN: part (a) pins the OLD stale-default behavior; part (b) proves SetContext
    // re-scopes the next open (delete the `_replayEnd = replayEnd` line in SetContext → (b) FAILs).
    // Covers: SIDEBAR-05 (＋Add open scopes to scenario.end), SIDEBAR-15 (picker context driven by root mode/end)
    static string Section11_PickerContextDrivenByScenarioEnd()
    {
        var provider = new RecordingProvider();
        var go = new GameObject("universe_sidebar_ctx_e2e", typeof(RectTransform), typeof(UniverseSidebarView));
        var reg = new InstrumentRegistry();
        var ctrl = new UniverseSidebarController(reg, new SelectedSymbol(), new UniverseWriteback(), provider);
        var view = go.GetComponent<UniverseSidebarView>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        try
        {
            // Bind WITHOUT a replayEnd arg, exactly as BackcastWorkspaceRoot did → the Bind default.
            view.Bind(ctrl, new StubStrategyProvider { Path = null }, font);
            var addBtn = AddButton(view);
            if (addBtn == null) return "view: _addBtn not reflectable (renamed?)";

            // (a) characterize the BUG: a context-less view opens the picker against the stale Bind
            // default "2024-12-31" — the exact query that returned the empty list.
            addBtn.onClick.Invoke();   // open
            if (!provider.Queried) return "view: opening [+ Add] did not query the supply at all";
            if (provider.LastEnd != "2024-12-31")
                return $"view: pre-context open queried end '{provider.LastEnd}', expected stale Bind default 2024-12-31";
            addBtn.onClick.Invoke();   // close

            // (b) the fix: root pushes the live scenario.end → the NEXT open scopes to THAT date.
            view.SetContext(UniverseSourceMode.Replay, "2025-12-04");
            addBtn.onClick.Invoke();   // open
            if (provider.LastMode != UniverseSourceMode.Replay || provider.LastEnd != "2025-12-04")
                return $"view: post-SetContext open queried ({provider.LastMode},{provider.LastEnd}), expected (Replay,2025-12-04)";
            addBtn.onClick.Invoke();   // close

            // (c) Live context → mode flips to Live and the Replay end is dropped (the live universe is
            // current-as-of-fetch, not date-scoped: Picker.Toggle snapshots null for Live).
            view.SetContext(UniverseSourceMode.Live, "2025-12-04");
            addBtn.onClick.Invoke();   // open
            if (provider.LastMode != UniverseSourceMode.Live || provider.LastEnd != null)
                return $"view: Live context queried ({provider.LastMode},{provider.LastEnd}), expected (Live,null)";
            addBtn.onClick.Invoke();   // close

            // (d) NULL Replay end — the root pushes `_scenario.Params?.End`, which is null before a
            // scenario is populated — must normalize to "" so the provider takes the empty-end →
            // latest-fallback path (findings 0084), never a null end. Litmus: drop the `?? ""` in
            // SetContext → this queries (Replay,null) and FAILs.
            view.SetContext(UniverseSourceMode.Replay, null);
            addBtn.onClick.Invoke();   // open
            if (provider.LastMode != UniverseSourceMode.Replay || provider.LastEnd != "")
                return $"view: null Replay end queried ({provider.LastMode},'{provider.LastEnd}'), expected (Replay,\"\")";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 12. the GUI itself: opening [+ Add] RENDERS the supplied candidates as uGUI rows in the
    // picker list (cand:<id> GameObjects + "+ <id>" labels), and a status result renders its placeholder
    // text instead. Section11 proves the picker QUERIES with the right (mode,end); this proves the
    // RESULT actually paints into _pickerListContent — the display the user looks at. Python-FREE: the
    // StubProvider drives the supply status directly (the real DuckDB/fallback is gated by pytest).
    // Non-vacuous: rows are ABSENT before open, PRESENT for Ready ids, and GONE (replaced by the
    // placeholder) when the status flips to Empty — so a dead PopulatePickerListContent can't false-green.
    // Covers: SIDEBAR-06 (候補行の uGUI 描画), SIDEBAR-09 (placeholder の uGUI 描画), SIDEBAR-16 (picker list GUI 反映)
    static string Section12_PickerListGuiRendersCandidatesAndPlaceholder()
    {
        var provider = new StubProvider();
        var go = new GameObject("universe_sidebar_pickergui_e2e", typeof(RectTransform), typeof(UniverseSidebarView));
        var reg = new InstrumentRegistry();
        var ctrl = new UniverseSidebarController(reg, new SelectedSymbol(), new UniverseWriteback(), provider);
        var view = go.GetComponent<UniverseSidebarView>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        try
        {
            view.Bind(ctrl, new StubStrategyProvider { Path = null }, font);
            view.SetContext(UniverseSourceMode.Replay, "2025-12-04");
            var addBtn = AddButton(view);
            var pickerContent = PickerListContent(view);
            if (addBtn == null) return "view: _addBtn not reflectable (renamed?)";
            if (pickerContent == null) return "view: _pickerListContent not reflectable (renamed?)";

            // presence guard: nothing rendered before the picker is opened (vacuous-negative kill).
            if (HasCandRow(pickerContent, "7203.TSE")) return "view: candidate row rendered before picker opened";

            // Ready ids → the uGUI list paints one clickable row per id, with the "+ <id>" label.
            provider.Next = AvailableInstrumentsResult.Ready(new[] { "7203.TSE", "1301.TSE" });
            addBtn.onClick.Invoke();   // open → PopulatePickerListContent
            if (!HasCandRow(pickerContent, "7203.TSE") || !HasCandRow(pickerContent, "1301.TSE"))
                return "view: picker list did not render candidate rows for Ready ids";
            if (!PickerHasText(pickerContent, "+ 1301.TSE")) return "view: candidate row label text missing/incorrect";
            if (PickerHasText(pickerContent, "No instruments")) return "view: placeholder text rendered alongside real rows";

            // flip the status to Empty (close+reopen re-queries) → rows are gone, placeholder painted.
            addBtn.onClick.Invoke();   // close
            provider.Next = AvailableInstrumentsResult.Empty;
            addBtn.onClick.Invoke();   // open
            if (HasCandRow(pickerContent, "7203.TSE")) return "view: stale candidate row survived a placeholder status";
            if (!PickerHasText(pickerContent, "No instruments for this date"))
                return "view: picker list did not render the Empty placeholder text";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 13. ASYNC supply auto-refresh: the production BackendAvailableInstrumentsProvider fetches on a
    // background thread and returns Loading until the cache fills, so the FIRST [+ Add] open shows
    // "Loading..." — and nothing repaints it when the fetch lands (only discrete Rebuild events query the
    // supply). The user saw a stuck "Loading..." on 1st open, list only on 2nd open (findings 0084). Fix:
    // UniverseSidebarView.Update() polls the open picker each frame and repaints when the supply resolves.
    // We model the async fetch with a StubProvider flipped Loading→Ready and drive one Update() tick.
    // Non-vacuous RED→GREEN: remove the Update() poll → InvokeUpdate does nothing → the row never appears
    // (FAIL "did not auto-refresh"). The placeholder→rows transition also kills a vacuous always-green.
    // Covers: SIDEBAR-17 (async supply resolve → picker auto-refresh without re-open)
    static string Section13_PickerAutoRefreshesWhenAsyncSupplyResolves()
    {
        var provider = new StubProvider { Next = AvailableInstrumentsResult.Loading };
        var go = new GameObject("universe_sidebar_async_e2e", typeof(RectTransform), typeof(UniverseSidebarView));
        var reg = new InstrumentRegistry();
        var ctrl = new UniverseSidebarController(reg, new SelectedSymbol(), new UniverseWriteback(), provider);
        var view = go.GetComponent<UniverseSidebarView>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        try
        {
            view.Bind(ctrl, new StubStrategyProvider { Path = null }, font);
            view.SetContext(UniverseSourceMode.Replay, "2025-12-04");
            var addBtn = AddButton(view);
            var pickerContent = PickerListContent(view);
            if (addBtn == null || pickerContent == null) return "view: _addBtn/_pickerListContent not reflectable";

            // first open while the backend is still fetching → Loading placeholder, no candidate rows.
            addBtn.onClick.Invoke();
            if (!PickerHasText(pickerContent, "Loading")) return "view: first open did not show the Loading placeholder";
            if (HasCandRow(pickerContent, "7203.TSE")) return "view: candidate row rendered during Loading";

            // the background fetch lands (provider now Ready) — but nothing has rebuilt the list yet.
            provider.Next = AvailableInstrumentsResult.Ready(new[] { "7203.TSE" });
            if (HasCandRow(pickerContent, "7203.TSE")) return "view: list appeared before any poll (test setup invalid)";

            // a single frame tick must auto-refresh the OPEN picker — without the user re-opening it.
            InvokeUpdate(view);
            if (!HasCandRow(pickerContent, "7203.TSE"))
                return "view: picker did not auto-refresh when async supply resolved (stuck on Loading — findings 0084)";
            if (PickerHasText(pickerContent, "Loading")) return "view: stale Loading placeholder survived the resolve";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }
}
