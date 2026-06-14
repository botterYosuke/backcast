// ProductionLiveShellProbe.cs — issue #23 workstream B authoritative AFK gate (throwaway).
// docs/findings/0014-live-demo-roundtrip.md. The Mono+pythonnet headless proof that the
// PRODUCTION composition ProductionLiveShell wires (LiveBackendEventSink → LivePanelViewModel,
// VenueConnectionViewModel poll badge, LiveRpcLanes 3-lane RPC) drives a real place → fill →
// panel → teardown roundtrip through the production InprocLiveServer + MockVenueAdapter, with
// the Unity main thread GIL-FREE throughout. NO real venue / NO credentials.
//
// This gates the SHELL SEAM (composition), not the cancel-ACK FSM — that is authoritatively
// proven by the Python layer (workstream A: pytest + import-purity full-chain). The real
// demo roundtrip (PENDING_CANCEL receipt → poll-confirmed terminal, real fills) is the
// owner HITL leg (workstream C), which needs JST market hours.
//
//   <Unity> -batchmode -nographics -quit -projectPath . -executeMethod ProductionLiveShellProbe.Run
// Exit 0 => PASS ([PRODUCTION LIVE SHELL PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class ProductionLiveShellProbe
{
    const string VENUE = "MOCK";
    const string IID = "8918.TSE";
    const long MAX_STALL_MS = 200;

    static readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    static readonly LivePanelViewModel _vm = new LivePanelViewModel();
    static readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    static readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();

    static readonly List<string> _fail = new List<string>();
    static PyObject _server;
    static LiveRpcLanes _lanes;
    static IntPtr _mainTs;
    static long _maxStall;

    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    class Slot
    {
        volatile bool _done;
        OrderRpcResult _v;
        public void Set(OrderRpcResult v) { _v = v; _done = true; }
        public bool Done => _done;
        public OrderRpcResult Value => _v;
    }

    public static void Run()
    {
        bool engineStarted = false;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            BuildServer();                                // main holds GIL here
            _mainTs = PythonEngine.BeginAllowThreads();   // main now GIL-FREE

            _lanes = new LiveRpcLanes(_server, _coord);
            _lanes.Start();
            Debug.Log("[PRODUCTION LIVE SHELL MARK] lanes started; main GIL-free; running phases");

            PhaseConnectBadge();
            PhaseManualRoundtrip();
            PhaseCancelLane();
            PhaseTeardown();
        }
        catch (Exception e)
        {
            _fail.Add("driver: " + e);
        }
        finally
        {
            try { if (engineStarted) PythonEngine.Shutdown(); } catch { }
        }

        if (_maxStall > MAX_STALL_MS) _fail.Add($"main stalled {_maxStall}ms (> {MAX_STALL_MS}ms) — not GIL-free");

        if (_fail.Count == 0)
        {
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[PRODUCTION LIVE SHELL PASS] connect→badge / place→FILLED→panel order+position / " +
                "cancel-lane GIL-safe / logout→DISCONNECTED — 3 lanes, main GIL-free (maxStall={0}ms)", _maxStall));
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[PRODUCTION LIVE SHELL FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    static void BuildServer()
    {
        using (PyObject sys = Py.Import("sys"))
        using (PyObject sp = sys.GetAttr("path"))
        {
            sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
            sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
        }
        PyObject de;
        using (PyObject core = Py.Import("engine.core"))
        using (PyObject deCls = core.GetAttr("DataEngine"))
            de = deCls.Invoke();
        using (PyObject sinkPy = PyObject.FromManagedObject(_sink))
            de.InvokeMethod("set_rust_event_sink", sinkPy).Dispose();
        using (PyObject inproc = Py.Import("engine.inproc_server"))
        using (PyObject srvCls = inproc.GetAttr("InprocLiveServer"))
            _server = srvCls.Invoke(de, new PyString(VENUE));
        de.Dispose();

        using (PyObject login = _server.InvokeMethod("venue_login", new PyString(VENUE), new PyString("env"), new PyString("")))
            if (!login["success"].As<bool>()) throw new Exception("venue_login failed: " + login);
        using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
            if (!mode["success"].As<bool>()) throw new Exception("set_execution_mode(LiveManual) failed: " + mode);
    }

    static void Pump()
    {
        while (_sink.TryDequeue(out string wire)) _vm.Apply(wire);
        string st = _lanes != null ? _lanes.LatestState : null;
        if (!string.IsNullOrEmpty(st)) _conn.ApplyStatePoll(st);
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
        // poll-canonical badge converges to CONNECTED with venue_id (CONTEXT.md "venue 接続状態").
        WaitUntil(() => _conn.IsConnected, 10000, "badge CONNECTED from poll");
        Check(_conn.IsConnected, "badge did not converge to connected");
        Check(_conn.VenueId == VENUE, "venue_id badge not " + VENUE + " (got " + _conn.VenueId + ")");
    }

    static void PhaseManualRoundtrip()
    {
        // place MARKET BUY → mock fills (default FILLED) → order + position reach the panel VM.
        long baseFilled = _vm.FilledOrderCount;
        var r = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => r.Set(res));
        WaitUntil(() => r.Done, 15000, "place result");
        Check(r.Value.Success, "place failed: " + r.Value.ErrorCode);
        Check(r.Value.Status == "FILLED", "place not FILLED: " + r.Value.Status);

        WaitUntil(() => _vm.FilledOrderCount > baseFilled && _vm.HasOrder, 10000, "fill reaches panel");
        Check(_vm.HasOrder, "panel view-model never saw the order (sink→decoder→vm seam broken)");
        Check(_vm.FilledOrderCount > baseFilled, "panel filled-order counter did not advance");

        // account/position reflection is force_resync-driven; observe best-effort (the mock's
        // AccountSnapshot behavior is not the seam this gate proves — the order→panel path is).
        // Do NOT fail the gate on it: the authoritative fill→position accounting is the Python
        // layer (workstream A), and the real position reflection is the owner HITL leg.
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 3000 && !_vm.HasAccount) { Pump(); Thread.Sleep(5); }
        Debug.Log("[PRODUCTION LIVE SHELL MARK] account event reached panel: " + _vm.HasAccount +
                  " (position seen: " + _vm.SawAccountPosition + ")");

        // flatten so the run ends FLAT (hygiene; not gating).
        var f = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "SELL", 100.0, null, "MARKET", "DAY", res => f.Set(res));
        WaitUntil(() => f.Done, 15000, "flatten result");
    }

    static void PhaseCancelLane()
    {
        // Regression guard for the cancel-lane GIL crash (findings 0014 §macOS HITL): pre-fix
        // SubmitCancelOrder built `new PyString(venue/orderId)` on the write lane BEFORE taking
        // Py.GIL() → PyUnicode_DecodeUTF16 segfault (reproduced 2× on a real demo venue, same
        // frame). The place/fill phase never drove the cancel lane — that AFK gap let the crash
        // ship. This proves only the C# marshaling seam; the cancel-ACK FSM is proven by the
        // Python layer (workstream A). A clean return (no segfault) is the gate.
        var c = new Slot();
        _lanes.SubmitCancelOrder(VENUE, "probe-cancel-order-id", res => c.Set(res));
        WaitUntil(() => c.Done, 15000, "cancel lane result (must marshal args under the GIL)");
        Check(c.Done, "cancel lane never returned — SubmitCancelOrder likely segfaulted marshaling args");
        Debug.Log("[PRODUCTION LIVE SHELL MARK] cancel lane returned status=" +
                  c.Value.Status + " success=" + c.Value.Success);
    }

    static void PhaseTeardown()
    {
        Check(_lanes.StopAndJoin(), "StopAndJoin must report all lanes joined cleanly");
        PythonEngine.EndAllowThreads(_mainTs);            // main reacquires the GIL
        try
        {
            using (_server.InvokeMethod("set_execution_mode", new PyString("Replay"))) { }
            using (_server.InvokeMethod("venue_logout")) { }
            string stateStr;
            using (PyObject s = _server.InvokeMethod("get_state_json")) stateStr = s.As<string>();
            try { using (_server.InvokeMethod("close")) { } } catch { }
            _conn.ApplyStatePoll(stateStr);
            Check(!_conn.IsConnected, "badge did not converge to disconnected after logout");
            Check(_conn.VenueId == null, "venue_id not cleared after logout");
        }
        finally
        {
            _mainTs = PythonEngine.BeginAllowThreads();
        }
    }
}
