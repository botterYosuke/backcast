# findings 0011 — Kernel Live Foundation（#25・grill 中）

方針: **ADR-0004 案 C**（pure-Python Backcast Execution Kernel）。本書は #25 スライスの下位確定事実を
記録し、ADR-0004 を「方針: ADR-0004」として参照する（ADR-0004 は自己保護のため編集しない）。
#24 の tracer（findings 0008）を **Live/Auto 経路**へ拡張する。

## ゴール（issue #25 再掲）

`NautilusLiveEngineController`（`engine_controller.py`・`live_orchestrator.py:143` の既定）を、
**Rust core を一切ロードしない** `KernelLiveEngineController` に置換し、mock venue Live で
start→order→fill→position→stop を AFK GREEN にする。order FSM / marshal / mock venue seam を grill で詰める。

## 確定事実（grill）

### D1. fill source = 同期 `OrderResult`（mock tracer の authoritative source）

- MockVenueAdapter は `set_execution_hooks` が no-op で、**非同期の order event を一切 emit しない**
  （`mock_adapter.py:359`）。観測可能な唯一の fill 信号は `submit_order` / `modify_order` が返す
  **同期 `OrderResult`**（`mock_adapter.py:158` 既定 FILLED 全約定、`set_next_order_outcome` で
  PARTIALLY_FILLED / REJECTED を one-shot 注入可）。既存 Nautilus 経路も submit 結果を即
  `_apply_submit_result` に渡す（`nautilus_exec_client.py:198/355`）。
- ⇒ LiveBroker は同期 `OrderResult` から FSM を駆動する。非同期 EC/polling は**同一 FSM 入口へ
  接続する補助 seam として用意**するが、完全な非同期 reconciliation は **#23（実 venue 実弾・非目標）**。

**order FSM（live 完全版）**:
```
INITIALIZED → DENIED                         # kernel pre-trade gate（venue 未到達）
            → SUBMITTED → REJECTED           # adapter/venue reject（ACCEPTED を経由しない）
                        → ACCEPTED
                            → PARTIALLY_FILLED
                            → FILLED
                            → CANCELED
```
- `REJECTED` は `ACCEPTED` を経由させない。`DENIED` は SUBMITTED 前（INITIALIZED から）。
  → 現 `OrderEngine.submit()` は gate 通過時点で即 `ACCEPTED`（`orders.py:92`）なので、Live 用に
  **precheck→SUBMITTED**（venue ACK 前）と **adapter result→ACCEPTED/REJECTED/fill** に分離する。

**正規化入口**: 同期結果と非同期イベントで別々の FSM 更新を作らず、同じ入口へ正規化する。
```python
result = await adapter.submit_order(...)
apply_venue_update(order, result, source="submit_result")
# 将来の EC/polling
apply_venue_update(order, event, source="venue_stream")
```

**fill 重複排除 = 累積約定数量 delta**（受信イベント数ではない・#24 の client_order_id dup-guard の live 拡張）:
```
delta = incoming_cumulative_filled_qty - order.filled_qty
delta <= 0  → duplicate/stale として無視
delta > 0   → delta 分だけ Portfolio に適用
```
mock の同期 FILLED 後に同一 EC イベントが届いても二重建玉にならない。

**set_execution_hooks**: orchestrator が既に `_publish_order_event` 用に設定済み
（`live_orchestrator.py:234`）。LiveBroker は**再設定して上書きしない**。既存 callback から
UI/account-sync と broker の双方へ fan-out する。

### D2. Live UI transport = 既存 backend_events seam（AC④ は projection 互換ゲート）

- 本番 Live UI の権威チャネルは現状維持: 注文=`on_order_event`→`BackendEvent.OrderEvent`、
  口座・建玉=`AccountSync`→`AccountEvent`、run lifecycle=`LiveStrategyEvent`、統計=`LiveStrategyTelemetry`、
  safety/log=各 backend event、市場データ=`LiveReducerBridge`。`EventSink.push_*` は **Live UI の配送路にしない**。
- `KernelLiveEngineController` は `NautilusLiveEngineController` と**同一 callback seam**を駆動する。
- **AC④ = projection 互換ゲート**: 同じ kernel fill / Portfolio state を**既存 `EventSink` serializer**
  （`sink.py:63`）へ投影すると #24 と同じ `push_order` / `push_portfolio` JSON になり、**無改修
  `ReplayPanelDecoder`** で実値 decode できる、を試験で担保する（#24 `KernelSinkDecodeProbe` 同型）。
  配送チャネルの統一でも、backend `OrderEvent`(8 field) と Replay payload(symbol/side/qty/price…) の
  field 一致でもない（後者は意図的に別。`live_orchestrator.py:943`）。二重送信しない。
- **position/balance の UI 権威は venue `AccountSnapshot`→`AccountEvent`（`AccountSync`）のまま**。
  kernel Portfolio は gate・MTM・決定的テスト用の内部状態。fill 後は既存 `AccountSync` を促進（force_resync）する。
