# findings 0092 — #112 marimo cell を Replay/Auto 両駆動（mode-aware `bt` façade・A-1 ランデブー・granularity 配線）

方針: [ADR-0025](../adr/0025-marimo-cell-drives-both-replay-and-live-mode-aware-bt.md)（marimo 戦略 cell を Replay と Auto の両方で駆動する）。
関連: ADR-0016（notebook = backtest・per-cell RUN）／ADR-0012（marimo embed 実行モデル）／ADR-0021（単一 venue 実行時再バインド）／findings 0026（footer LiveAuto）／findings 0046（thin_drain）／findings 0073（Phase 4 `bt.replay()`）。

本 findings は **#112** の `/grill-with-docs` セッション（2026-06-23・owner HITL Q1–Q6）で確定した下位決定を会話で消えないように固定する。ADR-0025 / ADR-0016 / ADR-0012 / ADR-0006 / ADR-0007 は immutable（自己保護条項）。実装事実は本 findings に固定し ADR を「方針」として参照する。

---

## 0. 現状の構造（コードで両端まで裏取り済み）

| seam | 場所 | 事実 |
|---|---|---|
| Replay cell 駆動 | `_backend_impl.py:731 run_cell` → `:1062 _build_notebook_bt` | `bt.replay`/`bt.step` を含む cell でだけ `Backtester.from_scenario(sink=ReplayKernelObserver, stop_event)` を build し marimo globals へ注入。`bt` は `KernelStepper`（Replay 専用 state machine）の薄いラッパ＝cell が pull |
| Live 登録 | `register_live_strategy` → `strategy_registry.register` → `strategy_loader.load(base_cls=Strategy)` | marimo cell を Strategy サブクラス 0 個で reject（`strategy_loader.py:116`）。marimo 検出も `MarimoStrategy` も **live 側未配線** |
| Live 実体化 | `start_live_strategy` → `strategy_host.start_run`（`:182` 再 load → `:223 controller.attach(strategy_cls=…)`）→ `controller.attach`（`:176 strategy_cls(**kwargs)` → `:186 KernelLiveDriver(strategy=…)`）| `strategy_cls` が単一 materialize 点。marimo cell は持たない |
| Live bar 駆動 | `controller.attach` → `driver.start_consumer` → `driver._consume`（`driver.py:281-302`）| asyncio live-loop thread。**`is_closed=True` 確定 bar のみ** `strategy.on_bar(bar)` → `await _drain()`。partial は弾く（D3） |
| Live universe | `controller.py:146 _live_instrument_ids(scenario)` → `:216 runner.subscribe(iid)` | scenario の **universe 全体を subscribe**（cell の multi-instrument を満たす） |
| Live 発注 | `_Ctx.submit_market` → `driver._enqueue`（`driver.py:316`）→ `_drain` → `_process_intent`（pre-trade rails）→ `broker.submit` | `_intents` deque は **live-loop thread しか触らない**。`_drain` 再入安全（`_draining` ガード） |
| Live 余力 | `driver._buying_power`（`driver.py:152-162`）| venue 余力 provider 権威・未取得時 kernel cash fallback |
| 非同期 fill | `driver.apply_venue_async_event`（`driver.py:228-273`）| poll/EC 由来。**bar-drain とは別経路で live loop 上を走り portfolio を mutate**（R2 の根拠） |
| per-cell RUN ⟂ footer ▶ | `NotebookRunController` / `NotebookRunLane` vs `LiveAutoTransportViewModel` | **完全別経路・共有コードなし** |

**issue 本文の誤認（訂正済み）**: 「`v19_morning.py`/`.json` 削除済み・Auto 手段ゼロ」は現 main（c6d9006）で **false**。3 ファイルとも HEAD に存在（`git ls-files` 確認）＝parity oracle として活きる。

---

## 1. 確定した設計（Q1–Q6・owner HITL binding）

### Q1 — 案A（mode-aware `bt` façade）。案B（reactive 再利用）却下

