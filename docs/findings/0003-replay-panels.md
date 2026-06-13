# Replay panels Findings: status / positions / orders / run_result パネルの継ぎ目

- Issue: #11 (Replay panels — status/positions/orders/run_result）
- 親: #3 (Step 1: Unity host + 埋め込み Python + Replay parity)
- 関連 ADR（方針として参照・**変更しない**）: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed**, decision 7/8）, [ADR-0002 — embedded Python runtime placement & resolution](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（accepted）, [ADR-0003](../adr/0003-layout-persistence-capability-parity.md)（accepted）
- 配置の根拠: ADR-0002 self-protection ルール（slice 内で確定する下位事実は ADR に書き戻さず `docs/findings/` に記録し ADR を方針として参照）。本ファイルは #11 で確定した**設計判断と一次コード読みの記録**であり、ADR の方針を変更しない。
- 先行: #9（Replay tracer / [findings 0001](./0001-s1-replay-seam.md)）, #10（Replay chart / [findings 0002](./0002-replay-chart.md)）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ pythonnet 3.1.0 / CPython 3.13.13 / nautilus_trader 1.226.0（`HIGH_PRECISION=false`, 8-byte）/ venv `python/.venv`

> **状態: AFK + HITL 両ゲート GREEN（Mac leg, 2026-06-13）。** 設計は `grill-with-docs` で確定、`pair-relay` で M0–M3 実装。AFK 検証は §10、owner HITL playmode PASS は §11.1 に記録。ADR-0001 は `proposed` 維持（昇格条件 = Windows 再走, §9）。

---

## 1. 一次コード読みで確定した事実（issue 本文の前提を 2 点訂正）

issue #11 のタイトル「`get_state_json()` poll 駆動」と AC は、`get_state_json` の中身を誤って仮定していた。コードを読むと（S0 の "16-byte pin" 誤認・#10 POINT-A の silent zero-fill と同型の事実誤認）:

1. **`replay_state` を poll で取ると `IDLE` のまま。** 本 slice が継ぐ replay パス `start_nautilus_replay`（#9/#10 と同一）は `engine._replay_state` を一切触らない。`RUNNING`/`IDLE` を設定するのは *legacy* `DataEngine.start()/stop()`（`core.py:133/139`）のみ。→ poll の `TradingState.replay_state`（`models.py:69`）は nautilus replay 中ずっと `IDLE`。AC1 を poll 単独では満たせない。
2. **`positions`/`orders` は `get_state_json` に存在しない。** `TradingState`（`models.py:53`）に positions/orders フィールドは無い（grep 0 件）。replay の order/position は **sink** 経由で流れる（`push_order` ← `events.order` 購読 / `push_portfolio` ← `events.position`、`gui_bridge_actor.py:154/183`）。`get_portfolio()` は live `DataEngine` を見るが、nautilus replay の order/position は別インスタンスの `BacktestEngine` cache に居るため **live からは構造的に見えない**（poll では取れない）。
3. **`run_result` は `push_run_complete(run_id, summary)`** の `summary`（`nautilus_backtest_runner.py:148`、`fills_count`/`equity_points`/`max_drawdown`/`sharpe`/`sortino`）= sink。poll ではない。

→ **panels の権威ソースは `ReplayEventSink`（#10 の seam）と C# adapter lifecycle であって `get_state_json()` ではない。** poll の残存価値は AC4（GIL-marshaled poll が描画を stall させない機構実証）＋静的 config（`execution_mode`/`venue_state`）の運搬のみ。

---

## 2. パネル主データ源（確定）

| パネル | ソース | 備考 |
|---|---|---|
| status (run lifecycle `RUNNING`/`DONE`/`FAILED`/`IDLE`) | **C# adapter lifecycle** | adapter が `start_nautilus_replay` を呼んだ瞬間 = `RUNNING`、terminal sink `push_run_complete`/`push_run_failed` 受信 = `DONE`/`FAILED`、その後 `IDLE`。engine の `_replay_state` も poll も使わない。run を駆動する adapter が run-lifecycle の自然な所有者（ADR-0001 decision 8）。 |
| positions | `ReplayEventSink.TryDequeuePortfolio`（既存） | payload: `{buying_power, equity, positions:[{symbol, qty, avg_price}], orders:[]}` |
| orders | `ReplayEventSink.TryDequeueOrder`（既存） | payload: `{symbol, client_order_id, venue_order_id, strategy_id, side, status:"FILLED", qty, price, timestamp_ms}`（`OrderFilled` のみ） |
| run_result | `push_run_complete` の `Summary` | payload: `{fills_count, equity_points, max_drawdown, sharpe, sortino}` |
| poll (`get_state_json`) の役割 | **AC4 機構実証 ＋ `execution_mode`/`venue_state`** | replay 中 poll の `replay_state` は `IDLE`（誤情報）→ **使わない**。poll JSON から消費するのは `execution_mode`/`venue_state` のみ。 |

**却下した代替**:
- poll 忠実化（engine 改修で `start_nautilus_replay` を `_replay_state` 機械に結合）→ host 非依存で verbatim 移植中の engine に 2 replay パスの結合を増やす。買えるのは「AC の字面」だけ。木目に逆らうため却下。
- 純 poll（`get_portfolio()`/`get_orders()` 併用）→ backtest cache は別 `BacktestEngine` 内で live からは見えず空。アーキ上成立しないため却下。

---

## 3. trading fixture（AC2 を空転させない）

`push_order` は `OrderFilled`、`push_portfolio` は `PositionOpened/Changed` にのみ発火。既存の唯一の fixture `spike_bar_consumer`（#9）は **no-trade** → そのままだと positions/orders 空・`fills_count=0` で AC2 が空転（= #10 POINT-A の silent zero-fill / S0 の合成データ false-green と同型）。

**#11 は trading fixture を 1 個新規に著す**（`spike_bar_consumer` は #9 のまま不変・削除しない・#11 では使わない。「2 fixture 維持」の負担とは捉えない — #9 成果物は既にそこに在る）:

- **決定的な buy→sell シーケンス**: 既存 8918.TSE Daily catalog（**新規市場データ無し**）上で、bar N で固定数量 market buy、bar M で sell。→ `push_order` ≥2（buy fill + sell fill）、`push_portfolio` で PositionOpened→Changed→Closed、`run_result.fills_count` ≥ 2。
- **約定の確実性を構造 assert**: CASH account の market order が daily bar で実約定（次 bar open で fill）すること。lot_size/初期資金不足で silent reject されると `fills_count=0` に戻る → 「`fills_count > 0`」ではなく **期待 fill 数**を構造 assert（buy だけ通って sell が黙って落ちる回帰も捕まえる）。
- **層を分けて assert**（fixture バグ vs panel バグの切り分け）:
  - **sink 層**: `ReplayEventSink.OrdersPushed > 0` / `PortfoliosPushed > 0`（fixture が実際に trade した）。
  - **panel 層**: それを decode して描画できた。

---

## 4. playmode harness（supersede precedent の 3 つ目）

新規 throwaway **`ReplayPanelsHarness`** が同一 launcher（`InprocLiveServer` + `start_nautilus_replay` + **1 個の** `ReplayEventSink`）を駆動し、chart（`push_bar`）と status/positions/orders/run_result panels を **1 Play で同時描画**。chart＋panels が candlestick の隣で更新される統合視覚こそ #11 の成果物の形。

- `ReplayChartHarness`(#10) は `AutoBootstrapEnabled=false` 相当で **auto-bootstrap を flag off（温存・#10 再走用、削除しない）**。S0→#10 が踏んだ supersede precedent の延長（S0SpikeHarness を残したのと同じ）。
- **single Play-owner を構造的に担保**: 有効な `[RuntimeInitializeOnLoadMethod]` auto-bootstrap が Play あたり厳密に 1 個。`PythonEngine.Initialize()` の二重呼びを**静的ガードで検出して即 FAIL**（double-init / GIL 競合という既知失敗モードを「視覚ゲートが黙って成立しない」ではなく明示 FAIL に）。
- **1 sink・queue 別 drain**: chart=`TryDequeueBar`、panels=`TryDequeueOrder`/`TryDequeuePortfolio`（別 queue ⇒ 競合なし）。`Completed`/`Summary` は両者が GIL-free read-only。

---

## 5. poll & AC4（専用 poll thread + main GIL-free）

AC4「poll が main の描画を stall させない」を **専用 poll thread + main GIL-free read** で実装・実証する（main が `Py.GIL()` を取る間引き poll は AC4 が禁じる当の反パターン → 却下）:

- **poll thread** が `Py.GIL()` を取り `get_state_json` を呼ぶ。worker が GIL 保持中はこの poll thread が待つ（main ではない）。
- 結果 JSON を **latest-wins の volatile snapshot**（`volatile string _latestStateJson`）に上書き（status は最新状態のみ要る ⇒ queue ではなく単一スロット。bar sink が queue なのは全 bar 欲しいから）。
- **main は GIL を一切取らず** snapshot を read して status panel の `execution_mode`/`venue_state` を更新 → 構造的に stall 不能。
- **frame-time / frame-count assert が本体**（S0 の ≥300 フレームと同型）。poll thread + worker が回る間 main の frame cadence が予算内であることを構造 assert。これが無いと「動いてるが実は stall」を見逃す＝AC4 の false-green。
- **GIL-taker が 2 つ**（Python daemon worker + C# poll thread）→ poll cadence は控えめ（4–10Hz）で worker を starve させない。spike は correctness 重視でスループット低下は許容（Live の multi-GIL-taker により近い現実条件）。
- poll の `replay_state` は **使わない**（IDLE 固定の誤情報）。run-lifecycle は adapter から取る（§2）。

**AC4 framing**: #11 の poll は「機構の証明（non-stall GIL-marshaled poll）」であって「動的データ源」ではない。replay では静的 config を運ぶだけ。この proof は Live poll seam（#4/#7 — venue/state poll が tokio/asyncio loop と並走）への down-payment。replay の GIL 競合下で非 stall poll を最安で証明できる（今 harness は既に PythonEngine + per-bar GIL 保持 worker を回しており理想環境が立っている）。

---

## 6. issue #11 AC の訂正（適用済み）

issue #11 本文の AC は `get_state_json` の中身誤認に基づくため訂正した（S0 pin 訂正と同型）:

- ~~status panel が `replay_state` を poll で反映~~ → **status の権威ソース = adapter lifecycle**（poll の `replay_state` は IDLE 固定で使わない）。
- ~~positions/orders panel が `get_state_json()` スナップショットの該当フィールドで~~ → **sink drain**（`push_order`/`push_portfolio`）。
- run_result = `push_run_complete` summary（不変・元から sink）。
- poll の役割 = **AC4（non-stall GIL-marshaled poll）＋ `execution_mode`/`venue_state`** のみ。

---

## 7. 成果物（予定・durability）

| 区分 | 成果物 | 役割 | durability |
|---|---|---|---|
| panel decoder + model | `Assets/Scripts/ReplayChart/ReplayPanelDecoder.cs`（または同階層） | order/portfolio/run_result payload → 値モデル（JsonUtility を `Decode` に隠蔽、#10 `ReplayBarDecoder` と同型） | **durable** |
| adapter run-lifecycle | C# 側 run-lifecycle 所有型（start 呼出＋terminal sink で `RUNNING`/`DONE`/`FAILED`/`IDLE`） | status の権威ソース | **durable** |
| AFK 回帰ゲート | `Assets/Editor/ReplayPanelsDecodeProbe.cs`（予定） | headless decode ゲート + 期待 fill 数の構造 assert + sink 層/panel 層の分離 assert + orphan-free 継承 | durable（regression gate） |
| HITL playmode widget | `Assets/Scripts/ReplayChart/ReplayPanelsHarness.cs`（予定） | turnkey、chart＋4 panel を 1 Play 描画、double-init 静的ガード、frame-time assert | throwaway |
| trading fixture | `python/spike/fixtures/strategies/*.py`（予定） | 決定的 buy→sell、8918.TSE Daily catalog（新規データ無し） | — |
| reused seam | `Assets/Scripts/S1Spike/ReplayEventSink.cs`（#9） | order/portfolio queue + summary を既に保持（#11 が drain） | durable |
| superseded（温存） | `Assets/Scripts/ReplayChart/ReplayChartHarness.cs`（#10） | auto-bootstrap flag off で温存（#10 再走用） | — |

---

## 8. 🔭 trajectory flag（記録のみ・今は consolidate しない）

single-Play-owner 衝突が **S0 → #10 → #11 と 3 回連続**で噛んでいる。これは「throwaway harness が各々 PythonEngine lifecycle を所有する」設計の繰り返しコストの兆候。最終解は ADR-0001 decision 8 の **C# adapter が PythonEngine lifecycle を 1 度だけ所有し、chart/panels/canvas を attachable view として差す形**。#11 はまだ spike slice なので今は consolidate せず precedent に従う。この兆候は shell スライス（canvas/floating #5/#7）で「また新 harness か、adapter lifecycle owner に畳むか」を判断する材料。

---

## 10. 実装結果（ゲートログ・Mac leg）

実装は `pair-relay`（Navigator/Driver）で M0→M3 を順に実施。司令塔が全ゲートを authoritative に実行。

| Milestone | 成果物 | ゲート | 結果 |
|---|---|---|---|
| M0 | `python/spike/fixtures/strategies/spike_buy_sell.py` | CPython smoke（trading fixture が実トレード） | `bars=68 orders=2 portfolios=2 fills_count=2`（期待値ぴったり） |
| M1 | `ReplayPanelDecoder.cs` / `ReplayRunLifecycle.cs` | field-name audit ＋ Unity batchmode compile | producer key 逐語一致 / `exit=0` CS エラー 0 |
| M2 | `Assets/Editor/ReplayPanelsDecodeProbe.cs` | AFK headless 回帰ゲート（`-executeMethod ReplayPanelsDecodeProbe.Run`） | `[REPLAY PANELS DECODE PASS] bars=68 ordersPushed=2 portfoliosPushed=2 fills=2 orders=[1 BUY + 1 SELL FILLED] lifecycle=Idle->Running->Done`（sink 層＋panel 層 Decode＋FSM all GREEN under Mono） |
| M3 | `Assets/Scripts/ReplayChart/ReplayPanelsHarness.cs`（throwaway） | Unity batchmode compile（HITL playmode は §11） | `exit=0` CS エラー 0 |

成果物（durability は §7）:
- `ReplayChartHarness.cs`(#10) は `AutoBootstrapEnabled=false` で flag off（温存・supersede precedent）。`ReplayPanelsHarness` が唯一の Play-owner。
- M2 probe の **`Equity>0` panel-layer assert** が下記 §10.1 の engine バグを RED で検出 → fix 後 GREEN。

### 10.1 #11 で発見・修正した port 後 divergence（engine, fixed）

`GuiBridgeActor.make_position_handler`（`python/engine/live/gui_bridge_actor.py`）が raw `venue_str`（str）を `cache.account_for_venue()` に渡していたが、Nautilus は account を `Venue` オブジェクトで keying する — str lookup は `TypeError` を投げ、直後の `except Exception` がそれを握り潰して `equity`/`buying_power` を 0 に fallback していた（全 `push_portfolio` payload で equity 常に 0）。sibling の `replay_runner.py:135/208` は `Venue(venue_str)` で正しく取得しており、handler 側だけ str を渡して握り潰していた。M0 の count-only smoke では値を見ていなかったため不可視。M2 の値 assert（`Equity>0`、`ReplayPanelsDecodeProbe.cs`）が初めて検出。

**修正**（owner 承認 Option 3, 2026-06-13）: `account_for_venue(Venue(venue_str))` に wrap（lazy import）＋ fallback `except` に `log.warning(exc_info=True)` を追加（今後の回帰を silent zero ではなくログ化）。probe assert は弱めない。RED→GREEN: CPython portfolio smoke が PRE-fix `equity=0.0`（RED）→ POST-fix `equity=123456.0`（GREEN）、M2 再ゲート GREEN。verbatim 移植は初期移植の手段であり、backcast 所有 engine（ADR-0001 d7）の既知バグを温存する制約ではない、との owner 判断。

### 10.3 #9-12 レビュー指摘 Medium-1（closed position が panel に残る, fixed 2026-06-13）

`make_position_handler` が `cache.positions()` を使っていたため、SELL で決済された後も
Nautilus cache が保持する **closed position（qty=0）** が全 `push_portfolio` snapshot に乗り、
positions panel に `8918.TSE qty=0` の phantom 行が残り続けた（§11.1 の HITL ログがまさにこの症状）。

**修正**: `cache.positions_open()` に変更（closed position が drop し、決済完走で最終 snapshot が
flat になる）。RED→GREEN 回帰ゲート `python/tests/test_gui_bridge_positions.py`（fake cache の
`positions()`=closed qty=0 / `positions_open()`=空 で handler を駆動し payload の positions が空である
ことを assert。PRE-fix RED → POST-fix GREEN、pytest 非依存の単体スクリプト）。HITL probe
（`ReplayPanelsHarness.TryLogPass`）にも **完走時 positions=flat** のハード assert を追加（regress すると FAIL）。

### 10.2 code-review（simplify, CLAUDE.md 必須）

`code-review` を diff 全体に発動。Medium 1 件のみ修正: `ReplayPanelsHarness._pollServer`（launcher→poll thread に渡る PyObject 参照）を `volatile` 化（`ReplayEventSink` の `_runId`/`_summary` cross-thread 規律と一致、Mono の弱メモリモデル安全側）。再コンパイル `exit=0`。他指摘は throwaway-isolation 設計上許容（slice ごとの harness/probe 重複は supersede precedent の意図）か verified-correct な future-proofing で Medium 未満。

---

## 11. owner 向け HITL playmode ゲート手順（M3）

M3 harness は throwaway。AFK では compile までしか取れない（playmode 描画は owner が目視）。手順:

1. Unity でプロジェクトを開く（auto-bootstrap で Play を所有するのは `ReplayPanelsHarness` のみ — `ReplayChartHarness` は flag off）。
2. Play を押す。
3. 期待: 自前 Canvas に **candlestick chart ＋ 4 panel（status / positions / orders / run_result）** が表示され、replay 進行とともに更新。status が `RUNNING`→`DONE`、orders に BUY/SELL fill、positions に保有、run_result に summary 指標が出る。worker の backtest 実行中も main は滑らかに描画継続。
4. Console 最終的に一度だけ: `REPLAY PANELS PASS: frames=N bars=68 ...`（`frames>=TARGET_FRAMES` かつ poll サンプル取得 ＆ hitch<=MAX_HITCHES = AC4 非 stall 実証）。
5. PASS が出たら本 §11 に owner 目視ログを追記し ADR-0001 の Mac leg 証跡に積む（Windows leg は §9 で未消化のまま）。

### 11.1 owner HITL PASS（2026-06-13, 目視承認）

owner が Unity でプロジェクトを開き Play を押下。candlestick chart の右に 4 panel（status/positions/orders/run_result）が表示され、replay 進行とともに更新。Console 最終 PASS ログ（VERBATIM）:

```
REPLAY PANELS PASS: frames=3022 bars=68 orders=2 portfolios=2 fills=2 pollSamples=55 hitches=0 maxDt=0.040s mode=Replay venue=DISCONNECTED (chart + 4 panels + AC4 non-stall GIL-marshaled poll all GREEN under Unity Mono)
```

owner 目視（screenshot）で確認された panel 実値:
- **STATUS**: `Running` → 完走で `Done`、`mode: Replay  venue: DISCONNECTED`（poll 由来の静的 config が正しく表示＝AC4 poll seam が値を運んでいる）。
- **POSITIONS**: `equity=10000000 bp=10000000`, `8918.TSE qty=0 @ 8` — **equity が非 0**。これは §10.1 の `Venue(venue_str)` fix の視覚的証跡（未修正なら equity=0 表示になる）。なお `qty=0` 行は **後述 §10.3 で fix した別バグ**（`cache.positions()` が closed position を残す）の症状。fix 後は決済完走で最終 snapshot が **flat**（positions 空）になる。
- **ORDERS (2)**: `BUY 100 @ 8 FILLED` / `SELL 100 @ 8 FILLED` — trading fixture の決定的 buy→sell が両 fill とも panel に decode 描画。
- **RUN RESULT**: 完走で summary 指標表示（screenshot は frame 2553 の run 進行中スナップショットのため空、PASS 時点 frames=3022 で populated）。

**AC 達成の証跡**:
- AC1（status = adapter lifecycle）: `STATUS: Running`→`Done`（poll の replay_state IDLE には依存せず、`ReplayRunLifecycle` 駆動）。
- AC2（positions/orders = sink drain）: ORDERS/POSITIONS が push_order/push_portfolio 由来の実値で更新。
- AC3（run_result = push_run_complete summary）: fills=2 を含む summary 表示。
- AC4（non-stall GIL-marshaled poll）: `pollSamples=55` 取得（poll thread が GIL 越しに get_state_json 稼働）かつ `maxDt=0.040s` / `hitches=0`（main の frame cadence が 200ms 予算内 = worker + poll thread の 2 GIL-taker 下でも main 無 stall）。

worker の backtest 実行中（bars 0→68 流入）も main は滑らかに描画継続、`OnDestroy` で poll thread を retire し interpreter は alive 据え置き（GIL 再取得 deadlock 回避、#10 同型）。**#11 Replay panels は Mac leg で AFK + HITL 両ゲート GREEN。** ADR-0001 は `proposed` 維持（昇格条件は Windows 再走、§9）。

---

## 9. 射程外

- **Windows leg**（#2 から継承）: Mac-green は `win_amd64` wheel を Windows-Mono で動かす経路を未証明。
- **multi-instrument**: #11 は single-instrument。multi は #5/#7。
- **layout persistence**（#12）: 本 slice に含まない。
- ADR-0001 は `proposed` 維持（accepted 昇格条件は Windows 再走 PASS）。本ファイルは Mac leg のみで昇格を主張しない。
