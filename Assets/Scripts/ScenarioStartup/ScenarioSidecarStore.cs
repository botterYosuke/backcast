// ScenarioSidecarStore.cs — issue #29 "Replay 実行設定パネル" (DURABLE tier, persistence seam)
//
// The single seam that MERGE-WRITES the engine-owned scenario sidecar <strategy>.json
// "scenario" object (CONTEXT.md: "scenario（実行設定）/ scenario sidecar"; engine read
// entry = engine.strategy_runtime.scenario.load_scenario). Mirrors TTWR
// src/ui/scenario_sidecar/write.rs::atomic_mutate_scenario_object: read the whole JSON,
// mutate ONLY the target keys inside the "scenario" object, preserve every sibling
// verbatim, atomic-write.
//
// WHY NEWTONSOFT (ADR-0005): the engine's scenario.validate is STRICT (_check_keys
// rejects unknown keys), so the panel must preserve the v3 optionals it does NOT edit —
// account_type / instruments_ref (scalars) and strategy_init_kwargs (an ARBITRARY nested
// dict). JsonUtility cannot round-trip an arbitrary dict (same limitation as the depth
// per_instrument map), so a JsonUtility write would silently DROP strategy_init_kwargs
// and corrupt a strict-validated sidecar. Newtonsoft JObject is the C# equivalent of
// serde_json::Value (TTWR's DOM) and round-trips any nested value losslessly.
//
// CONTAINMENT (ADR-0005 decision 2): Newtonsoft is used ONLY inside this store. Callers
// see SetStartupParams / SetInstruments / ReadScenario — never a JObject. The layout
// sidecar stays on JsonUtility (LayoutStore is untouched). Mirrors LayoutStore's
// parser-hiding discipline so a future swap stays local.
//
// SCHEMA: Unity writes schema_version = 3 ONLY (owner 2026-06-14; v1/v2 切り捨て). The
// 5 panel-owned keys are start / end / granularity / initial_cash / instruments. Every
// other key already present in the "scenario" object (account_type, instruments_ref,
// strategy_init_kwargs, …) is preserved verbatim.

using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class ScenarioSidecarStore
{
    public const int SchemaVersion = 3;

    // foo.py -> foo.json ; foo.bar.py -> foo.bar.json (stem-based, matches the engine's
    // scenario._sidecar_path: strategy_path.with_name(stem + ".json")).
    public static string SidecarPathFor(string strategyPath)
    {
        if (string.IsNullOrEmpty(strategyPath))
            throw new ArgumentException("strategyPath must not be empty", nameof(strategyPath));
        return Path.ChangeExtension(strategyPath, ".json");
    }

    // ---- READ (sidecar only; the .py inline SCENARIO fallback is a populate-wiring
    // concern handled via pythonnet load_scenario, NOT here — keeps this seam Python-free
    // and probe-testable). Returns null when no sidecar exists or it carries no
    // "scenario" object. Throws ScenarioSidecarException on malformed JSON (a corrupt
    // user file is a real error the caller surfaces, not a silent default). ----
    public static ScenarioSnapshot ReadScenario(string strategyPath)
    {
        string path = SidecarPathFor(strategyPath);
        if (!File.Exists(path)) return null;

        JObject root = ParseFile(path);
        if (!(root["scenario"] is JObject scenario)) return null;
        return ScenarioSnapshot.FromJObject(scenario);
    }

    // ---- WRITE: startup params (start/end/granularity/initial_cash). MUTATE-EXISTING-ONLY
    // (#67): merges into an EXISTING "scenario" object and preserves siblings; returns null
    // (writes NOTHING) when no sidecar/scenario object exists. A window-only sidecar (no
    // instruments) is just as incomplete as a universe-only one and would shadow the inline
    // .py SCENARIO, so this never creates one. initial_cash is written as an integer when the
    // text parses, else null (TTWR set_startup_params / atomic_mutate_scenario_object parity:
    // the validated-for-write projection carries text; the run gate blocks invalid values). ----
    public static WritebackOutcome? SetStartupParams(string strategyPath, StartupParamsForWrite p)
    {
        return Mutate(strategyPath, scenario => ApplyStartupParams(scenario, p), allowCreate: false);
    }

    // ---- WRITE: universe instruments (the InstrumentRegistry SoT → sidecar). Order
    // preserved as supplied (caller dedups). MUTATE-EXISTING-ONLY (#67): returns null and
    // writes NOTHING when no sidecar/scenario object exists — an instruments-only sidecar
    // would be missing the backtest window and break register_live_strategy / start_engine
    // (STRATEGY_LOAD_FAILED). The edit stays in the in-memory registry, persisted later by
    // Run-commit's full sidecar. Mirrors TTWR set_instruments. ----
    public static WritebackOutcome? SetInstruments(string strategyPath, IReadOnlyList<string> ids)
    {
        return Mutate(strategyPath, scenario => ApplyInstruments(scenario, ids), allowCreate: false);
    }

    // ---- WRITE: startup params + instruments in ONE read-modify-write. The panel's Commit
    // writes both together, so a single atomic mutate keeps them consistent (a crash between
    // two separate writes would leave start/end/cash persisted but instruments stale, or vice
    // versa) and halves the I/O. SetStartupParams / SetInstruments remain for the deferred
    // registry-only writeback (#31 picker).
    // This is the ONLY writer that may CREATE a sidecar (allowCreate: true), because it writes
    // the full 5-key scenario in one shot — the result is always complete, never the partial
    // sidecar #67 guards against. Always returns a value (the write always happens).
    public static WritebackOutcome SetStartupParamsAndInstruments(
        string strategyPath, StartupParamsForWrite p, IReadOnlyList<string> ids)
    {
        return Mutate(strategyPath, scenario =>
        {
            ApplyStartupParams(scenario, p);
            ApplyInstruments(scenario, ids);
        }, allowCreate: true).Value;
    }

    static void ApplyStartupParams(JObject scenario, StartupParamsForWrite p)
    {
        scenario["start"] = p.Start ?? "";
        scenario["end"] = p.End ?? "";
        scenario["granularity"] = p.Granularity ?? "";
        scenario["initial_cash"] = long.TryParse(p.InitialCashText, out long cash)
            ? (JToken)new JValue(cash)
            : JValue.CreateNull();
    }

    static void ApplyInstruments(JObject scenario, IReadOnlyList<string> ids)
    {
        var arr = new JArray();
        foreach (string id in ids) arr.Add(id);
        scenario["instruments"] = arr;
        // v1 legacy single-instrument key is not produced; drop it if a stale file has one.
        scenario.Remove("instrument");
    }

    // ---- core read-modify-write (atomic_mutate_scenario_object parity) ----
    // allowCreate=false is MUTATE-EXISTING-ONLY (#67 / TTWR atomic_mutate_scenario_object): if
    // the file is absent OR carries no "scenario" object, return null and write NOTHING — so an
    // individual setter can never leave an incomplete sidecar that shadows the inline .py
    // SCENARIO. allowCreate=true (the combined Run-commit writer only) seeds a fresh scenario.
    static WritebackOutcome? Mutate(string strategyPath, Action<JObject> mutate, bool allowCreate)
    {
        string path = SidecarPathFor(strategyPath);

        bool exists = File.Exists(path);
        if (!exists && !allowCreate) return null;  // no sidecar to merge into (TTWR: IO error)

        JObject root = exists ? ParseFile(path) : new JObject();
        if (!(root["scenario"] is JObject scenario))
        {
            if (!allowCreate) return null;         // no scenario object (TTWR: "missing scenario object")
            scenario = new JObject();
            root["scenario"] = scenario;
        }

        // v3-only: stamp the version on every write (preserves nothing about v1/v2).
        scenario["schema_version"] = SchemaVersion;
        mutate(scenario);

        string json = root.ToString(Formatting.Indented);
        AtomicWriteAllText(path, json);
        return new WritebackOutcome(path, File.GetLastWriteTimeUtc(path));
    }

    static JObject ParseFile(string path)
    {
        try
        {
            string raw = File.ReadAllText(path);
            return JObject.Parse(raw);
        }
        catch (JsonException e)
        {
            throw new ScenarioSidecarException($"invalid JSON in scenario sidecar '{path}': {e.Message}", e);
        }
    }

    // Write to a temp file in the same directory, then atomically replace — so a crash
    // mid-write never leaves the user's strategy sidecar truncated.
    static void AtomicWriteAllText(string path, string contents)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path))
        {
            // File.Replace preserves a single atomic swap on NTFS; no backup file kept.
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}