- Mock AFK は分けて検証: ①kernel 内部（fill→Portfolio position）②UI 経路（OrderEvent/telemetry/lifecycle）
  ③account 表示（MockVenue `AccountSnapshot`→`AccountEvent`）④AC④（同 fill/Portfolio を `EventSink`
  へ投影→`ReplayPanelDecoder` 実値 decode）。
- 不採用: `ReplayEventSink`（`Assets/Scripts/S1Spike/ReplayEventSink.cs:61`）は replay worker 直結の
  push target で Live orchestrator に受け口が無い。Live 本番経路化は push target 新設・C# drain lifecycle・
  UI 二重イベント・連続稼働 Live での `push_run_complete` 意味付けが要り、「class 名だけ交換」に反する。

### D3. Live market-data transport = `LiveRunner.bus` 単一 async consumer + KlineUpdate closed/partial 識別

- kernel live driver は `runner.bus.subscribe()` を単一 async task（live-loop thread・`LiveReducerBridge` 同型）で
  消費し、run の instrument にフィルタして `TradesUpdate`→kernel tick→`on_tick`、**確定** `KlineUpdate`→
  kernel `Bar`→`on_bar`。`add_tick_listener` は Nautilus data-client 注入専用で kernel では使わない。
- **前提修正（必須）**: production `LiveRunner` は bucket-rollover の確定 bar（`aggregator.py` `on_tick` 戻り値）
  と 1 秒間隔の UI 用 partial bar（`_partial_push`・`live_orchestrator.py:203` で有効）を**同じ `KlineUpdate`**
  として bus へ流し、識別子が無い。naive 購読だと strategy が partial を毎秒 `on_bar` で受け重複発注する。
  → `KlineUpdate` に `is_closed: bool = True` を追加。rollover/venue-direct = True、periodic partial = False。
  driver は `is_closed=True` のみ `on_bar`。`LiveReducerBridge`（UI）は partial 含む従来挙動を維持。
- 順序保証: `LiveRunner._run` は raw trade を publish 後に確定 bar を publish（`live_runner.py:133`）、bus は
  subscriber ごと FIFO（`event_bus.py:44`）→ `on_tick(new-bucket tick)` → `on_bar(previous bucket)` は決定的。
- aggregator は wall-clock timer ではなく `ts_ns // interval_ns` の bucket 遷移で確定（`aggregator.py`）→
  kernel 側に clock も第二 aggregator も不要。
- テスト: ①venue-direct closed `KlineUpdate`→`on_bar`×1 ②異 bucket の `TradesUpdate`×2→`on_tick`×2＋
  前 bucket `on_bar`×1 ③partial `KlineUpdate`×N→`on_bar`×0。

### D4. kernel-native strategy loader = 既存 `strategy_loader.load(base_cls=)` のパラメータ化

- `load(path, *, original_path=None, base_cls=None)`。`base_cls is None` で従来 nautilus 挙動（import は関数内＝
  module import は pure 据え置き）。kernel は `engine.kernel.strategy.Strategy` を渡す→nautilus import 行に到達しない。
- **default 引数に nautilus クラスを書かない**（書くと module import で Rust core ロード）。
- 共通維持: ADR-0021 の `module.__file__`＝`exec_module` **前**設定（1 箇所・`strategy_loader.py:72`）/ `load_scenario`/
  当該ファイル定義クラスのみ選択 / 0・複数件 `StrategyLoadError` / `_check_compat` AST 検査 / traceback 正規化。
- error 文言は base 名で（`f"no {base_cls.__module__}.{base_cls.__name__} subclass found in {path}"`）。
  `_check_compat` の `"not supported in replay mode"` は Live 共用後 `"not supported by this runtime"` 等へ。
- ⚠️ loader が pure でも、**ロード対象 strategy 自身が nautilus を import すれば Rust core が入る**。kernel twin は
  kernel base のみ import。purity gate は **ロード完了後の `sys.modules`** を検査する。
- テスト: ①kernel twin を `base_cls=KernelStrategy` でロード ②ロード後 `nautilus_trader*` 不在 ③`base_cls` 省略で
  既存 nautilus strategy をロード ④誤 base で 0 件。
- `engine.strategy_runtime.scenario`（`load_scenario`）は nautilus-free（確認済）。`instrument_factory`
  （`make_equity_instrument`）/ `bar_supply`（module-scope `TradeTick`）/ `catalog_data_loader` の nautilus 部 /
  `replay_runner`/`engine_runner` は Rust core を引くため kernel Live 経路から import しない。

### D5. 配置 = `engine/kernel/live/` 隔離 ＋ 3 層 import-purity gate

