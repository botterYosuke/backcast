// ScenarioStartupProbe.cs — issue #29 "Replay 実行設定パネル" (THROWAWAY AFK regression gate)
//
// Headless, Python-FREE gate for the #29 FOUNDATION seams (stage 1): the scenario sidecar
// MERGE-WRITE (ScenarioSidecarStore), the editing-buffer VALIDATION (ScenarioStartup
// Validation), and the universe SoT (InstrumentRegistry). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod ScenarioStartupProbe.Run -logFile <log>
//   # expect: [SCENARIO STARTUP PASS] ... / exit=0
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
using UnityEditor;
using UnityEngine;

public static class ScenarioStartupProbe
{
    static string TempDir => Path.Combine(Application.temporaryCachePath, "scenario_startup_probe");
    static string StrategyPath => Path.Combine(TempDir, "my_strategy.py");
    static string SidecarPath => Path.Combine(TempDir, "my_strategy.json");

    public static void Run()
    {
        string fail = null;
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            fail = Section1_MergePreservesSiblingsNonVacuous()
                ?? Section2_Validation()
                ?? Section3_InstrumentRegistry()
                ?? Section4_ReadRoundTripAndNewSidecar()
                ?? Section5_ControllerRoundTripAndRunGate();
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[SCENARIO STARTUP PASS] merge-preserve + validation + registry verified");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[SCENARIO STARTUP FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ---- 1. merge-write preserves untouched siblings (incl. nested dict) non-vacuously ----
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

        return null;
    }

    // ---- 2. validation gate (AC④) ----
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
    static string Section4_ReadRoundTripAndNewSidecar()
    {
        // No sidecar at all → null (populate falls back to .py SCENARIO / defaults elsewhere).
        string fresh = Path.Combine(TempDir, "fresh_strategy.py");
        if (ScenarioSidecarStore.ReadScenario(fresh) != null) return "read: missing sidecar should be null";

        // Brand-new sidecar created by the store gets schema_version 3 + the written fields.
        ScenarioSidecarStore.SetStartupParams(fresh, new StartupParamsForWrite("2023-01-01", "2023-03-01", "Daily", "250000"));
        ScenarioSidecarStore.SetInstruments(fresh, new[] { "6758.TSE" });
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
    sealed class FakeProvider : IStrategyFileProvider
    {
        public string Path;
        public bool Supplyable;
        public bool TryGetStrategyFile(out string path) { path = Path; return Supplyable; }
    }

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
        var noStrat = ctrl.TryStartRun(new FakeProvider { Path = strat, Supplyable = false });
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
        var ready = ctrl.TryStartRun(new FakeProvider { Path = strat, Supplyable = true });
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
        var bad = ctrl2.TryStartRun(new FakeProvider { Path = strat, Supplyable = true });
        if (bad.Gate != RunGate.BlockedInvalidScenario) return "controller: invalid scenario not gated at run";
        if (bad.Errors == null || bad.Errors.Universe == null) return "controller: run-block missing universe error";

        // fallback precedence: sidecar wins over a pythonnet load_scenario fallback.
        string strat2 = Path.Combine(TempDir, "fallback_strategy.py");
        ScenarioSidecarStore.SetStartupParams(strat2, new StartupParamsForWrite("2022-01-01", "2022-02-01", "Daily", "100000"));
        ScenarioSidecarStore.SetInstruments(strat2, new[] { "6758.TSE" });
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
}
