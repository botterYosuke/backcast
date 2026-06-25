# 0101 — #34 注文訂正 UI（modify modal）設計グリル

方針: ADR-0001（Unity + pythonnet・venue ロジックは Python 集約／frontend は薄く）。
parity oracle: TTWR `src/ui/modify_modal.rs` + `order_context_menu.rs` + `venue_capabilities.rs`。
関連: CONTEXT.md「取消受付 / 取消確定」「訂正受付（PENDING_UPDATE）」/ #25（broker honoring）/
#29・#236（sync facade・get_orders 静的属性）/ #23（demo roundtrip・blocked-by 解消済み）。

`/grill-with-docs`（owner all-in 指示「実装コスト度外視・理想形・手を抜くな」/ `/kabusapi` `/tachibana`）。

## 現状（grill 冒頭のコード裏取り）

engine + C# binding は **既に全結線済み**で、欠けているのは Unity の UI 入口のみ:

- Python: `broker.modify`（PENDING_UPDATE honoring・over-fill ceiling・#25）→ `order_facade.modify`
  → 3 adapter の `modify_order`（mock/kabu/tachibana）→ `live_orchestrator.modify_order`
  → `_backend_impl.modify_order`。
- C#: `LiveRpcLanes.SubmitModifyOrder(venue, orderId, newQty, newPrice, onResult)` が write lane に**存在するが
  caller 0**（#23 re-home で投機的に足された）。
- backcast には **resting 注文一覧パネルが無い**（OrderTicket は「Cancel last」= `_manualOrderId` のみ・
  `OrderEvent` は qty/price を運ばない）。TTWR の `order_context_menu`（行を右クリック）を載せる先が無い。

## 実ソフト調査（owner 依頼）

- **kabuステーション**（三菱UFJ eスマート）: 注文約定照会 window（注文一覧）→ 選択 → 訂正/取消。
  **数量訂正は発注パネル不可**＝注文約定照会経由 or「取消して再発注」。kabu 自身の GUI が qty 訂正を
  cancel+new 扱い＝backcast adapter / TTWR kabu 警告と一致。
- **楽天 マーケットスピードII**: 注文照会 → 選択 → 訂正画面。訂正可は数量/価格/執行条件、**数量は減数のみ**。

→ 業界共通の UX は「注文照会一覧 → 行選択 → 訂正ダイアログ」。AC の「resting 注文を選び」はこれ。

## 設計の木（凍結）

- **[x] D1 選択 UI** = OrderTicket window 内に **最小 resting 一覧を埋め込み**（form 下・LiveManual 時のみ）。
  `get_orders`（#29 Slice3a の symbol/side/qty/price を持つ）を読み、各行に inline `[訂正]`/`[取消]` ボタン
  （kabu「訂」ボタン流・uGUI 向き。TTWR の右クリック context menu は Bevy idiom なので採らない）。
  選択行から訂正 modal を開き原値を prefill。**専用「注文照会」Hakoniwa window は後続 additive slice**
  （理想形だが今回は OrderTicket 同居を選択）。

- **[x] D2 数量 validation = 減数のみ**（楽天準拠）。`filled ≤ new_qty < original` のみ受付。
  **増数・同値は UI（modal `can_confirm`）で拒否**。価格は `>0 かつ ≠ original`。qty/price とも空欄=変更なし、
  両方無変更は拒否。原値は選択行（get_orders）由来＝AC#3「原注文と同値」が非 vacuous。
  broker の `new_qty < filled` ガードは**最終防壁として残す**。減数のみは UI ポリシー（venue 不変条件ではない・
  増数は cancel+new で技術的に可）なので broker/facade には強制しない＝UI 一点に置く。

- **[x] D3 AC#2「PENDING_UPDATE 受付/確定 返し分け」→ status 返し分けに再解釈**。
  manual 経路の 3 adapter は訂正を**同期確定**（mock=ACCEPTED / kabu=cancel+new の終端 ACCEPTED|CANCELED|REJECTED /
  tachibana=atomic ACCEPTED）し **PENDING_UPDATE を一切返さない**（kabu は `_await_order_terminal` 後に再発注して
  新 leg の ACCEPTED を返す＝受付ではなく確定）。PENDING_UPDATE honoring は **broker/Auto の既済関心（#25）**で
  #34 では再実装しない（投機的一般化＝YAGNI。将来 adapter が返すなら broker `_apply_pending_receipt` が雛形・
  facade `apply_venue_event` も既存）。UI 返し分け:
  - ACCEPTED（+FILLED/PARTIALLY_FILLED）→「訂正確定」・一覧に新 qty 反映。
  - REJECTED/DENIED →「訂正拒否」・原注文と intent 据え置き。
  - CANCELED/EXPIRED →「取消成立・要再発注」（kabu 取消成立＋新規失敗）。

- **[x] D4 facade.modify の took-effect ゲート修正（潜在バグ修正）**。
  現 `facade.modify` は `REJECTED` だけ特別扱いし、それ以外は `new_state.status=res.status` にして
  `_intents` に new_qty を**無条件書き込み**（`order_facade.py:394-406`）。kabu の `CANCELED`
  （`reject_reason="MODIFY_NEW_FAILED:…再発注してください"` `kabusapi_execution.py:446-451`）でも new_qty を
  終端注文に書く＝**幻の新 qty 表示**。修正: `_intents`/`_states` への new_qty/new_price 反映を
  **took-effect（ACCEPTED/FILLED/PARTIALLY_FILLED）に限定**し、CANCELED/EXPIRED は new_qty を書かず終端化。
  broker の `modify_took_effect` を同期確定向けに写したもの。AC#2 の本意（「効いてから反映」）を満たす。