```
engine/kernel/live/
  __init__.py        # 空 or docstring のみ。便利 re-export を置かない（import 連鎖を広げる）
  controller.py      # KernelLiveEngineController（LiveEngineController Protocol 実体）
  broker.py          # LiveBroker（OrderingVenueAdapter bridge + live FSM + dedup）
  driver.py          # LiveRunner.bus → kernel Strategy（market-data consumer）
```
- FSM 本体 / `Order` / `Portfolio` / `RiskEngine` は `engine/kernel/` 直下。`live/` は Live 固有 I/O 接続のみ。
- 依存方向固定: `engine.kernel.live → engine.kernel → engine.live.{adapter,event_bus,strategy_host,order_types}`。
  **禁止**: `engine.live.engine_controller` / `bar_supply` / `nautilus_*`。
- **3 層 gate**:
  1. **module-import gate**（早期警戒）: fresh subprocess で `controller`/`broker`/`driver` を**明示列挙** import
     （wildcard は submodule を読まない）→ `leaked_nautilus_modules(sys.modules)==[]`。
  2. **CPython full LiveAuto gate**（AC の権威ゲート）: fresh subprocess で `LiveLoopManager`→MockVenue
     login/start→kernel strategy load→`attach`→Kline/tick 注入→order→同期 fill→Portfolio position→backend
     events→stop/cancel/detach→shutdown→**purity 検査（stop/detach 後）**→exit 0。controller 単体では
     `live_orchestrator.py:1533` のような上流リークを検出できない。
  3. **Unity-Mono full Live gate**: #24 の `KernelTeardownProbe`（Replay のみ）では不十分。mock LiveAuto の
     同シナリオを Mono 内で完走させ leaked==[]・clean `PythonEngine.Shutdown`・exit 0・heartbeat 生存・新規 dump 無し。
- gate 前提: `live_orchestrator.py:1533` の nautilus `InstrumentId` import を stdlib 構文検査へ置換。

### D6. live order FSM = orders.py additive 拡張 ＋ FSM/dedup は LiveBroker（Replay golden 不変）

- 責務分割: `OrderEngine` = ID 予約・重複防止・共通 pre-trade gate。`LiveBroker` = SUBMITTED 以降の遷移・
  adapter I/O・fill dedup。`ReplayBroker` = 従来どおり `ACCEPTED → FILLED`。
- `OrderEngine.precheck(...) -> RailViolation | None`: dup 予約 + regulation + SafetyRails、deny 時のみ `DENIED`、
  成功時は `INITIALIZED` のまま返す。Replay `submit()` = `precheck()` + 通過時 `ACCEPTED` 設定（観測可能な遷移・
  sink 順序・イベント内容は不変。変更後 #24 golden 再実行）。
- LiveBroker: `precheck` 成功後に `SUBMITTED`→`adapter.submit_order`→`apply_venue_update(order, result)`。
- **regulation**: `RiskEngine(rails, regulation_provider=None)`（Replay default None）。`check_pre_trade` から
  `evaluate_pre_trade(..., regulation_provider=...)` へ。**PAUSE/run gate は RiskEngine の責務ではない**——
  LiveBroker が venue 送信前に判定し `INITIALIZED → DENIED`。on_safety_violation も LiveBroker が呼ぶ。
- **`OrderFilled.last_qty` は増分（delta）数量のまま**（Portfolio が適用する単位・意味変更禁止）。
  `cumulative_filled_qty: float = 0` を additive 追加。dedup: `delta = incoming_cumulative − order.filled_qty`、
  `delta<=0`→無視、`>0`→`order.filled_qty=incoming_cumulative`・`OrderFilled(last_qty=delta, cumulative_filled_qty=...)`。
  → `Portfolio.apply_fill` と Replay sink は無改修。
- `OrderStatus` 加算: `SUBMITTED` / `PARTIALLY_FILLED` / `REJECTED` / `CANCELED` / `EXPIRED` / `PENDING_UPDATE`。
  modify 拒否は注文全体 `REJECTED` にせず**元状態へ復帰**するイベント。

### D7. Portfolio seed = venue snapshot（cash＋既存建玉）／post-trade 権威は既存 AccountSnapshot 経路

- attach 時 `adapter.fetch_account()` を 1 回行い `initial_cash = snapshot.cash`＋**既存建玉**（symbol/qty/avg_price）
  を kernel Portfolio へ seed。cash のみだと既存建玉を無視し `max_position_size_jpy` が誤判定。**取得失敗は
  fail-closed `STRATEGY_ATTACH_FAILED`**（cash=0 継続にしない）。`AccountSync.last_snapshot` があれば再利用、
  無ければ controller が明示 fetch（初回 fetch task と race し得るため）。
- 役割: venue `AccountSnapshot` = UI 残高・建玉＋post-trade rail の権威。kernel `Portfolio` = strategy 起点 fill・
  pre-trade position cap・MTM telemetry の **shadow state**（Live 口座の完全 mirror ではない。手動/外部約定 drift の
  reconcile は #23）。scenario `initial_cash` は Replay 専用。
