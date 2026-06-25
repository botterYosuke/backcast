// MenuBarVerify.cs — headless EditMode verification of the issue-#42 menu bar decision logic.
//
// Runs the PURE decision logic (no Python engine) so it executes in `Unity -batchmode
// -executeMethod MenuBarVerify.Run`. Asserts the AC behaviour: prod connect always enabled
// (ADR-0027: prod-allow env gate abolished — #130), File→New
// refuse-when-running / clear / guarded-LiveManual no-op, File→Open LiveAuto side-effect, and
// ScenarioStartupController.Clear(). The engine-touching mode round-trip (get_state_json) is
// the separate Play-mode MenuBarHitlHarness; this proves the branch logic deterministically.
using UnityEditor;
using UnityEngine;

public static class MenuBarVerify
{
    static int _pass, _fail;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; Debug.Log("[MENU BAR VERIFY PASS] " + what); }
        else { _fail++; Debug.LogError("[MENU BAR VERIFY FAIL] " + what); }
    }

    public static void Run()
    {
        var conn = new VenueConnectionViewModel();          // starts DISCONNECTED
        var coord = new LiveLogoutCoordinator();

        // ---- prod connect ALWAYS enabled (ADR-0027: prod-allow env gate abolished) ----
        // 旧挙動 (KABU_ALLOW_PROD / TACHIBANA_ALLOW_PROD でのグレーアウト) は廃止。prod variant は
        // env フラグ無しでも切断中なら常に enable。これが「Connect (Prod) を押しても無反応」の不具合修正の核。
        var vm = new VenueMenuViewModel(conn, coord);
        Check(vm.CanConnectEnv("TACHIBANA", "prod"), "tachibana prod connectable when disconnected (no env flag — ADR-0027)");
        Check(vm.CanConnectEnv("KABU", "prod"), "kabu prod connectable when disconnected (no env flag — ADR-0027)");
        Check(vm.CanConnectEnv("TACHIBANA", "demo"), "demo connectable while disconnected");
        Check(vm.CanConnectEnv("KABU", "verify"), "verify connectable while disconnected");
        Check(VenueMenuViewModel.ConnectVariants.Length == 4, "4 connect variants present");
        // delete-litmus: prod は CanConnect に収斂しただけ（常時 true ではない）。接続中は prod も無効。
        var connectedConn = new VenueConnectionViewModel();
        connectedConn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"KABU\"}");
        var connectedVm = new VenueMenuViewModel(connectedConn, coord);
        Check(!connectedVm.CanConnectEnv("KABU", "prod"), "prod NOT connectable once connected (follows CanConnect)");
        // Action-ID rollup tag (#130 / ADR-0027): prod-enable マイルストンに到達したら PASS を吐く。
        // prod チェックは先頭なので、ここで _fail==0 ならそれらは全て通っている（失敗時はタグ不在＝未到達）。
        if (_fail == 0) Debug.Log("[E2E PRODGATE-07 PASS] prod connect enabled without env flag (ADR-0027)");

        // ---- MenuBarViewModel decision logic ----
        string mode = "Replay"; bool autoRun = false, replayRun = false;
        var mb = new MenuBarViewModel(vm, conn, () => mode, () => autoRun, () => replayRun);

        // refuse while running (ADR-0001 safety) — both run signals independently.
        replayRun = true;
        Check(mb.FileNew(out _, out string r1) == FileNewDecision.RefusedRunning && !string.IsNullOrEmpty(r1), "File→New refused while replay running");
        replayRun = false; autoRun = true;
        Check(mb.FileNew(out _, out _) == FileNewDecision.RefusedRunning, "File→New refused while live-auto running");
        autoRun = false;

        // disconnected: clear, but the LiveManual switch is a gated no-op (TTWR observable no-op).
        Check(mb.FileNew(out string m1, out _) == FileNewDecision.ClearWorkspace && m1 == null, "File→New clears + mode no-op while disconnected");

        // connected: clear + guarded LiveManual.
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"MOCK\"}");
        Check(mb.LiveModeAllowed, "LiveModeAllowed once CONNECTED");
        Check(mb.FileNew(out string m2, out _) == FileNewDecision.ClearWorkspace && m2 == "LiveManual", "File→New sends LiveManual while connected");

        // File→Open mode side-effect: LiveAuto only when already Live (and connected).
        mode = "Replay";
        Check(mb.FileOpenModeSideEffect() == null, "File→Open no side-effect from Replay");
        mode = "LiveManual";
        Check(mb.FileOpenModeSideEffect() == "LiveAuto", "File→Open from LiveManual → LiveAuto");
        mode = "LiveAuto";
        Check(mb.FileOpenModeSideEffect() == "LiveAuto", "File→Open from LiveAuto → LiveAuto");
        conn.ApplyStatePoll("{\"venue_state\":\"DISCONNECTED\"}");
        mode = "LiveManual";
        Check(mb.FileOpenModeSideEffect() == null, "File→Open side-effect gated off when disconnected");

        // ---- ScenarioStartupController.Clear() (findings 0017 §4) ----
        var sc = new ScenarioStartupController();
        sc.Universe.ReplaceAll(new[] { "7203.TSE", "6758.TSE" });
        sc.SetStart("2025-01-01");
        Check(sc.Universe.Count == 2 && sc.Params.Dirty, "scenario seeded + dirty before clear");
        sc.Clear();
        Check(sc.Universe.Count == 0 && !sc.Params.Dirty && string.IsNullOrEmpty(sc.Params.Start), "scenario Clear() empties universe + resets buffer");

        string summary = $"[MENU BAR VERIFY] {_pass} pass / {_fail} fail";
        if (_fail == 0) Debug.Log(summary + " — ALL PASS"); else Debug.LogError(summary);
        EditorApplication.Exit(_fail == 0 ? 0 : 1);
    }
}
