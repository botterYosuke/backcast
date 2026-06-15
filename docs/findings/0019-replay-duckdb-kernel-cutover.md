# findings 0019 — production Replay を DuckDB→kernel へ張替え（#29 panel/HITL 再緑化）（#49）

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。本書は #49 の下位確定事実を
記録し、ADR-0006 を「方針: ADR-0006」として参照する（ADR-0006 は自己保護のため編集しない）。前提スライス:
findings 0008（kernel tracer）/ 0015（Scenario Startup panel）/ 0017（#47 日足 reader）/ 0018（#48 分足・universe）。

## 0. ゴール

#29 の production Replay 起動経路（`load_replay_data` → `start_engine`）を、nautilus catalog +
`BacktestEngine`（`catalog_data_loader` → `replay_runner`）から **kernel + DuckDB 直読み**（#47/#48）へ
張り替える。出力 seam（`apply_replay_event`/`GetState` ポーリング + `RunBuffer`→`get_portfolio`）は
**無改修**で、C# decoder はそのまま。runtime で `nautilus_trader*` を一切 import しない。

## 1. アーキ決定（grill 2026-06-15）

### D1. 1 ループ・observer 差し替え（KernelRunner 拡張、別建てしない）

per-bar 実行列（`on_bar`→submit→bar close で MARKET fill→portfolio→risk）は golden #24 が凍結済み。
production 用に複製すると golden は緑のまま production だけ drift する典型ハザード。よって **`KernelRunner`
の単一ループを opt-in の注入 seam で拡張**し、golden と production が同じ発火点を共有する（anti-divergence）。

- 注入 seam（既定オフ＝golden は byte-identical）:
  - `sink`（observer）: 既定は `EventSink(push_target)`（#24 RustBacktestSink JSON）。production は
    `ReplayKernelObserver` を渡す。
  - `on_equity(ts_ms, cash)`: **新規**。`equity_curve.append` と同じ行で getattr ガード呼び出し。
    `EventSink` は未実装（no-op）→ golden の 4 メソッド出力も summary も不変。
  - `run_event`: 各 bar 前に `run_event.wait()`（pause/resume/stop）。legacy `replay_runner` と同じ
    単一 Event セマンティクス（stop-flag は持たない・`force_stop` は set して走り切らせる）。
  - `bar_interval_sec`: bar 末で wallclock sleep（GIL を解放しポーリングが bar-by-bar を読む）。production=0.01。
- kernel は host 非依存を維持: `ReplayKernelObserver`/`RunBuffer`/`apply_replay_event`/`_backend_impl` を
  **import しない**。production observer は adapter 層（`engine/strategy_runtime/replay_kernel_observer.py`）。
- 回帰ゲート: `test_kernel_subprocess_matches_committed_golden`（nautilus-free）が byte-identical を担保。

### D2. ReplayKernelObserver のフィールド対応（adapter 層）

| kernel 発火          | production 写像                                                                 |
|----------------------|--------------------------------------------------------------------------------|
| `push_bar(Bar)`      | `engine.apply_replay_event(KlineUpdate)` ＝ kernel `Bar`(plain float)から直接構築 |
| `push_order(OrderFilled)` | `rb.write_fill({instrument_id, side: side.value, qty: str, price: str, ts_event_ms})` |
| `on_equity(ts_ms,cash)` | `rb.write_equity({ts_event_ms, equity: cash})`（per-bar・post-fill）          |
| `push_portfolio` / `push_run_complete` | no-op（`last_portfolio` は run 後に `compute_portfolio(fills, equity)` で導出） |

- `bar_to_kline_update`（`nautilus_adapter`）は使わない: nautilus `Price.as_double()` を要求し Rust core を
  引く。observer は `Bar` の plain float から `KlineUpdate` を直接組む。
- equity = `portfolio.cash`（CASH 口座の `balance_total(JPY)` 相当）で legacy nautilus 経路の per-bar 記録と一致。
- commission/tags は kernel tracer に無いので fill record から省略（`run_buffer_reader.Fill` は不要）。

### D3. 経路選択 — DuckDB-only production・nautilus fallback 無し（legacy code は #50 まで温存）

- root 解決: `DataEngine(duckdb_root=)` ctor 引数 > `.env` `BACKCAST_JQUANTS_DUCKDB_ROOT`
  （`engine.paths` が import 時に `.env` を `os.environ` へ。in-proc も解決）。
