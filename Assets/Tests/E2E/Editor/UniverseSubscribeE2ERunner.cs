// UniverseSubscribeE2ERunner.cs — ADR-0031 S4/S5 (#144/#145) release-gate slice runner (台本:
// same-dir UniverseSubscribeE2ERunner.md). 方針: ADR-0031 D6 + findings 0115 §S4/§S5.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod UniverseSubscribeE2ERunner.Run -logFile <abs>
//   # expect: [E2E UNIVERSE SUBSCRIBE PASS] / exit=0（確認は Bash `grep -a "UNIVERSE SUBSCRIBE"`）
//   # NOTE: the full-stack section runs MOCK Python → exit may be 139 (pythonnet shutdown segfault); the
//   #       verdict is the [E2E … PASS] tags (E2E-INDEX convention), NOT the process exit code.
//
// WHAT THIS GATES — ADR-0031 D6: membership change → subscription follows SYMMETRICALLY. A strategy
// cell's bt.universe.add(X) OR a UI [+ Add] mutates the registry → InstrumentRegistry.Changed →
// LiveSubscriptionCoordinator.OnUniverseChanged subscribes the FRESH id while in a Live mode (S4);
// a remove/clear unsubscribes (S5). This SUPERSEDES ADR-0022's "deliberately no universe-Changed
// auto-subscribe". Subscription stays SUBORDINATE to membership (ADR-0022 D3): a subscribe/unsubscribe
// failure NEVER changes the registry.
//
// 2-gate split: the COORDINATOR CONTRACT (mode-gating, fresh-only, dedup, unsubscribe, membership
// invariant) is gated by a Python-FREE recording-sink unit harness (UNISUB-01..06) — deterministic,
// fast. The REAL ROOT WIRING (registry.Changed actually drives the coordinator on the production
// BackcastWorkspaceRoot, with NO hook involved) is gated by a full-stack MOCK section over a venue-free
// MockVenueAdapter (UNISUB-07) — proving the seam I added (`_scenario.Universe.Changed += _subCoord.
// OnUniverseChanged`) is live.
//
// RED→GREEN litmus (findings 0115 §S4/§S5):
//   * delete OnUniverseChanged's BulkSubscribeUniverse call → UNISUB-01/07 RED (add never subscribes).
//   * delete the IsLive(_lastMode) gate → UNISUB-02 RED (Replay add wrongly subscribes).
//   * delete OnUniverseChanged's unsubscribe loop → UNISUB-04/05/08 RED (S5: remove never unsubscribes).
//   * delete the `Universe.Changed += _subCoord.OnUniverseChanged` wiring in BuildWorkspace → UNISUB-07/08 RED.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class UniverseSubscribeE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string VENUE = "MOCK";
    const string A = "7203.TSE", B = "9984.TSE", C = "6758.TSE";

    // Recording sink: captures the coordinator's subscribe/unsubscribe egress without Python.
    sealed class RecordingSink : ISubscribeSink
    {
        public readonly List<string> Subs = new List<string>();
        public readonly List<string> Unsubs = new List<string>();
        public bool Throw;
        public void Subscribe(string id) { if (Throw) throw new Exception("subscribe boom"); Subs.Add(id); }
        public void SubscribeBatch(IReadOnlyList<string> ids) { if (Throw) throw new Exception("batch boom"); Subs.AddRange(ids); }
        public void Unsubscribe(string id) { if (Throw) throw new Exception("unsubscribe boom"); Unsubs.Add(id); }
    }

    static WorkspaceEngineHost s_host;
    static PyObject s_server, s_mi;
    static int s_ts = 1;

    public static void Run()
    {
        string fail;
        try
        {
            fail = U01_LiveAdd_Subscribes()           // S4
                ?? U02_ReplayAdd_NoSubscribe()        // S4 mode-gate
                ?? U03_Dedup_NoResubscribe()          // S4 dedup
                ?? U04_LiveRemove_Unsubscribes()      // S5
                ?? U05_Clear_UnsubscribesAll()        // S5
                ?? U06_SubscribeFailure_KeepsMembership() // S4/S5 invariant
                ?? U07_RealRootWiring_FullStack();    // S4 real wiring (MOCK)
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E UNIVERSE SUBSCRIBE PASS] registry membership 変化 → 購読の対称追従（add→subscribe / remove・clear→"
                    + "unsubscribe）が InstrumentRegistry.Changed 起動で成立。Live のみ・dedup・購読失敗は membership 不可侵。"
                    + "実 root 配線も full-stack MOCK で確認（hook 非経由の registry.Add→subscribe→depth・registry.Remove→unsubscribe→feed 停止）。UNISUB-01..08。findings 0115 §S4/§S5。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E UNIVERSE SUBSCRIBE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── UNISUB-01 (S4): in a Live mode, a registry add (what bt.universe.add ultimately does — NOT a
    //   hook call) drives Changed → the new id is subscribed. ──
    static string U01_LiveAdd_Subscribes()
    {
        var (reg, sink, _) = Wire();
        Pump(reg, "LiveAuto");            // enter LiveAuto (empty universe → no entry bulk)
        reg.Add(A);                       // pure membership add → Changed → OnUniverseChanged
        if (!sink.Subs.Contains(A)) return "UNISUB-01: LiveAuto registry.Add(" + A + ") did not subscribe (Changed-driven subscribe missing)";
        if (sink.Subs.Count != 1) return "UNISUB-01: expected exactly 1 subscribe, got " + sink.Subs.Count;
        Debug.Log("[E2E UNISUB-01 PASS] LiveAuto registry.Add → subscribe via InstrumentRegistry.Changed (no hook).");
        return null;
    }

    // ── UNISUB-02 (S4): in Replay the same add does NOT subscribe (engine precondition-rejects; the
    //   bt.universe data join is S2). ──
    static string U02_ReplayAdd_NoSubscribe()
    {
        var (reg, sink, _) = Wire();
        Pump(reg, "Replay");
        reg.Add(A);
        if (sink.Subs.Count != 0) return "UNISUB-02: Replay registry.Add subscribed (" + string.Join(",", sink.Subs) + ") — must be Live-gated";
        Debug.Log("[E2E UNISUB-02 PASS] Replay registry.Add does NOT subscribe (Live-gated).");
        return null;
    }

    // ── UNISUB-03 (S4): an id already covered (entry bulk) is not re-subscribed on a later Changed. ──
    static string U03_Dedup_NoResubscribe()
    {
        var (reg, sink, _) = Wire();
        reg.ReplaceAll(new List<string> { A });
        Pump(reg, "LiveAuto");            // rising edge → bulk subscribes A
        if (!sink.Subs.Contains(A)) return "UNISUB-03: entry bulk did not subscribe A (precondition)";
        sink.Subs.Clear();
        reg.Add(B);                       // Changed → subscribe only B (A already subscribed)
        if (!sink.Subs.SequenceEqual(new[] { B })) return "UNISUB-03: expected only [B] subscribed on the add, got [" + string.Join(",", sink.Subs) + "] (dedup broke)";
        Debug.Log("[E2E UNISUB-03 PASS] only the FRESH id is subscribed on a later Changed (dedup vs entry bulk).");
        return null;
    }

    // ── UNISUB-04 (S5): in a Live mode, a registry remove unsubscribes the gone id. ──
    static string U04_LiveRemove_Unsubscribes()
    {
        var (reg, sink, _) = Wire();
        reg.ReplaceAll(new List<string> { A, B });
        Pump(reg, "LiveAuto");            // bulk subscribes A, B
        sink.Subs.Clear();
        reg.Remove(A);                    // Changed → unsubscribe A (gone), keep B
        if (!sink.Unsubs.Contains(A)) return "UNISUB-04: registry.Remove(" + A + ") did not unsubscribe (S5 missing)";
        if (sink.Unsubs.Contains(B)) return "UNISUB-04: survivor " + B + " was wrongly unsubscribed";
        if (sink.Subs.Count != 0) return "UNISUB-04: a remove must not subscribe anything (got " + string.Join(",", sink.Subs) + ")";
        Debug.Log("[E2E UNISUB-04 PASS] LiveAuto registry.Remove → unsubscribe the gone id (add↔remove symmetry).");
        return null;
    }

    // ── UNISUB-05 (S5): clear unsubscribes every subscribed id. ──
    static string U05_Clear_UnsubscribesAll()
    {
        var (reg, sink, _) = Wire();
        reg.ReplaceAll(new List<string> { A, B });
        Pump(reg, "LiveAuto");
        sink.Unsubs.Clear();
        reg.ReplaceAll(new List<string>());   // clear → Changed → unsubscribe all
        if (!(sink.Unsubs.Contains(A) && sink.Unsubs.Contains(B)))
            return "UNISUB-05: clear did not unsubscribe all (got [" + string.Join(",", sink.Unsubs) + "])";
        Debug.Log("[E2E UNISUB-05 PASS] clear → unsubscribe every id.");
        return null;
    }

    // ── UNISUB-06 (S4/S5 invariant): a subscribe/unsubscribe FAILURE never changes registry membership
    //   (subscription is subordinate — ADR-0022 D3 / #107). ──
    static string U06_SubscribeFailure_KeepsMembership()
    {
        var (reg, sink, _) = Wire();
        Pump(reg, "LiveAuto");
        sink.Throw = true;                 // the venue rejects every subscribe/unsubscribe
        try { reg.Add(A); } catch { /* the coordinator may surface, but membership must hold */ }
        if (!reg.Ids.Contains(A)) return "UNISUB-06: a subscribe failure removed " + A + " from the registry (membership 不可侵 broken)";
        try { reg.Remove(A); } catch { }
        if (reg.Ids.Contains(A)) return "UNISUB-06: remove failed to drop membership when unsubscribe threw (membership is owned by the registry, not the venue)";
        Debug.Log("[E2E UNISUB-06 PASS] subscribe/unsubscribe failure leaves registry membership intact (subordinate).");
        return null;
    }

    // ── UNISUB-07 (S4 real wiring): on the REAL BackcastWorkspaceRoot, in a Live mode, a PURE
    //   _scenario.Universe.Add(X) (NO SelectRow / NO [+ Add] → the hook is NOT invoked) subscribes X via
    //   the Changed wiring → the mock board renders. Isolates the seam I added from the hook. ──
    static string U07_RealRootWiring_FullStack()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "UNISUB-07: BackcastWorkspaceRoot missing in scene";
        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root) as FooterModeViewModel;
        var driveFooter = ty.GetMethod("DriveFooter", BF);
        var subCoord = ty.GetField("_subCoord", BF)?.GetValue(root);
        if (host == null || scenario == null || footerMode == null || driveFooter == null) return "UNISUB-07: root seams not built (renamed?)";
        if (subCoord == null) return "UNISUB-07: _subCoord null — production wiring missing";
        s_host = host;

        try
        {
            host.InitializePython(VENUE);
            if (!host.ServerReady) return "UNISUB-07: host not server-ready";
            s_server = typeof(WorkspaceEngineHost).GetField("_server", BF)?.GetValue(host) as PyObject;
            if (s_server == null) return "UNISUB-07: host _server not found";
            using (Py.GIL()) s_mi = Py.Import("spike.live_adapter.mock_inject");

            bool loginOk = false;
            host.VenueLogin(VENUE, "env", "", (ok, _) => loginOk = ok);
            if (!WaitUntil(() => loginOk, 10000) || !loginOk) return "UNISUB-07: venue login failed";
            if (!WaitUntil(() => host.Conn.IsConnected, 10000)) return "UNISUB-07: badge did not converge CONNECTED";

            // enter a Live mode (empty universe → entry bulk subscribes nothing) and pump the poll so the
            // coordinator's _lastMode is Live before the add.
            bool modeOk = false;
            host.SetExecutionMode(FooterModeViewModel.LiveManual, ok => modeOk = ok);
            if (!WaitUntil(() => modeOk, 10000) || !modeOk) return "UNISUB-07: SetExecutionMode(LiveManual) failed";
            if (!WaitUntil(() => { driveFooter.Invoke(root, null); return footerMode.DisplayMode == FooterModeViewModel.LiveManual; }, 10000))
                return "UNISUB-07: DisplayMode did not converge to LiveManual";

            // PURE registry add — NOT SelectRow / NOT AddFromPicker. The ONLY subscribe trigger is the
            // Changed wiring I added. If the board renders, that seam is live.
            scenario.Universe.Add(A);
            if (!EmitAndWaitDepth(A, 12000))
                return "UNISUB-07: pure registry.Add(" + A + ") board did not render — Universe.Changed→OnUniverseChanged wiring missing (hook NOT involved here)";
            Debug.Log("[E2E UNISUB-07 PASS] real root: pure _scenario.Universe.Add → subscribe via Changed wiring → board renders (no hook).");

            // ── UNISUB-08 (S5 real wiring): pure _scenario.Universe.Remove(A) → unsubscribe via Changed →
            //   the venue feed stops (the engine forgets A; the subscribe-gated mock emit no longer lands). ──
            scenario.Universe.Remove(A);
            if (!WaitUntilDepthGone(A, 12000))
                return "UNISUB-08: after registry.Remove(" + A + ") the board kept rendering — Universe.Changed→unsubscribe wiring missing (feed not stopped)";
            Debug.Log("[E2E UNISUB-08 PASS] real root: pure _scenario.Universe.Remove → unsubscribe via Changed wiring → feed stops.");
            return null;
        }
        finally
        {
            try { s_host?.Stop(); } catch (Exception e) { Debug.LogWarning("[E2E UNIVERSE SUBSCRIBE] host.Stop failed (non-fatal): " + e.Message); }
        }
    }

    // ---- helpers ----

    static readonly Dictionary<InstrumentRegistry, LiveSubscriptionCoordinator> s_coords = new Dictionary<InstrumentRegistry, LiveSubscriptionCoordinator>();

    static (InstrumentRegistry, RecordingSink, LiveSubscriptionCoordinator) Wire()
    {
        var reg = new InstrumentRegistry();
        var sink = new RecordingSink();
        var coord = new LiveSubscriptionCoordinator(sink, reg);
        reg.Changed += coord.OnUniverseChanged;   // mirrors BackcastWorkspaceRoot's production wiring
        s_coords[reg] = coord;
        return (reg, sink, coord);
    }

    // feed the coordinator the poll's execution_mode so _lastMode tracks the mode (drives the rising-edge
    // bulk + the IsLive gate). The coordinator is reached via the registry's Changed handler we wired.
    static void Pump(InstrumentRegistry reg, string mode) => s_coords[reg].OnModePoll(mode);

    static bool EmitAndWaitDepth(string id, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int t = Interlocked.Increment(ref s_ts);
            using (Py.GIL())
            using (var pid = new PyString(id))
            using (var pi = new PyInt(t))
            using (var pb = new PyFloat(100.0))
            using (var pa = new PyFloat(101.0))
                s_mi.InvokeMethod("emit_depth_for", s_server, pid, pi, pb, pa).Dispose();
            string st = s_host.LatestStateJson;
            if (!string.IsNullOrEmpty(st)) { s_host.Conn.ApplyStatePoll(st); if (DepthDecoder.Decode(st, id).HasDepth) return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            // pump the live state into Conn each iteration (edit-mode has no Update loop, so the badge
            // VM only advances when we ApplyStatePoll the latest snapshot — mirrors SUBWIRE's Pump()).
            if (s_host != null) { string st = s_host.LatestStateJson; if (!string.IsNullOrEmpty(st)) s_host.Conn.ApplyStatePoll(st); }
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    // Poll the live state until `id`'s depth is GONE (unsubscribe → engine forget_instrument drops it
    // from per_instrument). Do NOT emit here — the mock emit is subscribe-gated, and re-emitting during
    // the async unsubscribe race could transiently re-land depth. Non-vacuous: the caller proved HasDepth
    // was TRUE first (UNISUB-07).
    static bool WaitUntilDepthGone(string id, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            string st = s_host.LatestStateJson;
            if (!string.IsNullOrEmpty(st)) { s_host.Conn.ApplyStatePoll(st); if (!DepthDecoder.Decode(st, id).HasDepth) return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
