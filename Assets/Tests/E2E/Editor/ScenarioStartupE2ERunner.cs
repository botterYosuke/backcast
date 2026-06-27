// ScenarioStartupE2ERunner.cs — Scenario Startup tile サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// ScenarioStartupE2ERunner.md）。第二波で `ScenarioStartupProbe`（throwaway AFK gate, Assets/Editor）から
// 昇格・改名（ADR-0015 の回帰ゲート命名規約。元 Probe は削除＝先例: ReplayToHakoniwaProbe→E2ERunner）。
// 実証済み Probe の Section1〜10 を assert 1 行も削らず移送し、台本に無い SCENARIO-12（File→New Clear）を
// Section11 として追加した。Python-FREE（validation/merge/registry/inline-read はすべて pure C#）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod ScenarioStartupE2ERunner.Run -logFile <log>
//   # expect: [E2E SCENARIO STARTUP PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。共有 pure
// validation（Section2 の Validate()）は Action ID ごとに人工分割せず一つの自然な検証単位で assert する
// （E2E-CONVENTIONS.md「runner section ↔ Action ID 対応方針」）。
//
// THE NON-VACUOUS MERGE KILL (ADR-0005, the whole point): a JsonUtility-style writer that
// silently DROPS unknown keys would still pass a naive "write panel fields → read them
// back" test. So Section 1 seeds a sidecar carrying v3 optionals the panel never edits —
// account_type (scalar), instruments_ref (scalar), strategy_init_kwargs (NESTED dict) —
// plus a non-scenario sibling "layout" key, then merges startup params + instruments, and
// asserts BOTH: (a) the mutated values reached the on-disk JSON TEXT, and (b) every
// untouched sibling — including the nested strategy_init_kwargs value — survived verbatim.
// A writer that drops strategy_init_kwargs corrupts a strict-validated sidecar and FAILS.

