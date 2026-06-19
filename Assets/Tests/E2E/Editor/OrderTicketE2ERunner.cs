// OrderTicketE2ERunner.cs — manual Order ticket surface E2E regression gate (台本: same-dir
// OrderTicketE2ERunner.md). 第二波11本目・全行新規。OrderTicketView (form) + BackcastWorkspaceRoot
// OnManualPlace/OnManualCancel/DriveOrderTicket (validation + lane marshalling) を網羅する。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod OrderTicketE2ERunner.Run -logFile <log>
//   # expect: [E2E ORDER TICKET PASS] ... / exit=0  （確認は Bash `grep -a "E2E ORDER TICKET"`. ripgrep/Select-String は取りこぼす）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// 設計判断 — 反射駆動（OrderTicketValidation を抽出しない）: OnManualPlace/OnManualCancel の検証ゲートは
// いずれも _orderTicket.SetStatus(...) を同期で呼んで return するので、OrderTicketView._status を反射で読めば
// 拒否理由が観測できる。RunButtonE2ERunner SectionD と同型に実 root を反射合成し private メソッドを反射 invoke
// する（production を変えずに済む・parity-first / 最小 diff）。よって抽出は不要。
//
// ゲート順 (production 1213→1225): qty → (LIMIT 時) limit-price → connect(!ServerReady||!Conn.IsConnected||
// Lanes==null) → instrument(ManualInstrument() 空). connect ゲートが instrument ゲートの手前にあるため、ORDER-09
// (instrument 未解決) は接続済み MOCK host でしか非 vacuous に検査できない（未接続だと connect ゲートで先に弾かれ
// ORDER-08 を検査してしまう）。そのため ORDER-09 は Section D (接続済み) に置く。
//
// 非 vacuity: 拒否系 section は同期 _status テキストが early-return 経路の証拠。Section D が同一接続 host 上で
// ORDER-05 happy place が実際に lane を呼ぶ (_manualOrderId が立つ) ことを実証するので、ORDER-09 の「lane 未呼出
// (_manualOrderId/_manualStatusDirty が clean のまま)」が意味を持つ（RunButton SectionD の blocked-vs-ready 同型）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public static class OrderTicketE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string VENUE = "MOCK";
    const string IID = "8918.TSE";

    static WorkspaceEngineHost s_host;

    public static void Run()
    {
        string fail;
        try
        {
            fail = SectionA_FormView()           // ORDER-01/02/03/04 (OrderTicketView form, Python-FREE)
                ?? SectionB_ValidationGates()     // ORDER-06/07/08/11a (reject gates, unconnected root)
                ?? SectionC_DisplayState()        // ORDER-12/13(off)/14/15 (display/state, unconnected root)
                ?? SectionD_MockLaneWiring();     // ORDER-05/09/10/11b/13(on) (connected MOCK lane)
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E ORDER TICKET PASS] form toggles + validation gates + display/state + MOCK lane place/cancel wiring green.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E ORDER TICKET FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── A. ORDER-01/02/03/04: the OrderTicketView form widgets. Build under a bare RectTransform (no GPU —
    //    read props / activeSelf, not pixels). vacuity guard: assert the widgets EXIST first (a renamed field
    //    → null → the toggles would false-green). ──
    // Covers: ORDER-01, ORDER-02, ORDER-03, ORDER-04
    static string SectionA_FormView()
    {
        var go = new GameObject("order_ticket_view_e2e", typeof(RectTransform));
        try
        {
            var body = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var view = new OrderTicketView();
            view.Build(body, font);

            var vt = typeof(OrderTicketView);
            var sideBtn = vt.GetField("_sideBtn", BF)?.GetValue(view) as Button;
            var typeBtn = vt.GetField("_typeBtn", BF)?.GetValue(view) as Button;
            var qty = vt.GetField("_qty", BF)?.GetValue(view) as InputField;
            var price = vt.GetField("_price", BF)?.GetValue(view) as InputField;
            var priceRow = vt.GetField("_priceRow", BF)?.GetValue(view) as RectTransform;
            if (sideBtn == null) return "form: _sideBtn not built";
            if (typeBtn == null) return "form: _typeBtn not built";
            if (qty == null) return "form: _qty not built";
            if (price == null) return "form: _price not built";
            if (priceRow == null) return "form: _priceRow not built";

            // ORDER-01: BUY/SELL toggle flips SideBuy.
            if (!view.SideBuy) return "ORDER-01: default side should be BUY";
            sideBtn.onClick.Invoke();
            if (view.SideBuy) return "ORDER-01: side did not flip to SELL on click";
            sideBtn.onClick.Invoke();
            if (!view.SideBuy) return "ORDER-01: side did not flip back to BUY";

            // ORDER-02: MARKET/LIMIT toggle flips Limit AND shows/hides the price row.
            if (view.Limit) return "ORDER-02: default type should be MARKET (not Limit)";
            if (priceRow.gameObject.activeSelf) return "ORDER-02: price row visible while MARKET";
            typeBtn.onClick.Invoke();
            if (!view.Limit) return "ORDER-02: type did not flip to LIMIT on click";
            if (!priceRow.gameObject.activeSelf) return "ORDER-02: price row not shown under LIMIT";
            typeBtn.onClick.Invoke();
            if (view.Limit) return "ORDER-02: type did not flip back to MARKET";
            if (priceRow.gameObject.activeSelf) return "ORDER-02: price row not hidden back under MARKET";

            // ORDER-03: Qty edit reads back through the Qty prop.
            qty.text = "250";
            if (view.Qty != "250") return "ORDER-03: Qty prop did not reflect field text (got " + view.Qty + ")";

            // ORDER-04: Limit price edit reads back through the Price prop.
            price.text = "1234.5";
            if (view.Price != "1234.5") return "ORDER-04: Price prop did not reflect field text (got " + view.Price + ")";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ── B. ORDER-06/07/08/11a: the validation reject gates, on an UNCONNECTED real root (Python-FREE). Each
    //    reject SetStatus()es synchronously then returns BEFORE the lane call — the _status text is the proof
    //    of the early-return path. vacuity guard: assert the order ticket widgets EXIST first. ──
    // Covers: ORDER-06, ORDER-07, ORDER-08, ORDER-11a (cancel "not connected"; ORDER-11b oid-resolution in SectionD)
    static string SectionB_ValidationGates()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "gates: BackcastWorkspaceRoot missing in scene";

        var ticket = ty.GetField("_orderTicket", BF)?.GetValue(root) as OrderTicketView;
        if (ticket == null) return "gates: _orderTicket not built";
        var vt = typeof(OrderTicketView);
        var qty = vt.GetField("_qty", BF)?.GetValue(ticket) as InputField;
        var price = vt.GetField("_price", BF)?.GetValue(ticket) as InputField;
        var typeBtn = vt.GetField("_typeBtn", BF)?.GetValue(ticket) as Button;
        var status = vt.GetField("_status", BF)?.GetValue(ticket) as Text;
        if (qty == null || price == null || typeBtn == null || status == null)
            return "gates: order ticket widgets not built (renamed?)";

        var onPlace = ty.GetMethod("OnManualPlace", BF);
        var onCancel = ty.GetMethod("OnManualCancel", BF);
        if (onPlace == null) return "gates: OnManualPlace not found (renamed?)";
        if (onCancel == null) return "gates: OnManualCancel not found (renamed?)";

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "gates: _host not found";
        if (host.ServerReady) return "gates: precondition — host unexpectedly server-ready (should be offline)";

        // ORDER-06: invalid qty rejected at the FIRST gate (before any host access).
        qty.text = "0";
        onPlace.Invoke(root, null);
        if (status.text != "last order: invalid qty") return "ORDER-06: qty=0 not rejected as invalid qty (got " + status.text + ")";
        qty.text = "abc";
        onPlace.Invoke(root, null);
        if (status.text != "last order: invalid qty") return "ORDER-06: qty=abc not rejected as invalid qty (got " + status.text + ")";

        // ORDER-07: LIMIT with an empty/unparseable price rejected at the limit-price gate.
        qty.text = "100";                              // qty valid → reach the price gate
        if (!ticket.Limit) typeBtn.onClick.Invoke();   // MARKET→LIMIT
        if (!ticket.Limit) return "ORDER-07: could not switch to LIMIT";
        price.text = "";
        onPlace.Invoke(root, null);
        if (status.text != "last order: invalid limit price") return "ORDER-07: empty limit price not rejected (got " + status.text + ")";

        // ORDER-08: a valid MARKET order on an UNCONNECTED host rejected at the connect gate.
        if (ticket.Limit) typeBtn.onClick.Invoke();    // LIMIT→MARKET (skip the price gate)
        if (ticket.Limit) return "ORDER-08: could not switch back to MARKET";
        qty.text = "100";
        onPlace.Invoke(root, null);
        if (status.text != "last order: connect a venue first") return "ORDER-08: unconnected place not rejected at connect gate (got " + status.text + ")";

        // ORDER-11a: Cancel with no lanes (unconnected) → "not connected".
        onCancel.Invoke(root, null);
        if (status.text != "last order: not connected") return "ORDER-11a: cancel on unconnected host not 'not connected' (got " + status.text + ")";

        return null;
    }

    // ── C. ORDER-12/13(disabled)/14/15: display/state on an UNCONNECTED root (Python-FREE). Drive footer mode
    //    via the real FooterModeViewModel.ApplyPoll (DisplayMode has a private setter) and DriveOrderTicket.
    //    vacuity guard: assert seams/widgets EXIST first. ──
    // Covers: ORDER-12, ORDER-13 (disabled — enabled in SectionD), ORDER-14, ORDER-15
    static string SectionC_DisplayState()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "display: BackcastWorkspaceRoot missing in scene";

        var ticket = ty.GetField("_orderTicket", BF)?.GetValue(root) as OrderTicketView;
        var window = ty.GetField("_orderWindow", BF)?.GetValue(root) as RectTransform;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var footerSelected = ty.GetField("_footerSelected", BF)?.GetValue(root) as SelectedSymbol;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        if (ticket == null) return "display: _orderTicket not built";
        if (window == null) return "display: _orderWindow missing in scene";
        if (footerMode == null) return "display: _footerMode not built";
        if (footerSelected == null) return "display: _footerSelected not built";
        if (scenario == null) return "display: _scenario not found";

        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (applyPoll == null) return "display: FooterModeViewModel.ApplyPoll not found (renamed?)";
        var drive = ty.GetMethod("DriveOrderTicket", BF);
        if (drive == null) return "display: DriveOrderTicket not found (renamed?)";

        var vt = typeof(OrderTicketView);
        var instrumentLabel = vt.GetField("_instrument", BF)?.GetValue(ticket) as Text;
        var statusLabel = vt.GetField("_status", BF)?.GetValue(ticket) as Text;
        var placeBtn = vt.GetField("_placeBtn", BF)?.GetValue(ticket) as Button;
        var cancelBtn = vt.GetField("_cancelBtn", BF)?.GetValue(ticket) as Button;
        if (instrumentLabel == null || statusLabel == null || placeBtn == null || cancelBtn == null)
            return "display: order ticket widgets not built (renamed?)";

        void Poll(string mode, string venueState)
            => applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"" + venueState + "\"}" });

        // ORDER-14: the order window is visible ONLY under the LiveManual footer mode.
        Poll("Replay", "");
        drive.Invoke(root, null);
        if (window.gameObject.activeSelf) return "ORDER-14: order window visible under Replay";
        Poll("LiveAuto", "CONNECTED");
        drive.Invoke(root, null);
        if (window.gameObject.activeSelf) return "ORDER-14: order window visible under LiveAuto";
        Poll("LiveManual", "CONNECTED");
        drive.Invoke(root, null);
        if (!window.gameObject.activeSelf) return "ORDER-14: order window NOT visible under LiveManual";

        // ORDER-12: the instrument label follows ManualInstrument() — empty → hint, Universe[0] fallback,
        //   _footerSelected priority. (footer stays LiveManual so DriveOrderTicket pushes the instrument.)
        footerSelected.Clear();
        foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);
        drive.Invoke(root, null);
        if (instrumentLabel.text != "instrument: — (select one)")
            return "ORDER-12: empty resolution did not show the select-one hint (got " + instrumentLabel.text + ")";
        scenario.AddInstrument("7203.TSE");
        drive.Invoke(root, null);
        if (instrumentLabel.text != "instrument: 7203.TSE")
            return "ORDER-12: Universe[0] fallback not shown (got " + instrumentLabel.text + ")";
        footerSelected.Set("8918.TSE");
        drive.Invoke(root, null);
        if (instrumentLabel.text != "instrument: 8918.TSE")
            return "ORDER-12: footer-selected priority not shown (got " + instrumentLabel.text + ")";

        // ORDER-13 (disabled half): an unconnected host → SetInteractable(false) → buttons greyed.
        drive.Invoke(root, null);
        if (placeBtn.interactable) return "ORDER-13: Place interactable on an unconnected host";
        if (cancelBtn.interactable) return "ORDER-13: Cancel interactable on an unconnected host";

        // ORDER-15: a worker-thread status (volatiles + dirty flag) is marshaled to the view on the next
        //   DriveOrderTicket (main) and the dirty flag is cleared.
        var lineField = ty.GetField("_manualStatusLine", BF);
        var dirtyField = ty.GetField("_manualStatusDirty", BF);
        if (lineField == null || dirtyField == null) return "ORDER-15: status marshal fields not found (renamed?)";
        lineField.SetValue(root, "FILLED (E2E-OID-1)");
        dirtyField.SetValue(root, true);
        drive.Invoke(root, null);
        if (statusLabel.text != "last order: FILLED (E2E-OID-1)")
            return "ORDER-15: dirty status not marshaled to the view (got " + statusLabel.text + ")";
        if ((bool)dirtyField.GetValue(root)) return "ORDER-15: dirty flag not cleared after marshal";

        return null;
    }

    // ── D. ORDER-05/09/10/11b/13(enabled): the connected MOCK lane. Claim Python on THIS host
    //    (host.InitializePython MOCK — bypass the batchmode ownership skip) + login the MOCK venue, pump the
    //    host until the poll badge converges to CONNECTED, then reflect-invoke OnManualPlace/OnManualCancel.
    //    NON-VACUOUS: ORDER-09 refuses (no instrument) and leaves the lane untouched; ORDER-05 then proves the
    //    SAME host DOES reach the lane (an order id appears) — so "lane not called" is meaningful. ──
    // Covers: ORDER-05, ORDER-09, ORDER-10, ORDER-11b (oid-resolution), ORDER-13 (enabled)
    static string SectionD_MockLaneWiring()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "lane: BackcastWorkspaceRoot missing in scene";

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "lane: _host not found";
        s_host = host;

        var ticket = ty.GetField("_orderTicket", BF)?.GetValue(root) as OrderTicketView;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var footerSelected = ty.GetField("_footerSelected", BF)?.GetValue(root) as SelectedSymbol;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        if (ticket == null || footerMode == null || footerSelected == null || scenario == null)
            return "lane: root seams not built (renamed?)";

        var onPlace = ty.GetMethod("OnManualPlace", BF);
        var onCancel = ty.GetMethod("OnManualCancel", BF);
        var drive = ty.GetMethod("DriveOrderTicket", BF);
        if (onPlace == null || onCancel == null || drive == null) return "lane: manual order methods not found (renamed?)";

        var vt = typeof(OrderTicketView);
        var status = vt.GetField("_status", BF)?.GetValue(ticket) as Text;
        var placeBtn = vt.GetField("_placeBtn", BF)?.GetValue(ticket) as Button;
        var cancelBtn = vt.GetField("_cancelBtn", BF)?.GetValue(ticket) as Button;
        var qty = vt.GetField("_qty", BF)?.GetValue(ticket) as InputField;
        if (status == null || placeBtn == null || cancelBtn == null || qty == null)
            return "lane: order ticket widgets not built (renamed?)";

        var oidField = ty.GetField("_manualOrderId", BF);
        var dirtyField = ty.GetField("_manualStatusDirty", BF);
        if (oidField == null || dirtyField == null) return "lane: status volatiles not found (renamed?)";

        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (applyPoll == null) return "lane: ApplyPoll not found (renamed?)";

        try
        {
            host.InitializePython(VENUE);
            if (!host.ServerReady) return "lane: host not server-ready after InitializePython";
            bool loginOk = false;
            host.VenueLogin(VENUE, "env", "", (ok, _) => loginOk = ok);
            if (!WaitUntil(() => loginOk, 10000, "venue login ack")) return "lane: venue login timed out";
            if (!loginOk) return "lane: venue login failed";
            if (!WaitUntil(() => host.Conn.IsConnected, 10000, "badge CONNECTED")) return "lane: badge did not converge to CONNECTED";

            // ORDER-13 (enabled half): a connected live session → SetInteractable(true) → buttons usable.
            applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"LiveManual\",\"venue_state\":\"CONNECTED\"}" });
            drive.Invoke(root, null);
            if (!placeBtn.interactable) return "ORDER-13: Place NOT interactable on a connected live session";
            if (!cancelBtn.interactable) return "ORDER-13: Cancel NOT interactable on a connected live session";

            // ORDER-11b: cancel with no resolvable order (oid empty, no panel order) → "no order to cancel"
            //   (lanes ARE present — this is the oid-resolution reject, not the not-connected reject).
            footerSelected.Clear();
            oidField.SetValue(root, "");
            onCancel.Invoke(root, null);
            if (status.text != "last order: no order to cancel") return "ORDER-11b: cancel with no order not rejected (got " + status.text + ")";

            // ORDER-09: connect gate PASSES (connected) but the instrument is unresolvable (empty universe +
            //   no focus) → live-order safety REFUSES and the lane is NOT called.
            foreach (var id in new List<string>(scenario.Universe.Ids)) scenario.RemoveInstrument(id);
            footerSelected.Clear();
            oidField.SetValue(root, "");
            dirtyField.SetValue(root, false);
            qty.text = "100";
            onPlace.Invoke(root, null);
            if (status.text != "last order: select an instrument (sidebar/universe) first")
                return "ORDER-09: unresolved instrument not refused (got " + status.text + ")";
            WaitUntil(() => false, 300, "(settle)");   // pump briefly; confirm nothing came back
            if ((string)oidField.GetValue(root) != "") return "ORDER-09: lane was called despite refusal (_manualOrderId set)";
            if ((bool)dirtyField.GetValue(root)) return "ORDER-09: lane callback fired despite refusal (dirty set)";

            // ORDER-05: a resolvable instrument + valid qty → OnManualPlace marshals to SubmitPlaceOrder; the
            //   MOCK venue fills and the result callback sets _manualOrderId. The non-vacuity anchor: the SAME
            //   connected host demonstrably reaches the lane here (an order id appears).
            footerSelected.Set(IID);
            qty.text = "100";
            onPlace.Invoke(root, null);
            if (!WaitUntil(() => !string.IsNullOrEmpty((string)oidField.GetValue(root)), 15000, "place ACK order id"))
                return "ORDER-05: place did not produce an order id (lane not reached or no ACK)";
            string placedOid = (string)oidField.GetValue(root);
            drive.Invoke(root, null);   // marshal the worker status to the view
            if (!status.text.StartsWith("last order: ")) return "ORDER-05: status not marshaled (got " + status.text + ")";

            // ORDER-10: Cancel last → oid resolves to _manualOrderId → SubmitCancelOrder; the synchronous
            //   status proves the right oid was marshaled, then the lane ACK arrives (取消受付 = non-terminal).
            dirtyField.SetValue(root, false);
            onCancel.Invoke(root, null);
            if (status.text != "last order: cancel " + placedOid + "…")
                return "ORDER-10: cancel did not target the placed order id (got " + status.text + ")";
            if (!WaitUntil(() => (bool)dirtyField.GetValue(root), 15000, "cancel ACK"))
                return "ORDER-10: cancel lane never returned an ACK";

            return null;
        }
        finally
        {
            try { s_host?.Stop(); } catch (Exception e) { Debug.LogWarning("[E2E ORDER TICKET] host.Stop failed (non-fatal): " + e.Message); }
        }
    }

    // ---- helpers ----
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

    static void Pump()
    {
        if (s_host == null) return;
        s_host.DrainLiveEvents();
        string st = s_host.LatestStateJson;
        if (!string.IsNullOrEmpty(st)) s_host.Conn.ApplyStatePoll(st);
    }

    static bool WaitUntil(Func<bool> cond, int timeoutMs, string label)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }
}