- **[x] D5 kabu 警告 ack を今回移植**（safety の対称性: D3/D4 の CANCELED 事後表示の**事前**半分）。
  ただし TTWR の C# registry コピーは **しない**（ADR-0001 invariant）:
  1. capability は **Python が宣言**＝active adapter のクラス属性 `modify_is_cancel_replace`
     （kabu=True / tachibana=False / mock=False。`venue_id="KABU"` 等の隣）。`OrderingVenueAdapter` に
     既定 False を置く。
  2. **既存 poll seam に相乗り**: `_backend_impl.get_state_json`（`:583-618`・`connected` ゲート下で venue_id 等を
     merge する箇所）に `modify_is_cancel_replace` を足す → C# `StateDto` に `public bool modify_is_cancel_replace;`
     を bind（`VenueConnectionViewModel`）。新 lane 不要。
  3. Unity modal はそのフラグを読み、True のとき警告バナー＋「理解した上で訂正する」ack チェックを
     **Confirm の前提**にする（未 ack は Confirm disabled）。**C# で `venue=="kabu"` 分岐しない**。
  4. **単一キーのみ**宣言（多キー registry は consumer が出たら育てる・YAGNI）。
     `requires_second_password` は**入れない**（D6 の SecretRequired 反応経路が担う）。

- **[x] D6 secret 経路（code-determined・決定不要）**。modal は **secret を集めない**。
  `SubmitModifyOrder` は secret 引数を持たず None 相当を送る。tachibana `CLMKabuCorrectOrder`（atomic・
  sSecondPassword 必須）は `_resolve_secret("correct_order")` → `SecretRequired` push → 既存 #21 secret modal
  → `submit_secret` で反応的に解決（place_order と同型）。

- **[x] D7 検証（実装完了 2026-06-24）**: `behavior-to-e2e` で Action-ID（例 `MODIFY-01..`）を切り、
  `scripts/run-all-tests.ps1` rollup に載せる。
  - Unity AFK: `ModifyOrderE2ERunner`（`OrderTicketE2ERunner` をミラー）—一覧描画／行選択 prefill／減数のみ
    can_confirm gate／kabu フラグ警告 ack gate／status 返し分け（mock `set_next_modify_outcome` で
    ACCEPTED/REJECTED/**CANCELED** を仕込み RED→GREEN）。
  - pytest: facade took-effect ゲート修正（D4）の characterization（CANCELED で _intents 不更新）／
    get_orders honoring。
  - **demo HITL = 立花**（atomic・警告不要）。kabu cancel-replace の CANCELED は kabu HITL（翌営業日 09:57JST 接続）。

### 実装着地（2026-06-24）

- Python: `order_facade.modify` took-effect ゲート（D4）/ `modify_is_cancel_replace` capability（D5・mock=False・
  kabu=True・tachibana=False → `TradingState`/`get_state_json`）。pytest `test_order_facade_modify.py` 4 件 GREEN
  （`@scenario("MODIFY-20")`=CANCELED で幻 qty 無し / `MODIFY-21`=ACCEPTED で反映）。**full suite 554 passed**。
- C#: `VenueConnectionViewModel.ModifyIsCancelReplace`（StateDto bind）/ `LiveRpcLanes.SubmitGetOrders`（write lane・
  `RestingOrderRpcRow`/`OrdersRpcResult`）/ `OrderTicketView` resting 一覧（行 inline `[訂正]`/`[取消]`・`[更新]`）/
  `ModifyModalController`（検証の頭脳・plain C#）/ `ModifyModalOverlay`（uGUI）/ `BackcastWorkspaceRoot` 配線
  （`OnRowModify`/`OnRowCancel`/`RefreshRestingOrders`/`OnModifyConfirm`/`DriveModifyModal`）。
- Unity AFK: `ModifyOrderE2ERunner` **10/10 PASS**（MODIFY-01/02/02b/03/04/05/06/07/08/09・Python-FREE）。
  `scripts/run-all-tests.ps1` merged rollup = **ALL TESTS PASS**。
- **RED→GREEN litmus**: (a) D4 — 旧 facade は CANCELED でも new_qty を `_intents` に書く＝MODIFY-20 は qty==100 期待が
  60 で RED、ゲート追加で GREEN（構成上自明）。(b) MODIFY-06 — controller の `q >= OriginalQty` を外すと増数が
  通り RED。(c) 実装中の実 RED: `ModifyModalOverlay` の `GetComponent<Canvas>() ?? AddComponent`（Unity fake-null を
  `??` が無視）で SectionC が `MissingComponentException` RED → 明示 `== null` idiom（SecretModalOverlay 同型）で GREEN。
- **残**: MODIFY-10 UI 行の実 lane roundtrip（engine 半分は pytest 済）は MOCK-live section が後続・MODIFY-11 は HITL。
  owner HITL（立花 demo で訂正→確定の目視）。

## Avoid

- resting 一覧を facade `_intents` の即時 new_qty で「確定前に新 qty 表示」すること（D4 で took-effect ゲート）。
- C# に `venue=="kabu"` を散らすこと（D5・capability は Python 宣言を読む）。
- PENDING_UPDATE stash/swap を facade に足すこと（D3・返す adapter が無い dead code）。
- 減数のみを broker/facade に強制すること（D2・UI ポリシーであって venue 不変条件ではない）。
- TTWR の右クリック context menu / 専用 OrdersPanel をそのまま移植すること（D1・今回は OrderTicket 同居 + inline ボタン）。
