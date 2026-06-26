# findings 0118 — live 運用中の universe 追加/削除を kabu mock で回帰する（LIVEUNIV-01..05）

2026-06-26。owner 依頼「live 運用中に銘柄を追加/削除する strategy を作り、kabu live mock データで
不具合が起きないかテストする」。`bt.universe.*`（#141-145 / [ADR-0031](../adr/0031-cell-driven-dynamic-universe-bt-universe-api.md)）と
kabu mock fixture（[findings 0117](0117-kabu-live-mock-fixture.md)）を **1 本の縦串**に縫い、実 prod
キャプチャを流しながら戦略 cell が universe を mid-stream 編集する経路をゲート化した。方針は ADR-0031（無改変）。

**追補（2026-06-26・owner 依頼「データ供給層を kabu か立花か選択可能に」）**: gate を **venue-selectable** に拡張。
`_make_feed(venue)`／`_VENUES=["kabu","tachibana"]` を追加し、fixture を**両 venue で parametrize**（LIVEUNIV-01..05 ×
{kabu, tachibana} ＝ **10 passed/3.5s**）。データ供給層だけが venue 別（kabu=`KabuPushFrameProcessor.process(frame)`／
tachibana=`FdFrameProcessor.process(fields,recv_ts_ms)`・`row="1"`）で、`decode(rec)→TradesUpdate|None` に正規化＝
それ以降の churn 駆動・assert は **venue 非依存**。tachibana mock は [findings 0119](0119-tachibana-live-mock-fixture.md)
（`tachibana_live_mock_4sym.json` ＋ raw capture）。立花 RED litmus も確認（remove enqueue 削除→LIVEUNIV-02/03 RED）。
**設計**: 各 venue feed は production adapter を忠実ミラー（`spike/kabu_replay_multi.py`／`spike/tachibana_replay_multi.py`）。
tachibana は FD frame だけが market-data（KP/SS/US/ST は接続/account 層＝skip）・`recv` ISO から絶対 epoch ms を復元（raw t_ms は
capture 相対で `ts_ms` fallback に使えない）・side="unknown" の trade は捨てる。

## 設計の確定（grill でコード裏取り・2026-06-26）

### 既存ゲートが覆っていない seam（縦串の穴）

| 既存ゲート | 何を pin しているか | 抜けている所 |
|---|---|---|
| `BTUNIV-01..16`（pytest/AFK） | `bt.universe.*` の enqueue/list・C# registry apply | **fake registry**（実データ無し） |
| `UNISUB-01..08`（AFK） | C# `Registry.Changed`→subscribe/unsubscribe 配線 | **MOCK venue**（market-data 無し） |
| `kabu_replay_multi`（spike）| codec→aggregator→reducer の 4 銘柄同時更新 | **static universe**（編集無し） |
| **穴** | — | **戦略 cell が add/remove する *最中に実 kabu フレームが集約パイプラインを流れる*** |

「live add/remove でチャート/フィードが壊れる」型のバグはまさにこの継ぎ目に棲む（未購読銘柄の残留
フレーム・`_partial_push` 中の `_aggregators` 変異・reducer per_id の orphan）。pure-Python で決定的に閉じる。

### 駆動経路（忠実な LiveAuto・ADR-0025 + ADR-0031 S4/S5）

`build_live_marimo_loader(universe_bridge=EngineUniverseBridge(engine))` →
実 marimo cell（`spike/fixtures/strategies/universe_churn_cell.py`）を
`KernelLiveEngineController` → `driver` → `LiveCellBridge` worker で駆動。cell の
`for bar in bt.replay(): bt.universe.add/remove(...)` が edit を engine universe channel に enqueue。
**PUMP が C# host 役**を演じる: `engine.drain_universe_edits()` を drain し各 op を
`runner.subscribe/unsubscribe` ＋ `engine.push_universe_ids` に適用（`DriveUniverseBridge` →
`InstrumentRegistry.Changed` → `LiveSubscriptionCoordinator` の忠実模写）。market-data は **実 prod kabu
キャプチャ**を production `KabuPushFrameProcessor` で decode → subscribe-gated `MockVenueAdapter` に inject。