`bt` = **(BarSource, ExecutionSeam) の façade**。Replay = historical iterator + `KernelStepper`／Auto = venue 確定 bar キュー + `KernelLiveDriver.ctx`。cell は無改変。案B 却下根拠は ADR-0025 D1（休眠枝蘇生＋cell 書き直し退行＋AC 違反）。**nautilus DNA（backtest/live で戦略は分岐しない・engine が data/exec 供給）の踏襲**＝`bt` が register-time に mode 適合の seam を注入する。

### Q3 — A-1 ランデブー（lock-step）＋ R1 ＋ R2

- **A-1**: live consumer が `await bridge.drive_bar(bar)`（bar を `queue.Queue` へ put → cell の「bar 完了」signal を `await`）→ 解けたら `await _drain()`。cell ループ（worker）は `bt.replay().next()` が `queue.get()` でブロック → 本体実行 → 次の `next()` で完了 signal。**業界標準の単一スレッド順序保証をスレッド分割越しに再構築**。
- **R1**: submit は worker ローカル list に貯め、次の `next()` で「intent 束＋完了 signal」を **1 回の `call_soon_threadsafe`** で live loop へ。`on_bar→drain` 順序を構造保証（タイミング非依存）。`_intents` は live-loop thread のみ。
- **R2**: `bt.portfolio()`/`bt.buying_power()` も live loop へ marshal（`run_coroutine_threadsafe(...).result()`）。非同期 fill（`apply_venue_async_event`）との positions dict 競合を構造的に消す（立花 demo 場中 = AC#3 で踏む）。
- **R3（Q6 で追補）**: `bt.submit_market(qty)` は instrument 引数なしのまま、live では **現在 drive 中の bar の instrument** へ（replay "open bar's instrument" の live 版）。primary `instrument_id` は attach nominal で submit ルーティングと別物。

機構の確定形（bar 配送と完了通知で別プリミティブ）:
- **bar**: live-loop → worker = `queue.Queue`（worker が `.get()` でブロック）。
- **完了**: worker → live-loop = `call_soon_threadsafe` で loop-bound future / event を解決（R1 の一括ホップに同梱）。

### Q4 — per-cell RUN を mode-aware 単一 launcher・footer ▶ 退役

per-cell RUN が ExecutionMode を見て Replay bt / Live bt を分岐（ADR-0025 D3）。footer ▶ 廃止（mode セグメントは存続）。live run は「marimo cell を run する」と同一・システムは誘導しない。走行中 ▶→■ で stop=teardown。

### Q5 — marimo 強制 guard（非marimo = error・分岐なし）

`_kernel_loader` を「marimo App を build → `bridge_factory` を返す／build 不能なら raise」に**置換**（marimo↔imperative 分岐を持たない）。判定は `load_app(...) is None`（replay `_backend_impl.py:348-350` と同じ）を**唯一の道**。registry（検証）/ host（実体化）は同一 seam（`live_orchestrator.py:155`）＝1 箇所。エラー契約: `StrategyLoadError("not a marimo notebook: <path>")` ＋ 専用 `error_code = NOT_A_MARIMO_NOTEBOOK`（UI「marimo notebook ではありません」）。broken syntax は `SyntaxError` 素通し。命令型 `strategy_loader.load` は editor live 経路から外れる（oracle 直接 caller 用に存続）。スコープ = run/materialize のみ marimo 強制（Open 時 1-cell wrap 据え置き）。

### Q6 — 終了は ■/切断のみ・granularity 配線

