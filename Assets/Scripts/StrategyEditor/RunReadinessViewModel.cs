// RunReadinessViewModel.cs — issue #76 S6b-β-clean U1 (DURABLE tier, pure logic)
//
// The presentation brain for the Strategy Editor title-bar Run button (findings 0046,
// "S6b-β-clean 設計の木" U1). A PLAIN C# class (no UnityEngine, no pythonnet) so the AFK probe
// drives every enablement decision deterministically — the uGUI Run button (StrategyEditorRunButton)
// is a thin view on top.
//
// It mirrors BackcastWorkspaceRoot.OnRun's gate ORDER as a NON-MUTATING readiness read. OnRun's
// _scenario.TryStartRun MUTATES (it commits the sidecar), so that stays at click time; this VM reads
// the two non-mutating predicates instead — the provider's 5-condition supplyable (strategyReady) and
// the scenario validation (scenarioValid) — plus the durable owner / running flags. The block-reason
// consts below are THE single source for that wording: ScenarioStartupController.TryStartRun and
// BackcastWorkspaceRoot.OnRun reference NoStrategy / InvalidScenario / NotOwner here (no drift between
// the steady title-bar reason and the click-time gate message).
//
// Gate order = OnRun (BackcastWorkspaceRoot.OnRun): running → no-strategy → invalid-scenario →
// not-owner. Run is enabled only when all four pass.

public sealed class RunReadinessViewModel
{
    // OnRun's block messages (one source of wording — ScenarioStartupController.TryStartRun for the
    // scenario gates, OnRun for the owner gate; "Running…" is the title-bar in-flight label).
    public const string Running = "Running…";
    public const string NoStrategy = "No saved strategy to run — open and save a strategy first.";
    public const string InvalidScenario = "Scenario has invalid values — fix them to enable Run.";
    public const string NotOwner = "Not the Python owner — cannot run.";

    public bool CanRun { get; private set; }
    public string BlockReason { get; private set; }   // null when CanRun

    // Recompute from the four inputs (the root samples these every frame). strategyReady =
    // IStrategyFileProvider.TryGetStrategyFile (non-mutating); scenarioValid = !Validate().Any.
    public void Evaluate(bool isOwner, bool isRunning, bool strategyReady, bool scenarioValid)
    {
        BlockReason = Reason(isOwner, isRunning, strategyReady, scenarioValid);
        CanRun = BlockReason == null;
    }

    // Pure decision (probe-drivable): the OnRun gate order, returning the block reason or null.
    public static string Reason(bool isOwner, bool isRunning, bool strategyReady, bool scenarioValid)
    {
        if (isRunning) return Running;
        if (!strategyReady) return NoStrategy;
        if (!scenarioValid) return InvalidScenario;
        if (!isOwner) return NotOwner;
        return null;
    }
}