- telemetry: closed bar 毎に `last_prices[id]=bar.close`。`unrealized_pnl = Σ signed_qty×(last_price−avg_px)`、
  `order_count`=作成注文数、`fill_count`=**実 fill イベント数（partial 含む counter）**（「filled_qty>0 の注文数」ではない）。
  `realized_pnl = cash−initial_cash` は seed 建玉/未決済があると不正（`portfolio.py:61` は flat-book 限定コメント）→
  **Portfolio に実現損益を明示積算**（additive。Replay golden の realized PnL は不変＝flat 時 cash-delta と一致を維持・再走確認）。
- post-trade `max_daily_loss` 権威 = 既存 `AccountSync→AccountSnapshot→_evaluate_post_trade_loss→fail_run→
  cancel_inflight_orders→detach`。kernel `RiskEngine.check_post_trade` は Replay 専用（Live で二重評価しない）。
- **baseline 修正**: 現行は run 開始時に baseline を消去し次 snapshot で初設定（`live_orchestrator.py:1568`）→
  最初の次 snapshot が fill 後だと最初の損失を見逃す。run 開始成功時に既存 snapshot
  （`last_snapshot` or 明示 fetch）から即時 `_run_equity_baseline[run_id]=equity_from_snapshot(snapshot)`。

### D8. lifecycle / teardown 機構（NautilusLiveEngineController を踏襲＋調整）

- **attach 順序**（初回 market event を逃さない順・失敗時は逆順 rollback）:
  `Portfolio seed → strategy 生成・register → bus.subscribe() 同期確立 → driver task 開始 →
  runner.subscribe(instruments) → strategy.on_start() → on_start intents drain`。
- **intent drain**: `on_bar` 後だけでなく `on_start`/`on_tick`/`on_order` からも発注可能。
  **再入防止フラグ付き単一 drain loop**で FIFO 逐次 `await adapter`。strategy callback 内では adapter を await しない。
  `callback → callback が積んだ intent を drain → adapter I/O → order event → on_order → 新 intent あれば続行`。
- **stop**: `on_stop()` は detach 時に最大 1 回。Live では `push_run_complete` を発行しない（D2）。最終状態は
  既存 `LiveStrategyEvent`＋必要なら最後の `on_telemetry`。
- **UI channel**: driver は callback seam のみ駆動。`EventSink` は AC④ projection テスト専用。
- **cancel 対象**（run 単位）: `SUBMITTED`/`ACCEPTED`/`PARTIALLY_FILLED`/`PENDING_UPDATE`。`PENDING_CANCEL` は
  再 cancel せず完了を best-effort 待ち。各 cancel 失敗は個別 try で残りを止めない。
- **detach**: kernel は `add_tick_listener` を使わない→「listener を外す」は不要。
  `新規 intent 受付停止 → bus consumer 停止・await → on_stop 最大 1 回 → 内部参照解放`。
  **共有 `LiveRunner.unsubscribe()` は呼ばない**（購読は UI 等と共有・ref-count されない。既存 Nautilus controller も
  detach で unsubscribe しない。bus の queue は consumer task 終了時に `event_bus._iter` の finally で自動解除）。
- **instrument validation**: `:1533` の nautilus import を**純 Python validator へ共通化**（単なる `split(".")` 不可）。
  最低: 全体 `SYMBOL.VENUE`・空 segment なし・dot 1 個・許可文字のみ。
- **追加不変条件**: `attach`/`detach`/`cancel_inflight_orders` は idempotent。全 strategy callback と adapter I/O は
  live loop thread 上。detach 開始後の新規 intent は破棄でも DENIED でもなく**明示的 terminal event**で閉じる。
  host の順序は既存どおり `cancel_inflight_orders → detach`。attach 失敗時に consumer task / strategy 参照を残さない。

## 実装インベントリ（計画・grill 確定 D1–D8）

**新規 `engine/kernel/live/`**:
- `broker.py` — `LiveBroker`: `apply_venue_update(order, result|event, source)`（同期結果と非同期イベントを同一入口へ正規化）・
  cumulative-qty dedup・FSM 遷移（SUBMITTED 以降）・`adapter.submit_order/cancel_order/modify_order` I/O。
- `driver.py` — `LiveRunner.bus` 単一 async consumer（run instrument filter・`is_closed` の closed bar のみ on_bar・
  TradesUpdate→on_tick）・再入防止 intent drain loop・strategy ctx（submit_market は intent enqueue）。
- `controller.py` — `KernelLiveEngineController`（`NautilusLiveEngineController` と**同一 ctor seam**：
  loop_provider/adapter_provider/runner_provider/on_safety_violation/on_order_event/on_telemetry/on_strategy_log/
  run_gate_provider）。`attach`/`detach`/`cancel_inflight_orders`。Portfolio seed（fetch_account・cash＋建玉）・
  telemetry 算出・on_order→OrderEventData 変換。