- **終了（ADR-0025 D5）**: 寿命 = ■/切断。live `bt.replay()` は **キュー空 ≠ StopIteration**（番兵を引いた時だけ StopIteration・空キューは黙ってブロック=idle）。場引けは idle で自然表現。
- **teardown スレッド順序（D5-R）**: ■/切断 → `controller.detach`（caller thread）→ `run_coroutine_threadsafe(driver.stop(), loop).result()`。`driver.stop`（live loop）: `_stopping` → consumer cancel → 番兵 put → `bridge.on_stop`、**worker join しない**。`.result()` 後に **caller スレッドで worker join**（R2 往復を live loop が捌けて安全）。不変条件: live loop は worker を block-join しない／往復を跨いでロック保持しない（nautilus self-deadlock 警告の契約化）。
- **scenario→live マッピング**: universe＋granularity = scenario（両モード）。venue = 接続中 venue。primary instrument = SelectedSymbol ∈ universe、無ければ universe[0]。initial_cash = 無視（venue snapshot 権威・`fetch_account` seed）。start/end = 無視（live は「今」）。
- **granularity 配線（D6・理想形採用）**: 現状 `live_orchestrator.py:233` で `interval_ns = 60*1e9` ハードコード・scenario.granularity は live で**未参照**＝v19 が "Minute" だから偶然一致。`scenario.granularity → normalize_granularity → interval_ns`（`duckdb_bars._GRANULARITIES` 由来・nautilus-free）で配線。`LiveRunner` は `intervals_ns` 対応済み（`live_runner.py:46-52,77`）。

---

## 2. parity oracle（合否の機械定義）

`v19_morning.py`（命令型 `V19MorningStrategy(Strategy)`・on_bar push）＋`test_v19_auto_live_afk.py` が **退役させずに**正解を定義する。cell live 経路の合否 = 「cell を Auto で回した発注/約定列が、`v19_morning.py` を `KernelLiveEngineController` で回したのと**同一**か」。oracle は editor loader を通らず `load_strategy(_V19_PY, base_cls=KernelStrategy)` を直接叩く（`test:85`）ので、D4 の「editor loader を marimo 専用化」と両立。

oracle が pin する AC（`test_v19_auto_live_afk.py`・cell 経路へ移植）:
- **AC#1**: RUNNING 到達・intent→mock fill→tracked position・**確定 bar（ts≥10:00 JST）のみ駆動**。
- **AC#2**: `buying_power()` は venue 余力 provider（¥15k）を読む（seed kernel cash ¥10M ではない）＝余力ゲートが top-k を 1 件にトリム。
- **AC#3**: **partial bar（is_closed=False）は on_bar を駆動しない**（partial 10:00 → 発注 0・確定 10:00 → 発注）。
- **AC#4**: graceful stop（cancel_inflight → detach）が driver を解放。

→ **退役順序**: cell live 経路が上記 AC を cell で緑化 → 最後に `v19_morning.py` 退役を判断（issue の「もう消した」前提のまま進めると正解定義を失う）。

---

## 3. 実装スライス（順序・コードが物理的に固定する依存）

| # | スライス | 内容 | gate |
|---|---|---|---|
| S1 | mode-aware `bt` BarSource/ExecutionSeam 抽象 | `Backtester` を (BarSource, ExecutionSeam) で構成できるよう façade 化（Replay 経路は byte-identical 不変） | 既存 #24 golden / `test_backtester_phase4` 不変 |
| S2 | `LiveCellBridge` ＋ driver フック | bridge（worker thread で cell ループ・rendezvous queue・完了 future）＋`_consume` の `await drive_bar` フック（imperative path 無改変） | pytest: rendezvous 順序・R1 一括・R2 marshal・番兵 teardown |
| S3 | live materialize（marimo guard） | `_kernel_loader` 置換（marimo build→bridge_factory／非marimo raise）・`NOT_A_MARIMO_NOTEBOOK` | pytest: marimo→bridge / 非marimo→error / registry・host 経由 |
| S4 | granularity 配線 | `scenario.granularity → interval_ns`・`LiveRunner` 供給・60s ハードコード撤去 | pytest: Daily/Minute で interval 一致・非 Minute cell が 1 分駆動しない |
| S5 | mode-aware `run_cell` ＋ C# per-cell RUN | Auto+接続で live bt 注入＋register→start→attach・▶→■ stop・footer ▶ 退役 | C# AFK（per-cell RUN が Auto で live run 起動・▶→■ teardown・mode セグメント存続） |
| S6 | **parity oracle 緑化（cell live）** | cell を `KernelLiveEngineController` 経由 MOCK で回し `v19_morning.py` と同一発注/約定列 | parity gate（cell ≡ v19_morning.py の order/fill 列） |
| S7 | HITL E2E（立花 demo・AC#3 本番） | Replay 実行 → 立花 demo ログイン → Auto 切替 → 同 cell が live bar 駆動 → 発注（demo・場中は約定）・**バイパス無し UI 操作再現** | owner HITL（CLAUDE.md E2E ルール: UI 操作・デプロイ済み環境・画面値と保存状態の両方） |

