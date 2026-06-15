// ReplayTransportViewModel.cs — issue #30 "Replay transport footer" (DURABLE tier, pure logic)
//
// The presentation brain of the footer transport (findings 0023 §5). A PLAIN C# class (no UGUI,
// no pythonnet) so the AFK probe drives every decision deterministically — the uGUI footer view
// (ReplayFooterView) and the InprocLiveServer transport RPCs are thin layers the host wires on.
//
// TTWR-faithful parity (ADR-0005, footer.rs): play / pause / step / speed / stop. backcast's
// transport seam is NOT TTWR's TransportCommand enum + mpsc — it is in-proc pythonnet direct
// calls; this VM only DECIDES (returns intents + enablement), the host marshals them over the
// GIL to the server forwarders. ▶ is context-sensitive like footer_pause_resume_system:
// Running→Pause, Paused→Resume, else→Run (re-arm). Step is 1 bar, PAUSED-only. The run launch
// CONFIG (universe/dates/cash) is #29's job; this VM drives the transport over an existing run.

public enum ReplayTransportIntent { Run, Pause, Resume, Step, Stop, SetSpeed }

public sealed class ReplayTransportViewModel
{
    // TTWR footer.rs SPEED_OPTIONS parity; default 1x (mult == 1).
    public static readonly int[] SpeedOptions = { 1, 2, 5, 10, 50 };

    readonly ReplayLifecycle _lifecycle;

    public ReplayTransportViewModel(ReplayLifecycle lifecycle)
    {
        _lifecycle = lifecycle ?? new ReplayLifecycle();
    }

    public ReplayLifecycle Lifecycle => _lifecycle;
    public ReplayPhase Phase => _lifecycle.Phase;

    // The displayed speed multiplier (the engine resets to 1x on a fresh start_engine; the VM
    // mirrors that on Run so the footer's highlight matches).
    public int CurrentSpeed { get; private set; } = 1;

    // ---- ▶ button: context-sensitive glyph + intent (TTWR footer_pause_resume_system). ----
    public bool IsRunning => Phase == ReplayPhase.Running;

    // "⏸" while running (acts as Pause), "▶" otherwise (Resume when paused, Run/re-arm at rest).
    public string PlayGlyph => IsRunning ? "⏸" : "▶";

    // The intent a ▶/⏸ click produces given the current phase.
    public ReplayTransportIntent PlayPauseIntent()
    {
        switch (Phase)
        {
            case ReplayPhase.Running: return ReplayTransportIntent.Pause;
            case ReplayPhase.Paused:  return ReplayTransportIntent.Resume;
            default:                  return ReplayTransportIntent.Run; // Idle/Loaded/Done/Failed → re-arm
        }
    }

    // ---- button enablement (the footer greys out disabled controls). ----
    // Step advances exactly one bar and is only meaningful while PAUSED (the runner must be
    // blocked in its gate). Stop/Speed only make sense over a live run (Running or Paused).
    public bool CanStep => Phase == ReplayPhase.Paused;
    public bool CanStop => Phase == ReplayPhase.Running || Phase == ReplayPhase.Paused;
    public bool CanSpeed => Phase == ReplayPhase.Running || Phase == ReplayPhase.Paused;

    // ▶ is always clickable: at rest it launches/re-arms (the host's OnRun enforces the strategy
    // supplyability + scenario gate from #29); while running/paused it pauses/resumes.
    public bool CanPlayPause => true;

    // ---- speed selection. Returns true (host then sends SetSpeed) only for a known, changed,
    // and currently-applicable multiplier; ignores clicks while no run is live. ----
    public bool SelectSpeed(int multiplier)
    {
        if (!CanSpeed) return false;
        if (System.Array.IndexOf(SpeedOptions, multiplier) < 0) return false;
        if (multiplier == CurrentSpeed) return false;
        CurrentSpeed = multiplier;
        return true;
    }

    // Called by the host when a fresh run starts (▶ at rest → Run): mirror the engine's 1x reset
    // and clear the terminal latch so the footer re-arms.
    public void OnRunStarted()
    {
        CurrentSpeed = 1;
        _lifecycle.Rearm();
    }
}
