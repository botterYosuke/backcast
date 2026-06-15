// LiveAutoTransportViewModel.cs — issue #39 Slice 3 "footer LiveAuto launch" (DURABLE, pure logic)
//
// The presentation brain of the footer's LiveAuto ▶ (findings 0025 §3 D2/D3). A PLAIN C# class
// (no UGUI, no pythonnet) so the AFK probe drives every decision deterministically — the uGUI
// footer view raises clicks and the composition root (ProductionLiveShell) marshals the resulting
// RPCs (register_live_strategy → start_live_strategy / pause / resume / stop_live_strategy) over
// the GIL. This VM only DECIDES (returns intents + enablement); it never touches Python.
//
// TTWR-faithful parity (ADR-0005, footer_pause_resume_system LiveAuto branch): ▶ is one button
// with three roles — Start (at rest / after terminal = re-arm), Pause (RUNNING), Resume (PAUSED) —
// guarded against a double-press while a start is in flight. The run-launch is backcast's 2-stage
// register→start (CONTEXT.md "footer": the transport seam is in-proc pythonnet direct calls, not
// TTWR's TransportCommand::StartLiveAuto wire). Live run state is read from the durable authority
// LivePanelViewModel.LatestLifecycle (LiveLifecycleEvent, fed by LiveBackendEventSink).
//
// D2 (no stop button — backcast adds a teardown TTWR's footer lacks, justified by the orphan-
// absence invariant of findings 0017 §4 / ADR-0001): leaving LiveAuto with an active run must
// stop_live_strategy(run_id) FIRST; that orchestration is decided by FooterModeViewModel, which
// asks this VM whether a run is active (HasActiveRun / ActiveRunId).

using System;
using System.Collections.Generic;

public enum LiveAutoAction { None, Start, Pause, Resume }

public enum LiveAutoStartGate
{
    Ready,
    BlockedDoublePress,   // a start is already in flight (run_id not yet confirmed)
    BlockedNoStrategy,    // IStrategyFileProvider not supplyable at call time
    BlockedNoInstrument,  // scenario universe empty (nothing to trade)
    BlockedNoVenue,       // no venue identity (not connected)
}

public struct LiveAutoStartRequest
{
    public LiveAutoStartGate Gate;
    public string StrategyFile;   // register_live_strategy(strategy_file=...)
    public string OriginalPath;   // register_live_strategy(original_path=...)
    public string InstrumentId;   // start_live_strategy(instrument_id=...)
    public string Venue;          // start_live_strategy(venue=...)
    public string Message;        // human-facing block reason (footer status line)
}

// What a ▶/⏸ click resolves to given the current phase. Action==Start carries a populated Start
// (its Gate says whether the host may actually fire register→start); Pause/Resume carry RunId.
public struct LiveAutoPlayDecision
{
    public LiveAutoAction Action;
    public string RunId;                 // valid for Pause / Resume
    public LiveAutoStartRequest Start;   // valid for Start
    public string Message;               // block reason when Action==None
}

public sealed class LiveAutoTransportViewModel
{
    // Non-terminal live-run states (a run_id is "active" — ▶ pauses/resumes, never starts).
    static readonly HashSet<string> NonTerminal = new HashSet<string>
    {
        "LOADING", "READY", "RUNNING", "PAUSED", "STOPPING",
    };

    readonly LivePanelViewModel _panel;
    readonly IStrategyFileProvider _strategy;
    readonly SelectedSymbol _selected;
    readonly Func<IReadOnlyList<string>> _scenarioInstruments; // run universe (sidecar instruments)
    readonly Func<string> _venueIdentity;                      // poll venue_id, fallback configured

    // Double-press guard: TTWR command_in_flight. Set when the host fires register→start; released
    // when the lifecycle catches up to the STARTED run_id (any status — see ObserveLifecycle) or the
    // host reports the start failed. We key release on the synchronous run_id from start_live_strategy
    // (NOT on "ActiveRunId != null") so a fast first lifecycle of ERROR — which never passes through a
    // non-terminal state — still releases the guard instead of sticking ▶ on "start in flight".
    bool _startInFlight;
    string _startedRunId;   // run_id returned synchronously by start_live_strategy (guard release key)

