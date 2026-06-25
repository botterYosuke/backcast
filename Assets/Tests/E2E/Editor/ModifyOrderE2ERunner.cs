// ModifyOrderE2ERunner.cs — 注文訂正（modify modal）surface E2E regression gate（台本: same-dir
// ModifyOrderE2ERunner.md）。issue #34 / 設計の木 findings 0101（D1 一覧+modal / D2 減数のみ /
// D3 status 返し分け / D5 kabu 警告 ack）。OrderTicketE2ERunner をミラーした反射駆動・Python-FREE。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod ModifyOrderE2ERunner.Run -logFile <abs>
//   # expect: [E2E MODIFY ORDER PASS] ... / exit=0（確認は Bash `grep -a "E2E MODIFY"`）
//   # per-Action-ID タグ（[E2E MODIFY-01 PASS] 等・空白なし単一トークン）も到達点で吐き rollup に載せる。
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// 設計判断 — 検証ロジックは ModifyModalController（plain C#・Python 非依存）に集約したので、減数のみ /
// 同値拒否 / ack gate は controller を直接駆動して assert できる（最も非 vacuous で速い）。一覧描画と
// 行イベントは OrderTicketView を bare RectTransform 下に Build して反射、行→root→modal の配線は ComposeRoot
// の実 root で OnRowModify を反射 invoke して controller.Open を見る。
//
// 範囲外（本 runner では扱わない・台本に記載）: MODIFY-10 status 返し分けの実 lane roundtrip と
// MODIFY-20/21 facade took-effect は **pytest 正本**（test_order_facade_modify.py・[E2E MODIFY-20/21 PASS]）。
// MODIFY-11 実 kabu cancel+replace は HITL。

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public static class ModifyOrderE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail;
        try
        {
            fail = SectionA_ControllerValidation()   // MODIFY-05/06/07/08/09 (減数のみ / 同値 / ack gate)
                ?? SectionB_ListAndEvents()           // MODIFY-01/02/03/04 (一覧描画 + 行/更新 イベント)
                ?? SectionC_RootIntegration();        // MODIFY-02b (行[訂正]→root→modal open) + 警告行可視性
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E MODIFY ORDER PASS] resting list render + row events + 減数のみ/同値/ack validation + row→modal wiring green.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E MODIFY ORDER FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── A. ModifyModalController の検証ポリシー（findings 0101 D2/D5）。pure C#・Python-FREE。 ──
    // Covers: MODIFY-05 (変更なし拒否), MODIFY-06 (減数のみ=増数/同値拒否), MODIFY-07 (約定済み下限),
    //         MODIFY-08 (価格 同値拒否), MODIFY-09 (cancel+replace venue の ack gate)
    static string SectionA_ControllerValidation()
    {
        var c = new ModifyModalController();

        // 原: qty=100, price=2500, filled=20, 警告不要（atomic venue）。
        c.OpenFor("m1", 100.0, 2500.0, 20.0, requiresCancelReplaceAck: false);
        if (!c.Open) return "MODIFY-05: OpenFor did not open";

        // MODIFY-05: 両欄空 → 変更なしで Confirm 不可。
        if (c.CanConfirm()) return "MODIFY-05: empty form should not be confirmable";
        if (c.ValidationError() != "変更がありません") return "MODIFY-05: empty error wrong (" + c.ValidationError() + ")";
        Pass("MODIFY-05");

        // MODIFY-06: 数量は減数のみ。増数・同値は拒否、減数は可。
        c.NewQtyBuf = "120"; c.NewPriceBuf = "";
        if (c.CanConfirm()) return "MODIFY-06: qty increase (120>100) should be rejected";
        c.NewQtyBuf = "100";
        if (c.CanConfirm()) return "MODIFY-06: qty same (100==100) should be rejected";
        c.NewQtyBuf = "60";
        if (!c.CanConfirm()) return "MODIFY-06: valid decrease (60) should be confirmable (" + c.ValidationError() + ")";
        Pass("MODIFY-06");

        // MODIFY-07: 約定済み(20)を下回る減数は拒否。
        c.NewQtyBuf = "10";
        if (c.CanConfirm()) return "MODIFY-07: qty below filled (10<20) should be rejected";
        Pass("MODIFY-07");

        // MODIFY-08: 価格は原注文と同値を拒否、変更は可（qty 欄は空＝変更なし）。
        c.NewQtyBuf = ""; c.NewPriceBuf = "2500";
        if (c.CanConfirm()) return "MODIFY-08: price same (2500) should be rejected";
        if (c.ValidationError() != "価格が原注文と同値です") return "MODIFY-08: same-price error wrong (" + c.ValidationError() + ")";
        c.NewPriceBuf = "2400";
        if (!c.CanConfirm()) return "MODIFY-08: changed price (2400) should be confirmable (" + c.ValidationError() + ")";
        Pass("MODIFY-08");

        // MODIFY-09: cancel+replace venue（kabu）は ack を Confirm の前提にする。
        c.OpenFor("m2", 100.0, 2500.0, 0.0, requiresCancelReplaceAck: true);
        c.NewQtyBuf = "60"; c.NewPriceBuf = "";
        if (c.CanConfirm()) return "MODIFY-09: cancel-replace venue without ack should not confirm";
        if (c.ValidationError() != "訂正の確認（ack）が必要です") return "MODIFY-09: ack-gate error wrong (" + c.ValidationError() + ")";
        c.AckCancelReplace = true;
        if (!c.CanConfirm()) return "MODIFY-09: ack checked should make valid change confirmable (" + c.ValidationError() + ")";
        Pass("MODIFY-09");

        return null;
    }

    // ── B. OrderTicketView の resting 一覧描画 + 行/更新 イベント。bare RectTransform 下に Build。 ──
    // Covers: MODIFY-01 (一覧描画), MODIFY-02 (行[訂正]→ModifyRowRequested), MODIFY-03 (行[取消]→CancelRowRequested),
    //         MODIFY-04 ([更新]→RefreshRequested)
    static string SectionB_ListAndEvents()
    {
        var go = new GameObject("modify_list_view_e2e", typeof(RectTransform));
        try
        {
            var body = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var view = new OrderTicketView();
            view.Build(body, font);

            var vt = typeof(OrderTicketView);
            var container = vt.GetField("_restingContainer", BF)?.GetValue(view) as RectTransform;
            var header = vt.GetField("_restingHeader", BF)?.GetValue(view) as Text;
            if (container == null) return "list: _restingContainer not built (renamed?)";
            if (header == null) return "list: _restingHeader not built (renamed?)";

            // MODIFY-01: 2 行を渡すと 2 行ぶんの行 GameObject が生成され、見出しが件数を出す。
            view.SetRestingOrders(new List<OrderTicketView.RestingRowVM>
            {
                new OrderTicketView.RestingRowVM { OrderId = "m1", Label = "7203 BUY 100 @2500" },
                new OrderTicketView.RestingRowVM { OrderId = "m2", Label = "6758 SELL 50 @1200" },
            });
            var modBtns = FindButtonsByLabel(container, "訂正");
            var canBtns = FindButtonsByLabel(container, "取消");
            if (modBtns.Count != 2) return "MODIFY-01: expected 2 [訂正] buttons, got " + modBtns.Count;
            if (canBtns.Count != 2) return "MODIFY-01: expected 2 [取消] buttons, got " + canBtns.Count;
            if (header.text != "resting: 2") return "MODIFY-01: header wrong (" + header.text + ")";
            Pass("MODIFY-01");

            // MODIFY-02: 1 行目の [訂正] クリックで ModifyRowRequested(orderId="m1")。
            string modId = null; view.ModifyRowRequested += id => modId = id;
            modBtns[0].onClick.Invoke();
            if (modId != "m1") return "MODIFY-02: ModifyRowRequested orderId wrong (" + (modId ?? "<null>") + ")";
            Pass("MODIFY-02");

            // MODIFY-03: 1 行目の [取消] クリックで CancelRowRequested(orderId="m1")。
            string canId = null; view.CancelRowRequested += id => canId = id;
            canBtns[0].onClick.Invoke();
            if (canId != "m1") return "MODIFY-03: CancelRowRequested orderId wrong (" + (canId ?? "<null>") + ")";
            Pass("MODIFY-03");

            // MODIFY-04: [更新] クリックで RefreshRequested。
            bool refreshed = false; view.RefreshRequested += () => refreshed = true;
            var refreshBtns = FindButtonsByLabel(body, "更新");
            if (refreshBtns.Count < 1) return "MODIFY-04: [更新] button not found";
            refreshBtns[0].onClick.Invoke();
            if (!refreshed) return "MODIFY-04: RefreshRequested not raised on 更新 click";
            Pass("MODIFY-04");

            // 空一覧で件数が 0 になること（vacuity 逆コントロール）。行 GameObject の破棄は Destroy で
            // 次フレーム遅延するため、edit-mode で同フレーム同期に観測できる見出し件数で判定する。
            view.SetRestingOrders(new List<OrderTicketView.RestingRowVM>());
            if (header.text != "resting: 0") return "MODIFY-01: header not cleared on empty refresh (" + header.text + ")";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ── C. 行[訂正] → root.OnRowModify → ModifyModalController.Open の実 root 配線、警告行の可視性。 ──
    // Covers: MODIFY-02b (行→root→modal open + prefill 原値), 警告行 hidden（atomic venue・Conn 既定 false）
    static string SectionC_RootIntegration()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "root: BackcastWorkspaceRoot missing in scene";

        var modal = ty.GetField("_modifyModal", BF)?.GetValue(root) as ModifyModalController;
        var overlay = ty.GetField("_modifyOverlay", BF)?.GetValue(root) as ModifyModalOverlay;
        var onRowModify = ty.GetMethod("OnRowModify", BF);
        var onModifyCancel = ty.GetMethod("OnModifyCancel", BF);
        var restingField = ty.GetField("_restingRowsLatest", BF);
        if (modal == null) return "root: _modifyModal not built (renamed?)";
        if (overlay == null) return "root: _modifyOverlay not built (renamed?)";
        if (onRowModify == null) return "root: OnRowModify not found (renamed?)";
        if (onModifyCancel == null) return "root: OnModifyCancel not found (renamed?)";
        if (restingField == null) return "root: _restingRowsLatest not found (renamed?)";

        // 最新 get_orders 行を 1 件 stash（lane を回さず直接注入）。
        restingField.SetValue(root, new List<RestingOrderRpcRow>
        {
            new RestingOrderRpcRow
            { OrderId = "m1", Symbol = "7203", Side = "BUY",
              Qty = 100.0, FilledQty = 0.0, HasPrice = true, Price = 2500.0 },
        });

        if (modal.Open) return "MODIFY-02b: modal unexpectedly open before row click";
        onRowModify.Invoke(root, new object[] { "m1" });
        if (!modal.Open) return "MODIFY-02b: OnRowModify did not open the modal";
        if (modal.OrderId != "m1") return "MODIFY-02b: modal OrderId wrong (" + modal.OrderId + ")";
        if (modal.OriginalQty != 100.0) return "MODIFY-02b: modal OriginalQty not prefilled (" + modal.OriginalQty + ")";
        if (!modal.OriginalPrice.HasValue || modal.OriginalPrice.Value != 2500.0)
            return "MODIFY-02b: modal OriginalPrice not prefilled";

        // 警告行: 既定 MOCK/未接続 Conn は ModifyIsCancelReplace=false ⇒ 警告行は hidden。
        var warnRow = typeof(ModifyModalOverlay).GetField("_warnRow", BF)?.GetValue(overlay) as RectTransform;
        if (warnRow == null) return "MODIFY-02b: overlay _warnRow not built (renamed?)";
        if (warnRow.gameObject.activeSelf) return "MODIFY-09b: warning row should be hidden for atomic venue";
        Pass("MODIFY-02b");

        // MODIFY-09b (positive): cancel+replace venue（kabu）は Configure(...true) で警告行が active になる。
        overlay.Configure("m1", "7203 BUY", 100.0, 2500.0, 0.0, requiresCancelReplaceAck: true);
        if (!warnRow.gameObject.activeSelf) return "MODIFY-09b: warning row should be ACTIVE for cancel+replace venue";
        Pass("MODIFY-09b");

        // キャンセルで modal が閉じる。
        onModifyCancel.Invoke(root, null);
        if (modal.Open) return "MODIFY-02b: OnModifyCancel did not close the modal";

        return null;
    }

    // ── helpers ──
    static List<Button> FindButtonsByLabel(Component scope, string label)
    {
        var found = new List<Button>();
        if (scope == null) return found;
        foreach (var b in scope.GetComponentsInChildren<Button>(true))
        {
            var t = b.GetComponentInChildren<Text>(true);
            if (t != null && t.text == label) found.Add(b);
        }
        return found;
    }

    static void Pass(string id) => Debug.Log("[E2E " + id + " PASS]");

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
