// FooterModeE2ERunner.cs — workspace footer（実行モード）サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// FooterModeE2ERunner.md）。第二波 2本目。`FooterLiveAutoVerify`（throwaway EditMode verify, Assets/Editor）から
// 昇格・改名（ADR-0015・findings 0055。先例 ScenarioStartup=findings 0054）。`FooterModeViewModel` の純決定ロジック
// （D1 poll authority / Replay-immediate / Live-lock / reject / D2 stop-then-switch / G1 auto-replay / visibility）の
// 実証済み Check body を温存し FOOTER-01〜10 として Covers 化、FOOTER-06/07 の view 反映を薄い uGUI section で追加。
// Python-FREE・実 root 不要。self-failing gate（全 Check pass で exit 0、1 つでも fail で exit 1）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FooterModeE2ERunner.Run -logFile <log>
//   # expect: [E2E FOOTER MODE PASS] ... / exit=0  （確認は Bash `grep -a "E2E FOOTER MODE"`。ripgrep/Select-String は取りこぼす）
//
// SUPPORTING PIN: `LiveAutoTransportViewModel`（▶ Start/Pause/Resume・double-press・G2）は Live 運転コントロール
// サーフェスの責務（footer-mode 外）。回帰網を落とさないため温存するが FOOTER Action 行には数えない。専用 runner
// 著述時に移送する。section ↔ Action ID は各 divider の `Covers:` 参照（E2E-CONVENTIONS.md「section ↔ Action ID 方針」）。
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class FooterModeE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    static int _pass, _fail;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; Debug.Log("[E2E FOOTER MODE] pass: " + what); }
        else { _fail++; Debug.LogError("[E2E FOOTER MODE] FAIL: " + what); }
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

    // Reflect a SettingsModeSegmentView's private _modeSegs (List<(Button btn, Text label, string mode)>):
    // ValueTuple fields Item1/Item3 are public, so default GetField flags find them. (#127/ADR-0026:
    // the segments moved from the footer to the Settings Mode section; same tuple shape.)
    static Button SegButton(System.Collections.IList segs, string mode)
    {
        foreach (var item in segs)
        {
            var t = item.GetType();
            var fMode = t.GetField("Item3");   // string mode
            var fBtn = t.GetField("Item1");    // Button
            if (fMode == null || fBtn == null) return null;  // tuple shape changed → null → presence Check below FAILs cleanly (not an NRE)
            if ((string)fMode.GetValue(item) == mode) return (Button)fBtn.GetValue(item);
        }
        return null;
    }
    static bool SegActive(System.Collections.IList segs, string mode)
    { var b = SegButton(segs, mode); return b != null && b.gameObject.activeSelf; }
    static bool SegInteractable(System.Collections.IList segs, string mode)
    { var b = SegButton(segs, mode); return b != null && b.interactable; }

    public static void Run()
    {
        // ============ FooterModeViewModel — D1 poll authority (Covers: FOOTER-01, FOOTER-02, FOOTER-03) ============
        var mode = new FooterModeViewModel();
        Check(mode.DisplayMode == FooterModeViewModel.Replay, "mode starts Replay (engine default)");

        mode.ApplyPoll(Poll("LiveAuto", "SUBSCRIBED"));
        Check(mode.DisplayMode == "LiveAuto" && mode.VenueLive, "poll LiveAuto/SUBSCRIBED → DisplayMode=LiveAuto, VenueLive");
        mode.ApplyPoll(Poll("Replay", "SUBSCRIBED"));
        Check(mode.DisplayMode == "Replay", "poll always overwrites display (D1): LiveAuto → Replay");

        // ---- segment visibility — VM side (Covers: FOOTER-07; view reflection added below) ----
        mode.ApplyPoll(Poll("Replay", "DISCONNECTED"));
        Check(mode.ShowReplaySegment && !mode.ShowManualAutoSegments, "venue down: Replay shown, Manual/Auto hidden");
        mode.ApplyPoll(Poll("Replay", "CONNECTED"));
        Check(mode.ShowReplaySegment && mode.ShowManualAutoSegments, "venue live: Manual/Auto shown");

        // ---- RequestMode: Live target needs a live venue, else observable no-op (Covers: FOOTER-04) ----
        var m2 = new FooterModeViewModel();
        m2.ApplyPoll(Poll("Replay", "DISCONNECTED"));
        Check(m2.RequestMode("LiveAuto", false).Kind == FooterModeRequestKind.BlockedVenueNotLive,
            "Live target while venue down → BlockedVenueNotLive (no RPC)");

        // ---- Live target while live: lock + await poll; poll catch-up releases the lock (Covers: FOOTER-02, FOOTER-03) ----
        m2.ApplyPoll(Poll("Replay", "CONNECTED"));
        var r = m2.RequestMode("LiveManual", false);
        Check(r.Kind == FooterModeRequestKind.SwitchLockedLive && m2.Locked, "Live target → SwitchLockedLive + Locked");
        m2.ApplyPoll(Poll("LiveManual", "CONNECTED"));
        Check(!m2.Locked && m2.DisplayMode == "LiveManual", "poll catches up to target → lock released");

        // ---- rejection path: synchronous ack failure releases the lock (Covers: FOOTER-08) ----
        m2.ApplyPoll(Poll("Replay", "CONNECTED"));
        m2.RequestMode("LiveAuto", false);
        Check(m2.Locked, "Live request locks");
        m2.NotifyModeResult(false);
        Check(!m2.Locked, "NotifyModeResult(false) releases lock (engine rejected)");

        // ---- Replay is immediate, D1 single deviation: engine can't reject (Covers: FOOTER-01) ----
        var m3 = new FooterModeViewModel();
        m3.ApplyPoll(Poll("LiveAuto", "CONNECTED"));
        var rr = m3.RequestMode("Replay", false);
        Check(rr.Kind == FooterModeRequestKind.SwitchImmediate && m3.DisplayMode == "Replay" && !m3.Locked,
            "Replay target → SwitchImmediate (flips now, no lock)");

        // ---- D2: leaving LiveAuto WITH an active run → StopRunThenSwitch, stop FIRST (Covers: FOOTER-05) ----
        var m4 = new FooterModeViewModel();
        m4.ApplyPoll(Poll("LiveAuto", "CONNECTED"));
        var leave = m4.RequestMode("Replay", hasActiveLiveAutoRun: true);
        Check(leave.Kind == FooterModeRequestKind.StopRunThenSwitch && leave.Target == "Replay" && m4.Locked,
            "leave LiveAuto w/ active run → StopRunThenSwitch (D2)");
        var leaveManual = m4.RequestMode("LiveManual", hasActiveLiveAutoRun: true);
        Check(leaveManual.Kind == FooterModeRequestKind.StopRunThenSwitch,
            "leave LiveAuto→LiveManual w/ active run also stops first (no orphan under Manual)");

        // ---- same-mode reselect is a no-op (Covers: FOOTER-10) ----
        var mi = new FooterModeViewModel();
        mi.ApplyPoll(Poll("Replay", "CONNECTED"));
        var ig = mi.RequestMode("Replay", false);
        Check(ig.Kind == FooterModeRequestKind.Ignore && !mi.Locked,
            "same mode reselect (Replay→Replay) → Ignore (no RPC, no lock)");

        // ============ SettingsModeSegmentView reflects VM state (Covers: FOOTER-06, FOOTER-07 view side) ============
        // #127 (ADR-0026): the mode segments moved from the footer to the Settings modal's Mode section.
        // The VM-side ShowManualAutoSegments is asserted above; here we pin that the real Settings VIEW
        // SetActive(false)s Manual/Auto when venue is down (FOOTER-07) and interactable=!locked on every
        // visible segment when a Live switch locks (FOOTER-06). Headless: build under a bare RectTransform
        // and reflect _modeSegs (no GPU — we read activeSelf/interactable, not pixels).
        var sgo = new GameObject("settings_mode_view_e2e", typeof(RectTransform));
        var fgo = new GameObject("footer_view_e2e", typeof(RectTransform));
        try
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var fvm = new FooterModeViewModel();
            var modeView = new SettingsModeSegmentView(fvm, null, font);
            modeView.Build((RectTransform)sgo.transform);

            var segs = (System.Collections.IList)typeof(SettingsModeSegmentView)
                .GetField("_modeSegs", BF).GetValue(modeView);

            // presence guard: all 3 mode segments exist (SetActive(false) keeps the GameObject in the list,
            // so this is true even when Manual/Auto are hidden). Without it the negative checks below
            // (!SegActive / !SegInteractable) would false-green on a removed/renamed segment.
            Check(SegButton(segs, FooterModeViewModel.Replay) != null
                && SegButton(segs, FooterModeViewModel.LiveManual) != null
                && SegButton(segs, FooterModeViewModel.LiveAuto) != null,
                "view: all 3 mode segments present in Settings (guards the negative checks below from false-green)");

            // venue down (poll-less default VM): Replay active, Manual/Auto SetActive(false).
            Check(SegActive(segs, FooterModeViewModel.Replay), "view: Replay segment active when venue down");
            Check(!SegActive(segs, FooterModeViewModel.LiveManual) && !SegActive(segs, FooterModeViewModel.LiveAuto),
                "FOOTER-07 view: Manual/Auto SetActive(false) when venue down (Settings Mode section)");

            // venue live → Manual/Auto shown.
            fvm.ApplyPoll(Poll("Replay", "CONNECTED"));
            modeView.Refresh();
            Check(SegActive(segs, FooterModeViewModel.LiveManual) && SegActive(segs, FooterModeViewModel.LiveAuto),
                "FOOTER-07 view: Manual/Auto shown when venue live (Settings Mode section)");

            // a locked Live switch disables every visible segment (2nd click can't race the engine answer).
            fvm.RequestMode("LiveManual", false);
            Check(fvm.Locked, "FOOTER-06 precondition: Live request locked the VM");
            modeView.Refresh();
            Check(!SegInteractable(segs, FooterModeViewModel.Replay),
                "FOOTER-06 view: visible segment interactable=false while locked (Settings Mode section)");

            // ============ FOOTER-13 (ADR-0026 re-home of U4): the footer is mode-STATUS-ONLY now. After
            // #127 it has NO mode segment buttons (moved to Settings) AND still no replay-transport controls
            // (retired by #76). So the built footer has ZERO Buttons — only the status Text.
            // NON-VACUITY: the Settings mode view above proves the segments DID move (not merely vanished).
            // RED litmus: re-add AddModeSeg(...) to WorkspaceFooterView.Build → a Button reappears → RED. ============
            var footerVm = new FooterModeViewModel();
            footerVm.ApplyPoll(Poll("Replay", "CONNECTED"));   // venue live: segments (if still here) would be visible
            var footer = new WorkspaceFooterView(footerVm, null, font);
            footer.Build((RectTransform)fgo.transform);
            var footerBtns = ((RectTransform)fgo.transform).GetComponentsInChildren<Button>(true);
            Check(footerBtns.Length == 0,
                "FOOTER-13 (ADR-0026): footer is status-only — NO buttons (mode segments → Settings; transport long retired). Found " + footerBtns.Length);
        }
        finally { UnityEngine.Object.DestroyImmediate(sgo); UnityEngine.Object.DestroyImmediate(fgo); }

        // ============ SUPPORTING PIN — LiveAutoTransportViewModel ▶ context (Live 運転コントロール surface;
        // NOT a FOOTER Action row — preserved so the regression net doesn't drop; migrates to that surface's
        // runner when authored). Footer台本 references this VM only as the `hasActiveLiveAutoRun` supplier. ============
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

        // ============ G1: venue-drop while LiveAuto run active → host MUST stop, not just Replay
        // (Covers: FOOTER-09 = the VM `ShouldAutoReplay`; run-survival check below is SUPPORTING PIN side) ============
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

        string summary = $"{_pass} pass / {_fail} fail";
        if (_fail == 0) Debug.Log("[E2E FOOTER MODE PASS] " + summary + " (FooterModeViewModel FOOTER-01..10 + view FOOTER-06/07 + re-homed FOOTER-13; LiveAuto pin)");
        else Debug.LogError("[E2E FOOTER MODE FAIL] " + summary);
        EditorApplication.Exit(_fail == 0 ? 0 : 1);
    }
}
