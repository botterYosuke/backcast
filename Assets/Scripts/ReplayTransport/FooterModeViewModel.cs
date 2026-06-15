// FooterModeViewModel.cs — issue #39 Slice 3 "footer mode segment" (DURABLE tier, pure logic)
//
// The presentation brain of the footer's Replay / LiveManual / LiveAuto segment toggle
// (findings 0025 §3 D1/D2). A PLAIN C# class (no UGUI, no pythonnet) so the AFK probe drives
// every decision deterministically; the uGUI footer view raises segment clicks and the
// composition root (ProductionLiveShell) marshals SetExecutionMode (and, for D2, the prior
// stop_live_strategy) over the GIL. This VM only DECIDES; it never touches Python.
//
// D1 — mode truth source = optimistic + poll answer-check (poll value always overwrites):
//   * Replay (engine can NEVER reject it) switches the display IMMEDIATELY — the single,
//     recorded deviation from TTWR's pure poll-canonical model (findings 0025 §3 D1): immediate
//     reflection can't desync because the poll returns the same value. Speed/step (the #30
//     surface) share this rationale.
//   * Live (LiveManual/LiveAuto) CAN be rejected (ModeManager EXECUTION_MODE_PRECONDITION), so it
//     LOCKS (spinner) on click and waits for the poll/ack — structurally no UI↔engine desync.
// The poll's execution_mode is already authoritative (engine core.py get_current_state populates
// it from mode_manager.current_mode); the gap was C#-side — VenueConnectionViewModel parses only
// venue_state/venue_id by design, so THIS VM parses execution_mode from the same poll string with
// its own minimal DTO, leaving the tested venue VM untouched (findings 0025 §2).
//
// D2 — leaving LiveAuto with an active run must stop the run FIRST (backcast adds a teardown
// TTWR's footer lacks; justified by the orphan-absence invariant, findings 0017 §4 / ADR-0001).
// This VM recognizes that case (StopRunThenSwitch) given whether a LiveAuto run is active; the
// host then orchestrates stop_live_strategy(run_id) → SetExecutionMode and only switches on stop
// success (otherwise stays in LiveAuto + error).

using System;
using UnityEngine;

public enum FooterModeRequestKind
{
    Ignore,             // target == current display (no-op)
    SwitchImmediate,    // Replay: engine can't reject → flip display now + send SetExecutionMode
    SwitchLockedLive,   // Live target: lock + spinner, send SetExecutionMode, await poll/ack
    StopRunThenSwitch,  // leaving LiveAuto with an active run: stop_live_strategy FIRST (D2)
    BlockedVenueNotLive,// Live target while venue not connected → TTWR observable no-op (no send)
}

public struct FooterModeRequest
{
    public FooterModeRequestKind Kind;
    public string Target;     // "Replay" | "LiveManual" | "LiveAuto"
    public string Message;    // footer status / block reason
}

public sealed class FooterModeViewModel
{
    public const string Replay = "Replay";
    public const string LiveManual = "LiveManual";
    public const string LiveAuto = "LiveAuto";

    [System.Serializable]
    class ModeDto
    {
        public string execution_mode;
        public string venue_state;
    }

    // Optimistic display, ALWAYS overwritten by the poll (D1). Seeded to Replay (engine default).
    public string DisplayMode { get; private set; } = Replay;

    // A Live transition is in flight (clicked, awaiting poll/ack). The footer shows a spinner and
    // disables re-clicking the segments while locked.
    public bool Locked { get; private set; }
    public string PendingTarget { get; private set; }

    // Venue liveness, tracked from the poll's venue_state (CONNECTED/SUBSCRIBED/RECONNECTING).
    public bool VenueLive { get; private set; }

    // Segment visibility (TTWR apply_venue_live_button_visibility_system): Replay always shows;
    // Manual/Auto only while the venue is live.
    public bool ShowReplaySegment => true;
    public bool ShowManualAutoSegments => VenueLive;

