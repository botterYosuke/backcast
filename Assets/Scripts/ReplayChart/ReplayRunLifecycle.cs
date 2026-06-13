// ReplayRunLifecycle.cs — issue #11 "Replay panels" (M1 hand 2, DURABLE tier)
//
// The authoritative source of the STATUS panel (RUNNING / DONE / FAILED / IDLE).
// Per docs/findings/0003-replay-panels.md §2, the replay run's status is NOT in
// engine._replay_state (IDLE-pinned during nautilus replay) and NOT in the poll
// JSON — it lives in the C# adapter that drives the run: the adapter is RUNNING
// the instant it invokes start_nautilus_replay, and the run is DONE / FAILED when
// the terminal sink callback (push_run_complete / push_run_failed) arrives. This
// type owns that little finite state machine so the status panel and the AFK probe
// share one source of truth (ADR-0001 decision 8: the run-driver owns run-lifecycle).
//
// PLAIN POCO (no MonoBehaviour, no UnityEngine, no Python): so the M2 headless AFK
// probe can unit-assert the transitions and the M3 throwaway harness can drive it
// from Update(). Lives in the default Assembly-CSharp / global namespace (matches
// ReplayEventSink / ReplayPanelDecoder — this folder uses no asmdef, no namespace).
//
// GIL-FREE observation (the crux): Observe(sink) reads ReplayEventSink.Failed /
// .Completed / .Error, which are plain reads of the sink's volatile-backed fields
// (ReplayEventSink.cs lines 96-99: "observation surface — called from the main
// (probe) thread, GIL-free"). NO Py.GIL() is taken anywhere in this type — exactly
// the pattern ReplayChartHarness.Update already uses to read _sink.Failed /
// _sink.Completed on the main thread. The Python daemon writes those volatiles under
// the GIL; main reads them GIL-free. This type never touches pythonnet.
//
// TWO failure channels, one terminal:
//   * MarkFailed(reason) — the START-ERROR channel: the launcher rejected
//     start_nautilus_replay or threw (the C# _startError string in the harness).
//   * Observe(sink) -> Failed when sink.Failed (push_run_failed from the daemon).
//
// IDLE-RETURN DECISION (the open question from the hand-2 sketch, now DECIDED):
//   Done / Failed are STICKY-terminal within a run — they do NOT auto-return to Idle.
//   The status panel's whole purpose in the #11 visual gate is the owner SEEING the
//   final DONE/FAILED, so erasing it back to IDLE would defeat the gate. The findings
//   §2 "その後 IDLE" is the conceptual full lifecycle (live / multi-run future); here
//   it is realized as RE-ARM on the next MarkRunning() (which resets to Running from
//   any state). #11 is a single-run throwaway so it never re-arms, but the type
//   supports it for free. There is deliberately NO explicit Reset() — YAGNI for a
//   single-run throwaway. First terminal wins; MarkRunning() is the only re-arm.
//
// INTERMEDIATE STATE: this file compiles but is UNUSED until the M2 probe asserts its
// transitions and the M3 harness drives it. No .meta is authored here — Unity
// generates ReplayRunLifecycle.cs.meta on the next import.

public enum RunStatus
{
    Idle,
    Running,
    Done,
    Failed,
}

public sealed class ReplayRunLifecycle
{
    // The status panel renders directly off this. Starts IDLE (pre-run).
    public RunStatus Status { get; private set; } = RunStatus.Idle;

    // Non-null only when Status == Failed: the launcher reject/throw string, or the
    // sink's push_run_failed error. The status panel can surface it; the run_result
    // panel still decodes sink.Summary itself (this type stays status-only by design).
    public string FailureReason { get; private set; }

    // The adapter calls this the instant it invokes start_nautilus_replay. Re-arms
    // from ANY state (the conceptual IDLE->RUNNING re-entry; unused in single-run #11).
    public void MarkRunning()
    {
        Status = RunStatus.Running;
        FailureReason = null;
    }

    // START-ERROR channel: the launcher rejected start_nautilus_replay or threw.
    // First-terminal-wins: a no-op once already Done/Failed (only MarkRunning re-arms).
    public void MarkFailed(string reason)
    {
        if (Status == RunStatus.Done || Status == RunStatus.Failed) return;
        Status = RunStatus.Failed;
        FailureReason = reason;
    }

    // Frame-driven, GIL-FREE: reads the sink's volatile-backed terminal flags on the
    // main thread (no Py.GIL). Idempotent and sticky — safe to call every Update().
    public void Observe(ReplayEventSink sink)
    {
        if (Status == RunStatus.Done || Status == RunStatus.Failed) return; // sticky terminal
        if (sink == null) return;

        if (sink.Failed)
        {
            Status = RunStatus.Failed;
            FailureReason = sink.Error;
            return;
        }
        if (sink.Completed)
        {
            Status = RunStatus.Done;
        }
        // otherwise: hold current state (Idle if never armed, Running if MarkRunning ran).
    }
}