using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class ScenarioStartupE2ERunner
{
    static string TempDir => Path.Combine(Application.temporaryCachePath, "scenario_startup_e2e");
    static string StrategyPath => Path.Combine(TempDir, "my_strategy.py");
    static string SidecarPath => Path.Combine(TempDir, "my_strategy.json");

    public static void Run()
    {
        string fail = null;
        // SCENARIO-16/17 verdicts, captured independently so each emits its own per-Action-ID rollup tag
        // (not a shared pair gated on the whole suite). "not-run" = an earlier exception preempted them.
        string s16 = "not-run", s17 = "not-run";
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            fail = Section1_MergePreservesSiblingsNonVacuous()
                ?? Section2_Validation()
                ?? Section3_InstrumentRegistry()
                ?? Section4_ReadRoundTripAndNewSidecar()
                ?? Section5_ControllerRoundTripAndRunGate()
                ?? Section6_RegistryChangeNotifies()
                ?? Section7_TileResyncsFromSharedUniverse()
                ?? Section8_TileBlurResyncsStaleField()
                ?? Section9_IndividualWritersAreMutateExistingOnly()
                ?? Section10_InlineReaderMatchesGolden()
                ?? Section11_FileNewClearsInMemory()
                ?? Section12_StartupTileHasNoRunButton();

            // Section13/14 are independent pure checks (reflection over BaseDockWindowIds / catalog lookup),
            // so run them unconditionally for accurate per-section verdicts even if an earlier section failed.
            s16 = Section13_DockClusterIsRunResultOnly();
            s17 = Section14_ForwardCompatSkipsStartup();
            fail = fail ?? s16 ?? s17;
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        // Per-Action-ID verdicts for SCENARIO-16/17 — emitted on their OWN result, independent of the suite
        // (M3 fix 2026-06-25): a failure elsewhere no longer hides these tags, and a 16/17 failure no longer
        // collapses into the shared FAIL only. "not-run" (earlier exception preempted them) emits no tag.
        if (s16 != "not-run") Debug.Log(s16 == null ? "[E2E SCENARIO-16 PASS]" : "[E2E SCENARIO-16 FAIL] " + s16);
        if (s17 != "not-run") Debug.Log(s17 == null ? "[E2E SCENARIO-17 PASS]" : "[E2E SCENARIO-17 FAIL] " + s17);

        if (fail == null)
        {
            Debug.Log("[E2E SCENARIO STARTUP PASS] merge-preserve + validation + registry + File→New clear + " +
                      "dock base → run_result only (SCENARIO-16: ADR-0026 startup + ADR-0038 3 panels retired) + " +
                      "forward-compat startup skip (SCENARIO-17) verified");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E SCENARIO STARTUP FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. merge-write preserves untouched siblings (incl. nested dict) non-vacuously ----
    // Covers: SCENARIO-11 (Commit が sidecar を merge 書き・兄弟キー保全)
    static string Section1_MergePreservesSiblingsNonVacuous()
    {
        // Seed a sidecar with v3 optionals the panel does NOT edit + a non-scenario sibling.
        const string seed = @"{
  ""scenario"": {
    ""schema_version"": 3,
    ""instruments"": [""1301.TSE""],
    ""start"": ""2020-01-01"",
    ""end"": ""2020-02-01"",
    ""granularity"": ""Daily"",
    ""initial_cash"": 1000000,
    ""account_type"": ""MARGIN"",
    ""instruments_ref"": ""universe.json"",
    ""strategy_init_kwargs"": { ""lookback"": 20, ""nested"": { ""k"": ""v"" } }
  },
  ""layout"": { ""foo"": ""bar"" }
}";
        File.WriteAllText(SidecarPath, seed);

        // Merge new startup params + a new universe.
        ScenarioSidecarStore.SetStartupParams(
            StrategyPath, new StartupParamsForWrite("2024-05-01", "2024-06-01", "Minute", "500000"));
        ScenarioSidecarStore.SetInstruments(StrategyPath, new[] { "9984.TSE", "7203.TSE" });

        string text = File.ReadAllText(SidecarPath);

        // (a) the mutation reached the on-disk TEXT.
        if (!text.Contains("2024-05-01")) return "merge: new start not on disk";
        if (!text.Contains("2024-06-01")) return "merge: new end not on disk";
        if (!text.Contains("Minute")) return "merge: new granularity not on disk";
        if (!text.Contains("500000")) return "merge: new initial_cash not on disk";
        if (!text.Contains("9984.TSE") || !text.Contains("7203.TSE")) return "merge: new instruments not on disk";

        // (b) untouched siblings survived verbatim — THE non-vacuous kill.
        if (!text.Contains("\"account_type\"") || !text.Contains("MARGIN"))
            return "merge: account_type sibling DROPPED (writer is lossy)";
        if (!text.Contains("\"instruments_ref\"") || !text.Contains("universe.json"))
            return "merge: instruments_ref sibling DROPPED";
        if (!text.Contains("\"strategy_init_kwargs\"") || !text.Contains("\"lookback\"") || !text.Contains("\"nested\""))
            return "merge: strategy_init_kwargs NESTED dict DROPPED (JsonUtility-class corruption)";
        if (!text.Contains("\"layout\"") || !text.Contains("\"foo\""))
            return "merge: non-scenario layout sibling DROPPED";

        // stale values must be gone (proves overwrite, not append).
        if (text.Contains("2020-01-01")) return "merge: stale start still present";

        // re-read the 5 panel fields via the store.
        var snap = ScenarioSidecarStore.ReadScenario(StrategyPath);
        if (snap == null) return "merge: ReadScenario returned null after write";
        if (snap.Start != "2024-05-01" || snap.End != "2024-06-01") return "merge: readback dates wrong";
        if (snap.Granularity != "Minute") return "merge: readback granularity wrong";
        if (snap.InitialCash != 500000) return "merge: readback initial_cash wrong";
        if (snap.Instruments.Count != 2 || snap.Instruments[0] != "9984.TSE" || snap.Instruments[1] != "7203.TSE")
            return "merge: readback instruments wrong/order lost";

        // combined atomic write (the Commit path) ALSO preserves untouched siblings (it clobbers
        // StrategyPath, so this runs AFTER all StrategyPath assertions above).
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            StrategyPath, new StartupParamsForWrite("2024-07-01", "2024-08-01", "Daily", "123456"), new[] { "1301.TSE" });
        string text2 = File.ReadAllText(SidecarPath);
        if (!text2.Contains("123456") || !text2.Contains("1301.TSE")) return "combined: new values not on disk";
        if (!text2.Contains("\"strategy_init_kwargs\"") || !text2.Contains("\"nested\""))
            return "combined: strategy_init_kwargs sibling DROPPED";

        // initial_cash written as a JSON FLOAT must read back as a value (not null → spurious 'empty').
        string floatStrat = Path.Combine(TempDir, "floatcash_strategy.py");
        File.WriteAllText(ScenarioSidecarStore.SidecarPathFor(floatStrat),
            "{\"scenario\":{\"schema_version\":3,\"instruments\":[\"1301.TSE\"],\"start\":\"2024-01-01\",\"end\":\"2024-02-01\",\"granularity\":\"Daily\",\"initial_cash\":1000000.0}}");
        var fsnap = ScenarioSidecarStore.ReadScenario(floatStrat);
        if (fsnap == null || fsnap.InitialCash != 1000000) return "read: float initial_cash dropped to null";

        return null;
    }

    // ---- 2. validation gate (AC④) ----
    // Covers: SCENARIO-01, SCENARIO-02, SCENARIO-03, SCENARIO-04, SCENARIO-05, SCENARIO-08, SCENARIO-09
    // (共有 pure validation = ScenarioStartupValidation.Validate/TryBuildForWrite。Action ID ごとに分割しない)
    static string Section2_Validation()
    {
        // all-valid buffer with a non-empty universe → no errors + buildable.
        var good = new ScenarioStartupParams
        {
            Start = "2024-01-01", End = "2024-06-01", Granularity = GranularityChoice.Daily, InitialCash = "1000000",
        };
        if (ScenarioStartupValidation.Validate(good, 1).Any) return "validation: valid buffer reported errors";
        if (!ScenarioStartupValidation.TryBuildForWrite(good, 1, out _, out _))
            return "validation: valid buffer failed TryBuildForWrite";

        // empty universe rejected.
        if (ScenarioStartupValidation.Validate(good, 0).Universe == null) return "validation: empty universe not rejected";

        // negative / zero / non-int cash rejected.
        if (ScenarioStartupValidation.Validate(Clone(good, cash: "-5"), 1).InitialCash == null)
            return "validation: negative cash not rejected";
        if (ScenarioStartupValidation.Validate(Clone(good, cash: "0"), 1).InitialCash == null)
            return "validation: zero cash not rejected";
        if (ScenarioStartupValidation.Validate(Clone(good, cash: "abc"), 1).InitialCash == null)
            return "validation: non-int cash not rejected";

        // bad date + empty date rejected.
        if (ScenarioStartupValidation.Validate(Clone(good, start: "2024/01/01"), 1).Start == null)
            return "validation: malformed date not rejected";
        if (ScenarioStartupValidation.Validate(Clone(good, start: ""), 1).Start == null)
            return "validation: empty date not rejected";

        // start > end cross-field.
        if (ScenarioStartupValidation.Validate(Clone(good, start: "2024-07-01"), 1).CrossField == null)
            return "validation: start>end not rejected";

        // no granularity selected.
        if (ScenarioStartupValidation.Validate(Clone(good, gran: GranularityChoice.None), 1).Granularity == null)
            return "validation: unset granularity not rejected";

        // invalid buffer must NOT build for write.
        if (ScenarioStartupValidation.TryBuildForWrite(Clone(good, cash: "-1"), 1, out _, out _))
            return "validation: invalid buffer built for write (AC④ breached)";

        return null;
    }

    // ---- 3. InstrumentRegistry SoT ----
    // Covers: SCENARIO-05 (Universe 編集 = InstrumentRegistry の dedup/order/editable gate)
    static string Section3_InstrumentRegistry()
    {
        var reg = new InstrumentRegistry();
        if (!reg.Add("1301.TSE")) return "registry: first Add returned false";
        if (reg.Add("1301.TSE")) return "registry: duplicate Add returned true";
        if (!reg.Add("7203.TSE")) return "registry: second Add returned false";
        if (reg.Count != 2) return "registry: count wrong after adds";
        if (!reg.Remove("1301.TSE")) return "registry: Remove returned false";
        if (reg.Count != 1 || reg.Ids[0] != "7203.TSE") return "registry: state wrong after remove";

        // replace_all dedups, preserves order, reports change / no-change.
        if (!reg.ReplaceAll(new[] { "A", "B", "A", "C" })) return "registry: ReplaceAll returned false on change";
        if (reg.Count != 3 || reg.Ids[0] != "A" || reg.Ids[1] != "B" || reg.Ids[2] != "C")
            return "registry: ReplaceAll dedup/order wrong";
        if (reg.ReplaceAll(new[] { "A", "B", "C" })) return "registry: idempotent ReplaceAll reported change";

        // editable=false gates mutation.
        reg.Editable = false;
        if (reg.Add("Z") || reg.Remove("A") || reg.ReplaceAll(new[] { "X" }))
            return "registry: mutators ran while not editable";

        return null;
    }

    // ---- 4. read fallbacks + brand-new sidecar creation ----
    // Covers: SCENARIO-10 (Populate の優先順 sidecar>inline>seed・seed 既定値・新規 sidecar 作成所有権)
    static string Section4_ReadRoundTripAndNewSidecar()
    {
        // No sidecar at all → null (populate falls back to .py SCENARIO / defaults elsewhere).
        string fresh = Path.Combine(TempDir, "fresh_strategy.py");
        if (ScenarioSidecarStore.ReadScenario(fresh) != null) return "read: missing sidecar should be null";

        // Brand-new sidecar creation is OWNED ONLY by the combined Run-commit writer (#67): the
        // individual setters are mutate-existing-only so neither can leave an incomplete sidecar.
        // SetStartupParamsAndInstruments creates the full 5-key sidecar (schema_version 3 + fields).
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            fresh, new StartupParamsForWrite("2023-01-01", "2023-03-01", "Daily", "250000"), new[] { "6758.TSE" });
        string text = File.ReadAllText(ScenarioSidecarStore.SidecarPathFor(fresh));
        if (!text.Contains("\"schema_version\"") || !text.Contains("3")) return "read: new sidecar missing schema_version 3";
        var snap = ScenarioSidecarStore.ReadScenario(fresh);
        if (snap == null || snap.InitialCash != 250000 || snap.Instruments.Count != 1)
            return "read: new sidecar readback wrong";

        // SeedDefaults: start = end − 3 months, cash default.
        var seeded = ScenarioStartupParams.SeedDefaults(new DateTime(2026, 6, 14));
        if (seeded.Start != "2026-03-14") return "seed: start not today−3mo (got " + seeded.Start + ")";
        if (seeded.End != "2026-06-14") return "seed: end not today";
        if (seeded.InitialCash != ScenarioStartupParams.DefaultInitialCash) return "seed: cash default wrong";

        return null;
    }

    // ---- 5. controller: populate → edit → run-gate → commit → restore (stages 3+4 core) ----
    // #169 (ADR-0036 D5): the run gate takes a Func<string> resolver (the path to run, or null when nothing is
    // supplyable). A supplyable strategy yields its path; a non-supplyable one yields null (→ BlockedNoStrategy).
    static Func<string> Supply(string path, bool supplyable) => () => supplyable ? path : null;

    // Covers: SCENARIO-09 (editing→validated-for-write run-gate), SCENARIO-10 (populate/restore round-trip)
    static string Section5_ControllerRoundTripAndRunGate()
    {
        string strat = Path.Combine(TempDir, "ctrl_strategy.py");
        var today = new DateTime(2026, 6, 14);

        var ctrl = new ScenarioStartupController();

        // populate with no sidecar + no fallback → default seed, empty universe.
        ctrl.Populate(strat, today);
        if (ctrl.Params.Start != "2026-03-14" || ctrl.Params.End != "2026-06-14")
            return "controller: default seed dates wrong";
        if (ctrl.Universe.Count != 0) return "controller: universe not empty on fresh populate";
        if (ctrl.Validate().Universe == null) return "controller: empty universe not flagged after seed";

        // run gate before a strategy exists → BlockedNoStrategy (distinct from scenario errors).
        var noStrat = ctrl.TryStartRun(Supply(strat, false));
        if (noStrat.Gate != RunGate.BlockedNoStrategy) return "controller: missing strategy not gated";

        // edit a valid scenario + universe.
        ctrl.SetGranularity(GranularityChoice.Minute);
        ctrl.SetInitialCash("750000");
        ctrl.SetStart("2025-01-06");
        ctrl.SetEnd("2025-03-31");
        ctrl.AddInstrument("8918.TSE");
        ctrl.AddInstrument("7203.TSE");
        if (!ctrl.Params.Dirty) return "controller: edits did not set Dirty";
        if (ctrl.Validate().Any) return "controller: valid edited scenario reported errors";

        // run gate with a supplyable strategy → Ready + sidecar written + Dirty cleared.
        var ready = ctrl.TryStartRun(Supply(strat, true));
        if (!ready.IsReady) return "controller: valid run gate not Ready (" + ready.Message + ")";
        if (ready.StrategyPath != strat) return "controller: run gate returned wrong strategy path";
        if (ctrl.Params.Dirty) return "controller: Dirty not cleared after commit";

        // restore (AC②): a FRESH controller populates from the just-written sidecar.
        var ctrl2 = new ScenarioStartupController();
        ctrl2.Populate(strat, today);
        if (ctrl2.Params.Start != "2025-01-06" || ctrl2.Params.End != "2025-03-31")
            return "controller: restored dates wrong";
        if (ctrl2.Params.Granularity != GranularityChoice.Minute) return "controller: restored granularity wrong";
        if (ctrl2.Params.InitialCash != "750000") return "controller: restored cash wrong";
        if (ctrl2.Universe.Count != 2 || ctrl2.Universe.Ids[0] != "8918.TSE" || ctrl2.Universe.Ids[1] != "7203.TSE")
            return "controller: restored universe wrong/order lost";
        if (ctrl2.Params.Dirty) return "controller: restored buffer should not be Dirty";

        // run gate with an invalidated scenario (empty universe) → BlockedInvalidScenario.
        ctrl2.RemoveInstrument("8918.TSE");
        ctrl2.RemoveInstrument("7203.TSE");
        var bad = ctrl2.TryStartRun(Supply(strat, true));
        if (bad.Gate != RunGate.BlockedInvalidScenario) return "controller: invalid scenario not gated at run";
        if (bad.Errors == null || bad.Errors.Universe == null) return "controller: run-block missing universe error";

        // fallback precedence: sidecar wins over a pythonnet load_scenario fallback.
        string strat2 = Path.Combine(TempDir, "fallback_strategy.py");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            strat2, new StartupParamsForWrite("2022-01-01", "2022-02-01", "Daily", "100000"), new[] { "6758.TSE" });
        var fb = new ScenarioSnapshot { Start = "1999-01-01", End = "1999-02-01", Granularity = "Minute", InitialCash = 9 };
        fb.Instruments.Add("9999.TSE");
        var ctrl3 = new ScenarioStartupController();
        ctrl3.Populate(strat2, today, fb);
        if (ctrl3.Params.Start != "2022-01-01" || ctrl3.Universe.Ids[0] != "6758.TSE")
            return "controller: sidecar did not win over fallback";

        // fallback used when NO sidecar exists.
        string strat3 = Path.Combine(TempDir, "inline_only_strategy.py");
        var ctrl4 = new ScenarioStartupController();
        ctrl4.Populate(strat3, today, fb);
        if (ctrl4.Params.Start != "1999-01-01" || ctrl4.Universe.Ids[0] != "9999.TSE")
            return "controller: fallback not used when sidecar absent";

        return null;
    }

    // ---- 6. universe SoT change-notification (#59 完成形: registry → second-view sync) ----
    // Changed fires ONLY on a real set change (the `return true` paths), never on a no-op — so a
    // subscriber re-pulls exactly when it must and never churns on idempotent edits.
    // Covers: SCENARIO-07 (共有 SoT の Changed 通知 = held-mode 再 sync の前提)
    static string Section6_RegistryChangeNotifies()
    {
        var reg = new InstrumentRegistry();
        int fires = 0;
        reg.Changed += () => fires++;

        if (!reg.Add("1301.TSE")) return "event: real Add returned false";
        if (fires != 1) return "event: Changed did not fire on real Add";
        reg.Add("1301.TSE");                                   // duplicate → no change
        if (fires != 1) return "event: Changed fired on no-op duplicate Add";
        reg.Remove("ZZZZ.TSE");                                // absent → no change
        if (fires != 1) return "event: Changed fired on absent Remove";
        if (!reg.Remove("1301.TSE")) return "event: real Remove returned false";
        if (fires != 2) return "event: Changed did not fire on real Remove";
        if (!reg.ReplaceAll(new[] { "A", "B" })) return "event: real ReplaceAll returned false";
        if (fires != 3) return "event: Changed did not fire on real ReplaceAll";
        reg.ReplaceAll(new[] { "A", "B" });                    // idempotent → no change
        if (fires != 3) return "event: Changed fired on idempotent ReplaceAll";

        reg.Editable = false;                                  // gated mutators never fire
        reg.Add("Z"); reg.Remove("A"); reg.ReplaceAll(new[] { "X" });
        if (fires != 3) return "event: Changed fired while not editable";
        return null;
    }

    // ---- 7. startup tile re-syncs its (held-mode uGUI) universe field when the SHARED SoT is
    // edited elsewhere (#31 sidebar) — the registry→tile one-directional gap. Without it the field
    // stays stale and a later tile edit ReplaceAll(stale)s away the sidebar's add. ----
    // Covers: SCENARIO-07 (外部編集→held-mode 再 sync・Dispose unsubscribe)
    static string Section7_TileResyncsFromSharedUniverse()
    {
        var go = new GameObject("probe_tile", typeof(RectTransform));
        try
        {
            var tileRt = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var ctrl = new ScenarioStartupController();
            ctrl.Populate(Path.Combine(TempDir, "tile_strategy.py"), new DateTime(2026, 6, 14)); // empty universe
            var tile = new ScenarioStartupTile(ctrl, font);
            tile.Build(tileRt);

            var fld = typeof(ScenarioStartupTile)
                .GetField("_universeField", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(tile) as InputField;
            if (fld == null) return "tile: _universeField missing";

            // a SIDEBAR-side add into the SHARED universe SoT (field is not focused headless).
            ctrl.Universe.Add("9999.TSE");
            if (!fld.text.Contains("9999.TSE"))
                return "tile: universe field did NOT re-sync from registry Changed (一方向 sync gap)";

            // and a removal propagates too.
            ctrl.Universe.Remove("9999.TSE");
            if (fld.text.Contains("9999.TSE")) return "tile: field not re-synced after remove";

            // Dispose unsubscribes — no orphan handler keeps re-syncing a discarded tile.
            tile.Dispose();
            ctrl.Universe.Add("8888.TSE");
            if (fld.text.Contains("8888.TSE")) return "tile: still re-syncing after Dispose (handler leaked)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 8. held-mode universe field RE-PULLS on blur/submit (#59 focus-loss recovery). While the
    // field is FOCUSED, OnUniverseRegistryChanged skips the rewrite (the field is the live editor),
    // so a sidebar edit made mid-type leaves the field stale; without an onEndEdit re-pull the next
    // keystroke ReplaceAll(stale)s and erases the sidebar's change. Headless: isFocused is always
    // false, so we DIRECTLY stale the field, then fire onEndEdit (the REAL wiring, not the private
    // helper) and assert it reconciles to the SoT WITHOUT re-committing the stale text. ----
    // Covers: SCENARIO-06 (Universe フィールド blur で SoT 再 pull・stale 上書き防止)
    static string Section8_TileBlurResyncsStaleField()
    {
        var go = new GameObject("probe_tile_blur", typeof(RectTransform));
        try
        {
            var tileRt = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var ctrl = new ScenarioStartupController();
            ctrl.Populate(Path.Combine(TempDir, "tile_blur_strategy.py"), new DateTime(2026, 6, 14));
            ctrl.Universe.Add("A.TSE");                         // the SoT after a sidebar settled to [A]
            var tile = new ScenarioStartupTile(ctrl, font);
            tile.Build(tileRt);

            var fld = typeof(ScenarioStartupTile)
                .GetField("_universeField", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(tile) as InputField;
            if (fld == null) return "blur: _universeField missing";
            if (fld.text != "A.TSE") return "blur: field not synced to SoT on build";

            // a field that went STALE while focused (the focused-skip path): it still shows a phantom
            // id the SoT no longer carries. NOTE: Unity 6000.4 legacy InputField.SetTextWithoutNotify
            // STILL fires onValueChanged (verified via diag: SetTextWithoutNotify("A.TSE") ->
            // OnUniverseChanged("A.TSE")), which would ReplaceAll and corrupt the registry. The REAL
            // focus-skip path never writes the field at all (OnUniverseRegistryChanged skips while
            // focused), leaving the SoT clean. So set the backing m_Text directly — no notify, no
            // rebuild, no ReplaceAll — to model "field stale, registry clean" faithfully.
            typeof(InputField).GetField("m_Text", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(fld, "9999.TSE, A.TSE");

            // blur/submit fires onEndEdit — the recovery wiring must re-pull the field from the SoT.
            fld.onEndEdit.Invoke(fld.text);

            if (fld.text != "A.TSE")
                return "blur: onEndEdit did NOT re-pull field from SoT (stale survives -> next keystroke ReplaceAll(stale))";
            if (ctrl.Universe.Count != 1 || ctrl.Universe.Ids[0] != "A.TSE")
                return "blur: onEndEdit MUTATED the registry (must re-pull, never ReplaceAll the stale text)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 9. #67: the individual setters are MUTATE-EXISTING-ONLY. They must never create a fresh
    // sidecar — an incomplete one (universe-only, or window-only) would shadow the inline .py
    // SCENARIO (load_scenario prefers the sidecar) and break register_live_strategy /
    // start_engine with STRATEGY_LOAD_FAILED. Mirrors TTWR set_instruments / set_startup_params
    // (atomic_mutate_scenario_object errors "missing scenario object" rather than create). Only
    // SetStartupParamsAndInstruments (Run-commit) may create, and it writes the full 5-key sidecar.
    // Covers: SCENARIO-11 (個別 setter は mutate-existing-only・不完全 sidecar を作らない)
    static string Section9_IndividualWritersAreMutateExistingOnly()
    {
        // (a) SetInstruments on a path with NO sidecar → skip (null), create NOTHING. THE kill:
        // the old create-on-absent wrote {schema_version, instruments} and broke live load.
        string uni = Path.Combine(TempDir, "uni_only_strategy.py");
        var r1 = ScenarioSidecarStore.SetInstruments(uni, new[] { "7203.TSE" });
        if (r1 != null) return "#67: SetInstruments created a sidecar with no existing one (must skip)";
        if (File.Exists(ScenarioSidecarStore.SidecarPathFor(uni)))
            return "#67: SetInstruments wrote an incomplete sidecar file from nothing";

        // (b) SetStartupParams on a path with NO sidecar → also skip (window-only is incomplete too).
        string win = Path.Combine(TempDir, "win_only_strategy.py");
        var r2 = ScenarioSidecarStore.SetStartupParams(win, new StartupParamsForWrite("2024-01-01", "2024-02-01", "Daily", "500000"));
        if (r2 != null) return "#67: SetStartupParams created a sidecar with no existing one (must skip)";
        if (File.Exists(ScenarioSidecarStore.SidecarPathFor(win)))
            return "#67: SetStartupParams wrote an incomplete sidecar file from nothing";

        // (c) once a COMPLETE sidecar exists, SetInstruments mutates it (non-null) and preserves
        // the startup window verbatim — the universe edit no longer destroys start/end/gran/cash.
        string full = Path.Combine(TempDir, "full_strategy.py");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            full, new StartupParamsForWrite("2023-06-01", "2023-09-01", "Minute", "777777"), new[] { "1301.TSE" });
        var r3 = ScenarioSidecarStore.SetInstruments(full, new[] { "9984.TSE", "6758.TSE" });
        if (r3 == null) return "#67: SetInstruments on an existing sidecar must mutate (returned null)";
        var snap = ScenarioSidecarStore.ReadScenario(full);
        if (snap == null) return "#67: ReadScenario null after mutate-existing";
        if (snap.Instruments.Count != 2 || snap.Instruments[0] != "9984.TSE" || snap.Instruments[1] != "6758.TSE")
            return "#67: mutate-existing did not replace instruments in order";
        if (snap.Start != "2023-06-01" || snap.End != "2023-09-01" || snap.Granularity != "Minute" || snap.InitialCash != 777777)
            return "#67: universe writeback clobbered the startup window (merge not preserved)";

        return null;
    }

    static ScenarioStartupParams Clone(
        ScenarioStartupParams p, string start = null, string end = null,
        GranularityChoice? gran = null, string cash = null)
    {
        return new ScenarioStartupParams
        {
            Start = start ?? p.Start,
            End = end ?? p.End,
            Granularity = gran ?? p.Granularity,
            InitialCash = cash ?? p.InitialCash,
        };
    }

    // ---- 10. #66 cross-language pin (findings 0043 §2, Leg B): the pure-C# ScenarioInlineReader
    // reproduces the committed golden — which Leg A (test_scenario_inline_golden.py) pins to the
    // canonical Python load_scenario SoT. This is what guards the C# literal parser from drifting
    // from the run-time loader. Also asserts the Absent vs Unparseable boundary (#66 D3): a .py with
    // no SCENARIO is Absent (silent), a present-but-broken SCENARIO is Unparseable (loud). ----
    [Serializable] class GoldenScenario { public string start, end, granularity; public long initial_cash; public string[] instruments; }
    [Serializable] class GoldenEntry { public string path; public GoldenScenario scenario; }
    [Serializable] class GoldenFile { public GoldenEntry[] fixtures; }

    // Covers: supporting golden pin（直接 Action 行ではない。SCENARIO-10 の populate fallback = inline reader を
    // Python load_scenario SoT に cross-language で固定。台本「既存 Probe との対応」S10 参照）
    static string Section10_InlineReaderMatchesGolden()
    {
        // repo root = parent of <repo>/Assets (the golden + fixture paths are repo-relative).
        string repoRoot = Directory.GetParent(Application.dataPath).FullName;
        string goldenPath = Path.Combine(repoRoot,
            "python", "tests", "golden", "scenario_inline_golden.json");
        if (!File.Exists(goldenPath))
            return "inline golden: missing " + goldenPath + " (run `python -m tests.capture_scenario_inline_golden`)";

        GoldenFile golden;
        try { golden = JsonUtility.FromJson<GoldenFile>(File.ReadAllText(goldenPath)); }
        catch (Exception e) { return "inline golden: unreadable golden JSON: " + e.Message; }
        if (golden?.fixtures == null || golden.fixtures.Length == 0)
            return "inline golden: golden has no fixtures";

        foreach (GoldenEntry entry in golden.fixtures)
        {
            string fixturePath = Path.Combine(repoRoot, entry.path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fixturePath)) return "inline golden: fixture not found: " + fixturePath;

            ScenarioSnapshot snap = ScenarioInlineReader.Read(fixturePath, out ScenarioReadStatus status);
            if (status != ScenarioReadStatus.Found || snap == null)
                return "inline golden: " + entry.path + " expected Found, got " + status;

            GoldenScenario g = entry.scenario;
            if (snap.Start != g.start) return "inline golden: " + entry.path + " start " + snap.Start + " != " + g.start;
            if (snap.End != g.end) return "inline golden: " + entry.path + " end " + snap.End + " != " + g.end;
            if (snap.Granularity != g.granularity) return "inline golden: " + entry.path + " granularity " + snap.Granularity + " != " + g.granularity;
            if (!snap.InitialCash.HasValue || snap.InitialCash.Value != g.initial_cash)
                return "inline golden: " + entry.path + " initial_cash " + snap.InitialCash + " != " + g.initial_cash;
            int gn = g.instruments != null ? g.instruments.Length : 0;
            if (snap.Instruments.Count != gn) return "inline golden: " + entry.path + " instrument count " + snap.Instruments.Count + " != " + gn;
            for (int i = 0; i < gn; i++)
                if (snap.Instruments[i] != g.instruments[i])
                    return "inline golden: " + entry.path + " instrument[" + i + "] " + snap.Instruments[i] + " != " + g.instruments[i];
        }

        // Absent vs Unparseable boundary (#66 D3). Use a scratch dir so we never touch the fixtures.
        string absentPy = Path.Combine(TempDir, "no_scenario.py");
        File.WriteAllText(absentPy, "x = 1\n# this strategy has no SCENARIO\n");
        ScenarioInlineReader.Read(absentPy, out ScenarioReadStatus absentStatus);
        if (absentStatus != ScenarioReadStatus.Absent)
            return "inline boundary: a .py with no SCENARIO must be Absent, got " + absentStatus;

        string brokenPy = Path.Combine(TempDir, "broken_scenario.py");
        File.WriteAllText(brokenPy, "SCENARIO = {bad: @@@ not a literal}\n");
        ScenarioInlineReader.Read(brokenPy, out ScenarioReadStatus brokenStatus);
        if (brokenStatus != ScenarioReadStatus.Unparseable)
            return "inline boundary: a present-but-broken SCENARIO must be Unparseable, got " + brokenStatus;

        // a TRUNCATED / unbalanced dict (mid-edit) is Unparseable (loud), NOT Absent (silent no-op).
        string truncPy = Path.Combine(TempDir, "trunc_scenario.py");
        File.WriteAllText(truncPy, "SCENARIO = {\"start\": \"x\"\n");
        ScenarioInlineReader.Read(truncPy, out ScenarioReadStatus truncStatus);
        if (truncStatus != ScenarioReadStatus.Unparseable)
            return "inline boundary: a truncated/unbalanced SCENARIO must be Unparseable, got " + truncStatus;

        // a `SCENARIO = {...}` INSIDE a module docstring is prose, not an assignment → Absent.
        string docPy = Path.Combine(TempDir, "docstring_scenario.py");
        File.WriteAllText(docPy, "\"\"\"\nSCENARIO = {\"start\": \"BOGUS\"}\n\"\"\"\nimport os\n");
        ScenarioInlineReader.Read(docPy, out ScenarioReadStatus docStatus);
        if (docStatus != ScenarioReadStatus.Absent)
            return "inline boundary: a SCENARIO inside a docstring must be Absent, got " + docStatus;

        // a missing file is Absent (never throws — Awake-safe).
        ScenarioInlineReader.Read(Path.Combine(TempDir, "does_not_exist.py"), out ScenarioReadStatus missingStatus);
        if (missingStatus != ScenarioReadStatus.Absent)
            return "inline boundary: a missing .py must be Absent, got " + missingStatus;

        return null;
    }

    // ---- 11. File→New clears the editing buffer IN-MEMORY ONLY (findings 0017 §4). Clear() empties
    // Params + universe + errors and drops Dirty, but must NOT touch the on-disk sidecar (deleting it
    // would be destructive over-reach — TTWR FileNewRequested is an in-memory reset). THE non-vacuous
    // kill: seed a real sidecar on disk + a dirty edited buffer, Clear(), then assert the buffer is
    // blank/unbound AND the sidecar TEXT is byte-identical. delete-the-logic litmus: drop Clear's
    // Universe.ReplaceAll → universe survives (fails (b)); make Clear delete the sidecar → (d) fails.
    // ----
    // Covers: SCENARIO-12 (File→New で scenario を in-memory クリア・sidecar 不触)
    static string Section11_FileNewClearsInMemory()
    {
        string strat = Path.Combine(TempDir, "clear_strategy.py");
        var today = new DateTime(2026, 6, 14);

        // seed a COMPLETE sidecar on disk (the document the user had open) + a dirty edited buffer.
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            strat, new StartupParamsForWrite("2024-01-01", "2024-06-01", "Daily", "900000"), new[] { "8918.TSE", "7203.TSE" });
        string before = File.ReadAllText(ScenarioSidecarStore.SidecarPathFor(strat));

        var ctrl = new ScenarioStartupController();
        ctrl.Populate(strat, today);
        ctrl.SetInitialCash("123");                            // dirty the buffer
        ctrl.AddInstrument("9984.TSE");
        if (!ctrl.Params.Dirty) return "clear: precondition — edits did not set Dirty";
        if (ctrl.Universe.Count == 0) return "clear: precondition — universe empty before Clear";

        ctrl.Clear();

        // (a) buffer is blank + UNBOUND (NOT seeded — New is blank), not dirty.
        if (!string.IsNullOrEmpty(ctrl.Params.Start) || !string.IsNullOrEmpty(ctrl.Params.End))
            return "clear: dates not cleared (got '" + ctrl.Params.Start + "'/'" + ctrl.Params.End + "')";
        if (!string.IsNullOrEmpty(ctrl.Params.InitialCash)) return "clear: initial cash not cleared";
        if (ctrl.Params.Dirty) return "clear: buffer still Dirty after Clear (New must reset Dirty)";

        // (b) universe emptied.
        if (ctrl.Universe.Count != 0) return "clear: universe not emptied";

        // (c) errors recompute to the blank-buffer flags (granularity unset + empty universe), proving
        // the buffer really is blank/unbound — not stale from before the Clear.
        var errs = ctrl.Validate();
        if (errs.Granularity == null) return "clear: granularity not reset (Validate did not flag unset after Clear)";
        if (errs.Universe == null) return "clear: universe not reset (Validate did not flag empty after Clear)";

        // (d) THE non-vacuous kill: the on-disk sidecar is byte-identical (Clear is in-memory only).
        string after = File.ReadAllText(ScenarioSidecarStore.SidecarPathFor(strat));
        if (after != before) return "clear: Clear() mutated the on-disk sidecar (must be in-memory only — destructive over-reach)";

        return null;
    }

    // ---- 12. U5 cutover negative invariant — RE-HOMED from the retired RunButtonE2ERunner SectionC
    // (#95 Phase 6 Slice 9; findings 0075 §3c). The startup tile is SCENARIO-EDITING-ONLY (#76 S6b-β-clean
    // U5 / Phase 6 title-bar Run sunset): the Run button + run-readiness display moved to the Strategy
    // Editor title bar, so the tile keeps only the scenario fields + the Daily/Minute granularity buttons
    // and has NO run-trigger button. NON-VACUITY: pin the tile DID build its granularity buttons FIRST
    // (a tile that built nothing would false-green the Run-absence check). ScenarioStartupTile.MakeButton
    // names every button "btn:"+label, so a re-added Run control surfaces as btn:Run… under the tile.
    // RED litmus: add a MakeButton(tile,"Run Replay",…) to ScenarioStartupTile.Build → btn:Run Replay
    // appears → RED.
    // Covers: SCENARIO-15 (re-homed U5 — startup tile carries no Run trigger)
    static string Section12_StartupTileHasNoRunButton()
    {
        var go = new GameObject("probe_tile_norun", typeof(RectTransform));
        try
        {
            var tileRt = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var ctrl = new ScenarioStartupController();
            ctrl.Populate(Path.Combine(TempDir, "norun_strategy.py"), new DateTime(2026, 6, 14));
            var tile = new ScenarioStartupTile(ctrl, font);
            tile.Build(tileRt);

            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var b in tileRt.GetComponentsInChildren<Button>(true)) names.Add(b.gameObject.name);

            // non-vacuity guard: the tile really built its scenario-editing (granularity) buttons.
            if (!names.Contains("btn:Daily") || !names.Contains("btn:Minute"))
                return "U5: startup tile did not build its granularity buttons (non-vacuity guard failed)";

            // THE negative: no run-trigger button under the tile — verbatim retired name first, then a
            // broader contains-'Run' guard so a renamed Run button can't slip through.
            if (names.Contains("btn:Run Replay"))
                return "SCENARIO-15 (re-homed U5): startup tile still has its retired Run button (btn:Run Replay)";
            foreach (var n in names)
                if (n.IndexOf("Run", StringComparison.Ordinal) >= 0)
                    return "SCENARIO-15 (re-homed U5): startup tile has a run-trigger button '" + n + "' (Run moved to the Strategy Editor title bar)";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ---- 13. dock cluster reduced to run_result only (ADR-0026 startup → Settings; ADR-0038 3 panels → bar) ----
    // Covers: SCENARIO-16: BackcastWorkspaceRoot.BaseDockWindowIds. ADR-0026 retired "startup"; ADR-0038
    // (#174-178) retired buying_power/orders/positions to the account summary bar, so base is now just
    // run_result (sister #172 retires the last one next → 0). This is the single source SpawnBaseDockWindows
    // + FormFactoryBaseGroup both read; a 1-length {run_result} array IS the reduction. RED litmus: re-add a
    // retired id to the array → length 2 → RED. Non-vacuity: assert the retired ids are ABSENT.
    static string Section13_DockClusterIsRunResultOnly()
    {
        var f = typeof(BackcastWorkspaceRoot).GetField("BaseDockWindowIds",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (f == null) return "S13: BaseDockWindowIds field not found (renamed?)";
        var ids = (string[])f.GetValue(null);
        if (ids == null) return "S13: BaseDockWindowIds is null";
        if (ids.Length != 1) return $"S13: base dock has {ids.Length} windows, expected 1 (run_result only — ADR-0026/0038)";
        var set = new System.Collections.Generic.HashSet<string>(ids);
        if (set.Contains("startup")) return "S13: base dock still contains 'startup'";
        foreach (var retired in new[] { "buying_power", "orders", "positions" })
            if (set.Contains(retired)) return $"S13: base dock still contains retired '{retired}' (ADR-0038)";
        if (!set.Contains("run_result"))
            return "S13: base dock missing run_result (non-vacuity)";
        return null;
    }

    // ---- 14. forward-compat: a pre-ADR-0026 saved "startup" window is skipped on restore ----
    // Covers: SCENARIO-17 (#126 / ADR-0026): the catalog no longer resolves "startup", so RestoreLayout's
    // Spawn returns null and the loop continues (the entry is kept in the doc — the generic
    // forward-evolution path proven by FloatingWindowE2ERunner). Litmus: re-add the startup spec to
    // FloatingWindowCatalog.Default() → TryGet true → a stale "startup" would re-spawn → this RED.
    static string Section14_ForwardCompatSkipsStartup()
    {
        var catalog = FloatingWindowCatalog.Default();
        if (catalog.TryGet("startup", out _))
            return "S14: catalog still resolves 'startup' — a saved layout would re-spawn the retired window (must skip)";
        // non-vacuity: a SURVIVING dock kind still resolves (so the negative isn't an empty-catalog artifact).
        if (!catalog.TryGet(FloatingWindowCatalog.KIND_RUN_RESULT, out _))
            return "S14: catalog missing run_result (vacuous negative — catalog appears empty)";
        return null;
    }
}
