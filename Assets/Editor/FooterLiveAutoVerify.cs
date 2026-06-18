// FooterLiveAutoVerify.cs — headless EditMode verification of the issue-#39 Slice 3 footer logic.
//
// Runs the PURE decision logic (no Python engine) under `Unity -batchmode -executeMethod
// FooterLiveAutoVerify.Run`. Asserts the AC behaviour deterministically for the footer's mode
// segment (FooterModeViewModel) and the LiveAuto ▶ (LiveAutoTransportViewModel): D1 poll-authority
// + Replay-immediate / Live-lock, segment visibility, the context-sensitive ▶ (Start/Pause/Resume +
// re-arm + double-press guard), the D2 leave-LiveAuto stop-then-switch, and the two grill-mandated
// gates G1 (venue-drop while a LiveAuto run is active → the host MUST stop the run, not just flip to
// Replay) and G2 (start ok → a first lifecycle of ERROR still re-arms ▶ — no stuck guard). The
// engine-touching round-trip (real register→start→fill→teardown) is the MOCK/HITL in
// ProductionLiveShell; this proves the branch logic. (The footer ▶ that drove these VMs was retired in
// #76 S6b-β-clean — the LiveAuto VM logic stays for the Live epic's run controls; this gate pins it.)
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class FooterLiveAutoVerify
{
    static int _pass, _fail;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; Debug.Log("[FOOTER LIVEAUTO VERIFY PASS] " + what); }
        else { _fail++; Debug.LogError("[FOOTER LIVEAUTO VERIFY FAIL] " + what); }
    }

    // A minimal IStrategyFileProvider stub: supplyable returns a fixed path; otherwise false.
    sealed class FakeProvider : IStrategyFileProvider
    {
        public string Path;
        public bool Supplyable = true;
        public bool TryGetStrategyFile(out string path)
        {
            path = Supplyable ? Path : null;
            return Supplyable && !string.IsNullOrEmpty(Path);
        }
    }

    static string Poll(string mode, string venueState) =>
        "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"" + venueState + "\"}";

    static string Lifecycle(string runId, string status) =>
        "{\"LiveStrategyEvent\":{\"run_id\":\"" + runId + "\",\"strategy_id\":\"s1\",\"status\":\""
        + status + "\",\"ts_ms\":0}}";

    public static void Run()
    {
        // ============ FooterModeViewModel — D1 poll authority ============
        var mode = new FooterModeViewModel();
        Check(mode.DisplayMode == FooterModeViewModel.Replay, "mode starts Replay (engine default)");

        mode.ApplyPoll(Poll("LiveAuto", "SUBSCRIBED"));
        Check(mode.DisplayMode == "LiveAuto" && mode.VenueLive, "poll LiveAuto/SUBSCRIBED → DisplayMode=LiveAuto, VenueLive");
        mode.ApplyPoll(Poll("Replay", "SUBSCRIBED"));
        Check(mode.DisplayMode == "Replay", "poll always overwrites display (D1): LiveAuto → Replay");

        // ---- segment visibility (TTWR apply_venue_live_button_visibility_system) ----
        mode.ApplyPoll(Poll("Replay", "DISCONNECTED"));
        Check(mode.ShowReplaySegment && !mode.ShowManualAutoSegments, "venue down: Replay shown, Manual/Auto hidden");
        mode.ApplyPoll(Poll("Replay", "CONNECTED"));
        Check(mode.ShowReplaySegment && mode.ShowManualAutoSegments, "venue live: Manual/Auto shown");

        // ---- RequestMode: Live target needs a live venue (else observable no-op) ----
        var m2 = new FooterModeViewModel();
        m2.ApplyPoll(Poll("Replay", "DISCONNECTED"));
        Check(m2.RequestMode("LiveAuto", false).Kind == FooterModeRequestKind.BlockedVenueNotLive,
            "Live target while venue down → BlockedVenueNotLive (no RPC)");

        // ---- Live target while live: lock + await poll; poll catch-up releases the lock ----
        m2.ApplyPoll(Poll("Replay", "CONNECTED"));
        var r = m2.RequestMode("LiveManual", false);
        Check(r.Kind == FooterModeRequestKind.SwitchLockedLive && m2.Locked, "Live target → SwitchLockedLive + Locked");
        m2.ApplyPoll(Poll("LiveManual", "CONNECTED"));
        Check(!m2.Locked && m2.DisplayMode == "LiveManual", "poll catches up to target → lock released");

        // ---- rejection path: synchronous ack failure releases the lock ----
        m2.ApplyPoll(Poll("Replay", "CONNECTED"));
        m2.RequestMode("LiveAuto", false);
        Check(m2.Locked, "Live request locks");
        m2.NotifyModeResult(false);
        Check(!m2.Locked, "NotifyModeResult(false) releases lock (engine rejected)");

        // ---- Replay is immediate (D1 single deviation: engine can't reject) ----
        var m3 = new FooterModeViewModel();
        m3.ApplyPoll(Poll("LiveAuto", "CONNECTED"));
        var rr = m3.RequestMode("Replay", false);
        Check(rr.Kind == FooterModeRequestKind.SwitchImmediate && m3.DisplayMode == "Replay" && !m3.Locked,
            "Replay target → SwitchImmediate (flips now, no lock)");

        // ---- D2: leaving LiveAuto WITH an active run → StopRunThenSwitch (stop FIRST) ----
        var m4 = new FooterModeViewModel();
        m4.ApplyPoll(Poll("LiveAuto", "CONNECTED"));
        var leave = m4.RequestMode("Replay", hasActiveLiveAutoRun: true);
        Check(leave.Kind == FooterModeRequestKind.StopRunThenSwitch && leave.Target == "Replay" && m4.Locked,
            "leave LiveAuto w/ active run → StopRunThenSwitch (D2)");
        var leaveManual = m4.RequestMode("LiveManual", hasActiveLiveAutoRun: true);
        Check(leaveManual.Kind == FooterModeRequestKind.StopRunThenSwitch,
            "leave LiveAuto→LiveManual w/ active run also stops first (no orphan under Manual)");

        // ============ LiveAutoTransportViewModel — ▶ context ============
        var panel = new LivePanelViewModel();
        var sel = new SelectedSymbol();
        var provider = new FakeProvider { Path = "/x/strat.py", Supplyable = true };
        var universe = new List<string> { "7203.TSE", "8918.TSE" };
        var auto = new LiveAutoTransportViewModel(
            panel, provider, sel, () => universe, () => "MOCK");

        // at rest → Start, pre-flight Ready
        var d = auto.PlayPauseDecision();
        Check(d.Action == LiveAutoAction.Start && d.Start.Gate == LiveAutoStartGate.Ready, "no run → ▶ Start, pre-flight Ready");
        Check(d.Start.InstrumentId == "7203.TSE", "instrument defaults to first universe entry");
        Check(d.Start.StrategyFile == "/x/strat.py" && d.Start.OriginalPath == "/x/strat.py", "strategy path + original_path from provider");
        sel.Set("8918.TSE");
        Check(auto.BuildStartRequest().InstrumentId == "8918.TSE", "SelectedSymbol in universe → preferred over first");

        // pre-flight gates
        provider.Supplyable = false;
        Check(auto.BuildStartRequest().Gate == LiveAutoStartGate.BlockedNoStrategy, "non-supplyable provider → BlockedNoStrategy");
        provider.Supplyable = true;
        var emptyUni = new LiveAutoTransportViewModel(panel, provider, sel, () => new List<string>(), () => "MOCK");
        Check(emptyUni.BuildStartRequest().Gate == LiveAutoStartGate.BlockedNoInstrument, "empty universe → BlockedNoInstrument");
        var noVenue = new LiveAutoTransportViewModel(panel, provider, sel, () => universe, () => "");
        Check(noVenue.BuildStartRequest().Gate == LiveAutoStartGate.BlockedNoVenue, "no venue identity → BlockedNoVenue");

        // active run → ▶ Pause/Resume
        panel.Apply(Lifecycle("run-1", "RUNNING"));
        auto.ObserveLifecycle();
        var dp = auto.PlayPauseDecision();
        Check(dp.Action == LiveAutoAction.Pause && dp.RunId == "run-1" && auto.PlayGlyph == "⏸", "RUNNING → ▶ Pause (⏸)");
        panel.Apply(Lifecycle("run-1", "PAUSED"));
        Check(auto.PlayPauseDecision().Action == LiveAutoAction.Resume && auto.PlayGlyph == "▶", "PAUSED → ▶ Resume");
        panel.Apply(Lifecycle("run-1", "STOPPED"));
        Check(!auto.HasActiveRun && auto.PlayPauseDecision().Action == LiveAutoAction.Start, "STOPPED terminal → ▶ re-arm (Start)");

        // ---- double-press guard ----
        var g = new LiveAutoTransportViewModel(new LivePanelViewModel(), provider, sel, () => universe, () => "MOCK");
        g.NotifyStartIssued();
        Check(g.PlayPauseDecision().Action == LiveAutoAction.None, "start in flight → 2nd ▶ blocked (double-press guard)");
        g.NotifyStartResult(true, "run-2");
        Check(g.PlayPauseDecision().Action == LiveAutoAction.None, "ack ok but lifecycle not caught up → still blocked");

        // ============ G1: venue-drop while LiveAuto run active → host MUST stop (not just Replay) ============
        var mg = new FooterModeViewModel();
        mg.ApplyPoll(Poll("LiveAuto", "SUBSCRIBED"));
        var panelG = new LivePanelViewModel();
        panelG.Apply(Lifecycle("run-g", "RUNNING"));
        var ag = new LiveAutoTransportViewModel(panelG, provider, sel, () => universe, () => "MOCK");
        ag.ObserveLifecycle();
        mg.ApplyPoll(Poll("LiveAuto", "DISCONNECTED"));   // external venue drop, mode still LiveAuto
        Check(mg.ShouldAutoReplay, "G1: venue drop while LiveAuto → ShouldAutoReplay");
        // The teardown decision is the host's, but the two signals that FORCE it must both be present:
        // an active run survives the drop (engine does not stop it), so a correct host stops first.
        Check(ag.HasActiveRun && ag.ActiveRunId == "run-g",
            "G1: run survives venue drop (engine doesn't stop it) → host must stop_live_strategy, not just SetExecutionMode(Replay)");

        // ============ G2: start ok → first lifecycle is terminal ERROR → ▶ re-arms (no stuck guard) ============
        var panel2 = new LivePanelViewModel();
        var g2 = new LiveAutoTransportViewModel(panel2, provider, sel, () => universe, () => "MOCK");
        g2.NotifyStartIssued();
        g2.NotifyStartResult(true, "run-3");
        panel2.Apply(Lifecycle("run-3", "ERROR"));   // fast failure, never passed through a non-terminal state
        g2.ObserveLifecycle();
        Check(!g2.HasActiveRun && g2.PlayPauseDecision().Action == LiveAutoAction.Start,
            "G2: start ok → first event ERROR → guard releases, ▶ re-arms (not stuck on 'start in flight')");

        // sanity: normal release path (RUNNING) also clears the guard
        var panel3 = new LivePanelViewModel();
        var g3 = new LiveAutoTransportViewModel(panel3, provider, sel, () => universe, () => "MOCK");
        g3.NotifyStartIssued();
        g3.NotifyStartResult(true, "run-4");
        panel3.Apply(Lifecycle("run-4", "RUNNING"));
        g3.ObserveLifecycle();
        Check(g3.PlayPauseDecision().Action == LiveAutoAction.Pause, "guard releases on RUNNING → ▶ Pause");

        string summary = $"[FOOTER LIVEAUTO VERIFY] {_pass} pass / {_fail} fail";
        if (_fail == 0) Debug.Log(summary + " — ALL PASS"); else Debug.LogError(summary);
        EditorApplication.Exit(_fail == 0 ? 0 : 1);
    }
}
