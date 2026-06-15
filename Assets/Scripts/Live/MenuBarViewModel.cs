// MenuBarViewModel.cs — issue #42 "menu bar（全体メニュー）" (DURABLE tier, pure logic)
//
// The presentation brain of the global menu bar (findings 0017). A PLAIN C# class (no UGUI)
// so the AFK probe drives every decision deterministically — the IMGUI/uGUI menu bar and the
// pythonnet RPCs are thin layers the composition root (ProductionLiveShell) wires on top.
//
// TTWR-faithful parity (ADR-0005): top-level File / Edit / Venue / Help. This VM owns the two
// menu-bar-specific behaviours — File→New/Open mode SIDE-EFFECTS and the Live-mode gate — and
// COMPOSES the reused VenueMenuViewModel (#21) for the Venue submenu (logic never reimplemented,
// AC③). File is LAYOUT (not strategy .py — that's #16); explicit mode/run pickers live in the
// footer (#39/#30). The host performs the in-memory File→New clear and all RPC/local ops; this
// VM only DECIDES (returns requests), so it stays pure.
using System;

// The pure decision for File→New (findings 0017 §4).
public enum FileNewDecision { ClearWorkspace, RefusedRunning }

public sealed class MenuBarViewModel
{
    readonly VenueMenuViewModel _venue;
    readonly VenueConnectionViewModel _conn;
    readonly Func<string> _currentMode;       // engine execution_mode: "Replay"|"LiveManual"|"LiveAuto"
    readonly Func<bool> _isLiveAutoRunning;
    readonly Func<bool> _isReplayRunning;

    public MenuBarViewModel(
        VenueMenuViewModel venue,
        VenueConnectionViewModel conn,
        Func<string> currentMode,
        Func<bool> isLiveAutoRunning,
        Func<bool> isReplayRunning)
    {
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _currentMode = currentMode ?? (() => "Replay");
        _isLiveAutoRunning = isLiveAutoRunning ?? (() => false);
        _isReplayRunning = isReplayRunning ?? (() => false);
    }

    // The reused venue submenu — the menu bar COMPOSES it, never reimplements venue logic (AC③).
    public VenueMenuViewModel Venue => _venue;

    // gap② (findings 0017 §3): a Live execution mode may be SET only while the venue is
    // CONNECTED/SUBSCRIBED. Sending otherwise raises EXECUTION_MODE_PRECONDITION in ModeManager;
    // gating here reproduces TTWR's observable no-op instead of surfacing an exception.
    public bool LiveModeAllowed =>
        _conn.VenueState == "CONNECTED" || _conn.VenueState == "SUBSCRIBED";

    // Some run is in flight — File→New is refused (ADR-0001 safety: never leave a blank
    // workspace over a live order pump / running replay; teardown is #39/#30's job).
    public bool IsRunning => _isLiveAutoRunning() || _isReplayRunning();

    // ---- File→New. The host performs the in-memory clear when ClearWorkspace; `modeRequest`
    // is the SetExecutionMode the host sends AFTER clearing — null when the venue isn't
    // connected (observable no-op). The clear is unconditional; only the LiveManual switch is
    // gated (TTWR semantics). ----
    public FileNewDecision FileNew(out string modeRequest, out string refuseMessage)
    {
        modeRequest = null;
        refuseMessage = null;
        if (IsRunning)
        {
            refuseMessage = "Stop the running strategy / replay before New.";
            return FileNewDecision.RefusedRunning;
        }
        if (LiveModeAllowed) modeRequest = "LiveManual";
        return FileNewDecision.ClearWorkspace;
    }

    // ---- File→Open mode side-effect. TTWR: opening (a layout) WHILE already Live transitions
    // to LiveAuto. Returns the mode the host sends BEFORE loading the layout, or null. (Being in
    // a Live mode implies connected, but LiveModeAllowed is checked for safety.) ----
    public string FileOpenModeSideEffect()
    {
        string mode = _currentMode();
        bool live = mode == "LiveManual" || mode == "LiveAuto";
        return (live && LiveModeAllowed) ? "LiveAuto" : null;
    }
}