**下位機構の未決（実装中に pin）**:
- granularity フィルタ機構: `KlineUpdate` に `interval_ns` タグ追加（frozen pydantic・default で後方互換）して driver が `evt.interval_ns == run_interval` で filter する案 vs per-run aggregator 供給。`KlineUpdate` は現在 interval タグ無し（`adapter.py:98-117`）＝前者なら 1 field 追加。
- footer ▶ 退役に伴う `LiveAutoTransportViewModel` の pause/resume の処遇（per-cell へ移送 or 廃止）。
- bridge の `on_start` 相当（worker thread spawn → 最初の `next()` ブロック到達を「on_start precedes data」不変条件として保証・nautilus skill）。

---

## 3.5 既存カバレッジ棚卸し（behavior-to-e2e・新規は差分のみ）

新 gate は以下を**拡張**（重複新規しない）:

| 既存 | 種別 | #112 での役割 |
|---|---|---|
| `test_v19_auto_live_afk.py` | pytest（live・MOCK） | **parity oracle の雛形**。S6 の cell-live parity gate はこれを鏡映（同じ `KernelLiveEngineController`＋`MockVenueAdapter`＋余力 provider 構成で、cell を bridge 経由駆動し `v19_morning.py` と発注/約定列を突合） |
| `test_backtester_phase3/4/5.py` | pytest（replay parity） | S1 の façade 化が Replay を byte-identical に保つことを pin（不変で GREEN） |
| `test_live_auto_lifecycle_inproc_server.py` | pytest（live RPC） | S3 marimo-guard / register→start→attach の bridge 版を拡張 |
| `test_notebook_replay_afk.py` | Python e2e（実 backend） | S5 mode-aware `run_cell` の Replay 側不変＋Auto 分岐の DATA 半分を直駆動で固定 |
| `StrategyEditorNotebookE2ERunner`（STRATEGY-01..46） | C# AFK（Python-FREE） | S5 の per-cell RUN mode 分岐・▶→■ stop を新 STRATEGY-NN で追加（fake executor・control logic のみ） |
| `FooterModeE2ERunner`（FOOTER-01..13） | C# AFK | footer ▶ 退役を反映（mode セグメント存続を pin） |
| `V19ReplayLiveE2ERunner`（V19REPLAY-01..03・実 mount） | C# AFK + HITL | S7「Replay→Auto 切替→同 cell live 駆動」HITL レグの土台（**実 mount runner は Bash サンドボックス無効で起動**＝`/Volumes` masking 罠・memory `bash-sandbox-masks-volumes-nas`） |

新規ファイル: `test_live_cell_bridge.py`（S2 rendezvous/R1/R2/番兵）・`test_marimo_live_guard.py`（S3）・`test_live_granularity.py`（S4）・`test_v19_cell_auto_parity.py`（S6 centerpiece）。

---

## 4. behavior-to-e2e ハンドオフ

#112 は挙動を変える（AC が AFK probe・parity gate・HITL gate を要求する）ので、実装着手前に `behavior-to-e2e` を formal invoke して上記 S1–S7 の gate を RED 先行で固定する。AFK 正本 = MOCK の決定的 parity/rendezvous gate、HITL 正本 = 立花 demo の UI 操作再現（CLAUDE.md E2E ルール準拠）。

> 🤖 `/grill-with-docs`（#112・2026-06-23）セッション記録（Claude Code）。ADR-0025 accepted。下位事実は本 findings に固定し ADR を「方針」として参照（自己保護条項＝ADR は編集しない）。

---

## 5. 実装着地（#112 slice landing・2026-06-23）