**`engine/kernel/` 既存改修（additive）**:
- `orders.py` — `OrderStatus` に SUBMITTED/PARTIALLY_FILLED/REJECTED/CANCELED/EXPIRED/PENDING_UPDATE 追加・
  `OrderEngine.precheck()`（INITIALIZED のまま返す・regulation 対応）・event dataclass（OrderAccepted/Rejected/Canceled・
  `OrderFilled.cumulative_filled_qty` additive）。Replay `submit()→ACCEPTED` 不変。
- `risk.py` — `RiskEngine(rails, regulation_provider=None)`・`check_pre_trade` から regulation 透過。
- `portfolio.py` — 実現損益の明示積算（seed 建玉/未決済対応）。`apply_fill`/Replay realized は不変。
- `strategy_loader`（strategy_runtime）— `load(base_cls=None)` パラメータ化。

**`engine/live/` 改修**:
- `adapter.py` — `KlineUpdate.is_closed: bool = True`。
- `aggregator.py` — rollover bar `is_closed=True` / `build_now` partial `is_closed=False`。
- `live_runner.py` — `_partial_push` は `is_closed=False` で publish。
- `live_orchestrator.py` — `:143` 既定を `KernelLiveEngineController` へ・`:1533` を純 validator へ・
  `:1568` post-trade baseline を run 開始時 snapshot から即時確立。

**テスト/ゲート**:
- kernel FSM 決定的テスト（accepted/partial/filled/rejected/canceled/modify＋cumulative-qty dedup）。
- driver bar/tick テスト（D3 の ①②③）。
- loader テスト（D4 の ①②③④）。
- import-purity 3 層（D5）: module-import / CPython full LiveAuto / Unity-Mono full Live probe。
- mock venue Live AFK: start→order→fill→position→stop（kernel 内部・UI 経路・account 表示を分離検証）。
- AC④ projection: 同 fill/Portfolio を `EventSink`→無改修 `ReplayPanelDecoder` で実値 decode（#24 probe 同型）。
- post-trade max_daily_loss live 発火（baseline 即時確立後）。
- behavior-to-e2e: App 側挙動変更（LiveAuto が kernel 経路・Rust core 非ロード）→ `FLOWS.md` manual gate 追記。

## 実装結果（GREEN 2026-06-13）

実装インベントリ（上記）どおり実装。**CPython gate 全 GREEN**（`uv run pytest` 全 93 passed）:

- kernel additive（`orders.py` FSM enum＋`precheck`＋`OrderFilled.cumulative_filled_qty`、`risk.py`
  `regulation_provider`、`portfolio.py` 実現損益積算＋`seed_position`、`strategy_loader.load(base_cls=)`）。
  **#24 golden bit-identical**（`tests/test_kernel_golden_cpython.py` GREEN・Replay `submit()→ACCEPTED` 不変）。
- `KlineUpdate.is_closed`＋aggregator rollover=True / partial=False（`tests/test_kernel_live_step2.py`）。
- `LiveBroker`（`engine/kernel/live/broker.py`）order FSM 決定的テスト 9 件
  （accepted/partial/filled/rejected/canceled/modify＋cumulative-qty dedup・`tests/test_kernel_live_step3.py`）。
- `KernelLiveEngineController`＋`KernelLiveDriver`（`engine/kernel/live/{controller,driver}.py`）。
  `live_orchestrator.py:143` 既定を swap・`:1533` を `engine.kernel.instrument_id.is_valid_instrument_id` へ・
  `:1568` post-trade baseline を run 開始時 snapshot から即時確立。host loader を `base_cls=KernelStrategy` に。
- mock venue Live AFK roundtrip（start→order→fill→position→stop・終端 FLAT・realized=200・
  `tests/test_kernel_live_step5_afk.py`）＋ pre-trade deny live 発火＋seed 建玉 pre-trade 反映。
- **import-purity（権威ゲート・D5 layer 2）**: `spike/kernel_live/run_mock_live.py` を fresh subprocess で
  完走させ stop/detach 後 `nautilus_trader*` 非ロードを確認（`tests/test_kernel_live_purity.py` GREEN・
  `[KERNEL LIVE PURITY PASS] fills=2 final_net=0.0 realized=200.0 nautilus_leaked=0`）。
- AC④ projection: 同 fill/Portfolio を `EventSink` へ投影し #24 §3 push_order/push_portfolio 契約を固定
  （`tests/test_kernel_live_ac4_projection.py`）。無改修 C# `ReplayPanelDecoder` 実 decode は Mono probe（下記）。

### code-review 反映（#25 codex review・2026-06-13）

レビュー指摘 6 件をすべて修正（GREEN 98 passed）:
- **D1 訂正（partial fill 増分価格）**: venue は累積平均価格を報告するので、Portfolio に渡す増分価格は
  **累積約定代金の差**から算出する（`(new_cum_qty×new_cum_avg − prev_cum_qty×prev_cum_avg)/delta`）。
  累積平均をそのまま増分価格に使うと平均が誤る（50@8 → 累積100@9 は後半 50@10）。`order.avg_px` は累積平均を保持。
  test: `test_partial_then_full_incremental_price_from_cumulative_notional`。