### grill で裏取りした load-bearing な事実

- **driver は bar を初期 `instrument_ids` に絞る**（`driver.py:296` `evt.instrument_id not in self._instrument_ids`）。
  ＝mid-stream **追加**銘柄は data/chart 購読になるが **cell ループを駆動しない**（cell は初期 universe の bar で回る）。
  逆に cell 駆動銘柄を **削除**するとその bar が止まる。この 2 つが試すべき端。
- **MockVenueAdapter は subscribe-gated**（`inject_tick` が未購読 id を drop・`mock_adapter.py:124`）＝
  **記録された market-data が在る ⟺ その瞬間購読されていた**。よって非空虚 floor が構造的に立つ（add の証明にタイムスタンプ不要）。
- **二重防衛**: adapter の subscribe-gate に加え `LiveRunner._run` も `_is_subscribed` で未購読フレームを落とす
  （`live_runner.py:199` 防衛線）。`_partial_push` は `list(self._aggregators.items())` のスナップショットで
  回す（churn 中の "dict changed size" を回避・`live_runner.py:235`）。unsubscribe は dead `_last_partial` key も掃く（:164）。
- **両 bt ハンドルは `Backtester`**。LiveAuto は `LiveCellBackend` に `join_instrument` 無し（duck-type skip）＝
  add は subscribe のみ（mid-stream data join は Replay 専用 S2）。

### churn シナリオ（`universe_churn_cell.py`）

cell 駆動銘柄＝scenario instruments `[7203, 8306]`（kabu mock の Minute bar）。mid-stream:

```
bar 1: bt.universe.add("9984.TSE"); bt.universe.add("285A.TSE")   # 追加（live 購読＋chart）
bar 2: bt.universe.remove("8306.TSE")                             # cell 駆動銘柄を削除（bar 停止）
bar 3: bt.universe.remove("9984.TSE")                             # 追加済み銘柄を削除（unsubscribe）
```

最終 universe → `{7203.TSE, 285A.TSE}`。kabu キャプチャは 150s・10:31:12→10:33:42 JST で 10:32/10:33 の
Minute 境界を跨ぐので、cell 駆動銘柄は 3 本以上の closed bar を産み bar1-3 が確実に発火する（境界順序より
bar1/2＝両 10:32 close、bar3＝survivor 7203 の 10:33 close）。

## gate（pytest `test_kabu_live_universe_churn.py`・module-scoped fixture で replay は 1 回）

| Action-ID | 不変条件 |
|---|---|
| `LIVEUNIV-01` | **mid-stream ADD**: 初期 universe に無い 9984/285A が `bt.universe.add`→subscribe 経由でのみ live data を産む（subscribe-gate により構造的非空虚） |
| `LIVEUNIV-02` | **REMOVE**: churn 後に削除 id（8306・9984）へ新 trade を inject しても bus に届かない／survivor（7203・285A）は流れる。01 の floor（no-op remove なら削除 id が流れ続け RED） |
| `LIVEUNIV-03` | **最終 membership 一致**: `runner.subscribed_ids()` ∧ engine registry mirror が共に `{7203,285A}` |
| `LIVEUNIV-04` | **no crash / clean teardown**: worker が無例外終了・attach が RUNNING 到達・detach で orphan 無し（`_partial_push` が churn を生存） |
| `LIVEUNIV-05` | **reducer per_id 一貫**: 記録した closed kline が per_id_ohlc_points を survivor 分埋め・未 close id の orphan series 無し |

`@pytest.mark.scenario("LIVEUNIV-0N")` で `conftest` が実 outcome から `[E2E LIVEUNIV-0N PASS/FAIL]` を吐く
（rollup レール）。**5 passed / 約 3.3s**。

## RED→GREEN litmus（delete-the-production-logic）

