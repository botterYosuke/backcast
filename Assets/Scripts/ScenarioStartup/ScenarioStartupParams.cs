// ScenarioStartupParams.cs — issue #29 "Replay 実行設定パネル" (editing buffer + validation)
//
// The 3-projection scenario-editing model (CONTEXT.md: "scenario 編集の 3 projection (TTWR
// 踏襲)"). Ported from TTWR src/ui/scenario_startup_panel/{mod,validate,sync}.rs.
//
//   (1) EDITING BUFFER  = ScenarioStartupParams  — raw user strings; MAY hold invalid
//                         values (empty universe, negative cash). The only place invalid
//                         state lives. Carries `Dirty` (gates external sync) and `Errors`.
//   (2) VALIDATED-FOR-WRITE = StartupParamsForWrite (in ScenarioSidecarStore) — produced
//                         only when validation passes.
//   (3) ON-DISK         = <strategy>.json "scenario" v3 (ScenarioSidecarStore owns it).
//
// AC④ ("不正値は run を起動しない") IS the (1)→(2) gate: an invalid editing buffer cannot be
// promoted to validated-for-write, so it neither persists for run nor launches a run.
// Engine-side scenario.validate() / NO_INSTRUMENTS remain defense-in-depth.

using System;
using System.Globalization;

// Canonical granularity choices. None = nothing selected yet (a validation error until
// the user picks one; TTWR sync.rs: "Please select a granularity to enable Run").
public enum GranularityChoice { None, Daily, Minute }

public sealed class ScenarioStartupParams
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DefaultInitialCash = "1000000"; // TTWR sync.rs:61

    // (1) raw editing buffer — may be invalid.
    public string Start = "";
    public string End = "";
    public GranularityChoice Granularity = GranularityChoice.None;
    public string InitialCash = "";

    // Dirty guard (TTWR ScenarioStartupParams.dirty): while true, external metadata sync
    // must NOT overwrite the buffer (the user is typing). Cleared by the sync layer only
    // when all fields validate.
    public bool Dirty;

    // Seed the buffer when the sidecar carries no value: start = today − 3 months, end =
    // today, cash = 1_000_000, granularity unset. These are overridable initial values,
    // NOT a lookback input (CONTEXT "run 期間 (start/end) vs lookback").
    public static ScenarioStartupParams SeedDefaults(DateTime today)
    {
        DateTime start = today.AddMonths(-3); // .NET AddMonths clamps month-end, like chrono checked_sub_months
        return new ScenarioStartupParams
        {
            Start = "2024-01-01", // start.ToString(DateFormat, CultureInfo.InvariantCulture),
            End = "2025-12-31", // today.ToString(DateFormat, CultureInfo.InvariantCulture),
            Granularity = GranularityChoice.Minute,
            InitialCash = DefaultInitialCash,
        };
    }

    public static string GranularityToString(GranularityChoice g)
    {
        switch (g)
        {
            case GranularityChoice.Daily: return "Daily";
            case GranularityChoice.Minute: return "Minute";
            default: return "";
        }
    }
}

// Per-field + cross-field error messages (TTWR ScenarioStartupParamsErrors). A null field
// means valid.
public sealed class ScenarioStartupErrors
{
    public string Start;
    public string End;
    public string Granularity;
    public string InitialCash;
    public string CrossField;
    public string Universe;

    public bool Any =>
        Start != null || End != null || Granularity != null ||
        InitialCash != null || CrossField != null || Universe != null;
}

public static class ScenarioStartupValidation
{
    // Validate the editing buffer against universe size. universeCount comes from the
    // InstrumentRegistry (universe is a separate SoT, not a buffer field).
    public static ScenarioStartupErrors Validate(ScenarioStartupParams p, int universeCount)
    {
        var e = new ScenarioStartupErrors();

        DateTime startDt;
        bool startOk = TryParseDate(p.Start, out startDt, out e.Start, "Start");
        DateTime endDt;
        bool endOk = TryParseDate(p.End, out endDt, out e.End, "End");

        // Cross-field only when both parse (TTWR sync.rs:131).
        if (startOk && endOk && startDt > endDt)
            e.CrossField = "start must be on or before end";

        // Granularity must be a canonical choice.
        if (p.Granularity == GranularityChoice.None)
            e.Granularity = "Please select a granularity to enable Run";

        // Initial cash: non-empty, parseable i64, positive (TTWR sync.rs:111).
        if (string.IsNullOrEmpty(p.InitialCash))
            e.InitialCash = "initial cash must not be empty";
        else if (!long.TryParse(p.InitialCash, NumberStyles.None, CultureInfo.InvariantCulture, out long cash))
            e.InitialCash = "invalid integer";
        else if (cash <= 0)
            e.InitialCash = "initial cash must be positive";

        // Universe: non-empty (AC④ "空 universe を拒否"). Dedup is the registry's job.
        if (universeCount <= 0)
            e.Universe = "universe must not be empty";

        return e;
    }

    // True when the buffer is runnable AND a supplyable strategy exists. Strategy
    // supplyability is the caller's (run-UI) concern, surfaced separately from scenario
    // validation per CONTEXT "active strategy 選択".
    public static bool TryBuildForWrite(
        ScenarioStartupParams p, int universeCount, out StartupParamsForWrite forWrite, out ScenarioStartupErrors errors)
    {
        errors = Validate(p, universeCount);
        if (errors.Any)
        {
            forWrite = default;
            return false;
        }
        forWrite = new StartupParamsForWrite(
            p.Start, p.End, ScenarioStartupParams.GranularityToString(p.Granularity), p.InitialCash);
        return true;
    }

    static bool TryParseDate(string raw, out DateTime dt, out string error, string fieldName)
    {
        dt = default;
        error = null;
        if (string.IsNullOrEmpty(raw))
        {
            error = $"{fieldName} must not be empty";
            return false;
        }
        if (!DateTime.TryParseExact(
                raw, ScenarioStartupParams.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            error = $"invalid date '{raw}'; use YYYY-MM-DD";
            return false;
        }
        return true;
    }
}