ADR-0025 が findings へ委ねた下位機構の確定形。**ADR 番号衝突訂正**: 設計時 `0024` と採番したが
`#108-111` の puzzle-feel-drag ADR-0024 と衝突したため、実装着手時に **ADR-0025** へ採番し直した
（decision 内容は無改変・番号衝突の文書修正のみ。ADR 末尾に注記）。

### S1 — `bt` façade（`backtester.py`）
- `Backtester(bar_source, *, execution_seam=None, on_run_begin=...)`。`BarSource` / `ExecutionSeam`
  を **Protocol**（ABC ではなく structural）で定義し、Replay は `KernelStepper` が両方を満たす
  （`execution_seam` 既定 = `bar_source`）。`self._stepper` → `self._bars` / `self._exec` へ改名。
  `test_backtester_phase3/4` の `bt._stepper` reach-in は `bt._bars` へ追従（byte-identical 不変）。
- gate: phase3/4/5・offline purity・v19_marimo_parity・#24 golden 全 GREEN（Replay byte-identical）。

### S2 — `LiveCellBridge` / `LiveCellBackend`（`engine/kernel/live/cell_bridge.py`・新規）
- **bridge** = driver が駆動する Strategy-duck（live loop 側・`drive_bar` async / `register` で
  `driver.ctx` 捕捉 / `on_start` で worker spawn / `on_stop` で番兵 put / `join_worker`）。
  **backend** = `bt` の (BarSource, ExecutionSeam)（worker 側・queue.get / submit buffer / portfolio marshal）。
- **rendezvous 確定形**: bar 配送 = `queue.Queue`（live→worker）。完了通知 = worker が `close_current_bar`
  で `call_soon_threadsafe(_complete_bar, bundle)` → live loop で **ctx 経由 enqueue（R1・`_intents` は
  live-loop thread のみ）** ＋ `drive_bar` の loop-bound future を resolve。lock-step ＝ 1 bar in-flight
  なので `_completion` は曖昧にならない。
- **R2**: `bt.portfolio()` は `run_coroutine_threadsafe(ctx.portfolio_snapshot(iid), live_loop).result()`。
  live loop は `drive_bar` await 中も responsive ＝ μs で返る（deadlock 無し）。
- **on_start precedes data**: backend の初回 `open_next_bar` 内（first get）で `_ready` set →
  `on_start` がそれを待ってから consumer 起動。cell が replay 前に raise したら `on_start` で fail-loud。
- **driver フック**（`driver.py:_consume`・外科的）: `drive_bar = getattr(strategy, "drive_bar", None)`;
  bridge なら `await drive_bar(bar)` / 命令型は `on_bar(bar)`（byte-identical・92 imperative tests GREEN）。
- **D5-R teardown**: `controller.detach` が `driver.stop().result()` 後に **caller スレッドで** `join_worker`
  （`driver._strategy` に `join_worker` があれば。命令型は no-op）。番兵で worker の `bt.replay()` だけ
  StopIteration。
- gate: `test_live_cell_bridge.py`（rendezvous 順序・R1 batch・R2 marshal・番兵 idle/teardown・fail-loud）。

### S3 — marimo guard ＋ cell 実行機構（`engine/strategy_runtime/live_cell_runtime.py`・新規）
- `_kernel_loader` = `build_live_marimo_loader()`（`live_orchestrator.py:155`）。`load_app(path)` が
  `None` **または** `NonMarimoPythonScriptError`（marimo 実装はこちらを raise）→ `StrategyLoadError(
  error_code=NOT_A_MARIMO_NOTEBOOK)`。`SyntaxError` は素通し（D4）。
- `StrategyLoadError` に `error_code` フィールド追加。registry / host が `error_code` を素通し
  （無印は `STRATEGY_LOAD_FAILED`）。
- loader は `(app, scenario, bridge_factory)` を返す（`module` 位置に app）。`bridge_factory(
  instrument_id=, **params)` → `LiveCellBridge`。`__name__ = "LiveCellBridgeFactory"`（registry の display_name）。