    // Set when the poll shows the venue dropped non-live while still in a Live mode — the host must
    // fall back to Replay (TTWR auto_replay_on_venue_disconnect_system). CRITICAL (findings 0025 §3
    // D2): a venue drop does NOT stop a running LiveAuto run — live_orchestrator._publish_venue_logout
    // only emits VenueLogoutDetected (no _teardown_live_components, no stop_live_strategy; the same
    // callback serves the kabu watchdog and the Tachibana SS hook). So auto-replay is NOT symmetric
    // with "the session tore the run down" — the host must treat it EXACTLY like a user-initiated
    // leave (D2): if liveAutoVm.HasActiveRun, stop_live_strategy(ActiveRunId) FIRST, then
    // SetExecutionMode(Replay) (graceful cancel is best-effort since the venue is gone, but the
    // local teardown still lands). Skipping the stop leaves a zombie run under a Replay display —
    // the exact orphan D2 forbids — that revives to Pause/Resume on reconnect. One-shot: consume clears.
    public bool ShouldAutoReplay { get; private set; }

    // ---- poll ingest: D1 overwrite + lock release + auto-replay detection. ----
    public void ApplyPoll(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return;
        ModeDto d;
        try { d = JsonUtility.FromJson<ModeDto>(stateJson); }
        catch { return; }
        if (d == null) return;

        string polled = string.IsNullOrEmpty(d.execution_mode) ? Replay : d.execution_mode;
        VenueLive = d.venue_state == "CONNECTED" || d.venue_state == "SUBSCRIBED"
                 || d.venue_state == "RECONNECTING";

        // D1: the poll is the authority — always overwrite the optimistic display.
        DisplayMode = polled;

        // A locked Live transition resolves once the poll catches up to the target (success) — a
        // rejection is surfaced synchronously via NotifyModeResult(false) instead.
        if (Locked && polled == PendingTarget)
        {
            Locked = false;
            PendingTarget = null;
        }

        // Venue dropped while still Live → ask the host to fall back to Replay (TTWR parity).
        if (!VenueLive && (polled == LiveManual || polled == LiveAuto))
            ShouldAutoReplay = true;
    }

    public bool ConsumeAutoReplay()
    {
        if (!ShouldAutoReplay) return false;
        ShouldAutoReplay = false;
        return true;
    }

    // ---- segment click decision. `hasActiveLiveAutoRun` comes from LiveAutoTransportViewModel
    // (HasActiveRun) so this VM stays single-responsibility and purely testable. ----
    public FooterModeRequest RequestMode(string target, bool hasActiveLiveAutoRun)
    {
        if (string.IsNullOrEmpty(target) || target == DisplayMode)
            return new FooterModeRequest { Kind = FooterModeRequestKind.Ignore, Target = target };

        bool targetLive = target == LiveManual || target == LiveAuto;

        // Live target requires a connected venue (TTWR precondition: warn-only, no RPC).
        if (targetLive && !VenueLive)
            return new FooterModeRequest
            {
                Kind = FooterModeRequestKind.BlockedVenueNotLive,
                Target = target,
                Message = "Connect a venue before switching to a live mode.",
            };

        // Leaving LiveAuto with an active run → stop the run FIRST (D2), to either Replay OR
        // LiveManual (a live auto loop must not keep firing under a Manual/Replay display).
        if (DisplayMode == LiveAuto && target != LiveAuto && hasActiveLiveAutoRun)
        {
            Locked = true;
            PendingTarget = target;   // display stays LiveAuto until stop+switch land (poll overwrites)
            return new FooterModeRequest
            {
                Kind = FooterModeRequestKind.StopRunThenSwitch,
                Target = target,
                Message = "Stopping LiveAuto run…",
            };
        }

        // Live target (no active-run teardown needed): lock + await poll/ack — do NOT optimistically
        // flip the display (it can be rejected).
        if (targetLive)
        {
            Locked = true;
            PendingTarget = target;
            return new FooterModeRequest { Kind = FooterModeRequestKind.SwitchLockedLive, Target = target };
        }

        // Replay: engine can't reject → flip the display immediately (D1 deviation).
        DisplayMode = Replay;
        return new FooterModeRequest { Kind = FooterModeRequestKind.SwitchImmediate, Target = Replay };
    }

    // The host calls this with the SetExecutionMode (or stop+switch) RPC result. On failure, release
    // the lock so the segments are usable again; the poll keeps the authoritative display.
    public void NotifyModeResult(bool ok)
    {
        if (!ok)
        {
            Locked = false;
            PendingTarget = null;
        }
    }
}