- **未解決は hard error**（`paths.jquants_duckdb_root()` は未設定で `None`）。**nautilus catalog への silent
  fallback は持たない**（理由: runtime nautilus-free が env 依存で非決定的になる／#50 の import-purity gate が
  成立不能になる／precision crash 経路の復活）。
- **DuckDB は明示 catalog 引数より優先**（HITL の vestigial `nautilus_catalog_path` は無視）。
- legacy catalog/`engine_run` code は production から un-wire（`replay_duckdb_root` が立つと kernel 経路）。
  ただし #47/#48 の等価テスト用に **code は #50 まで温存**（`_backend_impl` の catalog import は全て lazy
  nautilus なので import-purity は破れない）。

### D4. prime 廃止・stream-all（exactly-once）— ⚠️ stale-drop ではない

DuckDB 経路の `load_replay_data` は **bar0 を prime しない**。代わりに reducer を replay clock=0 の新 state へ
リセットする（static-mode init が clock=`now` にしてあるため、リセットしないと 2024 の historical bar が
**全て stale-drop**＝チャート空になる）。`_replay_primary_id`/`replay_duckdb_root`/`state=LOADED` は設定。

- **正しい不変条件 = exactly-once**: reducer の staleness guard は strict `<`（`reducer.py:69`）なので
  equal-ts は drop **されない**。prime+skip と no-prime+stream-all が同一ローソクになるのは「stale-drop が
  dedup するから」ではなく、**どちらも各 bar をちょうど 1 回流すから**。将来 prime を skip 無しで復活させると
  reducer は守らず即二重描画になる。
- 効果: `_primary_first_seen`/primary-skip ロジックが observer から消滅（スキルが警告する「primary-skip の
  bar 重複/欠落」を回避ではなく消滅）。Finding #4 の per-id 非対称・dual-load chart-mismatch も prime 由来なので消える。
- 歯止め: `test_replay_duckdb_kernel_afk` が `ohlc_points 数 == 流した bar 数`（global と per-id 両方）を assert。

### D5. strategy 調達 — 既定を kernel-native へ差替え（ADR-0006 strategy 移行の第一歩）

- `start_engine`(DuckDB) は `strategy_loader.load(file, base_cls=engine.kernel.strategy.Strategy)` で読み
  （`base_cls=None` の nautilus 既定に到達しない＝D4 import-purity）、`strategy_cls(instrument_id=primary)`
  （#25 construction contract）で instance 化して `KernelRunner` へ渡す。
- HITL 既定 `BACKCAST_HITL_STRATEGY` を nautilus `spike_buy_sell.py` → kernel-native
  `kernel_spike_buy_sell.py`（8918 日足・BUY bar3/SELL bar40・golden twin）へ差替え（C# harness 既定 +
  `.env.example`）。ユーザー向け canonical demo の新設は後続スライス。

## 2. 触ったファイル

- `python/engine/kernel/runner.py` — KernelRunner に `sink`/`run_event`/`bar_interval_sec`/`on_equity` 注入。
- `python/engine/strategy_runtime/replay_kernel_observer.py` — 新規 production observer（adapter 層）。
- `python/engine/core.py` — ctor `duckdb_root`・`load_replay_data` の DuckDB 分岐（`_load_replay_duckdb_locked`）・
  `replay_duckdb_root` property・stop/force-stop で root クリア。
- `python/engine/_backend_impl.py` — `start_engine` の DuckDB 分岐（`_start_engine_duckdb`）。
- `Assets/Scripts/ScenarioStartup/ScenarioStartupHitlHarness.cs` / `.env.example` — HITL strategy 既定。

## 3. テスト（PASS ログ）

```
.venv/bin/python -m pytest tests/test_replay_kernel_observer.py \
  tests/test_kernel_runner_production_seam.py tests/test_load_replay_data_duckdb.py \
  tests/test_replay_duckdb_kernel_afk.py -q
# → 15 passed
.venv/bin/python -m pytest tests/test_kernel_golden_cpython.py::test_kernel_subprocess_matches_committed_golden -q
# → 1 passed（golden byte-identical = KernelRunner 拡張が壊していない）
```

- `test_replay_duckdb_kernel_afk`: 合成 DuckDB（tmp の `stocks_daily/8918.duckdb`・50 日足）で
  `load_replay_data`→`start_engine`→kernel run を実走し、(a) success・fills 2 件・`last_portfolio`、
  (b) exactly-once（`ohlc_points`==50）、(c) **clean interpreter subprocess で nautilus 非ロード**（AC④）を assert。
  実データ faithfulness は #47/#48（owner DuckDB・skip-if-absent）の担当なので合成データで wiring を固める。