- **cell 実行 = 反応型ではなく per-cell RUN 機構の再利用**（D1 命令型ループ）: cell_runner は worker
  thread で `IncrementalNotebookSession()` を建て、`app._cell_manager.codes()` → cells、`bt.replay()` cell を
  pressed として `run_pressed(cells, pressed, inject={"bt": bt, "__file__": original_path})`。`_strategy`
  cell の `for bar in bt.replay()` がそのまま live ループを回す。**`finally: session.close()`** で
  worker thread 上で `teardown_context()`（findings 0080 の per-thread RuntimeContext leak を防ぐ＝
  漏らすと後続 marimo テストが間欠失敗）。
- gate: `test_marimo_live_guard.py`（loader / registry / host）＋ `test_live_auto_lifecycle_inproc_server.py`
  を cell fixture へ移行（bridge 版・register→start→idle→stop の full path）。

### S4 — granularity 配線
- `engine.kernel.duckdb_bars.granularity_to_interval_ns(granularity)`（単一 source of truth・
  `_GRANULARITY_INTERVAL_NS`: Daily=1日 / Minute=60s・nautilus-free）。
- `LiveRunner.set_interval_ns(interval_ns)`（購読済み instrument の aggregator を rebuild・idempotent）。
- `controller._do_attach` が subscribe 前に `runner.set_interval_ns(granularity_to_interval_ns(
  scenario["granularity"]))`。`live_orchestrator.py:233` の magic 60s → `granularity_to_interval_ns("Minute")`
  （session 既定・run が attach で上書き）。**採用 = per-run aggregator 供給**（`KlineUpdate` への
  interval タグ追加案は不採用＝oracle が KlineUpdate を直接注入する経路で interval filter が不要なため
  単純な方を採った）。Minute は 60s 不変（旧 accidental 値を明示化）、Daily は 1日（silently-1-minute 回避）。
- gate: `test_live_granularity.py`（helper・set_interval_ns rebuild・attach が Daily/Minute を駆動）。

### S6 — parity oracle 緑化（centerpiece）
- `test_cell_auto_bridge_roundtrip.py`: 決定論 spike cell（`kernel_spike_buy_sell_cell.py`）を
  controller 経由 MOCK で駆動し BUY@bar3/SELL@bar40（命令型 twin と同一 plan）を実 bar 注入で確認
  ＝**bridge ⇄ controller の実 `_consume`→`drive_bar`→worker→submit→`_drain` 結線を初めて end-to-end**。
- `test_v19_cell_auto_parity.py`: v19 **cell** を Auto で駆動し命令型 `v19_morning.py` の AC#1–4 を再現
  （`V19_ARTIFACTS_DIR` = 3-instrument universe ＋ 実 adv/prev/model、scenario は oracle と同一、余力
  provider ¥15k で 1 pick gate）。GREEN ＝「1 cell が Replay/Auto 両駆動」の binding 定義。
- 命令型 orchestrator テストの cell 移行（D4 帰結）: `spike/kernel_live/run_mock_live.py`（purity full-chain・
  `_TWIN_PATH`/`_REST_PATH` を cell twin へ・`nautilus_leaked=0` 維持）、`test_kernel_live_post_trade_live.py`
  （post-trade rails は AccountSync 駆動で idle cell でも発火／on_start 発注テストは「first-bar 発注」へ
  読み替え＋bar 注入）。新 fixture: `kernel_spike_buy_sell_cell.{py,json}` / `kernel_buy_once_cell.{py,json}`。
- **落とし穴（記録）**: cell を実行する orchestrator テストは **run を stop して worker を join しないと**
  marimo session（per-thread RuntimeContext）が孤児化し pytest の FdCapture を壊す（`OSError: [Errno 9]
  Bad file descriptor`）。idle cell テストは rail/fail_run が detach するので OK、明示 stop が無い経路は
  `stop_live_strategy` を finally に足す。

