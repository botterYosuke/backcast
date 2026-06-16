// WorkspaceLiveSeamProbe.cs — issue #23 re-home slice authoritative AFK gate (replaces
// ProductionLiveShellProbe). docs/findings/0014-live-demo-roundtrip.md RH5.
//
// The Mono + pythonnet headless proof that the LIVE SEAM the mainline workspace root now drives —
// WorkspaceEngineHost (LiveBackendEventSink → LivePanelViewModel, VenueConnectionViewModel poll
// badge, LiveRpcLanes 3-lane RPC) — carries a real connect → place → FILLED → panel → cancel-lane →
// teardown roundtrip through the production InprocLiveServer + MockVenueAdapter, with the Unity main
// thread GIL-FREE throughout. NO real venue / NO credentials. This proves the COMPOSITION SEAM the
// root wires (#23 re-home), not the cancel-ACK FSM — that is authoritatively proven by the Python
// layer (pytest + import-purity full-chain). The real demo roundtrip is the owner HITL leg.
//
//   <Unity> -batchmode -nographics -quit -projectPath . -executeMethod WorkspaceLiveSeamProbe.Run
// Exit 0 => PASS ([WORKSPACE LIVE SEAM PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class WorkspaceLiveSeamProbe
{
    const string VENUE = "MOCK";
    const string IID = "8918.TSE";
    const long MAX_STALL_MS = 200;

    static readonly List<string> _fail = new List<string>();
    static WorkspaceEngineHost _host;
    static long _maxStall;

    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    sealed class Slot
    {
        volatile bool _done;
        OrderRpcResult _v;
        public void Set(OrderRpcResult v) { _v = v; _done = true; }
        public bool Done => _done;
        public OrderRpcResult Value => _v;
    }

    public static void Run()
    {
        // Reset static accumulators so a second interactive Run() can't report phantom failures from a
        // prior run (the batchmode AFK gate is a single Run + Exit; this guards in-editor re-runs).
        _fail.Clear();
        _maxStall = 0;
        try
        {
            _host = new WorkspaceEngineHost();
            _host.InitializePython(VENUE);     // builds the persistent live-configured server + lanes; main GIL-free
            Debug.Log("[WORKSPACE LIVE SEAM MARK] host initialized; lanes polling; running phases");

            PhaseConnectBadge();
            PhaseManualRoundtrip();
            PhaseCancelLane();
            PhaseTeardown();
        }
        catch (Exception e)
        {
            _fail.Add("driver: " + e);
        }

        if (_maxStall > MAX_STALL_MS) _fail.Add($"main stalled {_maxStall}ms (> {MAX_STALL_MS}ms) — not GIL-free");

        if (_fail.Count == 0)
        {
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[WORKSPACE LIVE SEAM PASS] connect→badge / place→FILLED→panel order+position / " +
                "cancel-lane GIL-safe / teardown clean — host seam, main GIL-free (maxStall={0}ms)", _maxStall));
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[WORKSPACE LIVE SEAM FAIL]\n  - " + string.Join("\n  - ", _fail));
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // Drain push events into the Panel + converge the badge from the poll, exactly as the root does.
    static void Pump()
    {
        _host.DrainLiveEvents();
        string st = _host.LatestStateJson;
        if (!string.IsNullOrEmpty(st)) _host.Conn.ApplyStatePoll(st);
    }

    static bool WaitUntil(Func<bool> cond, int timeoutMs, string label)
    {
        var sw = Stopwatch.StartNew();
        long last = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (cond()) return true;
            long now = sw.ElapsedMilliseconds;
            long gap = now - last; last = now;
            if (gap > _maxStall) _maxStall = gap;
            Thread.Sleep(5);
        }
        _fail.Add($"timeout waiting: {label}");
        return false;
    }

    static void PhaseConnectBadge()
    {
        // login the MOCK venue through the host seam (login → set LiveManual), then the poll badge
        // converges to CONNECTED with venue_id (CONTEXT.md "venue 接続状態").
        var ok = false;
        _host.VenueLogin(VENUE, "env", "", (success, _) => ok = success);
        WaitUntil(() => ok, 10000, "venue_login ack");
        Check(ok, "venue_login failed");
        WaitUntil(() => _host.Conn.IsConnected, 10000, "badge CONNECTED from poll");
        Check(_host.Conn.IsConnected, "badge did not converge to connected");
        Check(_host.Conn.VenueId == VENUE, "venue_id badge not " + VENUE + " (got " + _host.Conn.VenueId + ")");
    }

    static void PhaseManualRoundtrip()
    {
        long baseFilled = _host.Panel.FilledOrderCount;
        var r = new Slot();
        _host.Lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => r.Set(res));
        WaitUntil(() => r.Done, 15000, "place result");
        Check(r.Value.Success, "place failed: " + r.Value.ErrorCode);
        Check(r.Value.Status == "FILLED", "place not FILLED: " + r.Value.Status);

        WaitUntil(() => _host.Panel.FilledOrderCount > baseFilled && _host.Panel.HasOrder, 10000, "fill reaches panel");
        Check(_host.Panel.HasOrder, "panel view-model never saw the order (sink→decoder→vm seam broken)");
        Check(_host.Panel.FilledOrderCount > baseFilled, "panel filled-order counter did not advance");

        // flatten so the run ends FLAT (hygiene; not gating).
        var f = new Slot();
        _host.Lanes.SubmitPlaceOrder(VENUE, IID, "SELL", 100.0, null, "MARKET", "DAY", res => f.Set(res));
        WaitUntil(() => f.Done, 15000, "flatten result");
    }

    static void PhaseCancelLane()
    {
        // Regression guard for the cancel-lane GIL crash (findings 0014 §macOS HITL): pre-fix
        // SubmitCancelOrder built new PyString(venue/orderId) on the write lane BEFORE Py.GIL() →
        // PyUnicode_DecodeUTF16 segfault. The place/fill phase never drove the cancel lane — that AFK
        // gap let the crash ship. A clean return (no segfault) is the gate; the cancel-ACK FSM itself
        // is proven by the Python layer.
        var c = new Slot();
        _host.Lanes.SubmitCancelOrder(VENUE, "probe-cancel-order-id", res => c.Set(res));
        WaitUntil(() => c.Done, 15000, "cancel lane result (must marshal args under the GIL)");
        Check(c.Done, "cancel lane never returned — SubmitCancelOrder likely segfaulted marshaling args");
        Debug.Log("[WORKSPACE LIVE SEAM MARK] cancel lane returned status=" + c.Value.Status + " success=" + c.Value.Success);
    }

    static void PhaseTeardown()
    {
        // The merged host teardown: force_stop + lanes/launcher join + server close, interpreter left
        // alive (ADR-0001). Idempotent + bounded; a clean TeardownComplete with no throw is the gate.
        _host.Stop();
        Check(_host.TeardownComplete, "host did not report TeardownComplete after Stop()");
        _host.Stop();   // idempotent: a second Stop must be a no-op (OnceGate), not a throw
        Check(_host.TeardownComplete, "host TeardownComplete regressed on a second Stop()");
    }
}
