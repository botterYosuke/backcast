// LiveManualTradeJourneyE2ERunner.cs — 第二波14本目・全行新規。Journey E2E（台本: same-dir
// LiveManualTradeJourneyE2ERunner.md）。venue 接続→LiveManual モードゲート→Order ticket→発注→第二暗証
// →mock fill→Positions 建玉→resting 取消→logout 収束 の手動実取引フローの横断縫い目を AFK で観測する。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod LiveManualTradeJourneyE2ERunner.Run -logFile <log>
//   # expect: [E2E LIVE MANUAL TRADE PASS] ... / exit=0  （確認は Bash `grep -a "E2E LIVE MANUAL TRADE"`。
//   #   PASS 行は → を含むので ripgrep/Select-String は取りこぼす — bash grep -a で見ること）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// 設計判断 — 二基層（findings 0067）:
//   * Section A = 接続済み MOCK root（OrderTicketE2ERunner SectionD と同型）で Order ticket の表示/操作可
//     ゲート（04）と ManualInstrument 解決/refuse（05）を観測する。
//   * Section B-E = secret-mock lanes（VenueLoginSecretProbe と同型・build_secret_mock_server で
//     SecretMockAdapter を注入）で接続/モードゲート/発注/第二暗証/fill/Positions/取消/logout/直列化/
//     logout-gate（02/03/06-13/15）を観測する。
//   production root の host.InitializePython は InprocLiveServer(de, venue)=MockVenueAdapter を built する
//   ため SecretRequired を出せない（注入 seam が無い）。よって secret 縫い目は lanes 直駆動で別基層に分ける。
//
// GIL 規律: Section A の host.InitializePython が PythonEngine.Initialize + BeginAllowThreads を済ませ
//   （main GIL-free・interpreter は alive）、host.Stop() 後も interpreter は生きたまま。Section B は GIL-free
//   な main を引き継ぐので BuildServer と全 _server 直呼びを using(Py.GIL()) で包む（WorkspaceEngineHost の
//   各メソッドと同型）。BeginAllowThreads/EndAllowThreads/Shutdown は呼ばない（host.Stop と同じ・process は
//   EditorApplication.Exit で終了）。lanes は内部で各自 Py.GIL() を取る。
//
// step 10（Positions）: get_portfolio_json は live では "" を返す（engine.last_portfolio に gated・Replay 専用）。
//   live の Positions ソースは AccountEvent push → sink → LivePanelViewModel → production FormatPositions。
//   MockVenueAdapter は fill から建玉を導出しない（fetch_account は armed snapshot を返す）ので、
//   arm_account_position で建玉を仕込み force_account_snapshot() で AccountEvent を push し、production
//   FormatPositions(_vm) を反射 invoke して建玉行を assert する（非 vacuous: arm 前は flat / 後は建玉）。
//   causal な fill→建玉 bookkeeping は実 venue 責務＝HITL（JOURNEY-LIVE-14）。
//
// 非 vacuity: 03 は接続後の受理（positive）も実証してから未接続拒否（negative, success=False）を gate に置く。
//   10 は arm 前 flat を先に実証。15 は直列化（2 write）と logout-gate（1 write の defer→promote）を分離
//   （合体すると #2 の BeginWrite が ConsumePendingLogout と race する）。drain は secret 提出（fast）で行い
//   SECRET_TIMEOUT phase は持ち込まない（AFK 短縮・flush race 低減）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class LiveManualTradeJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags SBF = BindingFlags.NonPublic | BindingFlags.Static;
    const string VENUE = "MOCK";
    const string IID = "8918.TSE";
    const string SECRET = "9753-secret";        // known marker — must never leak
    const long MAX_STALL_MS = 400;              // Python-FULL: looser than VenueLoginSecretProbe's 200ms

    // ── Section A (root) substrate ──
    static WorkspaceEngineHost s_host;

    // ── Section B-E (secret-mock lanes) substrate ──
    static readonly LiveBackendEventSink _sink = new LiveBackendEventSink();
    static readonly LivePanelViewModel _vm = new LivePanelViewModel();
    static readonly VenueConnectionViewModel _conn = new VenueConnectionViewModel();
    static readonly LiveLogoutCoordinator _coord = new LiveLogoutCoordinator();
    static readonly SecretModalController _modal = new SecretModalController();
    static readonly List<string> _fail = new List<string>();
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static PyObject _server;
    static LiveRpcLanes _lanes;
    static long _maxStall;
    static bool _leaked;
    static bool _lanesStopped;
    static bool _serverClosed;
    static string _placedOid;

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
        string fail = null;
        try
        {
            fail = SectionA_RootTicketGate();        // JOURNEY-LIVE-01a / 04 / 05
            if (fail == null) fail = RunTradeStory(); // 01b / 02 / 03 / 06-13 / 15
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E LIVE MANUAL TRADE PASS] mock venue connected → LiveManual gated on connection → order placed on the write lane → second-secret submitted on the urgent lane → mock FILLED → position surfaced in the Positions tile → resting order cancelled → logout converged to DISCONNECTED (3 lanes, main GIL-free, no plaintext leak).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E LIVE MANUAL TRADE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── A. JOURNEY-LIVE-01a/04/05: the Order ticket gates on connection + mode, on the real
    //    BackcastWorkspaceRoot (OrderTicketE2ERunner SectionD と同型). Claim Python on THIS host
    //    (MOCK) to prove the interactable-TRUE + instrument-refuse branches need a connected host.
    //    vacuity guard: assert seams/widgets EXIST first; prove interactable=false (unconnected)
    //    BEFORE interactable=true (connected); prove the lane is NOT touched on the refuse. ──
    // Covers: JOURNEY-LIVE-01 (ServerReady), JOURNEY-LIVE-04 (ticket visibility + interactable),
    //         JOURNEY-LIVE-05 (ManualInstrument resolution + empty refuse)
    static string SectionA_RootTicketGate()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "JOURNEY-LIVE-01: BackcastWorkspaceRoot missing in scene";

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "JOURNEY-LIVE-01: _host not found";
        s_host = host;

        var ticket = ty.GetField("_orderTicket", BF)?.GetValue(root) as OrderTicketView;
        var window = ty.GetField("_orderWindow", BF)?.GetValue(root) as RectTransform;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var footerSelected = ty.GetField("_footerSelected", BF)?.GetValue(root) as SelectedSymbol;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        if (ticket == null || window == null || footerMode == null || footerSelected == null || scenario == null)
            return "JOURNEY-LIVE-04/05: root seams not built (renamed?)";

        var onPlace = ty.GetMethod("OnManualPlace", BF);
        var drive = ty.GetMethod("DriveOrderTicket", BF);
        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (onPlace == null || drive == null || applyPoll == null) return "JOURNEY-LIVE-04/05: root methods not found (renamed?)";

        var vt = typeof(OrderTicketView);
        var instrumentLabel = vt.GetField("_instrument", BF)?.GetValue(ticket) as Text;
        var status = vt.GetField("_status", BF)?.GetValue(ticket) as Text;
        var placeBtn = vt.GetField("_placeBtn", BF)?.GetValue(ticket) as Button;
        var cancelBtn = vt.GetField("_cancelBtn", BF)?.GetValue(ticket) as Button;
        var qty = vt.GetField("_qty", BF)?.GetValue(ticket) as InputField;
        if (instrumentLabel == null || status == null || placeBtn == null || cancelBtn == null || qty == null)
            return "JOURNEY-LIVE-04/05: order ticket widgets not built (renamed?)";

        var oidField = ty.GetField("_manualOrderId", BF);
        var dirtyField = ty.GetField("_manualStatusDirty", BF);
        if (oidField == null || dirtyField == null) return "JOURNEY-LIVE-05: status volatiles not found (renamed?)";

        void Poll(string mode, string venueState)
            => applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"" + venueState + "\"}" });

        try
        {
            // 04 visibility: order window visible ONLY under LiveManual (non-vacuous: hidden under Replay).
            Poll("Replay", "");
            drive.Invoke(root, null);
            if (window.gameObject.activeSelf) return "JOURNEY-LIVE-04: order window visible under Replay";
            Poll("LiveManual", "CONNECTED");
            drive.Invoke(root, null);
            if (!window.gameObject.activeSelf) return "JOURNEY-LIVE-04: order window NOT visible under LiveManual";

            // 04 interactable (disabled half): unconnected host → buttons greyed (the non-vacuity anchor
            // for the connected-TRUE branch proven after login below).
            if (placeBtn.interactable) return "JOURNEY-LIVE-04: Place interactable on an unconnected host";

            // 05 ManualInstrument resolution: empty → hint; Universe[0] fallback; footer-selected priority.
            footerSelected.Clear();
            foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);
            drive.Invoke(root, null);
            if (instrumentLabel.text != "instrument: — (select one)")
                return "JOURNEY-LIVE-05: empty resolution did not show the select-one hint (got " + instrumentLabel.text + ")";
            scenario.AddInstrument("7203.TSE");
            drive.Invoke(root, null);
            if (instrumentLabel.text != "instrument: 7203.TSE")
                return "JOURNEY-LIVE-05: Universe[0] fallback not shown (got " + instrumentLabel.text + ")";
            footerSelected.Set(IID);
            drive.Invoke(root, null);
            if (instrumentLabel.text != "instrument: " + IID)
                return "JOURNEY-LIVE-05: footer-selected priority not shown (got " + instrumentLabel.text + ")";

            // Connect this host (MOCK) — bypass the batchmode ownership skip — and pump until CONNECTED.
            host.InitializePython(VENUE);
            if (!host.ServerReady) return "JOURNEY-LIVE-01: host not server-ready after InitializePython";
            bool loginOk = false;
            host.VenueLogin(VENUE, "env", "", (ok, _) => loginOk = ok);
            if (!WaitHost(() => loginOk, 10000, "venue login ack")) return "JOURNEY-LIVE-02: venue login timed out";
            if (!loginOk) return "JOURNEY-LIVE-02: venue login failed";
            if (!WaitHost(() => host.Conn.IsConnected, 10000, "badge CONNECTED")) return "JOURNEY-LIVE-02: badge did not converge to CONNECTED";

            // 04 interactable (enabled half): connected live session → SetInteractable(true) → buttons usable.
            Poll("LiveManual", "CONNECTED");
            drive.Invoke(root, null);
            if (!placeBtn.interactable) return "JOURNEY-LIVE-04: Place NOT interactable on a connected live session";
            if (!cancelBtn.interactable) return "JOURNEY-LIVE-04: Cancel NOT interactable on a connected live session";

            // 05 empty refuse: connect gate PASSES (connected) but instrument is unresolvable → live-order
            // safety REFUSES and the lane is NOT called (oid stays empty).
            foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);
            footerSelected.Clear();
            oidField.SetValue(root, "");
            dirtyField.SetValue(root, false);
            qty.text = "100";
            onPlace.Invoke(root, null);
            if (status.text != "last order: select an instrument (sidebar/universe) first")
                return "JOURNEY-LIVE-05: unresolved instrument not refused (got " + status.text + ")";
            WaitHost(() => false, 300, "(settle)");
            if ((string)oidField.GetValue(root) != "") return "JOURNEY-LIVE-05: lane was called despite refusal (_manualOrderId set)";
            if ((bool)dirtyField.GetValue(root)) return "JOURNEY-LIVE-05: lane callback fired despite refusal (dirty set)";

            return null;
        }
        finally
        {
            try { s_host?.Stop(); } catch (Exception e) { Debug.LogWarning("[E2E LIVE MANUAL TRADE] Section A host.Stop failed (non-fatal): " + e.Message); }
        }
    }

    // ── B-E. JOURNEY-LIVE-01b/02/03/06-13/15: the cross-cutting trade story on the secret-mock lanes
    //    (VenueLoginSecretProbe technique). main is GIL-FREE here (Section A's host left it so); every
    //    discrete _server call is wrapped in using(Py.GIL()); the lanes take their own GIL. ──
    static string RunTradeStory()
    {
        try
        {
            BuildSecretMockServer();                       // under Py.GIL()
            _lanes = new LiveRpcLanes(_server, _coord);
            _lanes.Start();
            Debug.Log("[E2E LIVE MANUAL TRADE MARK] secret-mock lanes started; main GIL-free; running journey");

            PhaseConnectAndModeGate();                     // 01b / 02 / 03 (neg + pos)
            PhasePlaceSecretFill();                        // 06 / 07 / 08 / 09
            PhasePositionsAndCancel();                     // 10 / 11 / 12
            PhaseSerializationLogoutGateAndLogout();       // 15 / 13
        }
        catch (Exception e) { _fail.Add("driver(trade): " + e); }
        finally
        {
            try { if (_lanes != null && !_lanesStopped) { _lanes.StopAndJoin(); _lanesStopped = true; } } catch { }
            try { if (_server != null && !_serverClosed) using (Py.GIL()) { _server.InvokeMethod("close").Dispose(); _serverClosed = true; } } catch { }
            // interpreter intentionally LEFT ALIVE (mirror WorkspaceEngineHost.Stop); process exits via EditorApplication.Exit.
        }

        if (_maxStall > MAX_STALL_MS) _fail.Add($"main stalled {_maxStall}ms (> {MAX_STALL_MS}ms) — not GIL-free");
        if (_leaked) _fail.Add("plaintext secret leaked into a drained wire event");
        return _fail.Count == 0 ? null : string.Join(" | ", _fail);
    }

    // Build InprocLiveServer with the throwaway SecretMockAdapter (no venue_login yet — 03-neg observes
    // the disconnected reject first). main is GIL-free, so wrap the whole build in using(Py.GIL()).
    static void BuildSecretMockServer()
    {
        using (Py.GIL())
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
        }
    }

    static void PhaseConnectAndModeGate()
    {
        // 03 negative: the mode precondition REJECTS LiveManual while disconnected (success=False is the
        // load-bearing invariant; the backend wraps the ValueError so the boundary returns a dict, no throw).
        using (Py.GIL())
        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
        {
            bool ok = m["success"].As<bool>();
            string ec = ok ? "" : m["error_code"].As<string>();
            Check(!ok, "JOURNEY-LIVE-03: LiveManual accepted while disconnected (precondition missing)");
            Debug.Log("[E2E LIVE MANUAL TRADE MARK] disconnected LiveManual reject code=" + ec);
        }

        // 02: venue_login → poll converges to CONNECTED + venue_id populated.
        using (Py.GIL())
        using (PyObject login = _server.InvokeMethod("venue_login", new PyString(VENUE), new PyString("env"), new PyString("")))
            Check(login["success"].As<bool>(), "JOURNEY-LIVE-02: venue_login failed: " + login["error_code"].As<string>());
        Check(WaitLanes(() => _conn.IsConnected, 10000, "venue_state CONNECTED"),
              "JOURNEY-LIVE-02: venue_state never reached CONNECTED");
        Check(_conn.VenueId != null, "JOURNEY-LIVE-02: venue_id not populated after login");

        // 03 positive: now connected, LiveManual is accepted.
        using (Py.GIL())
        using (PyObject m = _server.InvokeMethod("set_execution_mode", new PyString("LiveManual")))
            Check(m["success"].As<bool>(), "JOURNEY-LIVE-03: LiveManual rejected while connected: " + m["error_code"].As<string>());
    }

    static void PhasePlaceSecretFill()
    {
        long baseS = _vm.SecretRequiredCount;
        var r = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => r.Set(res));

        // 06 + 07: write lane accepts → SecretRequired drains on main (GIL-free); logout forbidden in-flight.
        Check(WaitLanes(() => _vm.SecretRequiredCount > baseS, 15000, "SecretRequired drain"),
              "JOURNEY-LIVE-07: SecretRequired never drained (SecretMockAdapter not injected?)");
        Check(!_coord.CanUserLogout, "JOURNEY-LIVE-07: logout must be forbidden during an in-flight write");
        var req = _vm.LatestSecretRequired;
        Check(req.Venue == VENUE && req.Kind == "second_secret",
              "JOURNEY-LIVE-07: SecretRequired fields wrong: " + req.Venue + "/" + req.Kind);

        // 08: type → submit on the urgent-secret lane → buffer zeroized, no plaintext on the wire.
        TypeAndSubmit(req);

        // 09: place returns FILLED.
        Check(WaitLanes(() => r.Done, 15000, "place result"), "JOURNEY-LIVE-09: place never returned");
        Check(r.Value.Success, "JOURNEY-LIVE-09: place not Success: " + r.Value.ErrorCode);
        Check(r.Value.Status == "FILLED", "JOURNEY-LIVE-09: place not FILLED: " + r.Value.Status);
        Check(_coord.CanUserLogout, "JOURNEY-LIVE-07: logout should re-enable after the write drained");
        _placedOid = r.Value.OrderId;
    }

    static void PhasePositionsAndCancel()
    {
        var fmtPositions = typeof(BackcastWorkspaceRoot).GetMethod("FormatPositions", SBF);
        if (fmtPositions == null) { _fail.Add("JOURNEY-LIVE-10: FormatPositions not found (renamed?)"); return; }

        // 10 (before): no position armed yet — the production Positions formatter reads flat.
        Pump();
        string before = (string)fmtPositions.Invoke(null, new object[] { _vm });
        Check(before == "(flat / no account snapshot)",
              "JOURNEY-LIVE-10: positions not flat BEFORE arming (vacuity guard), got: " + before);

        // arm a position on the adapter, then force_account_snapshot → AccountEvent push → sink.
        using (Py.GIL())
        using (PyObject sm = Py.Import("spike.venue_login_secret.secret_mock"))
            sm.InvokeMethod("arm_account_position", _server,
                new PyString(IID), new PyFloat(100.0), new PyFloat(11.0), new PyFloat(0.0),
                new PyFloat(1000000.0), new PyFloat(1000000.0)).Dispose();
        using (Py.GIL())
        using (PyObject res = _server.InvokeMethod("force_account_snapshot"))
            Check(res["success"].As<bool>(), "JOURNEY-LIVE-10: force_account_snapshot failed");

        // 10 (after): the AccountEvent drains to the panel → production FormatPositions shows the row.
        Check(WaitLanes(() =>
              {
                  string s = (string)fmtPositions.Invoke(null, new object[] { _vm });
                  return s.Contains(IID) && s.Contains("数量: 100");
              }, 10000, "position in Positions tile"),
              "JOURNEY-LIVE-10: fill position never surfaced in the Positions tile");

        // 11 + 12: cancel a RESTING (non-terminal) order → CANCELED. A FILLED order is terminal, so the
        // facade refuses it (ORDER_NOT_CANCELABLE, raised before the venue) — the story's step-9 cancel is a
        // *resting* order. Prove that contrast non-vacuously: FILLED → refused (neg), then a freshly-armed
        // ACCEPTED order → open → CANCELED (pos).

        // 11-neg (vacuity anchor): the FILLED order from the place phase is NOT cancelable.
        Check(!string.IsNullOrEmpty(_placedOid), "JOURNEY-LIVE-11: no filled order id to probe the not-cancelable branch");
        var cf = new Slot();
        _lanes.SubmitCancelOrder(VENUE, _placedOid, res => cf.Set(res));
        Check(WaitLanes(() => cf.Done, 15000, "filled cancel ACK"), "JOURNEY-LIVE-11: filled cancel never returned");
        Check(!cf.Value.Success && cf.Value.ErrorCode == "ORDER_NOT_CANCELABLE",
              "JOURNEY-LIVE-11: cancelling a FILLED order must be refused ORDER_NOT_CANCELABLE, got success=" + cf.Value.Success + " code=" + cf.Value.ErrorCode);

        // arm the next place to ACCEPTED (resting/open, non-terminal) so it stays cancelable, then place it
        // on the write lane (full second-secret flow, like PhasePlaceSecretFill).
        using (Py.GIL())
        using (PyObject sm = Py.Import("spike.venue_login_secret.secret_mock"))
            sm.InvokeMethod("arm_order", _server, new PyString("ACCEPTED"), new PyFloat(0.0), new PyFloat(0.0)).Dispose();
        long baseSc = _vm.SecretRequiredCount;
        var rest = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => rest.Set(res));
        Check(WaitLanes(() => _vm.SecretRequiredCount > baseSc, 15000, "resting place SecretRequired"),
              "JOURNEY-LIVE-11: resting place never prompted SecretRequired");
        TypeAndSubmit(_vm.LatestSecretRequired);
        Check(WaitLanes(() => rest.Done, 15000, "resting place result"), "JOURNEY-LIVE-11: resting place never returned");
        Check(rest.Value.Success, "JOURNEY-LIVE-11: resting place not Success: " + rest.Value.ErrorCode);
        // vacuity guard: the order is genuinely RESTING (non-terminal ACCEPTED) → it CAN be cancelled.
        Check(rest.Value.Status == "ACCEPTED", "JOURNEY-LIVE-11: resting place not ACCEPTED (open/cancelable): " + rest.Value.Status);
        string restingOid = rest.Value.OrderId;

        // 12 (positive): cancel the RESTING order on the write lane → mock 受付=確定 CANCELED
        // (kabu PENDING_CANCEL→poll 確定 is HITL).
        var c = new Slot();
        _lanes.SubmitCancelOrder(VENUE, restingOid, res => c.Set(res));
        Check(WaitLanes(() => c.Done, 15000, "cancel ACK"), "JOURNEY-LIVE-11: cancel never returned an ACK");
        Check(c.Value.Success, "JOURNEY-LIVE-11: cancel not Success: " + c.Value.ErrorCode);
        Check(c.Value.Status == "CANCELED", "JOURNEY-LIVE-12: cancel not CANCELED (mock 受付=確定): " + c.Value.Status);
    }

    static void PhaseSerializationLogoutGateAndLogout()
    {
        // 15a serialization: two places on the single write lane — #2 is NOT prompted until #1 resolves.
        long baseS = _vm.SecretRequiredCount;
        var s1 = new Slot();
        var s2 = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => s1.Set(res));
        _lanes.SubmitPlaceOrder(VENUE, IID, "SELL", 100.0, null, "MARKET", "DAY", res => s2.Set(res));

        Check(WaitLanes(() => _vm.SecretRequiredCount > baseS, 15000, "#1 SecretRequired"),
              "JOURNEY-LIVE-15: #1 SecretRequired never drained");
        Check(_vm.SecretRequiredCount == baseS + 1,
              "JOURNEY-LIVE-15: place #2 prompted before #1 resolved — write lane not serialized");
        var p1 = _vm.LatestSecretRequired;
        TypeAndSubmit(p1);
        Check(WaitLanes(() => s1.Done, 15000, "#1 result"), "JOURNEY-LIVE-15: #1 never resolved");
        Check(s1.Value.Success, "JOURNEY-LIVE-15: serialized #1 failed: " + s1.Value.ErrorCode);
        Check(WaitLanes(() => _vm.SecretRequiredCount > baseS + 1, 15000, "#2 SecretRequired"),
              "JOURNEY-LIVE-15: #2 never prompted after #1 resolved");
        var p2 = _vm.LatestSecretRequired;
        TypeAndSubmit(p2);
        Check(WaitLanes(() => s2.Done, 15000, "#2 result"), "JOURNEY-LIVE-15: #2 never resolved");
        Check(s2.Value.Success, "JOURNEY-LIVE-15: serialized #2 failed: " + s2.Value.ErrorCode);

        // 15b logout-gate: a logout requested WHILE a single write is in flight DEFERS, then promotes once
        // the order-write lane drains (single write → clean idle after, no BeginWrite race).
        long baseS2 = _vm.SecretRequiredCount;
        var g = new Slot();
        _lanes.SubmitPlaceOrder(VENUE, IID, "BUY", 100.0, null, "MARKET", "DAY", res => g.Set(res));
        Check(WaitLanes(() => _vm.SecretRequiredCount > baseS2, 15000, "logout-gate SecretRequired"),
              "JOURNEY-LIVE-15: logout-gate write never prompted");
        Check(!_coord.CanUserLogout, "JOURNEY-LIVE-15: logout must be forbidden while a write is in flight");
        Check(!_coord.RequestLogout(), "JOURNEY-LIVE-15: RequestLogout during in-flight write must DEFER (false)");
        Check(!_coord.ConsumePendingLogout(), "JOURNEY-LIVE-15: deferred logout must NOT be ready mid-write");
        TypeAndSubmit(_vm.LatestSecretRequired);
        Check(WaitLanes(() => g.Done, 15000, "logout-gate write drains"), "JOURNEY-LIVE-15: logout-gate write never resolved");
        Check(g.Value.Success, "JOURNEY-LIVE-15: logout-gate write failed: " + g.Value.ErrorCode);
        Check(_coord.ConsumePendingLogout(), "JOURNEY-LIVE-15: deferred logout must promote once the write lane drains");

        // 13: logout → DISCONNECTED converge. All lanes idle now → StopAndJoin must report a clean join,
        // then logout under the GIL + capture the final state (lanes are gone, so poll it directly).
        Check(_lanes.StopAndJoin(), "JOURNEY-LIVE-13: StopAndJoin must report all lanes joined cleanly");
        _lanesStopped = true;
        string stateStr;
        using (Py.GIL())
        {
            using (_server.InvokeMethod("set_execution_mode", new PyString("Replay"))) { }
            using (_server.InvokeMethod("venue_logout")) { }
            using (PyObject s = _server.InvokeMethod("get_state_json")) stateStr = s.As<string>();
            using (_server.InvokeMethod("close")) { }
            _serverClosed = true;
        }
        _conn.ApplyStatePoll(stateStr);
        Check(!_conn.IsConnected, "JOURNEY-LIVE-13: venue_state not DISCONNECTED after logout");
        Check(_conn.VenueId == null, "JOURNEY-LIVE-13: venue_id not cleared after logout");
    }

    // Type the secret into the modal (char[] only) and reply on the urgent-secret lane; zeroize after.
    static void TypeAndSubmit(LiveSecretRequiredEvent req)
    {
        _modal.Open(req, NowSec());
        _coord.SetSecretModalOpen(true);
        foreach (char c in SECRET) _modal.AppendChar(c);
        Check(_modal.MaskedDisplay.Length == SECRET.Length, "JOURNEY-LIVE-08: mask length mismatch");
        char[] payload = _modal.Submit();
        _coord.SetSecretModalOpen(false);
        Check(_modal.BufferIsZeroed(), "JOURNEY-LIVE-08: modal buffer not zeroized after submit");
        _lanes.SubmitSecret(req.RequestId, payload, _ => { });
    }

    // ---- lanes-substrate pump/wait (main GIL-free) ----
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

    static bool WaitLanes(Func<bool> cond, int timeoutMs, string label)
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
        return false;
    }

    // ---- root-substrate pump/wait (Section A; host owns the poll lane) ----
    static void PumpHost()
    {
        if (s_host == null) return;
        s_host.DrainLiveEvents();
        string st = s_host.LatestStateJson;
        if (!string.IsNullOrEmpty(st)) s_host.Conn.ApplyStatePoll(st);
    }

    static bool WaitHost(Func<bool> cond, int timeoutMs, string label)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpHost();
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    // ---- helpers ----
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
