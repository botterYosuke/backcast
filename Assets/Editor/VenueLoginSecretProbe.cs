// VenueLoginSecretProbe.cs — issue #21 M4 authoritative AFK gate (throwaway)
// docs/findings/0012-venue-login-secret-flow.md (D2/D3/D5/D6/D7). The Mono+pythonnet
// analogue of spike.venue_login_secret.run_secret_smoke: it drives the PRODUCTION
// InprocLiveServer façade through the durable LiveRpcLanes (3 physical lanes), with a
// throwaway SecretMockAdapter injected (production MockVenueAdapter never emits
// SecretRequired). NO real venue / NO real credentials.
//
// Proves, with the Unity main thread GIL-FREE throughout:
//   1. SUCCESS: order-write lane blocks inside place_order → SecretRequired drains on
//      main (GIL-free) → secret typed into SecretModalController → submit_secret on the
//      SEPARATE urgent-secret lane → place returns FILLED. (lanes physically distinct.)
//   2. SECRET_TIMEOUT: never submit → error_code SECRET_TIMEOUT (NOT PLACE_TIMEOUT),
//      order never reached the venue (orphan-free).
//   3. SERIALIZATION: two places on the one write lane run strictly one-after-another
//      (place #2 is not prompted until place #1 resolves).
//   + while a write is in flight the logout coordinator forbids logout (D7 Wall 1).
//   + no plaintext secret in any drained wire event; modal char[] zeroized after submit.
//   + D7 teardown: stop lanes → venue_logout → get_state_json converges to DISCONNECTED.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod VenueLoginSecretProbe.Run
//
// Exit 0 => PASS ([VENUE LOGIN SECRET PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class VenueLoginSecretProbe
{
    const string VENUE = "MOCK";
    const string IID = "8918.TSE";
    const long MAX_STALL_MS = 200;
    const string SECRET = "9753-secret";   // known marker — must never leak

    static readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    static readonly LivePanelViewModel _vm = new LivePanelViewModel();
    static readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    static readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    static readonly SecretModalController _modal = new SecretModalController();

    static readonly List<string> _fail = new List<string>();
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static PyObject _server;
    static LiveRpcLanes _lanes;
    static IntPtr _mainTs;
    static long _maxStall;
    static bool _leaked;

    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }
    static double NowSec() => _clock.Elapsed.TotalSeconds;

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
        bool setupOk = false;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            BuildServer();                              // main holds GIL here
            _mainTs = PythonEngine.BeginAllowThreads(); // main now GIL-FREE
            setupOk = true;

            _lanes = new LiveRpcLanes(_server, _coord);
            _lanes.Start();
            Debug.Log("[VENUE LOGIN SECRET MARK] lanes started; main GIL-free; running phases");

            PhaseSuccess();
            PhaseSecretTimeout();
            PhaseSerialization();
            PhaseAuditAndTeardown();
        }
        catch (Exception e)
        {
            _fail.Add("driver: " + e);
        }
        finally
        {
            try
            {
                if (engineStarted)
                {
                    if (setupOk) PythonEngine.EndAllowThreads(_mainTs); // reacquire if not already
                }
            }
            catch { /* may already hold GIL from teardown */ }
            try { if (engineStarted) PythonEngine.Shutdown(); } catch { }
        }

        if (_maxStall > MAX_STALL_MS) _fail.Add($"main stalled {_maxStall}ms (> {MAX_STALL_MS}ms) — not GIL-free");
        if (_leaked) _fail.Add("plaintext secret leaked into a drained wire event");

        if (_fail.Count == 0)
        {
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[VENUE LOGIN SECRET PASS] secret roundtrip / SECRET_TIMEOUT / serialization / " +
                "logout-gate / no-leak — 3 lanes, main GIL-free (maxStall={0}ms)", _maxStall));
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[VENUE LOGIN SECRET FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    // main holds the GIL during BuildServer (right after Initialize).
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

        using (PyObject sm = Py.Import("spike.venue_login_secret.secret_mock"))
            _server = sm.InvokeMethod("build_secret_mock_server", de);
        de.Dispose();

        using (PyObject login = _server.InvokeMethod("venue_login", new PyString(VENUE), new PyString("env"), new PyString("")))
            if (!login["success"].As<bool>()) throw new Exception("venue_login failed: " + login);
        using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
            if (!mode["success"].As<bool>()) throw new Exception("set_execution_mode(LiveManual) failed: " + mode);
        // default submit_order outcome is FILLED — no arming needed (keeps main GIL-free mid-run).
    }

    // ---- main-thread, GIL-FREE coordination ---------------------------------------
    static void Pump()
    {
        while (_sink.TryDequeue(out string wire))
        {
            if (wire != null && wire.IndexOf(SECRET, StringComparison.Ordinal) >= 0) _leaked = true;
            _vm.Apply(wire);
        }
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

    // Type the secret into the modal and reply on the urgent-secret lane (D5).
    static void TypeAndSubmit(LiveSecretRequiredEvent req)
    {
        _modal.Open(req, NowSec());
        _coord.SetSecretModalOpen(true);
        foreach (char c in SECRET) _modal.AppendChar(c);
        Check(_modal.MaskedDisplay.Length == SECRET.Length, "mask length mismatch");
        char[] payload = _modal.Submit();
        _coord.SetSecretModalOpen(false);
        Check(_modal.BufferIsZeroed(), "modal buffer not zeroized after submit (D5)");
        _lanes.SubmitSecret(req.RequestId, payload, _ => { });
    }

    static void PhaseSuccess()
    {
        long baseS = _vm.SecretRequiredCount;
        var r = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => r.Set(res));

        WaitUntil(() => _vm.SecretRequiredCount > baseS, 15000, "phase1 SecretRequired");
        // Wall 1: while the write is in flight (blocked on the secret), logout is disabled.
        Check(!_coord.CanUserLogout, "logout must be disabled during in-flight write (D7 Wall 1)");
        var req = _vm.LatestSecretRequired;
        Check(req.Venue == VENUE && req.Kind == "second_secret", "SecretRequired fields wrong: " + req.Venue + "/" + req.Kind);

        TypeAndSubmit(req);
        WaitUntil(() => r.Done, 15000, "phase1 result");
        Check(r.Value.Success, "success leg failed: " + r.Value.ErrorCode);
        Check(r.Value.Status == "FILLED", "success leg not FILLED: " + r.Value.Status);
        Check(_coord.CanUserLogout, "logout should re-enable after write drained");
    }

    static void PhaseSecretTimeout()
    {
        long baseS = _vm.SecretRequiredCount;
        var r = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => r.Set(res));

        WaitUntil(() => _vm.SecretRequiredCount > baseS, 15000, "phase2 SecretRequired");
        // Do NOT submit the secret — the backend resolver must time out.
        WaitUntil(() => r.Done, 20000, "phase2 result");
        Check(r.Value.ErrorCode == "SECRET_TIMEOUT",
              "want SECRET_TIMEOUT (not PLACE_TIMEOUT), got: " + r.Value.ErrorCode);
    }

    static void PhaseSerialization()
    {
        long baseS = _vm.SecretRequiredCount;
        var s1 = new Slot();
        var s2 = new Slot();
        // Both queued on the single order-write lane; #2 must wait for #1.
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => s1.Set(res));
        _lanes.SubmitPlaceOrder(VENUE, IID, "SELL", 100.0, null, "MARKET", "DAY", res => s2.Set(res));

        WaitUntil(() => _vm.SecretRequiredCount > baseS, 15000, "phase3 #1 SecretRequired");
        var p1 = _vm.LatestSecretRequired;
        // #2 must NOT be prompted while #1 is still in flight (serialization on one lane).
        Check(_vm.SecretRequiredCount == baseS + 1,
              "place #2 prompted before #1 resolved — write lane not serialized");

        TypeAndSubmit(p1);
        WaitUntil(() => s1.Done, 15000, "phase3 #1 result");
        Check(s1.Value.Success, "serialized #1 failed: " + s1.Value.ErrorCode);

        WaitUntil(() => _vm.SecretRequiredCount > baseS + 1, 15000, "phase3 #2 SecretRequired");
        var p2 = _vm.LatestSecretRequired;
        TypeAndSubmit(p2);
        WaitUntil(() => s2.Done, 15000, "phase3 #2 result");
        Check(s2.Value.Success, "serialized #2 failed: " + s2.Value.ErrorCode);
    }

    static void PhaseAuditAndTeardown()
    {
        // D7: idle now → logout runs immediately; lanes stop; venue_logout under GIL.
        Check(_coord.CanUserLogout, "should be able to logout when idle");
        Check(_coord.RequestLogout(), "idle RequestLogout should be immediate");
        Check(_coord.ConsumePendingLogout(), "logout should be ready to tear down");

        _lanes.StopAndJoin();                       // joins all 3 lane threads
        PythonEngine.EndAllowThreads(_mainTs);      // main reacquires the GIL
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
            // leave main holding the GIL; Run()'s finally calls Shutdown(). Re-release
            // so the finally's EndAllowThreads is balanced.
            _mainTs = PythonEngine.BeginAllowThreads();
        }
    }
}
