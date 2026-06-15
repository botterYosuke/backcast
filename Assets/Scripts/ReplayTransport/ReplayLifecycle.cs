// ReplayLifecycle.cs — issue #30 "Replay transport footer" (DURABLE tier, pure logic)
//
// The 6-state run lifecycle the footer renders + gates buttons on (findings 0023 §5 C2). The
// pre-#50 ReplayRunLifecycle.cs was deleted with the nautilus scaffold; this is the minimal
// fusion type that replaces it for the DuckDB→kernel path.
//
// WHY A FUSION TYPE (not just the poll): the kernel path DOES drive engine._replay_state, so the
// poll get_state_json carries Idle/Loaded/Running/Paused honestly (unlike the deleted nautilus
// path — findings 0003 §1). But it CANNOT carry Done/Failed: start_engine is synchronous and on
// completion core.py force_stops → replay_state=IDLE. So terminal authority is the LAUNCHER
// result (start_engine returned / threw), latched here over the poll. ▶ re-arm clears the latch.
//
// A PLAIN C# class (no UnityEngine, no threading) so the AFK probe drives every transition
// deterministically. The host feeds it on the main thread: ApplyPoll from the poll snapshot,
// MarkDone/MarkFailed from the launcher's terminal signal, Rearm when a fresh run starts.

public enum ReplayPhase { Idle, Loaded, Running, Paused, Done, Failed }

public sealed class ReplayLifecycle
{
    enum Terminal { None, Done, Failed }

    Terminal _terminal = Terminal.None;
    string _pollState = "IDLE";

    // Set alongside Failed; surfaced by the footer status line.
    public string FailureMessage { get; private set; }

    // Poll snapshot → latest engine replay_state ("IDLE"/"LOADED"/"RUNNING"/"PAUSED"). Ignored
    // while a terminal is latched (poll says IDLE after a completed run; the latch wins).
    public void ApplyPoll(string replayState)
    {
        if (!string.IsNullOrEmpty(replayState)) _pollState = replayState;
    }

    // Launcher terminal authority: start_engine returned success / threw (or stop halted it).
    public void MarkDone() { _terminal = Terminal.Done; }
    public void MarkFailed(string message) { _terminal = Terminal.Failed; FailureMessage = message; }

    // ▶ re-arm: a fresh run clears the terminal latch and any stale poll so the next poll's
    // RUNNING shows through (the engine likewise clears its run state on start_engine).
    public void Rearm() { _terminal = Terminal.None; FailureMessage = null; _pollState = "IDLE"; }

    public ReplayPhase Phase
    {
        get
        {
            if (_terminal == Terminal.Done) return ReplayPhase.Done;
            if (_terminal == Terminal.Failed) return ReplayPhase.Failed;
            switch (_pollState)
            {
                case "LOADED": return ReplayPhase.Loaded;
                case "RUNNING": return ReplayPhase.Running;
                case "PAUSED": return ReplayPhase.Paused;
                default: return ReplayPhase.Idle;
            }
        }
    }

    public bool IsTerminal => _terminal != Terminal.None;
}
