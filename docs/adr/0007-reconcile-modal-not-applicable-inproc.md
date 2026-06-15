---
status: accepted
---

# TTWR `reconcile_modal` を ADR-0005 の 1:1 surface parity 契約から除外する（in-proc では発火不能な dead surface）

`grill-with-docs`（2026-06-15・owner 決定 案②）で導出。ADR-0005 は #5 カットオーバーの done を
「TTWR `src/ui` の各 UI 表面の 1:1 surface parity」と固定し、その対象例として `reconcile_modal` を名指ししている。
本 ADR は **`reconcile_modal`（issue #40）をその対象から除外**する。ADR-0005 の自己保護条項（「覆す場合はファイルを
編集せず supersede する新規 ADR を起こす」）に従い、ADR-0005 本体は編集せず、本 ADR が **当該サーフェスに限り**
ADR-0005 を上書きする。issue #40 は closed-as-not-applicable。

## なぜ（契機が構造的に発生しない）

TTWR の `reconcile_modal` が開く唯一の契機は、**別プロセスの backend が crash → supervisor が自動再起動 →
記憶を失った backend と UI の楽観的注文がズレる**ことである（`reconcile_modal.rs:4-12`・`GetOrdersAndReconcile`
→ `OrdersReconciled` → `ReconcileUnknownOrder{client_order_id, symbol, status}`・通知専従・採用/取消なし §3.8）。

backcast は ADR-0001 dec.3 で Python engine を Unity プロセスに**埋め込み**、「UI が死ねば engine も死ぬ／orphan は
構造的に存在し得ない」を不変条件にした。よって**「engine だけが再起動して記憶を失う」契機は構造的に発生しない**。
起動時の既存 venue 状態は modal ではなく seed で正として取り込む（connect-seed → 注文パネル／`seed_position` → 口座）。
実契機を作る open issue も無い（#40 body の「engine 非同期 reconcile は #23」は stale＝#23 は demo-roundtrip
done-gate として close 済みで本契機を提供しない）。＝実データで一度も開けない dead surface。

## 機能後退ではない理由

ADR-0005 が surface 網羅を契約にしたのは「欠落＝機能後退・TTWR 廃止で fallback 無し」だから。だが `reconcile_modal`
はユーザーが使う機能ではなく、**起こり得ない故障モード（engine 単独再起動）への反応 UI**。前提が ADR-0001 で構造的に
消えている以上、その反応 UI の不在は機能後退に当たらない。dead surface を移植しても dead code が増えるだけ。

## Consequences

- issue #40 は closed-as-not-applicable。詳細調査・ハード証拠・Q1–Q4 決定経路は findings 0021。
- 他の TTWR surface（settings/theme/menu_bar/instruments_universe_prune 等）は ADR-0005 の対象のまま。本除外は
  `reconcile_modal` 限定。
- 将来 venue 再ログイン突合（`VenueLogoutDetected` 後の「UI 楽観注文 vs venue 実態」提示）等、in-proc でも成立する
  別契機が欲しくなったら、**#40 を再 open せず**新規 issue を起こし本 ADR を参照して再検討する。