### S5 — mode-aware per-cell RUN（C#）＋ footer ▶ 退役
- **Python 側は S3 で着地済み**: editor live 経路は `register_live_strategy`/`start_live_strategy`
  （orchestrator）が marimo cell を bridge として駆動する（`_backend_impl.run_cell` の Replay 経路は
  無改変＝Auto は run_cell を通らず register→start 経路。AC#2 同一ジェスチャは C# が press を mode で
  振り分けることで実現）。
- **C# 配線**（`NotebookRunController` + `BackcastWorkspaceRoot`）: per-cell RUN を mode-aware 化。
  `RunCell` が `liveLaunchActive()`（LiveAuto ∧ venue 接続）なら `LaunchLive`（▶→■ optimistic →
  `onLiveLaunch` → root が `LiveAutoTransportViewModel.BuildStartRequest` で gate → `host.
  RegisterAndStartLiveAuto`）へ分岐。■ → `StopRunning` が live なら `host.StopLiveStrategy`、backtest
  なら `ForceStop`。`SyncLiveRunButton`（per-frame）が live 寿命（`HasActiveRun ∨ IsStartInFlight`）で
  ■→▶ を reconcile。RPC 結果は worker→main を volatile で marshal（`_liveStartResultPending` →
  `NotifyStartResult`）。
- **footer ▶ は本 root で既に launch 役を持たない**（`LiveAutoTransportViewModel` は run-state 追跡
  ＋ mode-switch teardown 専用・PlayPauseDecision は `FooterModeE2ERunner` のみが叩く）。#112 で per-cell
  RUN が唯一の launcher となり、footer の mode segment（Replay/Manual/Auto）は存続。
- gate: Unity **compile-only**（`error CS` 0・warning CS 0）＋ `StrategyEditorNotebookE2ERunner`
  Section23（**STRATEGY-47..50** Python-FREE control logic・AFK exit 0 GREEN）。台本 .md / E2E-INDEX 更新済。
  REAL register→start の縦串は S7 HITL が担う。

### 5.2 S7 HITL で炙り出した実バグ（teardown deadlock・修正済）
立花 demo 実機 HITL（2026-06-23・owner）で **per-cell RUN(Auto) → live run RUNNING → 実 venue 口座 seed**
まで実機確認 GREEN。だが **■ stop で ▶ に戻らずフリーズ**（UI メインスレッドは生存・Python teardown が停止）。
- **診断**: Editor.log に「live RPC still in flight after drain budget」＝`stop_live_strategy` がハング。
  faulthandler thread dump（`spike` で再現）で確定: worker は `cell_bridge._await_next_bar` の
  `queue.get()` で**番兵待ち**、stop thread は `controller.detach → join_worker → worker.join()` で
  **worker 待ち**。原因 = `driver.stop()` の **`await task`（無タイムアウト）** が、**uncancellable な venue
  submit に詰まった consumer**（v19 が退化 pick を出し、立花の第二暗証番号/HTTP 待ちで cancel 無視）で
  返らず → `on_stop`（番兵 put）に到達せず → worker 永久 leak。
- **切り分け**: MOCK + 単純 R2 cell も MOCK + v19 cell も stop は 0.5s で正常。**実 venue の
  uncancellable submit** が差分。
- **修正**: `driver.stop` の `await task` を **`asyncio.wait({task}, timeout=2.0)`** に。`await task` /
  `asyncio.wait_for` は cancel を握り潰す task を**再 await して同様にハング**するので不可（`wait_for` は
  timeout 時に再 cancel+await する）。`asyncio.wait` は (done, pending) を返すだけなので `on_stop`（番兵）が
  **必ず走る** → worker が抜ける → ■→▶。詰まった consumer task は best-effort で放棄（run は速やかに teardown）。
- **gate（RED→GREEN）**: `test_live_stop_wedged_consumer.py`（cancel 握り潰し submit + 発注 cell で
  consumer を wedge → `stop_live_strategy` が 8s 以内に返ることを watchdog thread で assert）。RED=無限ハング。
- nautilus skill の「live-loop self-deadlock / offloaded worker が blocking round-trip を跨いで lock 保持」
  警告の実例。S7 HITL がまさにこの種（MOCK では出ない継ぎ目）を炙り出した。