- **共通生成契約（generic kernel Strategy）**: 基底 `Strategy.__init__(*, strategy_id="", instrument_id="", **params)`
  にして専用 ctor なしの最小戦略も `strategy_cls(instrument_id=..., **params)` で生成可能に。controller は構築後
  `strategy.id = nautilus_strategy_id` で run identity を inject（Nautilus change_id 相当）。
- **import-purity 権威ゲート = full chain**: `run_mock_live` を `LiveLoopManager → register_live_strategy
  （StrategyRegistry→kernel loader）→ venue_login(MOCK) → set LiveAuto → start_live_strategy（host→controller.attach）`
  の本番経路に置換。controller 直結だった旧 harness は orchestrator/loader/host 結線退行を検出できなかった。
  **`StrategyRegistry` も kernel loader を使う**よう修正（register 時の Rust core ロード＋kernel 戦略の登録不能を解消）。
- **同期 fill 後の force_resync**: `_on_auto_order_event` が fill status で `account_sync.force_resync()` を促進し、
  venue 権威 position 表示と post-trade 評価を即時発火（mock は EC stream を出さないのでこれが唯一の促進点）。
- **走行中戦略例外 → fail_run**: driver に `on_strategy_error` seam。on_bar/on_tick/on_order 例外を握り潰さず
  controller→`_on_auto_strategy_error`→worker thread で `host.fail_run("STRATEGY_EXCEPTION")`。
  test: `test_mock_live_strategy_exception_signals_error`。
- **post-trade live 発火 + baseline 堅牢化**: baseline は `last_snapshot` が None（初回 fetch が Replay mode で
  抑止）でも **明示 fetch_account** で run 開始時に確定（D7）。test: `test_post_trade_max_daily_loss_fires_on_live_path`
  （full chain で損失 snapshot → `MAX_DAILY_LOSS` violation → run STOPPED）。

### code-review 反映 round 2（#25 codex review・2026-06-13・GREEN 101 passed）

Safety Rails 退行ほか 3 件（High）を修正:
- **Safety Rails を LiveAuto で実効化**: ①MARKET の `order_notional_jpy` を **直近価格×数量**
  （`driver.last_prices` は当該 bar close で更新済み）で precheck に渡し `max_position_size_jpy`/
  `max_order_value_jpy` を有効化（価格未取得時のみ 0＝Nautilus native も market data 前は notional 不可と同じ）。
  ②旧 native の `max_order_value_jpy` を **`SafetyRails.check_pre_trade` の pure rail に追加**
  （`KIND_MAX_ORDER_VALUE`）。③`max_orders_per_minute` を **driver の 60s 窓 rate limiter**として実装
  （`KIND_MAX_ORDERS_PER_MINUTE`・monotonic・controller が `rails.limits.max_orders_per_minute` を注入）。
  test: `test_mock_live_max_order_value_denies_oversized_order` / `test_mock_live_max_orders_per_minute_rate_limits`。
- **戦略例外後にキュー済み注文を送らない**: `_signal_strategy_error` で `_intents.clear()`、`_drain` ループ
  先頭と `_process_intent` 入口で `_stopping` を確認して破棄。test:
  `test_mock_live_no_send_of_queued_order_after_strategy_exception`。
- **post-trade baseline を attach（on_start 発注）より前に確定**: `start_live_strategy` が baseline snapshot を
  `start_run` の前に解決（`_resolve_post_trade_baseline_snapshot`・last_snapshot 優先／None なら明示 fetch）し
  run_id 採番後に設定。on_start 即時約定後 snapshot を baseline にして初回損失を見逃すのを防ぐ。

### code-review 反映 round 3（#25 codex review・2026-06-13・GREEN 103 passed）

attach 失敗時の注文漏れと post-trade 取りこぼし 2 件（High）を修正。検証面はこの repo では Rust e2e/
FLOWS.md が無く Python pytest（`uv run pytest -q`）。各 fix は RED→GREEN の回帰ガードを伴う:

- **finding 1: attach 失敗（on_start 例外）でも注文が venue に送られる** — `driver.run_on_start` が
  `finally: await self._drain()` で on_start 例外後もキュー済み intent を drain していた。**成功時のみ drain し、
  例外時は `self._intents.clear()` して再送出**する形に変更（`driver.py:run_on_start`）。fail-loud な戦略
  （artifact 不足で on_start raise）が attach 失敗するのに注文だけ venue 到達する穴を塞ぐ。
  RED→GREEN test: `test_kernel_live_step5_afk.py::test_mock_live_no_send_when_on_start_raises_after_queueing`
  （on_start で submit 後 raise → `submit_order_call_count == 0` ＆ attach は例外）。
