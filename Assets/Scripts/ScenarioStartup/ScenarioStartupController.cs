// ScenarioStartupController.cs — issue #29 "Replay 実行設定パネル" (orchestration core)
//
// The input-agnostic brain of the Startup tile — a PLAIN C# class (NOT a MonoBehaviour),
// so the AFK probe drives it against fakes headlessly (mirrors HakoniwaController). It owns
// the 3-projection editing flow and the run gate; the uGUI tile (RectTransforms / input
// fields) and the pythonnet run trigger are thin layers on top.
//
// RESPONSIBILITIES (CONTEXT.md):
//   * Populate — read the on-disk scenario (ScenarioSidecarStore) or a pythonnet
//     load_scenario fallback, else default seed; fill the editing buffer + InstrumentRegistry.
//   * Edit — buffer setters flip Dirty (gates external re-sync; CONTEXT "scenario 編集の 3
//     projection").
//   * Commit — validate → write the sidecar (SetStartupParams + SetInstruments), merge-
//     preserving the v3 optionals (ADR-0005).
//   * Run gate — resolve the runnable strategy path via a `Func<string>` resolver AT run time (#169
//     ADR-0036 D5: the resolver — BackcastWorkspaceRoot.BuildNotebookStrategyPath — owns the named-WYSIWYR
//     vs untitled-scratch decision, so this gate stays altitude-agnostic) AND validate the scenario; only
//     then write the sidecar and hand the caller a path to launch load_replay_data → start_engine (CONTEXT
//     "backcast Replay 起動経路"). The two block reasons are surfaced distinctly (AC④ scenario invalid vs
//     "no runnable strategy").

using System;
using System.Collections.Generic;

public enum RunGate { Ready, BlockedNoStrategy, BlockedInvalidScenario }

public struct RunGateResult
{
    public RunGate Gate;
    public string StrategyPath;            // canonical .py path when Ready
    public ScenarioStartupErrors Errors;   // populated when BlockedInvalidScenario
    public string Message;                 // human-facing reason for a block

    public bool IsReady => Gate == RunGate.Ready;
}

public sealed class ScenarioStartupController
{
    public ScenarioStartupParams Params { get; private set; } = new ScenarioStartupParams();
    public InstrumentRegistry Universe { get; } = new InstrumentRegistry();
    public ScenarioStartupErrors Errors { get; private set; } = new ScenarioStartupErrors();

    // #129: fires after Commit succeeds — covers params-only edits that Universe.Changed misses.
    public event Action Committed;

    // ---- POPULATE (read-on-populate; no live watcher in #29). `today` seeds defaults when
    // neither the sidecar nor the .py fallback carries a scenario. `fallback` is the
    // pythonnet load_scenario result for a strategy whose scenario lives in an inline .py
    // SCENARIO (no sidecar yet) — null in the Python-free probe / when unavailable. The
    // sidecar wins over the fallback (it is the going-forward source). ----
    public void Populate(string strategyPath, DateTime today, ScenarioSnapshot fallback = null)
    {
        if (Params.Dirty) return; // dirty-guard BEFORE the (fail-loud) read, as before
        PopulateFrom(ScenarioSidecarStore.ReadScenario(strategyPath) ?? fallback, today);
    }

    // ---- POPULATE from an ALREADY-RESOLVED snapshot (no sidecar read). The editor-seed seam
    // pre-reads the sidecar TOLERANTLY (ScenarioSidecarStore.TryReadScenario) so a CORRUPT sidecar
    // degrades to inline/empty instead of throwing out of File→Open (findings 0051 D3) — null snap =
    // seed defaults + empty universe (Run then blocks on the empty universe). ----
    public void PopulateFrom(ScenarioSnapshot snap, DateTime today)
    {
        if (Params.Dirty) return; // dirty-guard: never clobber an in-flight edit
        if (snap != null)
        {
            Params = new ScenarioStartupParams
            {
                Start = snap.Start ?? "",
                End = snap.End ?? "",
                Granularity = ParseGranularity(snap.Granularity),
                InitialCash = snap.InitialCash.HasValue
                    ? snap.InitialCash.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "",
            };
            Universe.ReplaceAll(snap.Instruments);
        }
        else
        {
            Params = ScenarioStartupParams.SeedDefaults(today);
            Universe.ReplaceAll(Array.Empty<string>());
        }
        Errors = new ScenarioStartupErrors();
    }

    // ---- CLEAR (File→New workspace reset; findings 0017 §4). In-memory ONLY: resets the
    // editing buffer to an empty unbound state, empties the universe, and clears errors. Does
    // NOT touch the on-disk sidecar (TTWR FileNewRequested is an in-memory reset; file deletion
    // would be destructive over-reach). The strategy `.py` buffer / editor panel despawn and
    // the (guarded) SetExecutionMode(LiveManual) are the menu bar's job — this owns only the
    // #29 scenario projections. Unconditional: the dirty-guard does NOT apply (New is a
    // deliberate discard, not an external re-sync). ----
    public void Clear()
    {
        Params = new ScenarioStartupParams();      // empty, Dirty=false (not seeded — New is blank)
        Universe.ReplaceAll(Array.Empty<string>());
        Errors = new ScenarioStartupErrors();
    }