### S7 — HITL（立花 demo・owner 実機）
- 残務（owner のみ実行可・CLAUDE.md E2E ルール: UI 操作・デプロイ済み環境・画面値と保存状態の両方）:
  Replay で v19 cell を per-cell RUN → 立花 demo ログイン → footer mode を **Auto** へ → **同じ cell の
  同じ RUN を押す** → live bar 駆動で発注（場中なら約定）→ ■ で stop=teardown。実 mount runner
  （`V19ReplayLiveE2ERunner` 系）は Bash サンドボックス無効で起動（`/Volumes` masking・memory
  `bash-sandbox-masks-volumes-nas`）。MOCK 縦串（S6 の `test_v19_cell_auto_parity` / 
  `test_cell_auto_bridge_roundtrip` / `run_mock_live` cell 版）が決定論的に同経路を先行検証済み。

### 5.1 code-review(simplify) で潰した bug（high effort・8 angle）
- **cell worker 死亡 deadlock（HIGH・修正）**: cell が bar の途中で raise すると worker が `_completion`
  future を resolve せず終了 → `drive_bar` の `await fut` が永久 block ＋ live loop が wedge、しかも
  error が握り潰された。修正: `_run_worker` finally → `_signal_worker_exit` → live loop の
  `_on_worker_exit` が `_worker_exited` を立て、in-flight future を **worker_error で set_exception**
  （`drive_bar` が re-raise → driver `_consume` が `_signal_strategy_error` → run を fail＝命令型 path の
  #25 finding 5 契約を回復）。`on_start` は `_reached_replay` で「replay 到達」と「worker 終了」を区別し、
  replay に到達せず終わった cell を fail-loud。gate: `test_live_cell_bridge` に
  `test_cell_error_mid_loop_raises_not_deadlock` / `test_cell_returns_without_replay_fails_on_start` 追加。
- **fail-loud message が空（修正）**: `live_cell_runtime` の error 文言が ran-row の `output` キーを読んで
  いたが `IncrementalNotebookSession` の row は `{cell_id, mimetype, data, console, ok}`（`output` 無し）
  ＝常に None。`console`/`data`/`result.error` を読むよう修正。
- **`MarimoFileError` 未マップ（修正）**: malformed notebook が `STRATEGY_LOAD_FAILED` に落ちていた。
  loader が `NonMarimoPythonScriptError` ＋ `MarimoFileError` を両方 catch → `NOT_A_MARIMO_NOTEBOOK`
  （`SyntaxError` は引き続き素通し）。
- **granularity 共有 runner 汚染（MED・修正）**: `set_interval_ns` が **共有** runner の全 aggregator
  （手動 watchlist 含む）を rebuild し、Daily run が手動シンボルの UI cadence を Daily に変えていた
  （detach で戻らず）。修正: `set_interval_ns(interval, instrument_ids)` で **run の universe に限定**、
  session 既定 `_intervals_ns`（手動 subscribe 用）は不変。controller は subscribe **後** に scoped 適用。
  gate: `test_set_interval_ns_scoped_leaves_other_symbols_untouched`。
- **dual-run guard（C#・修正）**: backtest 走行中に mode を Auto へ切替えて live launch すると 2 run
  同時 active → `StopRunning` が曖昧。`LaunchLive` が `_btRunActive` で reject。
- **REFUTED**: `granularity_to_interval_ns` が duckdb を live path に引く懸念 → driver が既に
  `from engine.kernel.duckdb_bars import Bar` で読込済（新規ロード無し）。
- **既知の軽微 edge（S7 HITL で実機確認・未修正）**: (1) live start-in-flight の ~100ms 窓で ■ を押すと
  その stop は no-op（■ は残るので active 化後に再押下で stop 可）。(2) live run 中に起動元 cell を削除
  すると `_liveRunRegion` が dangling（mode 離脱の teardown で run は止められる）。両者とも deferred-stop
  state machine が要り AFK 反復前提なので S7 で詰める。