- **finding 2: rails 登録前の post-trade 評価が初回損失を取りこぼす** — rails+baseline の登録が `start_run` の
  **後**だったため、start window 内で走る post-trade 評価（on_start 約定の force_resync 等）が rails 未登録で
  スキップされていた。**登録直後に最新 snapshot を即時 fetch して `_evaluate_post_trade_loss` で必ず再評価**する
  （`live_orchestrator.start_live_strategy` ＋新ヘルパ `_fetch_account_snapshot`。cached `last_snapshot` は
  AccountSync callback 後にしか更新されないため fresh fetch）。違反確定で rails を外すので後続 callback と二重
  発火しない（冪等）。**RUNNING/STARTED を publish した後に再評価**する（共有 record の状態を lazy 読みする
  publish より先に STOPPED 遷移が走ると STARTED が出ず `ERROR→STOPPED` になるため）。
  RED→GREEN test:
  `test_kernel_live_post_trade_live.py::test_post_trade_fires_on_loss_at_run_start_without_extra_snapshot`
  （baseline=10,000,000 確定後 venue equity が損失へ → 追加 force_account_snapshot 無しで STOPPED+違反、
  かつ STARTED→STOPPED 順を assert）。
  注: round 3 時点では on_start 約定は gate（READY）で起こせなかったが、round 4 で start_run が attach 前に
  RUNNING へ遷移するようになり on_start 約定が full-path で起こる。本 fix はその on_start 約定損失も含め
  「baseline 確定〜run 開始の間に venue equity が損失へ動いた」状態を塞ぐ防御線。

### code-review 反映 round 4（#25 codex + 自前 code-review・2026-06-13・GREEN 113 passed）

review 指摘 2 件（High/Medium）＋ 自前 high-effort code-review の指摘 6 件を修正。各 fix は RED→GREEN ガード付き:

- **on_start 発注が full-path で必ず DENIED（High・review finding 1）** — `start_run` が `READY` のまま attach し、
  on_start drain 時に run gate（`is_running` が False）が新規発注を `STRATEGY_PAUSED` で弾いていた（`venue_calls=0`）。
  **attach の前に `sm.transition_to(RUNNING)`** へ移動（D8「on_start から発注可能」）。attach 失敗は RUNNING→ERROR。
  test: `test_on_start_order_reaches_venue_full_path`。
- **pre-trade DENIED 注文が rate-limit 枠を消費（Medium・review finding 2）** — `driver._process_intent` の rate check が
  precheck より前で、`MAX_ORDER_VALUE` 等で拒否される注文も枠を食い後続の正常注文を誤 deny。**rate check を precheck
  後・venue 送信直前へ移動**。test: `test_mock_live_pretrade_denied_order_does_not_consume_rate_limit`。
- **attach タイムアウトで孤児 driver が fail-open 発注（High・自前 #1）** — boundary の `fut.result(timeout)` 超過でも
  `_do_attach` が走り続け self._driver を commit、run は unregister 済みで gate が開きガバナンス無し発注。**`asyncio.wait_for`
  で attach 本体を live loop 上で時間制限**し、超過時 cancel→rollback（`except BaseException`）で teardown（fail-closed）。
  test: `test_attach_timeout_does_not_leave_orphan_driver`。
- **戦略例外 terminal で rails/baseline がリーク（Medium・自前 #2）** — `_fail_run_for_strategy_error` が release 漏れ。
  loss 経路・`_control_run` と対称に `_release_run_rails_locked` を追加。test: `test_strategy_error_releases_run_rails`。
- **cancel 競合の約定を握り潰す（Medium・自前 #3）** — `broker.cancel` が拒否以外を無条件 CANCELED 化し、競合約定を捨て
  venue と desync。**拒否以外は `apply_venue_update` に通す**。test: `test_cancel_race_fill_is_not_discarded`。
- **max_position_size が決済注文を誤 deny（Medium・自前 #4）** — `|建玉|+|注文|` で方向無視。`order_increases_exposure`
  で **建て増し注文にのみ cap 適用**（返済は常に許可）。test: `test_mock_live_position_cap_allows_reducing_order`。
- **REJECTED が PARTIALLY_FILLED を上書き（Medium・自前 #5）** — 部分約定済みに後着の REJECTED が来ると既約定を捨てて
  全体 REJECTED 化。**約定済みなら CANCELED で終端**し fill を保持。test: `test_reject_after_partial_fill_terminates_as_canceled`。
- **loss 確定後 fail_run 失敗で違反 toast が消える（Medium・自前 #6）** — `_fail_run_for_loss` が `LiveStrategyHostError` で
  違反 publish 前に return。**違反を先に publish**してから stop。test: `test_loss_violation_published_even_if_fail_run_errors`。
- **bus 購読が rollback で leak（Low・自前 #7）** — consumer 未起動の rollback で生成器 finally が走らず queue が残留。
  `MarketDataBus` を closable `_Subscription` 化し `driver.stop` で明示 close。test: `test_market_data_bus_subscription_close_unsubscribes_without_iteration`。
- **instrument_id 検証が末尾改行を許容（Low・自前 #8）** — `re.match(r"...$")` が末尾 `\n` 直前で一致。`re.fullmatch` に変更。
  test: `test_instrument_id_rejects_trailing_newline`。