- `EngineUniverseBridge.remove` の enqueue を消す → **LIVEUNIV-02/03 RED**（削除 id が流れ続け membership 不収束）。01/04/05 は GREEN。
- `EngineUniverseBridge.add` の enqueue を消す → **LIVEUNIV-01/02/03 RED**（追加 id が live に出ない）。04/05 GREEN。
- 両 litmus とも 8s soft-converge poll により ~11.7s で granular な per-id RED を出し、revert で 5 passed に戻る（実機確認済み）。
- **注記（防衛の二重性）**: `LiveRunner.unsubscribe` の *venue 通知*（`adapter.unsubscribe`）だけを消しても
  LIVEUNIV-02 は GREEN のまま——`_run` の `_is_subscribed` 防衛線（aggregators pop）が残留フレームを落とすため。
  これは **vacuity ではなく resilience**（設計どおりの二重防衛）。非空虚の litmus は両層の上流（bridge enqueue）を壊して取る。

## 設計判断（fixture / 観測）

- **convergence は soft**（`_poll`・hard raise しない）。remove/add 鎖が壊れたら fixture error ではなく **granular な
  per-id RED**（LIVEUNIV-02/03）として surface させるため。`_wait` を hard-fail にすると 1 つの壊れで全 id が error 化し litmus が粗くなる（書き直して解決）。
- **raw capture を第一・committed fixture を fallback**: `_capture_path()` は owner 指定の raw
  `spike/captures/kabu_mock_20260626T013342Z.json`（14MB・.gitignore）を優先し、不在時は committed
  `tests/fixtures/kabu_live_mock_4sym.json`（findings 0117）に落ちる＝CI でも走る。両者は codec 忠実で同一 trade を産む。
- **観測は production seam 経由**: 独立 `runner.bus.subscribe()` で全 published event を記録／pump が registry mirror を push。
  reducer 半は production `live_kline_to_reducer_kline`＋`apply_event` を再利用（再実装しない・findings 0117 規約 #1）。

## Unity AFK 側（findings 0117 #4 の「4 銘柄 fixture 拡張」）

`KabuLiveChartRenderE2ERunner` を 4 銘柄 state JSON へ拡張（`CHARTRENDER-04/05`）。pytest が DATA 半
（codec→集約→購読 churn）を、AFK が RENDER 半（per_instrument state JSON→decode→ChartView 同時描画・削除 id→0 candle）を担う。

- **fixture**: `Assets/Tests/E2E/Editor/Fixtures/KabuMock4SymChartState.json`（80KB）。生成器
  `python/spike/gen_kabu_4sym_chart_state.py` が committed lightweight mock（`kabu_live_mock_4sym.json`・CI 再現可）を
  `kabu_replay_multi` と同じ production パイプラインで再生し、**production `TradingState.model_dump_json()`** で serialize
  （C# が poll する正確な形）。4 銘柄が全て per_instrument に在り、**実密度差**（7203=73/8306=62/9984=150/285A=151）を保持。
- `CHARTRENDER-04`: 各 per_instrument id を decode→render し各自の count（2*count rect）。**4 count に ≥2 distinct** を要求＝
  共有/定数 series を返す decoder を弾く非空虚 floor（単一銘柄 9501 fixture では作れない locator 曖昧性解消）。
- `CHARTRENDER-05`: 同 fixture で **不在 id（6758.TSE＝削除済み）→ 0 candle**、生存 sibling 285A は描く＝removal は当該 chart
  だけ blank（multi-entry per_instrument で sibling へ fall-through しない・CHARTRENDER-02 の absent-in-isolation を超える）。
- **AFK 結果**: `pwsh scripts/run-live-e2e.ps1 -Method KabuLiveChartRenderE2ERunner.Run` → **CHARTRENDER-01..05 5 PASS / exit 0**
  （Python-FREE・pure C# decode+paint）。compile PASS（`error CS` 0）。litmus は decoder/Render を壊すと 01/04 RED（共有 series→04 の
  distinct 不成立）／不在 id が sibling へ fall-through→05 RED（.md 台本に記載）。decoder 本体の RED→GREEN は CHARTRENDER-01（findings 0111）が既に pin。