    public LiveAutoTransportViewModel(
        LivePanelViewModel panel,
        IStrategyFileProvider strategy,
        SelectedSymbol selected,
        Func<IReadOnlyList<string>> scenarioInstruments,
        Func<string> venueIdentity)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _strategy = strategy;
        _selected = selected;
        _scenarioInstruments = scenarioInstruments ?? (() => Array.Empty<string>());
        _venueIdentity = venueIdentity ?? (() => "");
    }

    // ---- live run state, read from the durable authority (LivePanelViewModel.LatestLifecycle). ----
    string Status => _panel.HasLifecycle ? (_panel.LatestLifecycle.Status ?? "") : "";
    string LifecycleRunId => _panel.HasLifecycle ? _panel.LatestLifecycle.RunId : null;

    // A run is "active" only while its lifecycle is non-terminal (STOPPED/ERROR/IDLE → not active,
    // so ▶ re-arms and a mode switch needs no teardown).
    public bool HasActiveRun =>
        !string.IsNullOrEmpty(LifecycleRunId) && NonTerminal.Contains(Status);

    public string ActiveRunId => HasActiveRun ? LifecycleRunId : null;

    public bool IsRunning => Status == "RUNNING";
    public bool IsPaused => Status == "PAUSED";

    // ▶ while at rest / paused, ⏸ while running (acts as Pause). Matches ReplayTransportViewModel.
    public string PlayGlyph => IsRunning ? "⏸" : "▶";

    // ---- ▶ is always clickable; the decision encodes Start/Pause/Resume or a block reason. ----
    public bool CanPlayPause => true;

    public LiveAutoPlayDecision PlayPauseDecision()
    {
        // Double-press guard (TTWR): once a start is in flight and the run_id is not yet confirmed,
        // a 2nd ▶ must not fire a 2nd register→start.
        if (_startInFlight && ActiveRunId == null)
            return Blocked("LiveAuto start already in flight");

        // Active run: ▶ toggles Pause/Resume (graceful teardown not needed; these are immediate).
        if (ActiveRunId is string runId)
        {
            if (IsRunning) return new LiveAutoPlayDecision { Action = LiveAutoAction.Pause, RunId = runId };
            if (IsPaused) return new LiveAutoPlayDecision { Action = LiveAutoAction.Resume, RunId = runId };
            // LOADING / READY / STOPPING: transient, ignore the click (no stable action yet).
            return Blocked("run is transitioning (" + Status + ")");
        }

        // At rest / after terminal: ▶ starts a new run (re-arm). Build + gate the pre-flight.
        return new LiveAutoPlayDecision { Action = LiveAutoAction.Start, Start = BuildStartRequest() };
    }

    // Pre-flight for register→start (D3). Re-queries the strategy provider AT call time (supplyability
    // can flip if the editor went dirty after selection — CONTEXT "active strategy 選択", #29 parity).
    public LiveAutoStartRequest BuildStartRequest()
    {
        if (_startInFlight && ActiveRunId == null)
            return Gate(LiveAutoStartGate.BlockedDoublePress, "LiveAuto start already in flight");

        if (_strategy == null || !_strategy.TryGetStrategyFile(out string path) || string.IsNullOrEmpty(path))
            return Gate(LiveAutoStartGate.BlockedNoStrategy,
                "No saved strategy to run — open and save a strategy first.");

        string iid = ResolveInstrument();
        if (string.IsNullOrEmpty(iid))
            return Gate(LiveAutoStartGate.BlockedNoInstrument,
                "No instrument in the run universe — set the scenario universe first.");

        string venue = _venueIdentity();
        if (string.IsNullOrEmpty(venue))
            return Gate(LiveAutoStartGate.BlockedNoVenue, "Venue not connected.");

        return new LiveAutoStartRequest
        {
            Gate = LiveAutoStartGate.Ready,
            StrategyFile = path,
            // backcast's provider returns the real saved canonical .py (no cache/original split like
            // TTWR's StrategyBuffer), so original_path is that same path (AC2).
            OriginalPath = path,
            InstrumentId = iid,
            Venue = venue,
        };
    }

    // Instrument = SelectedSymbol if it's in the run universe, else the first universe entry
    // (TTWR check_live_auto_venue_and_instrument: prefer the focused symbol when it's in scenario).
    string ResolveInstrument()
    {
        IReadOnlyList<string> universe = _scenarioInstruments() ?? Array.Empty<string>();
        if (universe.Count == 0) return "";
        if (_selected != null && _selected.HasValue)
        {
            string sel = _selected.Value;
            for (int i = 0; i < universe.Count; i++)
                if (universe[i] == sel) return sel;
        }
        return universe[0];
    }

    // ---- guard lifecycle: the host reports when it fired a start and how it resolved. ----
    // Called right after the host fires register→start so a 2nd ▶ is blocked until the lifecycle
    // catches up to the started run_id or the start fails.
    public void NotifyStartIssued()
    {
        _startInFlight = true;
        _startedRunId = null;
    }

    // Called when the host's register→start RPC returns. On failure, release the guard so the user
    // can retry. On success, record the synchronous run_id; the guard then releases when the
    // lifecycle reflects THAT run_id (ObserveLifecycle) — keeping ▶ blocked through the window
    // between the RPC ack and the first lifecycle event (else ▶ would fire a 2nd run).
    public void NotifyStartResult(bool ok, string runId = null)
    {
        if (!ok) { _startInFlight = false; _startedRunId = null; return; }
        _startedRunId = runId;
    }

    // Release the guard once the lifecycle reflects the started run_id — REGARDLESS of status, so a
    // first event of ERROR (fast failure that skips the non-terminal states) releases too. Keyed on
    // the run_id (not "active") to distinguish the new run from a stale prior run's lingering event.
    // Idempotent — safe to call every frame after an event drain.
    public void ObserveLifecycle()
    {
        if (_startInFlight && _startedRunId != null && LifecycleRunId == _startedRunId)
        {
            _startInFlight = false;
            _startedRunId = null;
        }
    }

    static LiveAutoPlayDecision Blocked(string msg) =>
        new LiveAutoPlayDecision { Action = LiveAutoAction.None, Message = msg };

    static LiveAutoStartRequest Gate(LiveAutoStartGate gate, string msg) =>
        new LiveAutoStartRequest { Gate = gate, Message = msg };
}
