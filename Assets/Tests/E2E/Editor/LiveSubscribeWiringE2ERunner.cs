// LiveSubscribeWiringE2ERunner.cs — #107 production-binding regression gate (台本: same-dir
// LiveSubscribeWiringE2ERunner.md). 方針: ADR-0022 / findings 0086.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//           -executeMethod LiveSubscribeWiringE2ERunner.Run -logFile /tmp/live_subscribe.log
//   # expect: [E2E LIVE SUBSCRIBE PASS] ... / exit=0  （確認は Bash `grep -a "E2E LIVE SUBSCRIBE"`）
//   # compile-only: -executeMethod を外した同コマンドで error CS\d+ 0 件。
//
// WHAT THIS GATES (the #107 death-zone): the subscribe CHAIN was complete but no PRODUCTION trigger
// started it. This runner drives the REAL production path on the REAL BackcastWorkspaceRoot composition
// (ComposeRoot → BuildWorkspace wires LiveSubscriptionCoordinator + LiveSubscribeHook), over a venue-free
// MOCK adapter, and asserts that selecting/adding/entering-Live causes market data to be subscribed and the
// board to RENDER (DepthDecoder.HasDepth=true). Full-stack: real subscribe RPC → engine → runner → mock
// adapter → depth poll → DepthDecoder (owner Q2: heavy/full-stack gate, not Python-free).
//
// LITMUS (delete-the-production-logic, AC#6):
//   * delete BulkSubscribeUniverse (rising-edge bulk)        → Section1 RED (entry universe never renders).
//   * delete the LiveSubscribeHook 代入 in BackcastWorkspaceRoot → Section2 RED (post-entry add never renders).
//   * the mock adapter's emit is SUBSCRIBE-GATED, so a NON-subscribed id never renders (Section3 negative
//     control proves the depth assertion is non-vacuous).
//
// CRITICAL: this test NEVER calls SubmitSubscribeMarketData / SubmitSubscribeMarketDataBatch itself — that
// was the original gate's blind spot (it self-subscribed). It only drives SelectRow / AddFromPicker / mode
// entry, i.e. the real UI trigger. Depth injection (mock_inject.emit_depth_for) is the venue WS stand-in,
// NOT a subscribe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class LiveSubscribeWiringE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string VENUE = "MOCK";

    static WorkspaceEngineHost s_host;
    static PyObject s_server;
    static PyObject s_mi;       // spike.live_adapter.mock_inject
    static int s_ts = 1;

    sealed class StubSp : IStrategyFileProvider
    {
        public bool TryGetStrategyFile(out string path) { path = null; return false; }
    }

    public static void Run()
    {
        string fail;
        try { fail = Drive(); }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E LIVE SUBSCRIBE PASS] bulk-on-entry + row-select + [+ Add] subscribe → board renders; "
                    + "unsubscribed id stays blank (non-vacuous); test never self-subscribed.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E LIVE SUBSCRIBE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Drive()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var sidebar = ty.GetField("_sidebarCtrl", BF)?.GetValue(root) as UniverseSidebarController;
        var subCoord = ty.GetField("_subCoord", BF)?.GetValue(root);
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root) as FooterModeViewModel;
        var driveFooter = ty.GetMethod("DriveFooter", BF);
        if (host == null || scenario == null || sidebar == null || footerMode == null || driveFooter == null)
            return "root seams not built (renamed?)";
        if (subCoord == null) return "LiveSubscriptionCoordinator not wired (BuildWorkspace _subCoord null) — production wiring missing";
        s_host = host;

        var universe = scenario.Universe;
        if (universe == null) return "scenario.Universe null";

        try
        {
            // ── setup: claim Python on THIS host, login MOCK, converge CONNECTED ──
            host.InitializePython(VENUE);
            if (!host.ServerReady) return "host not server-ready after InitializePython";
            s_server = typeof(WorkspaceEngineHost).GetField("_server", BF)?.GetValue(host) as PyObject;
            if (s_server == null) return "host _server PyObject not found (renamed?)";
            using (Py.GIL()) s_mi = Py.Import("spike.live_adapter.mock_inject");

            bool loginOk = false;
            host.VenueLogin(VENUE, "env", "", (ok, _) => loginOk = ok);
            if (!WaitUntil(() => loginOk, 10000)) return "venue login timed out";
            if (!loginOk) return "venue login failed";
            if (!WaitUntil(() => host.Conn.IsConnected, 10000)) return "badge did not converge to CONNECTED";

            // ── Section 1 (AC#1 / AC#5): bulk-subscribe on LiveManual rising edge ──
            // Seed the universe (acts as the user / a restore) WHILE still Replay, then enter LiveManual via the
            // engine. Driving the REAL DriveFooter feeds the poll's execution_mode to the coordinator, whose
            // rising edge bulk-subscribes the whole universe. NO select/add here — only bulk can satisfy it.
            const string A = "7203.TSE", B = "9984.TSE";
            universe.ReplaceAll(new List<string> { A, B });

            bool modeOk = false;
            host.SetExecutionMode(FooterModeViewModel.LiveManual, ok => modeOk = ok);
            if (!WaitUntil(() => modeOk, 10000)) return "SetExecutionMode(LiveManual) timed out";
            if (!modeOk) return "SetExecutionMode(LiveManual) rejected";
            // pump DriveFooter until the poll-canonical DisplayMode flips to LiveManual (the flip-call fires the
            // coordinator's rising edge → bulk subscribe).
            if (!WaitUntil(() => { driveFooter.Invoke(root, null); return footerMode.DisplayMode == FooterModeViewModel.LiveManual; }, 10000))
                return "DisplayMode did not converge to LiveManual";

            if (!EmitAndWaitDepth(A, 12000)) return "Section1: universe[0] (" + A + ") board did not render after LiveManual entry (bulk subscribe missing?)";
            if (!EmitAndWaitDepth(B, 12000)) return "Section1: universe[1] (" + B + ") board did not render after LiveManual entry (bulk subscribe missing?)";

            // ── Section 2 (AC#2): per-instrument subscribe via LiveSubscribeHook (row-select AND [+ Add]) ──
            // Both ids are added to membership AFTER the bulk edge, so bulk did NOT cover them — only the hook can.
            const string C = "6758.TSE", D = "4063.TSE";
            universe.Add(C);                                  // membership add (no auto-subscribe — Changed has no handler)
            sidebar.SelectRow(C, UniverseSourceMode.Live);    // row-select → LiveSubscribeHook → subscribe C
            if (!EmitAndWaitDepth(C, 12000)) return "Section2: row-selected " + C + " board did not render (LiveSubscribeHook not wired?)";

            sidebar.AddFromPicker(D, UniverseSourceMode.Live, new StubSp(), s_ts);  // [+ Add] → LiveSubscribeHook → subscribe D
            if (!EmitAndWaitDepth(D, 12000)) return "Section2: [+ Add] " + D + " board did not render ([+ Add] hook not wired?)";

            // ── Section 3 (non-vacuity / litmus floor): an id NEVER subscribed never renders. The mock adapter's
            // emit is subscribe-gated, so depth for "0000.TSE" is a no-op — if this rendered, every assertion above
            // would be vacuous. EmitAndWaitDepth returns false iff the board never appeared in the window. ──
            const string Z = "0000.TSE";
            if (EmitAndWaitDepth(Z, 1500)) return "Section3: unsubscribed " + Z + " rendered depth — assertions are vacuous (emit not subscribe-gated)";

            return null;
        }
        finally
        {
            try { s_host?.Stop(); } catch (Exception e) { Debug.LogWarning("[E2E LIVE SUBSCRIBE] host.Stop failed (non-fatal): " + e.Message); }
        }
    }

    // emit depth for `id` repeatedly (the venue WS stand-in) until the board renders or we time out. Re-emitting
    // is safe: it is subscribe-gated, so it only lands once the async subscribe has completed. Returns true iff
    // the board rendered within the window — positive sections assert true; the Section3 negative control asserts
    // false (an unsubscribed id never renders because the mock adapter's emit is subscribe-gated).
    static bool EmitAndWaitDepth(string id, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            EmitDepth(id);
            Pump();
            Thread.Sleep(20);
            if (HasDepth(id)) return true;
        }
        return false;
    }

    static void EmitDepth(string id)
    {
        int t = Interlocked.Increment(ref s_ts);
        using (Py.GIL())
        using (var pid = new PyString(id))
        using (var pi = new PyInt(t))
        using (var pb = new PyFloat(100.0))
        using (var pa = new PyFloat(101.0))
            s_mi.InvokeMethod("emit_depth_for", s_server, pid, pi, pb, pa).Dispose();
    }

    static bool HasDepth(string id)
    {
        string st = s_host.LatestStateJson;
        if (string.IsNullOrEmpty(st)) return false;
        return DepthDecoder.Decode(st, id).HasDepth;
    }

    static void Pump()
    {
        if (s_host == null) return;
        string st = s_host.LatestStateJson;
        if (!string.IsNullOrEmpty(st)) s_host.Conn.ApplyStatePoll(st);
    }

    static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (cond()) return true;
            Thread.Sleep(10);
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
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
