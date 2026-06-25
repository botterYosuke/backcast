# ModifyOrderE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`ModifyOrderE2ERunner.cs` が自動検証する **注文訂正（modify modal）サーフェス**の台本。実装者は `.cs` と本 `.md`
をセットで読む。共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。
設計の木の正本は [docs/findings/0101-issue34-modify-order-ui.md](../../../../docs/findings/0101-issue34-modify-order-ui.md)。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。注文訂正は「resting 一覧 → 行選択 → 訂正 modal（数量/価格・
> 減数のみ・kabu 警告 ack）→ 訂正発行 → status 返し分け」。**実 venue での訂正約定**（kabu cancel+replace の
> 部分失敗含む）は外部認証・実資金依存なので *HITL*（`MODIFY-11`）。engine の took-effect 契約（CANCELED で
> 幻の新数量を書かない・findings 0101 D4）は **pytest 正本**（`test_order_facade_modify.py`・`MODIFY-20/21`）。

## 対象サーフェス

手動 Order ticket（`OrderTicketView`）に埋め込んだ resting 注文一覧と、`ModifyModalOverlay`（入力面）＋
`ModifyModalController`（検証の頭脳・plain C#）。一覧は `get_orders` を `LiveRpcLanes.SubmitGetOrders`（write lane）
で読み、`BackcastWorkspaceRoot.OnRowModify/OnRowCancel/RefreshRestingOrders/OnModifyConfirm/DriveModifyModal` が
配線する。**footer mode が LiveManual のときだけ可視**（Order ticket 窓に同居）。

## 対象ユーザー行動

resting 一覧の閲覧・[更新]、行の [訂正]（→ modal を原値表示で開く）/[取消]、modal での 数量/価格 入力、
kabu 警告 ack（cancel+replace venue のみ）、[確認して訂正]（減数のみ・同値拒否を満たすと有効）/[キャンセル]、
訂正結果の status 返し分け（ACCEPTED=訂正確定 / CANCELED=取消成立・要再発注 / REJECTED=訂正拒否）。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| MODIFY-01 | resting 一覧の描画 | `OrderTicketView.SetRestingOrders` | get_orders 行ごとに [訂正]/[取消] 行が生成、見出しが件数 | 2 行渡し → 行 GameObject/ボタン数・見出し text を反射 assert、空一覧で消えることも | 自動(E2E済) | — |
| MODIFY-02 | 行 [訂正] クリック | `OrderTicketView.cs`（行ボタン） | `ModifyRowRequested(orderId)` が上がる | 1 行目 訂正 invoke → orderId assert | 自動(E2E済) | — |
| MODIFY-02b | 行 [訂正] → modal open（原値 prefill） | `BackcastWorkspaceRoot.OnRowModify` | controller.Open / OrderId / OriginalQty / OriginalPrice が選択行で埋まる | 実 root に行を stash → OnRowModify 反射 invoke → controller 反射 assert | 自動(E2E済) | — |
| MODIFY-03 | 行 [取消] クリック | `OrderTicketView.cs`（行ボタン） | `CancelRowRequested(orderId)` が上がる | 1 行目 取消 invoke → orderId assert | 自動(E2E済) | — |
| MODIFY-04 | 一覧 [更新] | `OrderTicketView.cs`（更新ボタン） | `RefreshRequested` が上がる（root が get_orders 再取得） | 更新 invoke → flag assert | 自動(E2E済) | — |
| MODIFY-05 | 変更なしの拒否 | `ModifyModalController.ValidationError` | qty/price とも空 → 「変更がありません」・Confirm 不可 | controller 直駆動 | 自動(E2E済) | — |
| MODIFY-06 | 数量は減数のみ | `ModifyModalController.ValidationError` | 増数(120>100)・同値(100) 拒否、減数(60) 可 | controller 直駆動 | 自動(E2E済) | — |
| MODIFY-07 | 約定済み下限 | `ModifyModalController.ValidationError` | new_qty < filled(20) 拒否 | controller 直駆動 | 自動(E2E済) | — |
| MODIFY-08 | 価格 同値の拒否 | `ModifyModalController.ValidationError` | price==original 拒否「原注文と同値」、変更可 | controller 直駆動 | 自動(E2E済) | — |
| MODIFY-09 | kabu 警告 ack gate | `ModifyModalController.ValidationError` | cancel+replace venue は ack 未チェックで Confirm 不可、ack で可 | controller 直駆動（requiresAck=true） | 自動(E2E済) | — |
| MODIFY-09b | 警告行の可視性 | `ModifyModalOverlay.Configure` | atomic venue（Conn.ModifyIsCancelReplace=false）で警告行 hidden | 実 root overlay の `_warnRow.activeSelf` を反射 assert | 自動(E2E済) | — |
| MODIFY-10 | 訂正発行 → status 返し分け | `BackcastWorkspaceRoot.OnModifyConfirm` | ACCEPTED=訂正確定 / CANCELED=取消成立・要再発注 / REJECTED=訂正拒否 を status に返す | engine 半分（CANCELED で幻qty無し）は pytest `MODIFY-20/21`。UI 行の実 lane roundtrip は要 MOCK-live | 自動(pytest・engine)／要新規自動化(UI line) | `test_order_facade_modify.py` |
| MODIFY-11 | 実 venue 訂正（kabu cancel+replace の部分失敗含む） | adapter `modify_order` | demo/実 venue で訂正→確定、kabu は取消成立・要再発注の警告 | 外部認証・実資金依存 | HITL専用（demo=立花 atomic / 実 kabu cancel+replace） | — |
| MODIFY-20 | facade.modify CANCELED で幻の新数量を書かない | `order_facade.modify`（took-effect ゲート） | CANCELED は _intents 据え置き・終端化 | pytest `@scenario("MODIFY-20")` | 自動(pytest) | `test_order_facade_modify.py` |
| MODIFY-21 | facade.modify ACCEPTED で新数量を反映 | `order_facade.modify` | ACCEPTED は _intents に new_qty/new_price | pytest `@scenario("MODIFY-21")` | 自動(pytest) | `test_order_facade_modify.py` |

## カバレッジ要約

- 自動(E2E済)：MODIFY-01/02/02b/03/04/05/06/07/08/09/09b（Python-FREE・`ModifyOrderE2ERunner.cs`）
- 自動(pytest)：MODIFY-20/21（`test_order_facade_modify.py`・rollup に `[E2E MODIFY-20/21 PASS]`）
- 要新規自動化：MODIFY-10 の UI 行 実 lane roundtrip（engine 半分は pytest 済・MOCK-live section は後続）
- HITL専用：MODIFY-11（demo=立花 atomic／実 kabu cancel+replace の部分失敗。findings 0101 D7）