### code-review 反映 round 5（#25 codex review・2026-06-14・GREEN 116 passed）

live-safety bypass 2 件（High）を修正。各 fix は RED→GREEN ガード付き（`uv run pytest -q` 全 GREEN・
import-purity 権威ゲート PASS）:

- **on_start 発注が金額rail を回避（High・review finding 1）** — `driver._process_intent` の notional が
  `last_prices.get(.., 0.0) × qty` のため、market data 受信前の on_start 注文は常に 0 円扱いとなり
  `max_order_value_jpy` / `max_position_size_jpy` を素通りして venue に到達していた。**参照価格が未取得で、かつ
  金額rail（order value / position size）が有効なら `NO_REFERENCE_PRICE` で deny**（`order_engine.requires_reference_price`
  が金額rail有効を判定。金額rail無効時のみ従来通り notional=0 で precheck を通す）。
  test: `test_mock_live_on_start_order_without_price_denied_when_money_rail_set`。
- **不正数量（≤0 / NaN / inf）を venue へ送信（High・review finding 2）** — kernel 経路に手動経路
  （`order_facade._place`）相当の数量検証が無く、`-100` 等が adapter に到達していた。**`_process_intent` の precheck 前に
  `math.isfinite(qty) and qty > 0` を検証し `INVALID_QTY` で deny**。`broker.modify` の `new_qty` / `new_price` にも同等の
  有限・正数検証を追加（不正値を venue へ送らず order 据え置き）。
  test: `test_mock_live_invalid_quantity_denied_before_venue` / `test_broker_modify_rejects_invalid_new_qty`。

> **Unity-Mono full Live gate（D5 layer 3）実施済み・GREEN（2026-06-14）**: `KernelLiveProbe.Run` を Unity
> 6000.4.11f1 batchmode（`-nographics -quit`）で実行し **exit 0 / PASS**。ログ（`C:\tmp\kernel_live_probe.log`）に
> `[KERNEL LIVE PASS] full mock LiveAuto ran under Unity-Mono ... Rust core absent, Shutdown clean`、FAIL マーカー 0、
> 新規 Unity crash dump 無し（実行前後で計 10 件のまま・最新は実行前日付）。これで CPython 全ゲート（116 passed）・
> import-purity 権威ゲート・Mono full Live teardown の 3 層すべて GREEN となり、**#25 の残ゲートは解消**。

> 注: 本 findings は #16（Strategy Editor）が `docs/findings/0010-strategy-editor.md` を採番したため
> **0010 → 0011 にリネーム**（番号衝突回避）。

**manual gate（D5 layer 3・Unity-Mono full Live・owner が Windows で実行）— 実施済み GREEN（2026-06-14・exit 0）**:
`Assets/Editor/KernelLiveProbe.cs`（`KernelTeardownProbe` 同型）。`run_mock_live.run()` を Mono+pythonnet で
完走させ fills=2/final_net=0/realized=200・`leaked==0`・clean `PythonEngine.Shutdown`・exit 0・新規 crash dump
無しを確認する。`KernelTeardownProbe`（Replay のみ）の Live 拡張。

実行手順:
```
# CPython 全ゲート
uv run pytest -q
# import-purity 権威ゲート単体（fresh subprocess full LiveAuto）
uv run python -m spike.kernel_live.run_mock_live          # → [KERNEL LIVE PURITY PASS] / exit 0
# Unity-Mono full Live（owner・Windows）
<UnityEditor> -batchmode -nographics -quit -projectPath . -executeMethod KernelLiveProbe.Run
```

## 関連コード seam（grill で確認済み）

- 置換点: `live_orchestrator.py:143`（`NautilusLiveEngineController` 既定生成）。Protocol =
  `strategy_host.LiveEngineController`（`attach`/`detach`/`cancel_inflight_orders`）。
- 戦略 attach の構築引数 seam（controller ctor）: `loop_provider` / `adapter_provider` /
  `runner_provider` / `on_safety_violation` / `on_order_event` / `on_telemetry` /
  `on_strategy_log` / `run_gate_provider`。KernelLiveEngineController も同一 seam を満たし、
  swap は class 名だけにする（blast radius 最小化）。
- tick/bar 供給源: 共有 `LiveRunner`（`runner_provider`）。`bus.subscribe()`（topic 無し fan-out・
  `event_bus.py`）と `add_tick_listener`（生 TradesUpdate）の 2 系統。
- import-purity リスク: `live_orchestrator.start_live_strategy` が `nautilus_trader...InstrumentId`
  を import（`live_orchestrator.py:1533`）。Live 経路 purity の AC を満たすため pure 実装へ要置換。
- 現 `strategy_loader.load` は `nautilus_trader.trading.strategy.Strategy` を import して subclass 検出
  （`strategy_loader.py:93`）→ Rust core ロード。kernel-native loader（`engine.kernel.strategy.Strategy`
  subclass 検出・nautilus-free）が必要。