    // ---- SEED FRESH DEFAULTS (#169 / ADR-0036 D4 — fresh New / no-resume boot). Unconditional (like
    // Clear — a deliberate discard, NOT an external re-sync, so the dirty-guard does NOT apply): seed a
    // VALID default scenario (today-relative dates / granularity / cash via SeedDefaults) AND the default
    // `instruments` universe, both CLEAN (Dirty=false). Distinct from Clear (empty/blank) — the seeded
    // observe cell is then actually runnable (load_replay_data needs a real date window) and the FIRST Save
    // As COMMITS the scenario so the universe rides the sidecar writeback (otherwise an invalid empty
    // scenario fails Commit, the universe is never persisted, and the post-save reseed wipes it). Universe
    // last so its Changed fires after the params are in place. ----
    public void SeedFreshDefaults(DateTime today, IReadOnlyList<string> instruments)
    {
        Params = ScenarioStartupParams.SeedDefaults(today);   // today-3mo..today dates + cash, Dirty=false
        // SeedDefaults leaves Granularity = None (the populate path wants the user to pick); the FRESH seed
        // must be VALID so the observe cell runs (load_replay_data needs a granularity) and Save As commits —
        // pick Daily (universally available, the most "眺めやすい" for the observe replay). Field-set, not
        // SetGranularity, so Params stays clean (Dirty=false).
        Params.Granularity = GranularityChoice.Daily;
        Errors = new ScenarioStartupErrors();
        Universe.ReplaceAll(instruments);                      // fires Changed → chart spawn (universe↔chart)
    }

    // ---- EDIT (each mutates the buffer and flips Dirty) ----
    public void SetStart(string v) { Params.Start = v; Params.Dirty = true; }
    public void SetEnd(string v) { Params.End = v; Params.Dirty = true; }
    public void SetGranularity(GranularityChoice g) { Params.Granularity = g; Params.Dirty = true; }
    public void SetInitialCash(string v) { Params.InitialCash = v; Params.Dirty = true; }
    public bool AddInstrument(string id) { bool c = Universe.Add(id); if (c) Params.Dirty = true; return c; }
    public bool RemoveInstrument(string id) { bool c = Universe.Remove(id); if (c) Params.Dirty = true; return c; }

    // ---- VALIDATE (cheap; call on every edit to drive error labels / Run enablement) ----
    public ScenarioStartupErrors Validate()
    {
        Errors = ScenarioStartupValidation.Validate(Params, Universe.Count);
        return Errors;
    }

    // ---- COMMIT (persist the validated buffer; AC②). Returns false (and leaves the sidecar
    // untouched) when the buffer is invalid — the (1)→(2) gate (AC④). ----
    public bool Commit(string strategyPath)
    {
        if (!ScenarioStartupValidation.TryBuildForWrite(Params, Universe.Count, out var forWrite, out var errors))
        {
            Errors = errors;
            return false;
        }
        // One atomic read-modify-write so start/end/cash and instruments never persist
        // half-updated (a crash between two separate writes would leave a mismatched sidecar).
        ScenarioSidecarStore.SetStartupParamsAndInstruments(strategyPath, forWrite, Universe.Ids);
        Params.Dirty = false;
        Errors = new ScenarioStartupErrors();
        Committed?.Invoke();  // #129 (findings 0104 F1): Replay chart preview reseeds here
        return true;
    }

    // ---- RUN GATE. Resolves the runnable strategy file AT call time via `strategyPathResolver` and
    // validates the scenario. On Ready the sidecar is written and StrategyPath is returned; the caller
    // then drives load_replay_data → start_engine.
    //
    // #169 (ADR-0036 D5): the resolver — BackcastWorkspaceRoot.BuildNotebookStrategyPath — encodes the
    // full "what file does a RUN execute" decision so this gate stays altitude-agnostic (it just gets a
    // path or null): a NAMED clean doc → its bound .py (WYSIWYR — supplyability flips to null if the editor
    // went dirty); an UNTITLED doc with a non-empty cell → a lazily-written scratch .py (起動直後 Run 可・D6);
    // a named+dirty doc or an all-empty untitled buffer → null → BlockedNoStrategy (WYSIWYR unchanged). ----
    public RunGateResult TryStartRun(Func<string> strategyPathResolver)
    {
        string path = strategyPathResolver?.Invoke();
        if (string.IsNullOrEmpty(path))
        {
            return new RunGateResult
            {
                Gate = RunGate.BlockedNoStrategy,
                Message = RunReadinessViewModel.NoStrategy,   // single-source the wording (#76 U1)
            };
        }

        if (!Commit(path))
        {
            return new RunGateResult
            {
                Gate = RunGate.BlockedInvalidScenario,
                Errors = Errors,
                Message = RunReadinessViewModel.InvalidScenario,   // single-source the wording (#76 U1)
            };
        }

        return new RunGateResult { Gate = RunGate.Ready, StrategyPath = path };
    }

    static GranularityChoice ParseGranularity(string s)
    {
        if (s == "Daily") return GranularityChoice.Daily;
        if (s == "Minute") return GranularityChoice.Minute;
        return GranularityChoice.None;
    }
}
