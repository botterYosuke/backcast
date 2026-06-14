// ProductionLiveShell.cs — issue #23 "Live demo roundtrip" (workstream B, DURABLE).
//
// The first production composition of the Live UI: it wires the durable #20/#21/#26 pieces
// (LiveBackendEventSink → LivePanelViewModel, VenueConnectionViewModel, LiveLogoutCoordinator,
// SecretModalController, VenueMenuViewModel, LiveRpcLanes) into an always-on shell that an
// owner launches normally and uses to PLACE → FILL → see POSITION and CANCEL resting orders,
// for BOTH manual and auto (kernel strategy) order entry. Logic is reused, never reimplemented.
//
// PLACEMENT (findings 0014 D1, CONTEXT.md "infinite canvas"): the venue connect/disconnect
// menu, the connection badge, the secret modal overlay, the Manual/Auto mode + Auto Run ▶
// footer, the manual Order ticket and the Orders/Positions/Run-Result data panels are all
// rendered as SCREEN-FIXED CHROME (IMGUI / ScreenSpaceOverlay by nature) — outside any
// infinite-canvas Content, so they never pan/zoom. (Binding the data panels onto the uGUI
// Hakoniwa tiles #14 is the remaining additive integration; the data source — the durable
// LivePanelViewModel fed by LiveBackendEventSink — is the authority either way, findings 0011 D2.)
//
// GIL discipline mirrors VenueLoginSecretHitlHarness: workers take the GIL, main renders
// GIL-free. TargetVenue defaults to MOCK (AFK bring-up needs no credentials); the demo
// roundtrip legs set TACHIBANA / KABU via LiveDemoRoundtrip menu (workstream C).
using System;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class ProductionLiveShell : MonoBehaviour
{
    // Set before the GameObject is created (demo legs override; MOCK for AFK bring-up).
    public static string TargetVenue = "MOCK";
    public static string DefaultInstrumentId = "8918.TSE";

    // ── durable pieces (reused) ─────────────────────────────────────────────
    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    readonly LivePanelViewModel _vm = new LivePanelViewModel();
    readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    readonly SecretModalController _modal = new SecretModalController();
    VenueMenuViewModel _menu;
    LiveRpcLanes _lanes;
    string _venue;

    // ── engine ──────────────────────────────────────────────────────────────
    volatile PyObject _server;
    volatile bool _serverReady;
    volatile string _status = "starting…";
    volatile string _error;
    volatile bool _loginRunning;
    volatile bool _teardownComplete;
    volatile string _finalStateJson;
    Thread _login;
    IntPtr _mainThreadState;
    bool _engineStarted;
    bool _secretModalOpenPrev;

    // ── UI state (manual ticket / auto run) ──────────────────────────────────
    public string Mode = "Manual";          // "Manual" | "Auto"
    string _iidText = DefaultInstrumentId;
    string _qtyText = "100";
    string _priceText = "";
    bool _sideBuy = true;
    bool _limit;
    volatile string _lastManualOrderId = "";
    volatile string _lastManualStatus = "-";

    string _strategyFileText = "";
    volatile string _autoStrategyId = "";
    volatile string _autoRunId = "";
    volatile string _autoStatus = "-";

    // Public read-only seams the AFK probe / roundtrip recorder observe.
    public bool ServerReady => _serverReady;
    public VenueConnectionViewModel Conn => _conn;
    public LivePanelViewModel Panel => _vm;
    public VenueMenuViewModel Menu => _menu;
    public SecretModalController Modal => _modal;
    public LiveRpcLanes Lanes => _lanes;
    public string LastManualOrderId => _lastManualOrderId;
    public string AutoRunId => _autoRunId;

    void Start()
    {
        _venue = TargetVenue;
        _menu = new VenueMenuViewModel(_conn, _coord);
        try
        {
            if (PythonEngine.IsInitialized)
            {
                _error = "double-init: PythonEngine already owned by another component (single Play-owner only)";
                _status = "ERROR";
                Debug.LogError("[PRODUCTION LIVE SHELL FAIL] " + _error);
                return;
            }
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;

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
            Debug.LogError("[PRODUCTION LIVE SHELL FAIL] " + e);
        }
    }

    void Update()
    {
        if (!_serverReady) return;

        // GIL-free drain: backend events → view-model (SecretRequired opens the modal).
        long beforeSecret = _vm.SecretRequiredCount;
        while (_sink.TryDequeue(out string wire)) _vm.Apply(wire);
        if (_vm.SecretRequiredCount > beforeSecret && !_modal.IsOpen)
            _modal.Open(_vm.LatestSecretRequired, Time.realtimeSinceStartup);

        if (_modal.IsOpen && _modal.TickExpire(Time.realtimeSinceStartup))
            _status = "secret modal timed out (25s)";
        if (_modal.IsOpen != _secretModalOpenPrev)
        {
            _coord.SetSecretModalOpen(_modal.IsOpen);
            _secretModalOpenPrev = _modal.IsOpen;
        }

        // poll-canonical badge (CONTEXT.md "venue 接続状態"): after teardown the poll lane is
        // gone, so converge the badge with the single post-logout snapshot instead.
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

    // ── venue connect/disconnect (chrome) ────────────────────────────────────
    public void Connect()
    {
        if (_loginRunning) return;
        VenueConnectRequest req = _menu.BuildConnectRequest(_venue);
        _loginRunning = true;
        _status = "login (enter credentials)…";
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
                        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
                            if (!m["success"].As<bool>()) { ok = false; ec = "set_execution_mode: " + m["error_code"].As<string>(); }
                    _conn.ApplyLoginAck(ok, ec);
                    _status = ok ? "connected (LiveManual)" : ("login failed: " + ec);
                }
            }
            catch (Exception e) { _error = "login: " + e; _status = "ERROR"; }
            finally { _loginRunning = false; }
        }) { IsBackground = true, Name = "ShellVenueLogin" };
        _login.Start();
    }

    public void Disconnect()
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
                    _status = "logout aborted: a lane is still in flight (avoiding an order race)";
                    return;
                }
                using (Py.GIL())
                {
                    using (_server.InvokeMethod("set_execution_mode", new PyString("Replay"))) { }
                    using (_server.InvokeMethod("venue_logout")) { }
                    using (PyObject s = _server.InvokeMethod("get_state_json")) _finalStateJson = s.As<string>();
                }
                _teardownComplete = true;
                _status = "disconnected — exit Play to restart";
            }
            catch (Exception e) { _error = "logout: " + e; }
        }) { IsBackground = true, Name = "ShellVenueLogout" }.Start();
    }

    // ── manual Order ticket ──────────────────────────────────────────────────
    public void PlaceManual()
    {
        if (!double.TryParse(_qtyText, out double qty) || qty <= 0) { _status = "invalid qty"; return; }
        double? price = null;
        if (_limit)
        {
            if (!double.TryParse(_priceText, out double p) || p <= 0) { _status = "invalid limit price"; return; }
            price = p;
        }
        string side = _sideBuy ? "BUY" : "SELL";
        string type = _limit ? "LIMIT" : "MARKET";
        _status = "placing " + side + " " + qty + " " + type + "…";
        _lanes.SubmitPlaceOrder(_venue, _iidText, side, qty, price, type, "DAY", res =>
        {
            _lastManualStatus = res.Success ? res.Status : ("ERR " + res.ErrorCode);
            if (res.Success && !string.IsNullOrEmpty(res.OrderId)) _lastManualOrderId = res.OrderId;
            _status = "order: " + _lastManualStatus;
        });
    }

    public void CancelManual()
    {
        string oid = !string.IsNullOrEmpty(_lastManualOrderId) ? _lastManualOrderId
                   : (_vm.HasOrder ? _vm.LatestOrder.OrderId : "");
        if (string.IsNullOrEmpty(oid)) { _status = "no order to cancel"; return; }
        _status = "cancel " + oid + "…";
        _lanes.SubmitCancelOrder(_venue, oid, res =>
        {
            // ack-then-poll venue: PENDING_CANCEL = 取消受付（poll が終端 CANCELED を後追い・findings 0014）。
            _lastManualStatus = res.Success ? res.Status : ("ERR " + res.ErrorCode);
            _status = "cancel: " + _lastManualStatus;
        });
    }

    // ── auto run (kernel strategy) ───────────────────────────────────────────
    public void RegisterAndStartAuto()
    {
        if (string.IsNullOrEmpty(_strategyFileText)) { _autoStatus = "no strategy file"; return; }
        _autoStatus = "register+start…";
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                {
                    string sid;
                    using (PyObject r = _server.InvokeMethod("register_live_strategy", new PyString(_strategyFileText)))
                    {
                        if (!r["success"].As<bool>()) { _autoStatus = "register failed: " + r["error_code"].As<string>(); return; }
                        sid = r["strategy_id"].As<string>();
                    }
                    _autoStrategyId = sid;
                    using (PyObject s = _server.InvokeMethod("start_live_strategy",
                               new PyString(sid), new PyString(_iidText), new PyString(_venue)))
                    {
                        if (!s["success"].As<bool>()) { _autoStatus = "start failed: " + s["error_code"].As<string>(); return; }
                        _autoRunId = s["run_id"].As<string>();
                        _autoStatus = "running run=" + _autoRunId;
                    }
                }
            }
            catch (Exception e) { _autoStatus = "auto error: " + e.Message; }
        }) { IsBackground = true, Name = "ShellAutoStart" }.Start();
    }

    public void StopAuto()
    {
        if (string.IsNullOrEmpty(_autoRunId)) { _autoStatus = "no run to stop"; return; }
        string runId = _autoRunId;
        _autoStatus = "stopping (graceful → cancel resting → teardown)…";
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                using (PyObject s = _server.InvokeMethod("stop_live_strategy", new PyString(runId)))
                    _autoStatus = s["success"].As<bool>() ? "stopped run=" + runId : ("stop failed: " + s["error_code"].As<string>());
                _autoRunId = "";
            }
            catch (Exception e) { _autoStatus = "stop error: " + e.Message; }
        }) { IsBackground = true, Name = "ShellAutoStop" }.Start();
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

    void OnGUI()
    {
        // secret modal keyboard drain — per-key KeyDown; plaintext only ever lives in the char[].
        if (_modal.IsOpen)
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Backspace) _modal.Backspace();
                else if (ev.character != '\0') _modal.AppendChar(ev.character);
            }
        }

        DrawChrome();
        DrawPanels();
        DrawSecretModalOverlay();
    }

    void DrawChrome()
    {
        GUILayout.BeginArea(new Rect(12, 12, 440, 300), GUI.skin.box);
        GUILayout.Label($"<b>Live — {_venue}</b>   badge: {_menu.BadgeText}");
        GUILayout.Label("status: " + _status);
        if (_menu.ShowReloginHint) GUILayout.Label("<color=orange>session lost — reconnect</color>");
        if (_error != null) GUILayout.Label("<color=red>" + _error + "</color>");

        GUILayout.BeginHorizontal();
        GUI.enabled = _serverReady && _menu.CanConnect && !_loginRunning && !_teardownComplete;
        if (GUILayout.Button("Connect")) Connect();
        GUI.enabled = _serverReady && _menu.CanDisconnect && !_teardownComplete;
        if (GUILayout.Button("Disconnect")) Disconnect();
        GUI.enabled = true;
        Mode = GUILayout.Toggle(Mode == "Manual", "Manual", GUI.skin.button) ? "Manual" : "Auto";
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("instrument", GUILayout.Width(70));
        _iidText = GUILayout.TextField(_iidText, GUILayout.Width(120));
        GUILayout.EndHorizontal();

        bool canTrade = _serverReady && _conn.IsConnected && !_teardownComplete;
        if (Mode == "Manual") DrawManualTicket(canTrade);
        else DrawAutoControls(canTrade);
        GUILayout.EndArea();
    }

    void DrawManualTicket(bool canTrade)
    {
        GUILayout.Space(6);
        GUILayout.Label("<b>Order ticket</b> (manual)");
        GUILayout.BeginHorizontal();
        _sideBuy = GUILayout.Toggle(_sideBuy, "BUY", GUI.skin.button);
        _sideBuy = !GUILayout.Toggle(!_sideBuy, "SELL", GUI.skin.button);
        GUILayout.Label("qty", GUILayout.Width(28));
        _qtyText = GUILayout.TextField(_qtyText, GUILayout.Width(60));
        _limit = GUILayout.Toggle(_limit, "LIMIT", GUI.skin.button);
        if (_limit) { GUILayout.Label("@", GUILayout.Width(14)); _priceText = GUILayout.TextField(_priceText, GUILayout.Width(70)); }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUI.enabled = canTrade;
        if (GUILayout.Button("Place")) PlaceManual();
        if (GUILayout.Button("Cancel last")) CancelManual();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label("last order: " + _lastManualStatus + (string.IsNullOrEmpty(_lastManualOrderId) ? "" : " (" + _lastManualOrderId + ")"));
    }

    void DrawAutoControls(bool canTrade)
    {
        GUILayout.Space(6);
        GUILayout.Label("<b>Auto run</b> (kernel strategy)");
        GUILayout.BeginHorizontal();
        GUILayout.Label("strategy .py", GUILayout.Width(80));
        _strategyFileText = GUILayout.TextField(_strategyFileText, GUILayout.Width(260));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUI.enabled = canTrade && string.IsNullOrEmpty(_autoRunId);
        if (GUILayout.Button("▶ Register & Start")) RegisterAndStartAuto();
        GUI.enabled = canTrade && !string.IsNullOrEmpty(_autoRunId);
        if (GUILayout.Button("■ Stop")) StopAuto();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label("auto: " + _autoStatus);
    }

    void DrawPanels()
    {
        GUILayout.BeginArea(new Rect(464, 12, 380, 360), GUI.skin.box);

        GUILayout.Label("<b>Orders</b>");
        if (_vm.HasOrder)
        {
            LiveOrderEvent o = _vm.LatestOrder;
            GUILayout.Label($"  {o.ClientOrderId}  {o.Status}  filled={o.FilledQty}@{o.AvgPrice}");
        }
        else GUILayout.Label("  (none)");
        GUILayout.Label("  filled-order count: " + _vm.FilledOrderCount);

        GUILayout.Space(6);
        GUILayout.Label("<b>Positions</b>");
        if (_vm.HasAccount && _vm.LatestAccount.Positions != null && _vm.LatestAccount.Positions.Count > 0)
        {
            foreach (LivePosition p in _vm.LatestAccount.Positions)
                GUILayout.Label($"  {p.symbol}  qty={p.qty}  avg={p.avg_price}  uPnL={p.unrealized_pnl}");
            GUILayout.Label($"  cash={_vm.LatestAccount.Cash}  bp={_vm.LatestAccount.BuyingPower}");
        }
        else GUILayout.Label("  (flat / no account snapshot)");

        GUILayout.Space(6);
        GUILayout.Label("<b>Run Result</b>");
        if (_vm.HasLifecycle) GUILayout.Label($"  run={_vm.LatestLifecycle.RunId}  {_vm.LatestLifecycle.Status}");
        if (_vm.HasTelemetry)
        {
            LiveTelemetryEvent t = _vm.LatestTelemetry;
            GUILayout.Label($"  realized={t.RealizedPnl}  unrealized={t.UnrealizedPnl}  orders={t.OrderCount}  fills={t.FillCount}");
        }
        if (!_vm.HasLifecycle && !_vm.HasTelemetry) GUILayout.Label("  (no run)");
        GUILayout.EndArea();
    }

    void DrawSecretModalOverlay()
    {
        if (!_modal.IsOpen) return;
        // topmost, input-blocking overlay (chrome) — center of screen.
        float w = 360, h = 120;
        GUILayout.BeginArea(new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h), GUI.skin.box);
        GUILayout.Label("<b>Second password</b> (typed; masked, never stored as text)");
        GUILayout.Label("secret: " + _modal.MaskedDisplay);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Submit")) SubmitSecret();
        if (GUILayout.Button("Cancel")) { _modal.Cancel(); _coord.SetSecretModalOpen(false); _secretModalOpenPrev = false; }
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        try
        {
            if (_lanes != null && !_lanes.StopAndJoin())
                Debug.LogWarning("[PRODUCTION LIVE SHELL] teardown: a lane did not join in time");
            if (_login != null) _login.Join(2000);
            if (_engineStarted)
            {
                PythonEngine.EndAllowThreads(_mainThreadState);
                PythonEngine.Shutdown();
            }
        }
        catch (Exception e) { Debug.LogWarning("[PRODUCTION LIVE SHELL] teardown: " + e); }
    }
}
