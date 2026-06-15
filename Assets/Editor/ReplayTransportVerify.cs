// ReplayTransportVerify.cs — headless EditMode verification of the issue-#30 transport logic.
//
// Runs the PURE decision logic (no Python engine) under `Unity -batchmode -executeMethod
// ReplayTransportVerify.Run`. Asserts the AC behaviour deterministically: lifecycle fusion
// (poll Idle/Loaded/Running/Paused + launcher Done/Failed terminal authority), the context-
// sensitive ▶ (Run/Pause/Resume + re-arm), step PAUSED-only, stop/speed enablement, and the
// [1,2,5,10,50] speed options. The engine-touching round-trip (pause/step/speed actually moving
// bars) is the separate Play-mode HITL in ScenarioStartupHitlHarness; this proves the branch
// logic. Mirrors MenuBarVerify (findings 0017 §8).
using UnityEditor;
using UnityEngine;

public static class ReplayTransportVerify
{
    static int _pass, _fail;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; Debug.Log("[REPLAY TRANSPORT VERIFY PASS] " + what); }
        else { _fail++; Debug.LogError("[REPLAY TRANSPORT VERIFY FAIL] " + what); }
    }

    public static void Run()
    {
        // ---- lifecycle fusion: poll drives Idle/Loaded/Running/Paused ----
        var lc = new ReplayLifecycle();
        Check(lc.Phase == ReplayPhase.Idle, "lifecycle starts Idle");
        lc.ApplyPoll("LOADED"); Check(lc.Phase == ReplayPhase.Loaded, "poll LOADED → Loaded");
        lc.ApplyPoll("RUNNING"); Check(lc.Phase == ReplayPhase.Running, "poll RUNNING → Running");
        lc.ApplyPoll("PAUSED"); Check(lc.Phase == ReplayPhase.Paused, "poll PAUSED → Paused");

        // terminal authority: launcher Done/Failed wins over poll (which goes IDLE after force_stop).
        lc.MarkDone();
        lc.ApplyPoll("IDLE");
        Check(lc.Phase == ReplayPhase.Done, "launcher Done latches over poll IDLE (terminal authority)");
        var lcf = new ReplayLifecycle();
        lcf.ApplyPoll("RUNNING");
        lcf.MarkFailed("boom");
        Check(lcf.Phase == ReplayPhase.Failed && lcf.FailureMessage == "boom", "launcher Failed latches + message");

        // re-arm clears the terminal latch.
        lc.Rearm();
        Check(lc.Phase == ReplayPhase.Idle && !lc.IsTerminal, "re-arm clears terminal latch → Idle");

        // ---- transport VM: context-sensitive ▶ ----
        var vm = new ReplayTransportViewModel(lc);
        Check(vm.PlayPauseIntent() == ReplayTransportIntent.Run && vm.PlayGlyph == "▶", "Idle ▶ → Run (re-arm)");
        lc.ApplyPoll("RUNNING");
        Check(vm.PlayPauseIntent() == ReplayTransportIntent.Pause && vm.PlayGlyph == "⏸", "Running ▶ → Pause (⏸ glyph)");
        lc.ApplyPoll("PAUSED");
        Check(vm.PlayPauseIntent() == ReplayTransportIntent.Resume && vm.PlayGlyph == "▶", "Paused ▶ → Resume");
        lc.Rearm(); lc.MarkDone();
        Check(vm.PlayPauseIntent() == ReplayTransportIntent.Run, "Done ▶ → Run (re-arm after terminal)");

        // ---- button enablement ----
        var lc2 = new ReplayLifecycle();
        var vm2 = new ReplayTransportViewModel(lc2);
        lc2.ApplyPoll("PAUSED");
        Check(vm2.CanStep && vm2.CanStop && vm2.CanSpeed, "Paused: step/stop/speed enabled");
        lc2.ApplyPoll("RUNNING");
        Check(!vm2.CanStep && vm2.CanStop && vm2.CanSpeed, "Running: step DISABLED, stop/speed enabled");
        lc2.ApplyPoll("IDLE");
        Check(!vm2.CanStep && !vm2.CanStop && !vm2.CanSpeed, "Idle: step/stop/speed all disabled");
        lc2.Rearm(); lc2.MarkDone();
        Check(!vm2.CanStep && !vm2.CanStop && !vm2.CanSpeed, "Done (terminal): transport disabled");

        // ---- speed options + selection ----
        Check(ReplayTransportViewModel.SpeedOptions.Length == 5
              && ReplayTransportViewModel.SpeedOptions[0] == 1
              && ReplayTransportViewModel.SpeedOptions[4] == 50, "speed options = [1,2,5,10,50] (TTWR parity)");
        var lc3 = new ReplayLifecycle();
        var vm3 = new ReplayTransportViewModel(lc3);
        Check(!vm3.SelectSpeed(10) && vm3.CurrentSpeed == 1, "speed ignored while no live run (Idle)");
        lc3.ApplyPoll("RUNNING");
        Check(vm3.SelectSpeed(10) && vm3.CurrentSpeed == 10, "speed 10 applied while Running");
        Check(!vm3.SelectSpeed(10), "same speed → no re-send");
        Check(!vm3.SelectSpeed(3), "unknown multiplier rejected");
        vm3.OnRunStarted();
        Check(vm3.CurrentSpeed == 1, "OnRunStarted resets to 1x (engine fresh-run default)");

        string summary = $"[REPLAY TRANSPORT VERIFY] {_pass} pass / {_fail} fail";
        if (_fail == 0) Debug.Log(summary + " — ALL PASS"); else Debug.LogError(summary);
        EditorApplication.Exit(_fail == 0 ? 0 : 1);
    }
}