### 既知の pre-existing 失敗（#49 と無関係・本機の nautilus 環境）

`test_kernel_bars.py::test_kernel_reader_matches_nautilus_catalog_loader` と
`test_kernel_golden_cpython.py::test_oracle_subprocess_matches_committed_golden` は
`CatalogPrecisionMismatchError`（本機 nautilus=high-precision・catalog=standard）で失敗。これは ADR-0006 が
退役対象とする precision 地雷そのもの（stash 検証で #49 変更前から失敗）。`test_login_subprocess_env` の Windows
`X:` パス split 失敗も pre-existing。いずれも #49 のスコープ外。

## 3b. code-review(simplify) で直した Medium 級指摘

- **DuckDB precedence を provider-reuse guard より前へ**（`core.py`）: `load_replay_data` の
  `if self._replay_provider is not None: return LOADED` の前に root 解決を移動。catalog run を
  `stop_replay`（`_replay_provider` を残す）で終えた後の DuckDB load が stale provider に shadow され
  legacy 経路へ misroute するのを防ぐ。
- **granularity 検証を Daily/Minute に限定**（`core.py`）: load-arg granularity（reducer 既定 `"Trade"` が
  来うる・run は scenario の granularity を使う）で `db_path` を hard-fail させず、未知粒度は LOAD 検証を
  skip して run に委ねる。
- **`_finalize_run` 抽出**（`_backend_impl.py`）: rb.finish→summary→`last_portfolio` 導出の tail を legacy/
  DuckDB 両 runner で共有（drift 防止・#50 の legacy 削除後も単一の正本）。
- **`_REPLAY_BAR_INTERVAL_SEC` 定数化**: 0.01 を両 production 経路で共有（cadence をロック）。
- 不採用（parity/scope 外）: Stop の mid-run abort 不可（legacy `replay_runner` も同じ＝#49 の regression
  ではない）／replay-speed 未配線（legacy も 0.01 直書き）。

## 3c. HITL 実走で顕在化したバグと修正（warming-up placeholder）

owner HITL（AC⑤・2026-06-15）で **チャートは 68 本 bar-by-bar 前進・`CatalogPrecisionMismatchError`
無し＝合格**だったが、Console に `poll error (non-fatal): 2 validation errors for TradingState`
（`price`/`timestamp` が `Input should be greater than 0`）が出た。

- 原因: D4 で reducer を `ReducerState(timestamp_ms=0, price=0.0)` にリセットしたが、`TradingState`
  は `price>0` & `timestamp>0` を要求（`models.py`）。LOADED→first-bar の間に poll thread が
  `get_state_json`→`get_current_state` を叩くと `price=0/timestamp=0` で検証失敗。legacy catalog 経路は
  bar0 prime で `price=close>0` になるため踏まなかった＝**no-prime 化で生じた regression**。C# 側は
  non-fatal catch でチャートは描けていたがログ汚染＋poll 失敗。
- 修正: LOADED の reducer を **warming-up placeholder**（`timestamp_ms=1`・`price=_REPLAY_WARMUP_PRICE=1.0`）で
  seed。`timestamp_ms=1` は任意の historical bar ts 未満なので stale-drop を起こさず bar0 で上書きされ、
  `ohlc_points` は空のままなので placeholder は**描画されない**（exactly-once も不変）。bar データの prime
  ではなくスカラだけ（primary-skip は依然不要）。
- 歯止め: `test_load_replay_data_duckdb.py::test_loaded_state_is_pollable_before_streaming`＝LOAD 直後
  （pre-stream）の `get_current_state()` が `ValidationError` を投げず `price>0 & timestamp>0 & ohlc 空`
  を assert。`memory: HITL surfaces bugs AFK gates miss` の再確認（idle AFK では踏まない poll seam バグ）。

## 4. 再走手順（owner HITL・AC⑤）

1. `.env` に `BACKCAST_JQUANTS_DUCKDB_ROOT=/Volumes/StockData/jp`（owner 端末）。
2. Unity を Tools>Backcast の Scenario Startup HITL で起動（`ScenarioStartupHitlHarness`・`AutoBootstrap` 無効）。
3. Startup タイルで granularity/cash/start/end/universe を編集 → Run。
4. 期待: DuckDB→kernel で run・チャートにローソクが **bar-by-bar 前進**・`CatalogPrecisionMismatchError` が
   構造的に出ない（nautilus 非ロード）。strategy 既定は `kernel_spike_buy_sell.py`。
