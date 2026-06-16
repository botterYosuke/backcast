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
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
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
    MenuBarViewModel _menuBar;          // #42: global menu bar brain (composes _menu, AC③)
    LiveRpcLanes _lanes;
    string _venue;
    enum TopMenu { None, File, Edit, Venue, Help }   // open dropdown (screen-fixed chrome)
    TopMenu _openMenu = TopMenu.None;
    volatile string _execMode = "Replay";  // last-sent execution mode (FileOpen side-effect source)
    string _menuNotice = "";            // transient File→New refuse / menu hint

    // ── engine ──────────────────────────────────────────────────────────────
    volatile PyObject _server;
    volatile bool _serverReady;
    volatile string _status = "starting…";
    volatile string _error;
    volatile bool _loginRunning;
    volatile bool _modeSending;         // single-flight guard for SendMode worker thread (#42 F2)
    volatile bool _teardownComplete;
    volatile string _finalStateJson;
    Thread _login;
    IntPtr _mainThreadState;
    bool _engineStarted;
    bool _secretModalOpenPrev;

    // ── UI state (manual ticket / auto run) ──────────────────────────────────
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

    // ── #39 Slice 3: production footer (mode segments + LiveAuto ▶), replacing the retired IMGUI
    // Manual/Auto placeholder (findings 0017 §9c / 0025). The footer OWNS mode switching + LiveAuto
    // launch/pause/resume + the D2 stop-on-leave; pure-logic VMs decide, this shell marshals RPCs. ──
    Font _font;
    ReplayFooterView _footer;
    FooterModeViewModel _footerMode;
    LiveAutoTransportViewModel _footerAuto;
    ReplayTransportViewModel _footerReplay;     // the shared footer view needs one; Replay surface hidden in Live
    readonly SelectedSymbol _footerSelected = new SelectedSymbol();
    volatile bool _autoActionSending;           // single-flight for footer-driven start/stop/pause RPCs
    // worker→main signals (footer VMs are main-thread-owned; workers never mutate them directly).
    volatile bool _footerModeRejected;          // a SetExecutionMode (mode/stop-then-switch) failed
    volatile int _footerStartResult;            // 0 none / 1 ok / 2 fail (register→start outcome)
    volatile string _footerStartedRunId;        // run_id from a successful start (guard release key)
    string _lastFooterSig = "";                 // gate footer Refresh to real state changes

    // Public read-only seams the AFK probe / roundtrip recorder observe.
    public bool ServerReady => _serverReady;
    public VenueConnectionViewModel Conn => _conn;
    public LivePanelViewModel Panel => _vm;
    public VenueMenuViewModel Menu => _menu;
    public MenuBarViewModel MenuBar => _menuBar;
    public string ExecMode => _execMode;
    public SecretModalController Modal => _modal;
    public LiveRpcLanes Lanes => _lanes;
    public string LastManualOrderId => _lastManualOrderId;
    public string AutoRunId => _autoRunId;

    void Start()
    {
        _venue = TargetVenue;
        _menu = new VenueMenuViewModel(_conn, _coord);
        // #42: the live shell hosts no replay run, so isReplayRunning is always false here; the
        // full-workspace File→New clear is exercised by MenuBarHitlHarness. This shell wires the
        // engine-touching slice (venue submenu + File-op mode side-effects, AC②/AC③).
        _menuBar = new MenuBarViewModel(_menu, _conn,
            currentMode: () => _execMode,
            // Consult the lifecycle authority, not the _autoRunId mirror: _autoRunId is only cleared on
            // a StopRunThenSwitch, so a naturally-terminated run (ERROR/STOPPED/complete) would leave it
            // stale and wedge File→New as "auto running" forever. HasActiveRun follows the lifecycle.
            isLiveAutoRunning: () => _footerAuto != null && _footerAuto.HasActiveRun,
            isReplayRunning: () => false);
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
            BuildFooter();   // #39: production footer (mode segments + LiveAuto ▶)
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
            // Feed BOTH the badge and the footer mode VM the single post-logout snapshot before
            // nulling it — else the footer stays pinned to the last live mode (segments + manual
            // ticket visible) because DriveFooter runs after this and would read a nulled snapshot.
            if (_finalStateJson != null) { _conn.ApplyStatePoll(_finalStateJson); _footerMode?.ApplyPoll(_finalStateJson); _finalStateJson = null; }
        }
        else
        {
            string st = _lanes != null ? _lanes.LatestState : null;
            if (!string.IsNullOrEmpty(st)) _conn.ApplyStatePoll(st);
        }

        DriveFooter();
    }

    // #39: main-thread footer drive — consume worker→VM signals, overwrite the mode display from the
    // poll (D1), release the start guard as the lifecycle catches up, honour the venue-drop auto-replay
    // (G1: stop the run, not just fall back to Replay), and Refresh only on a real state change.
    void DriveFooter()
    {
        if (_footer == null) return;

        // worker outcomes → VM (kept on main so the pure VMs are never mutated off-thread).
        if (_footerModeRejected) { _footerModeRejected = false; _footerMode.NotifyModeResult(false); }
        int sr = _footerStartResult;
        if (sr != 0) { _footerStartResult = 0; _footerAuto.NotifyStartResult(sr == 1, _footerStartedRunId); }

        string st = _teardownComplete ? _finalStateJson : (_lanes != null ? _lanes.LatestState : null);
        if (!string.IsNullOrEmpty(st)) _footerMode.ApplyPoll(st);

        _footerAuto.ObserveLifecycle();

        // G1: a venue drop does NOT stop a running LiveAuto run (engine emits VenueLogoutDetected only),
        // so an active run must be stopped here too — not merely switched to Replay (findings 0025 §3 D2).
        // Only act (and consume the one-shot) when no auto RPC is in flight: ShouldAutoReplay is level-
        // triggered (re-armed each poll while the venue is non-live in a Live mode), so deferring a
        // frame is safe and avoids consuming it into a StopRunThenSwitch that would early-return. Once
        // the switch lands the poll reports Replay and the condition clears — no repeated stop on a
        // dead run.
        if (!_teardownComplete && !_autoActionSending && _footerMode.ShouldAutoReplay)
        {
            _footerMode.ConsumeAutoReplay();
            if (_footerAuto.HasActiveRun) StopRunThenSwitch(_footerAuto.ActiveRunId, FooterModeViewModel.Replay);
            else SendMode(FooterModeViewModel.Replay);
        }

        // Refresh only when something the footer renders actually changed (avoid per-frame churn).
        string sig = _footerMode.DisplayMode + "|" + _footerMode.Locked + "|" + _footerMode.VenueLive
                   + "|" + _footerAuto.HasActiveRun + "|" + _footerAuto.PlayGlyph + "|" + _autoStatus;
        if (sig != _lastFooterSig) { _lastFooterSig = sig; _footer.Refresh(); }
    }

    // ── venue connect/disconnect (now driven by the menu bar Venue submenu, #42) ──────
    // Back-compat default: MOCK bring-up via the env hint the VM resolves (AFK harnesses).
    public void Connect() => ConnectEnv(_venue, _menu.EnvironmentHintFor(_venue));

    // The 4 menu Venue variants route here with an explicit (venue, env). Prod variants are
    // gated by VenueMenuViewModel.CanConnectEnv at the menu layer; Python is the safety authority.
    public void ConnectEnv(string venue, string env)
    {
        if (_loginRunning) return;
        _venue = venue;
        VenueConnectRequest req = _menu.BuildConnectRequest(venue, env);
        // MOCK is a credential-less dev venue: connect with the "env" source every MOCK harness uses
        // (venue_login("MOCK","env","")) so the tkinter prompt subprocess never spawns (#39 HITL).
        if (venue == "MOCK") req.CredentialsSource = "env";
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
                        {
                            if (!m["success"].As<bool>()) { ok = false; ec = "set_execution_mode: " + m["error_code"].As<string>(); }
                            else _execMode = "LiveManual";
                        }
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
                    _execMode = "Replay";
                    using (_server.InvokeMethod("venue_logout")) { }
                    using (PyObject s = _server.InvokeMethod("get_state_json")) _finalStateJson = s.As<string>();
                }
                _teardownComplete = true;
                _status = "disconnected — exit Play to restart";
            }
            catch (Exception e) { _error = "logout: " + e; }
        }) { IsBackground = true, Name = "ShellVenueLogout" }.Start();
    }

    // ── File menu: Layout + mode side-effects (#42 / findings 0017 §4-5) ───────────────
    // The live shell holds no editor/scenario/canvas, so the FULL-workspace clear and the
    // layout apply/round-trip are exercised by MenuBarHitlHarness. Here the engine-touching
    // part — the File-op SetExecutionMode side-effects — is fully real (AC②).
    public void FileNew()
    {
        FileNewDecision d = _menuBar.FileNew(out string modeReq, out string refuse);
        if (d == FileNewDecision.RefusedRunning) { _menuNotice = refuse; return; }
        _strategyFileText = "";                          // live shell's only "loaded strategy" state
        _menuNotice = "New workspace";
        if (modeReq != null) SendMode(modeReq);          // guarded LiveManual (null = observable no-op)
    }

    public void FileOpenLayout()
    {
        string side = _menuBar.FileOpenModeSideEffect(); // TTWR: Open WHILE Live → LiveAuto, BEFORE load
        if (side != null) SendMode(side);
        string path = LayoutPathResolver.DefaultPath();
        LayoutStore.Load(path);                          // read-only seam exercise (fail-soft)
        _menuNotice = "Opened layout: " + path;
    }

    public void FileSaveLayout()
    {
        // No canvas/LayoutBinder in the live shell → nothing to capture. Writing Default() would
        // clobber a real layout, so save is a no-op here; the round-trip is in the harness.
        _menuNotice = "Save layout: no canvas in live shell (see MenuBarHitlHarness)";
    }

    // Fire-and-forget guarded SetExecutionMode (menu side-effect; runs off-main like Connect).
    void SendMode(string mode)
    {
        // single-flight: drop overlapping requests so two near-simultaneous menu mode-sets
        // can't race the engine final mode against the _execMode label (#42 F2). Called on the
        // Unity main thread, so this check-then-set is serial; the worker clears the flag.
        if (_modeSending) return;
        _modeSending = true;
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(mode)))
                {
                    if (m["success"].As<bool>()) { _execMode = mode; _status = "mode → " + mode; }
                    else { _status = "set_execution_mode(" + mode + ") rejected: " + m["error_code"].As<string>(); _footerModeRejected = true; }
                }
            }
            catch (Exception e) { _error = "set_execution_mode: " + e; }
            finally { _modeSending = false; }
        }) { IsBackground = true, Name = "ShellSetMode" }.Start();
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

    // ── #39 Slice 3: production footer (mode segments + LiveAuto ▶) ───────────────────────────
    // A text-field-backed IStrategyFileProvider: the live shell's "strategy" is the typed path
    // (the rich provider seam is #16; this shell keeps the simple text entry it always had).
    sealed class FuncStrategyProvider : IStrategyFileProvider
    {
        readonly Func<string> _get;
        public FuncStrategyProvider(Func<string> get) { _get = get; }
        public bool TryGetStrategyFile(out string path)
        {
            path = _get();
            return !string.IsNullOrEmpty(path);
        }
    }

    void BuildFooter()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasGo = new GameObject("ProductionFooterCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // New Input System (activeInputHandler=1): legacy modules throw every frame — disable them
        // and ensure an InputSystemUIInputModule EventSystem (mirrors the other harnesses).
        foreach (var legacy in FindObjectsByType<StandaloneInputModule>(FindObjectsSortMode.None))
            legacy.enabled = false;
        var existing = FindAnyObjectByType<EventSystem>();
        if (existing == null)
        {
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
        }
        else
        {
            var module = existing.GetComponent<InputSystemUIInputModule>();
            if (module == null) module = existing.gameObject.AddComponent<InputSystemUIInputModule>();
            module.enabled = true;
        }

        var footerBar = new GameObject("footer", typeof(RectTransform)).GetComponent<RectTransform>();
        footerBar.SetParent(canvasGo.transform, false);
        footerBar.anchorMin = new Vector2(0f, 0f);
        footerBar.anchorMax = new Vector2(1f, 0f);
        footerBar.pivot = new Vector2(0.5f, 0f);
        footerBar.anchoredPosition = Vector2.zero;
        footerBar.sizeDelta = new Vector2(0f, 40f);

        _footerReplay = new ReplayTransportViewModel(new ReplayLifecycle());
        _footerMode = new FooterModeViewModel();
        _footerAuto = new LiveAutoTransportViewModel(
            _vm,                                                  // LiveLifecycle authority
            new FuncStrategyProvider(() => _strategyFileText),
            _footerSelected,
            () => string.IsNullOrEmpty(_iidText) ? Array.Empty<string>() : new[] { _iidText },
            () => !string.IsNullOrEmpty(_conn.VenueId) ? _conn.VenueId : _venue);
        _footer = new ReplayFooterView(
            _footerReplay, OnFooterPlayPause, OnFooterStep, OnFooterStop, OnFooterSpeed, _font,
            _footerMode, _footerAuto, OnFooterMode);
        _footer.Build(footerBar);
    }

    // ── footer mode segment click → SetExecutionMode (D1) / stop-then-switch on leaving LiveAuto (D2) ──
    void OnFooterMode(string target)
    {
        // A footer-driven engine mutation (start/pause/resume/stop, or a start awaiting its first
        // lifecycle event) is in flight: block a concurrent mode switch. Otherwise set_execution_mode
        // could race start_live_strategy under the GIL (a run launched while the engine flips to Replay
        // = orphan), or a StopRunThenSwitch would lock the segment VM and then early-return without
        // firing the RPC (stuck spinner). The in-flight RPCs are sub-second; the user just waits.
        if (_autoActionSending || _footerAuto.IsStartInFlight)
        {
            _menuNotice = "Live action in flight — wait before switching mode.";
            return;
        }
        var req = _footerMode.RequestMode(target, _footerAuto.HasActiveRun);
        switch (req.Kind)
        {
            case FooterModeRequestKind.SwitchImmediate:
            case FooterModeRequestKind.SwitchLockedLive:
                SendMode(req.Target);
                break;
            case FooterModeRequestKind.StopRunThenSwitch:
                StopRunThenSwitch(_footerAuto.ActiveRunId, req.Target);
                break;
            case FooterModeRequestKind.BlockedVenueNotLive:
                _menuNotice = req.Message;
                break;
            case FooterModeRequestKind.Ignore:
                break;
        }
    }

    // ── footer ▶/⏸ — routed by the displayed mode (TTWR footer_pause_resume_system branch) ──
    void OnFooterPlayPause()
    {
        if (_footerMode.DisplayMode == FooterModeViewModel.LiveAuto)
        {
            var d = _footerAuto.PlayPauseDecision();
            switch (d.Action)
            {
                case LiveAutoAction.Start: FooterAutoStart(d.Start); break;
                case LiveAutoAction.Pause: CallAutoControl("pause_live_strategy", d.RunId, "paused"); break;
                case LiveAutoAction.Resume: CallAutoControl("resume_live_strategy", d.RunId, "running"); break;
                case LiveAutoAction.None: _autoStatus = d.Message; break;
            }
        }
        else
        {
            // The live shell hosts no replay run; the footer's Replay transport is the scenario shell's.
            _menuNotice = "Replay transport is driven from the scenario shell, not the live shell.";
        }
    }

    // step / stop / speed are Replay-only (hidden in Live); inert in the live shell.
    void OnFooterStep() { }
    void OnFooterStop() { }
    void OnFooterSpeed(int mult) { }

    // LiveAuto ▶ at rest → register → start (the 2-stage StartLiveAuto). Pre-flight already gated by
    // the VM; this only marshals the RPCs and reports the outcome to the start guard via main.
    void FooterAutoStart(LiveAutoStartRequest req)
    {
        if (req.Gate != LiveAutoStartGate.Ready) { _autoStatus = req.Message; return; }
        // Connection gate (parity with the retired DrawAutoControls' canTrade): the VM only checks a
        // non-empty venue-identity string, and the configured-venue fallback is never empty, so re-
        // assert the live session here before firing register→start (the manual ticket keeps this gate).
        if (!_serverReady || !_conn.IsConnected || _teardownComplete) { _autoStatus = "connect a venue before starting LiveAuto"; return; }
        if (_autoActionSending) { _autoStatus = "auto action already in flight"; return; }
        _autoActionSending = true;
        _footerAuto.NotifyStartIssued();
        _autoStatus = "register+start…";
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                {
                    string sid;
                    using (PyObject r = _server.InvokeMethod("register_live_strategy",
                               new PyString(req.StrategyFile), new PyString(req.OriginalPath)))
                    {
                        if (!r["success"].As<bool>())
                        {
                            _autoStatus = "register failed: " + r["error_code"].As<string>();
                            _footerStartResult = 2; return;
                        }
                        sid = r["strategy_id"].As<string>();
                    }
                    _autoStrategyId = sid;
                    using (PyObject s = _server.InvokeMethod("start_live_strategy",
                               new PyString(sid), new PyString(req.InstrumentId), new PyString(req.Venue)))
                    {
                        if (!s["success"].As<bool>())
                        {
                            _autoStatus = "start failed: " + s["error_code"].As<string>();
                            _footerStartResult = 2; return;
                        }
                        _autoRunId = s["run_id"].As<string>();
                        _footerStartedRunId = _autoRunId;
                        _footerStartResult = 1;
                        _autoStatus = "running run=" + _autoRunId;
                    }
                }
            }
            catch (Exception e) { _autoStatus = "auto error: " + e.Message; _footerStartResult = 2; }
            finally { _autoActionSending = false; }
        }) { IsBackground = true, Name = "ShellAutoStart" }.Start();
    }

    // LiveAuto ▶ on an active run → pause / resume (immediate; no graceful teardown).
    void CallAutoControl(string method, string runId, string okWord)
    {
        if (string.IsNullOrEmpty(runId) || _autoActionSending) return;
        _autoActionSending = true;
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                using (PyObject s = _server.InvokeMethod(method, new PyString(runId)))
                    _autoStatus = s["success"].As<bool>() ? okWord + " run=" + runId
                                                          : (method + " failed: " + s["error_code"].As<string>());
            }
            catch (Exception e) { _autoStatus = method + " error: " + e.Message; }
            finally { _autoActionSending = false; }
        }) { IsBackground = true, Name = "ShellAutoControl" }.Start();
    }

    // D2: leaving LiveAuto with an active run → stop_live_strategy FIRST; switch mode only on stop
    // success (else stay in LiveAuto + surface the error — no "Replay display over a live run" orphan).
    void StopRunThenSwitch(string runId, string target)
    {
        if (string.IsNullOrEmpty(runId)) { SendMode(target); return; }
        if (_autoActionSending) { _autoStatus = "auto action already in flight"; return; }
        _autoActionSending = true;
        _autoStatus = "stopping (graceful → cancel resting → teardown)…";
        new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                {
                    bool stopped;
                    using (PyObject s = _server.InvokeMethod("stop_live_strategy", new PyString(runId)))
                        stopped = s["success"].As<bool>();
                    if (!stopped)
                    {
                        _autoStatus = "stop failed — staying in LiveAuto";
                        _footerModeRejected = true;   // release the footer lock on main; mode unchanged
                        return;
                    }
                    _autoRunId = "";
                    using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString(target)))
                    {
                        if (m["success"].As<bool>()) { _execMode = target; _autoStatus = "stopped → " + target; }
                        else { _autoStatus = "stopped, but mode switch rejected: " + m["error_code"].As<string>(); _footerModeRejected = true; }
                    }
                }
            }
            catch (Exception e) { _autoStatus = "stop/switch error: " + e.Message; _footerModeRejected = true; }
            finally { _autoActionSending = false; }
        }) { IsBackground = true, Name = "ShellStopThenSwitch" }.Start();
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

        DrawMenuBar();
        DrawChrome();
        DrawPanels();
        DrawOpenSubmenu();      // after chrome/panels so submenus aren't overpainted (#42 F1)
        DrawSecretModalOverlay();
    }

    // ── global menu bar (screen-fixed chrome — CONTEXT "menu bar"; TTWR File/Edit/Venue/Help) ──
    void DrawMenuBar()
    {
        GUILayout.BeginArea(new Rect(0, 0, Screen.width, 22), GUI.skin.box);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("File", GUILayout.Width(48))) _openMenu = _openMenu == TopMenu.File ? TopMenu.None : TopMenu.File;
        if (GUILayout.Button("Edit", GUILayout.Width(48))) _openMenu = _openMenu == TopMenu.Edit ? TopMenu.None : TopMenu.Edit;
        if (GUILayout.Button("Venue", GUILayout.Width(56))) _openMenu = _openMenu == TopMenu.Venue ? TopMenu.None : TopMenu.Venue;
        if (GUILayout.Button("Help", GUILayout.Width(48))) _openMenu = _openMenu == TopMenu.Help ? TopMenu.None : TopMenu.Help;
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_menu.BadgeText}   mode: {_execMode}");
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    // Open submenu + transient notice are drawn AFTER chrome/panels so the chrome box
    // (Rect 12,34,…) can't overpaint the y=22 submenus (#42 F1). Modal stays topmost (drawn later).
    void DrawOpenSubmenu()
    {
        bool ready = _serverReady && !_teardownComplete;
        if (_openMenu == TopMenu.File) DrawFileMenu(ready);
        else if (_openMenu == TopMenu.Edit) DrawEditMenu();
        else if (_openMenu == TopMenu.Venue) DrawVenueMenu(ready);
        else if (_openMenu == TopMenu.Help) DrawHelpMenu();
        if (!string.IsNullOrEmpty(_menuNotice))
        {
            GUI.Label(new Rect(Screen.width - 360, 2, 350, 18), "<color=orange>" + _menuNotice + "</color>");
        }
    }

    // A greyed-out, non-actionable menu item (deferred/unavailable here). Self-contained so the
    // GUI.enabled reset can never be forgotten and silently grey out following widgets.
    static void DisabledItem(string label) { GUI.enabled = false; GUILayout.Button(label); GUI.enabled = true; }

    void DrawFileMenu(bool ready)
    {
        GUILayout.BeginArea(new Rect(0, 22, 220, 92), GUI.skin.box);
        GUI.enabled = ready;
        if (GUILayout.Button("New")) { FileNew(); _openMenu = TopMenu.None; }
        if (GUILayout.Button("Open  (layout)")) { FileOpenLayout(); _openMenu = TopMenu.None; }
        if (GUILayout.Button("Save  (layout)")) { FileSaveLayout(); _openMenu = TopMenu.None; }
        GUI.enabled = true;
        DisabledItem("Save As…  (deferred)");        // follow-up: native file picker
        GUILayout.EndArea();
    }

    void DrawEditMenu()
    {
        // Undo/Redo route to the active strategy editor (#16). The live shell hosts none, so
        // they are disabled here; the harness wires them to a real editor's EditHistory.
        GUILayout.BeginArea(new Rect(48, 22, 200, 50), GUI.skin.box);
        DisabledItem("Undo  (no active editor)");
        DisabledItem("Redo  (no active editor)");
        GUILayout.EndArea();
    }

    void DrawVenueMenu(bool ready)
    {
        GUILayout.BeginArea(new Rect(96, 22, 260, 140), GUI.skin.box);
        // #39 HITL: MOCK is the credential-less dev venue — not a TTWR ConnectVariant, so surface a
        // dev-only connect here when the shell was launched as MOCK (Footer LiveAuto HITL menu).
        if (_venue == "MOCK")
        {
            GUI.enabled = ready && !_conn.IsConnected && !_loginRunning;
            if (GUILayout.Button("Connect MOCK (dev)")) { ConnectEnv("MOCK", ""); _openMenu = TopMenu.None; }
        }
        foreach (var v in VenueMenuViewModel.ConnectVariants)
        {
            // Prod variants grey out unless *_ALLOW_PROD is set (mirrors the login dialog; Python
            // is the safety authority). All connect items disabled while connected/mid-auth.
            GUI.enabled = ready && _menu.CanConnectEnv(v.Venue, v.Env) && !_loginRunning;
            if (GUILayout.Button(v.Label)) { ConnectEnv(v.Venue, v.Env); _openMenu = TopMenu.None; }
        }
        GUI.enabled = ready && _menu.CanDisconnect;
        if (GUILayout.Button("Disconnect")) { Disconnect(); _openMenu = TopMenu.None; }
        GUI.enabled = true;
        GUILayout.EndArea();
    }

    void DrawHelpMenu()
    {
        // ADR-0005 lists Settings as its own surface — item present, body deferred to that slice.
        GUILayout.BeginArea(new Rect(144, 22, 220, 32), GUI.skin.box);
        DisabledItem("Settings  (deferred slice)");
        GUILayout.EndArea();
    }

    void DrawChrome()
    {
        GUILayout.BeginArea(new Rect(12, 34, 440, 300), GUI.skin.box);
        GUILayout.Label($"<b>Live — {_venue}</b>   badge: {_menu.BadgeText}");
        GUILayout.Label("status: " + _status);
        if (_menu.ShowReloginHint) GUILayout.Label("<color=orange>session lost — reconnect</color>");
        if (_error != null) GUILayout.Label("<color=red>" + _error + "</color>");

        // Venue Connect/Disconnect moved to the menu bar Venue submenu (#42 AC③ — no duplicate).
        // #39: mode switching + LiveAuto launch/pause/stop are now the uGUI footer (mode segments +
        // ▶); the old IMGUI Manual/Auto toggle is retired (findings 0017 §9c). The manual order
        // ticket shows when the footer mode is LiveManual; LiveAuto is the footer ▶ (no IMGUI auto
        // controls). While a menu is open, disable chrome's interactive controls so they can't steal
        // the open submenu's MouseDown in the overlap band (#42 F1-residual).
        bool menuOpen = _openMenu != TopMenu.None;
        GUI.enabled = !menuOpen;
        GUILayout.BeginHorizontal();
        GUILayout.Label("instrument", GUILayout.Width(70));
        _iidText = GUILayout.TextField(_iidText, GUILayout.Width(120));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("strategy .py", GUILayout.Width(80));
        _strategyFileText = GUILayout.TextField(_strategyFileText, GUILayout.Width(260));
        GUILayout.EndHorizontal();

        string fm = _footerMode != null ? _footerMode.DisplayMode : _execMode;
        bool canTrade = _serverReady && _conn.IsConnected && !_teardownComplete && !menuOpen;
        if (fm == "LiveManual") DrawManualTicket(canTrade);
        else if (fm == "LiveAuto") GUILayout.Label("auto: " + _autoStatus + "  (footer ▶ to start; switch mode to stop)");
        GUI.enabled = true;
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