## 再走手順

```
cd python && uv run pytest tests/test_kabu_live_universe_churn.py -v   # LIVEUNIV-01..05（5 passed）
# AFK（render 半）:
pwsh scripts/run-live-e2e.ps1 -Method KabuLiveChartRenderE2ERunner.Run
```

## §Review — code-review(simplify high) 後の修正（2026-06-26・2 finder angle × verify）

harness の vacuity/race を 8-angle review（2 並行 finder）が検出。**Medium を全解消**（GREEN baseline 5 passed・RED litmus 維持）:

- **F1（Medium・LIVEUNIV-05 が tautology）**: 旧版は fresh `ReducerState` に `closed_ids` をそのまま食わせ
  `keys ⊆ closed_ids` を assert＝apply_event の append-by-iid を写すだけ・`closed_klines` 空でも `set()<=set()` で空虚 GREEN。
  **修正**: recorder が**実 closed KlineUpdate（full OHLC）**を捕捉 → production reducer に流し ① 非空 guard ② **≥2 distinct id**
  （multi-symbol churn を要求）③ `per_id keys == closed-bar ids`（exact・merge/orphan を検出）④ **per-id count 一致**（routing）。
  litmus 実証: 全 kline を primary に mis-route すると per_id keys `{7203}` ≠ 4-symbol closed_ids ＝ **FAIL**（非空虚確認済み）。
- **F2（Medium・LIVEUNIV-04 `detached` が空虚）**: `controller.detach` は `_driver=None` を無条件代入し stop/join は例外を握る＝
  `_driver is None` は常に True。**修正**: detach 前に bridge を捕捉し、後で **worker thread の `is_alive()`** を観測（orphan/hung join を検出）＋`bg_errors==[]`。
- **F3（Medium・subscribed_ids race）**: `_poll` 内の `runner.subscribed_ids()`（=`set(self._aggregators.keys())`）を main thread で読む間に
  pump が loop thread で `_aggregators` を変異＝`RuntimeError: dict changed size`。**修正**: `_poll` の predicate を try/except で包み transient を「未達」として retry。
- **F4（Medium・pump/recorder 例外の握り潰し）**: `run_coroutine_threadsafe` の future を捨てていた＝dead recorder が silent に
  data assert を空虚化。**修正**: 両 loop task を try/except で包み `bg_errors` に記録、LIVEUNIV-04 で `==[]` を assert。
- **F5（Low・probe timing fragile・false-fail 方向）**: 固定 `sleep(0.3)` を **survivor 録画の `_poll`**（決定論 witness）へ置換＝遅い loop は待ちになり false RED 化しない。
- **F6（Low・9984 probe 自己充足性）**: `probe_after["9984.TSE"] is False` は never-subscribed でも成立。**修正**: LIVEUNIV-02 で
  8306/9984 が `data_ids_during` に在る（＝確かに live だった）ことを先に pin してから「churn 後 drop」を判定。

## 教訓

- **subscribe-gated mock は「記録された data の存在」を購読の構造的証明にできる**＝add の非空虚 floor がタイムスタンプ不要で立つ。
- **二重防衛（venue gate + runner guard）系のゲートは、片層を壊しても GREEN のまま（resilience）**。非空虚 litmus は
  両層の *上流*（feature の enqueue）を壊して取る——「片層 break で GREEN＝vacuity」と早合点しない。
- **convergence wait は soft に**＝壊れた production を granular な per-id RED に翻訳でき、litmus が鋭くなる（hard-fail は全 id を巻き込む）。
- grill で driver の bar フィルタ（初期 instrument_ids）を裏取りしたことで「追加銘柄は cell を駆動しない／削除で cell 駆動が止まる」
  という *試すべき端* を正しく特定できた（コード読解せず推測すると逆の cell 設計を書く）。