// Validated-for-write projection (3-projection model; CONTEXT "scenario 編集の 3
// projection"). POD crossing panel → store. Strings already validated by the caller;
// InitialCashText stays text so the store can write null for a still-empty value.
public struct StartupParamsForWrite
{
    public string Start;
    public string End;
    public string Granularity;     // canonical "Daily" | "Minute"
    public string InitialCashText;

    public StartupParamsForWrite(string start, string end, string granularity, string initialCashText)
    {
        Start = start;
        End = end;
        Granularity = granularity;
        InitialCashText = initialCashText;
    }
}

// The on-disk scenario, read back for populate / restore. Carries the 5 panel-owned
// fields; siblings are intentionally NOT surfaced (the store preserves them on write,
// the panel never reads them).
public sealed class ScenarioSnapshot
{
    public string Start;
    public string End;
    public string Granularity;
    public long? InitialCash;
    public List<string> Instruments = new List<string>();

    public static ScenarioSnapshot FromJObject(JObject scenario)
    {
        var s = new ScenarioSnapshot
        {
            Start = (string)scenario["start"],
            End = (string)scenario["end"],
            Granularity = (string)scenario["granularity"],
            InitialCash = ReadCash(scenario["initial_cash"]),
        };
        // v2/v3 "instruments" (list) is canonical; tolerate v1 legacy "instrument" (single).
        if (scenario["instruments"] is JArray arr)
        {
            foreach (JToken t in arr)
                if (t.Type == JTokenType.String) s.Instruments.Add((string)t);
        }
        else if (scenario["instrument"]?.Type == JTokenType.String)
        {
            s.Instruments.Add((string)scenario["instrument"]);
        }
        return s;
    }

    // Accept an integer OR a float (e.g. a hand-edited / engine-written 1000000.0); both are
    // valid cash. A non-numeric / absent value reads as null (the panel then flags "empty").
    static long? ReadCash(JToken tok)
    {
        if (tok == null) return null;
        if (tok.Type == JTokenType.Integer) return (long)tok;
        if (tok.Type == JTokenType.Float) return (long)System.Math.Round((double)tok);
        return null;
    }
}

// Returned by every write. #29 does not consume the mtime (no live watcher — read-on-
// populate only), but the shape matches TTWR WritebackOutcome so the deferred watcher
// slice can add record_write(outcome) for the ADR-0020-parity self-trigger fence WITHOUT
// reshaping this seam (CONTEXT: slicing, not deviation).
public struct WritebackOutcome
{
    public string Path;
    public DateTime MTimeUtc;

    public WritebackOutcome(string path, DateTime mtimeUtc)
    {
        Path = path;
        MTimeUtc = mtimeUtc;
    }
}

public sealed class ScenarioSidecarException : Exception
{
    public ScenarioSidecarException(string message, Exception inner) : base(message, inner) { }
}
