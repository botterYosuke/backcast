// VenueLoginSecretHitlHarness.cs — issue #21 "Venue login and secret flow"
// (THROWAWAY owner-manual playmode HITL leg) — docs/findings/0012 D3.
//
// The owner-only, DEFAULT-DISABLED real-venue leg that complements the headless AFK gate
// (VenueLoginSecretProbe, mock venue). The probe proves the 3-lane secret seam under
// -batchmode with a throwaway mock; THIS harness connects a REAL venue with REAL demo
// credentials so the owner can watch connect → place → SecretRequired → modal → submit →
// fill → logout end-to-end on a real Unity frame.
//
// It reuses the SAME durable pieces the production Live panel will: LiveRpcLanes (3 lanes),
// VenueConnectionViewModel (poll badge), SecretModalController (char[] keyboard drain),
// VenueMenuViewModel (connect/disconnect gating). Credentials are collected by the Python
// login_dialog_runner tkinter subprocess (D4) — this harness builds NO credential form.
//
// PLAY OWNERSHIP: spawned ONLY via Tools > Backcast > Venue Login Secret HITL (Tachibana
// demo / Kabu verify); never auto-bootstraps, so it never collides with the single Play
// owner. kabu requires Windows + a running kabuステーション本体 (platform-inapplicable on
// Mac). Mirrors LiveAdapterTracerHitlHarness GIL discipline: workers take the GIL, main
// renders GIL-free.
//
// HITL STEPS (record the outcome in findings 0012 §実証結果):
//   1. Tools menu → pick venue. The tkinter login dialog opens (enter demo credentials).
//   2. Badge turns "Connected: <VENUE>". Press [Place test order].
//   3. (tachibana) the secret modal opens — type the second password, press Submit.
//   4. The order fills (status FILLED). (kabu) NO secret modal ever appears.
//   5. Press [Disconnect] (disabled while a write/secret is in flight) → badge Disconnected.
using System;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class VenueLoginSecretHitlHarness : MonoBehaviour
{
    const string IID = "8918.TSE";

    // set by the Tools menu before the GameObject is created.
    public static string TargetVenue = "TACHIBANA";

    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    readonly LivePanelViewModel _vm = new LivePanelViewModel();
    readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    readonly SecretModalController _modal = new SecretModalController();

    VenueMenuViewModel _menu;
    LiveRpcLanes _lanes;
    string _venue;

    volatile PyObject _server;
    volatile bool _serverReady;
    volatile string _status = "starting…";
    volatile string _error;
    volatile string _lastOrder = "-";
    volatile bool _loginRunning;
    volatile bool _teardownComplete;   // Disconnect ran → lanes stopped, no more actions
    volatile string _finalStateJson;   // one post-logout snapshot to converge the badge

    Thread _login;
    IntPtr _mainThreadState;
    bool _engineStarted;
    bool _secretModalOpenPrev;

    void Start()
    {
        _venue = TargetVenue;
        _menu = new VenueMenuViewModel(_conn, _coord);
        try
        {
            if (PythonEngine.IsInitialized)
            {
                _error = "double-init: PythonEngine already owned by another harness (single Play-owner only)";
                _status = "ERROR";
                Debug.LogError("[VENUE LOGIN SECRET HITL FAIL] " + _error);
                return;
            }
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;

            // build the REAL-venue server on main while it holds the GIL.
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
                _server = srvCls.Invoke(de, new PyString(_venue));
            de.Dispose();

            // NOTE: set_execution_mode("LiveManual") is set AFTER login (in Connect) — a
            // pre-login call fails EXECUTION_MODE_PRECONDITION (venue DISCONNECTED), which
            // would leave the mode at Replay and reject every place_order.

            _mainThreadState = PythonEngine.BeginAllowThreads();   // main GIL-free
            _lanes = new LiveRpcLanes(_server, _coord);
            _lanes.Start();
            _serverReady = true;
            _status = "ready — press Connect";
        }
        catch (Exception e)
        {
            _error = "start: " + e;
            _status = "ERROR";
            Debug.LogError("[VENUE LOGIN SECRET HITL FAIL] " + e);
        }
    }

    void Update()
    {
        if (!_serverReady) return;

        // GIL-free drain: backend events → view-model (SecretRequired opens the modal).
        long beforeSecret = _vm.SecretRequiredCount;
        while (_sink.TryDequeue(out string wire)) _vm.Apply(wire);
        if (_vm.SecretRequiredCount > beforeSecret && !_modal.IsOpen)
        {
            _modal.Open(_vm.LatestSecretRequired, Time.realtimeSinceStartup);
        }

        // 25s absolute timeout (the char-by-char keyboard drain is in OnGUI, where per-key
        // KeyDown events arrive — NEVER via Input.inputString, an immutable secret string).
        if (_modal.IsOpen && _modal.TickExpire(Time.realtimeSinceStartup))
            _status = "secret modal timed out (25s)";
        // keep the coordinator's secret-modal flag in sync (Wall 1).
        if (_modal.IsOpen != _secretModalOpenPrev)
        {
            _coord.SetSecretModalOpen(_modal.IsOpen);
            _secretModalOpenPrev = _modal.IsOpen;
        }

        // poll-canonical badge. After teardown the poll lane is gone, so its LatestState
        // is frozen at the last "Connected" snapshot — apply the ONE post-logout snapshot
        // instead so the badge converges to DISCONNECTED.
        if (_teardownComplete)
        {
            if (_finalStateJson != null) { _conn.ApplyStatePoll(_finalStateJson); _finalStateJson = null; }
        }
        else
        {
            string st = _lanes != null ? _lanes.LatestState : null;
            if (!string.IsNullOrEmpty(st)) _conn.ApplyStatePoll(st);
        }
    }

    void Connect()
    {
        if (_loginRunning) return;
        VenueConnectRequest req = _menu.BuildConnectRequest(_venue);
        _loginRunning = true;
        _status = "login dialog open (enter credentials)…";
        // venue_login spawns the tkinter dialog and blocks → run off the main thread.
        _login = new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                using (PyObject res = _server.InvokeMethod("venue_login",
                           new PyString(req.Venue), new PyString(req.CredentialsSource),
                           new PyString(req.EnvironmentHint)))
                {
                    bool ok = res["success"].As<bool>();
                    string ec = ok ? "" : res["error_code"].As<string>();
                    if (ok)
                    {
                        // mode MUST be set after a successful login (D1: place_order needs
                        // LiveManual; a pre-login set fails EXECUTION_MODE_PRECONDITION).
                        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
                            if (!m["success"].As<bool>()) { ok = false; ec = "set_execution_mode: " + m["error_code"].As<string>(); }
                    }
                    _conn.ApplyLoginAck(ok, ec);
                    _status = ok ? "connected (LiveManual)" : ("login failed: " + ec);
                }
            }
            catch (Exception e) { _error = "login: " + e; _status = "ERROR"; }
            finally { _loginRunning = false; }
        }) { IsBackground = true, Name = "VenueLoginDialog" };
        _login.Start();
    }

    void PlaceTestOrder()
    {
        _status = "placing test order…";
        _lanes.SubmitPlaceOrder(_venue, IID, "BUY", 100.0, null, "MARKET", "DAY", res =>
        {
            _lastOrder = res.Success ? res.Status : ("ERR " + res.ErrorCode);
            _status = "order: " + _lastOrder + (res.Status == "ACCEPTED" ? " (no fill — closed/閉局?)" : "");
        });
    }

    void SubmitSecret()
    {
        char[] payload = _modal.Submit();
        if (payload == null) return;
        string reqId = _vm.LatestSecretRequired.RequestId;
        _coord.SetSecretModalOpen(false);
        _secretModalOpenPrev = false;
        _lanes.SubmitSecret(reqId, payload, _ => { });
    }

    void Disconnect()
    {
        if (!_coord.RequestLogout()) { _status = "logout deferred (write in flight)"; return; }
        if (!_coord.ConsumePendingLogout()) return;
        _status = "disconnecting…";
        new Thread(() =>
        {
            try
            {
                if (!_lanes.StopAndJoin())
                {
                    _status = "logout aborted: a lane is still in flight (not tearing down to avoid an order race)";
                    return;
                }
                using (Py.GIL())
                {
                    using (_server.InvokeMethod("set_execution_mode", new PyString("Replay"))) { }
                    using (_server.InvokeMethod("venue_logout")) { }
                    // poll lane is stopped — grab one final snapshot so the badge converges.
                    using (PyObject s = _server.InvokeMethod("get_state_json")) _finalStateJson = s.As<string>();
                }
                _teardownComplete = true;
                _status = "disconnected — exit Play to restart";
            }
            catch (Exception e) { _error = "logout: " + e; }
        }) { IsBackground = true, Name = "VenueLogout" }.Start();
    }

    void OnGUI()
    {
        // char-by-char keyboard drain for the secret modal — per-key KeyDown events, so the
        // plaintext only ever lives in the zeroable char[] (AppendChar filters control keys).
        if (_modal.IsOpen)
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Backspace) _modal.Backspace();
                else if (ev.character != '\0') _modal.AppendChar(ev.character);
            }
        }

        const int W = 460;
        GUILayout.BeginArea(new Rect(12, 12, W, 320), GUI.skin.box);
        GUILayout.Label($"<b>Venue Login + Secret HITL — {_venue}</b>");
        GUILayout.Label("badge: " + _menu.BadgeText);
        GUILayout.Label("status: " + _status);
        GUILayout.Label("last order: " + _lastOrder);
        if (_menu.ShowReloginHint) GUILayout.Label("<color=orange>session lost — reconnect</color>");
        if (_error != null) GUILayout.Label("<color=red>" + _error + "</color>");

        GUILayout.Space(6);
        GUI.enabled = _serverReady && _menu.CanConnect && !_loginRunning && !_teardownComplete;
        if (GUILayout.Button("Connect (" + _menu.CredentialsSourceFor(_venue) + ")")) Connect();
        GUI.enabled = _serverReady && _conn.IsConnected && !_teardownComplete;
        if (GUILayout.Button("Place test order (BUY 100 " + IID + ")")) PlaceTestOrder();
        GUI.enabled = _serverReady && _menu.CanDisconnect && !_teardownComplete;
        if (GUILayout.Button("Disconnect")) Disconnect();
        GUI.enabled = true;

        if (_modal.IsOpen)
        {
            GUILayout.Space(8);
            GUILayout.Label("<b>Second password</b> (type; chars are masked, never stored as text)");
            GUILayout.Label("secret: " + _modal.MaskedDisplay);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Submit")) SubmitSecret();
            if (GUILayout.Button("Cancel")) { _modal.Cancel(); _coord.SetSecretModalOpen(false); _secretModalOpenPrev = false; }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        try
        {
            if (_lanes != null && !_lanes.StopAndJoin())
                Debug.LogWarning("[VENUE LOGIN SECRET HITL] teardown: a lane did not join in time");
            // Let an open login dialog settle before reclaiming the GIL — venue_login
            // blocks on the tkinter subprocess (which has its own timeout); joining
            // here avoids EndAllowThreads contending with a live login thread on exit.
            if (_login != null) _login.Join(2000);
            if (_engineStarted)
            {
                PythonEngine.EndAllowThreads(_mainThreadState);
                PythonEngine.Shutdown();
            }
        }
        catch (Exception e) { Debug.LogWarning("[VENUE LOGIN SECRET HITL] teardown: " + e); }
    }
}
