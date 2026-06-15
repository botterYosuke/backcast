# findings 0021 — reconcile modal (#40) を not-applicable で close（in-proc では発火不能）

## 結論（owner 決定 2026-06-15・案②）

issue #40（reconcile modal）は**実装せず close**。`reconcile_modal` を ADR-0005 の 1:1 surface parity 契約から
**除外**する（ADR-0008）。コード追加なし。

## 調査（ハード証拠・TTWR src 実機照合）

- **TTWR `reconcile_modal.rs:1-12`**: 通知専従・単一「確認した」ボタン。in-modal の取消/再送は §3.8 二重発注リスクで
  **明示的に却下**（「モーダルは通知に徹する…自動で取消/再送するのは危険」）。ユーザーは Venue 再ログインで venue 側を確認。
- **diff は 1 クラスのみ**: `orders_model.rs:423-445` `reconcile_unknown_orders` → `ReconcileUnknownOrder
  {client_order_id, symbol, status}`（UI が楽観的に working と信じるが backend が追跡しない注文）。position reconcile は
  TTWR に**存在しない**（`trading.rs:402-404` `ReconcilePrompt{unknown}` のみ）。
- **契機の分離**: `protocol.rs:313-322` plain `GetOrders`（connect-seed only・no reconcile）vs `GetOrdersAndReconcile`
  （再起動時のみ・modal を駆動）。venue resting order は通常 connect-seed で注文パネルへ流れ、modal には出さない。
- **backcast in-proc**: ADR-0001 dec.3「UI 死＝engine 死／orphan 不在」→ engine 単独再起動が構造的に不在 →
  modal 契機（`GetOrdersAndReconcile`）が**発火不能**。
- **起動時は flat ではなく seed**: venue 状態を正として取り込む — `kernel/portfolio.py` `seed_position`（D7「venue
  AccountSnapshot が opening book の権威」）・`kernel/live/controller.py:161`（wired）・`live/account_sync.py:4`
  （起動直後 1 回 fetch_account 必ず emit）・`live/nautilus_exec_client.py:116-138`（connect 時 account seed）。
  → 「engine 像 vs venue の diff」ではない。
- **実トリガーを作る open issue 無し**: open issue 60 件確認。#40 自身以外に reconcile 契機を配線するものは皆無。
  #40 body の「engine 非同期 reconcile は #23」は **stale**（#23 は「Live demo roundtrip」done-gate として既に close・
  AC に reconcile 契機を含まない）。venue 再ログイン reconcile（`VenueLogoutDetected` 後の突合）も未配線・未起票。

## grill 決定経路（Q1–Q4）

1. **Q1 通知専従**（採用/取消なし）— TTWR §3.8 二重発注安全。backcast の取消は受付/確定分離（CONTEXT.md）で
   honoring 未完。owner 確定。
2. **Q2 diff クラス = `ReconcileUnknownOrder` 一択** — position drift は TTWR に precedent 無し、engine-unknown
   resting order は connect-seed の領分。owner 案B（TTWR 忠実 1:1）採用、案A（起動時 seed 状態の awareness modal）は
   "うるさすぎ" で却下。
3. **Q3 非ブロッキング** — TTWR が非ブロッキング。ブロッキング gate 化は Phase 10 LiveAuto run-start
   （`NoopLiveEngineController` placeholder）への結合を招くため不可。
4. **Q4 dead surface 判明 → 案②** — 案B で忠実移植しても契機が in-proc で発火せず、実 venue HITL 経路も無い
   （AC④ 達成不能）。owner は「出ない画面に工数をかけない」を選択し、移植せず除外（ADR-0008）。

## 影響・後続

- 追加コードなし。CONTEXT.md「broker reconciliation modal（#40）」を除外決定に更新。ADR-0008 起票。issue #40 close。
- 復活させるなら **#40 を再 open せず**、in-proc でも成立する別契機（venue 再ログイン突合等）を新規 issue で設計し
  ADR-0008 を参照する。
