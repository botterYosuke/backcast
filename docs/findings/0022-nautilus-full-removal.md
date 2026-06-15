# findings 0022 — nautilus 完全撤去（旧 catalog/backtest 経路削除・strategy kernel-native 化・パッケージ削除・import-purity gate）（#50）

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）/ **ADR-0004**（pure-Python
Backcast Execution Kernel）。本書は #50 の下位確定事実を記録し、両 ADR を「方針: ADR-0006/0004」として
参照する（両 ADR は自己保護のため編集しない）。前提スライス: findings 0017（#47 日足 reader）/ 0018
（#48 分足・universe）/ 0019（#49 production Replay の kernel 張替え）。

## 0. ゴール

#49 で production Replay が DuckDB→kernel へ移行した後、**nautilus を runtime から完全に取り除く**最終
スライス。旧 nautilus データ経路・#68 BacktestEngine→GUI bridge・旧 nautilus live 実行 stack・nautilus
strategy fixtures を撤去し、`nautilus-trader==1.226.0` を依存から外す。Replay/Live の全 runtime 経路で
`nautilus_trader*`/`nautilus_pyo3` が一切ロードされないことを import-purity gate で固定する。

## 1. grill で確定したスコープ（2026-06-15・owner）

issue の AC は **データ/backtest 経路のみ**を名指ししていたが、`nautilus-trader` をパッケージ削除すると、
それを（遅延でも）import する全モジュール・テストが import 不能になり AC「非 kernel スイート GREEN」に抵触する。
そのため AC に明記されていない 2 つの絡みを grill で確定した（いずれも owner が「完全撤去（推奨）」を選択）。

### D1. 旧 nautilus **live 実行 stack** を完全削除

本番 Live は既に `engine.kernel.live`（kernel driver・nautilus-free）に移行済みで、`engine/live/nautilus_*`
系は `NautilusLiveEngineController` から**遅延 import される dead code**。これらソースと専用テストを丸ごと削除する
（残置すると、パッケージ削除後に import 不能な dead ファイルとテストがツリーに残るため）。

### D2. #68 `start_nautilus_replay` / `NautilusBacktestRunner` を Python+C# 両方退役

#68 の nautilus `BacktestEngine`→GUI bridge は、C# 側で `ReplayChartHarness`(#10) / `ReplayPanelsHarness`(#11)
/ `ReplayRunLifecycle` / Editor probe 各種から駆動されるが、**それらソース自身が「THROWAWAY HITL visual gate・
mainline scene/DI(#5/#7) で削除予定」と明記**している throwaway scaffold。本番 Replay は
`ScenarioStartup → load_replay_data → start_engine → _start_engine_duckdb`（DuckDB→kernel）であり #68 経路は
superseded。Python 側（`start_nautilus_replay`/`NautilusBacktestRunner`/制御メソッド群/spike fixtures）と、それを
駆動する throwaway C# scaffold の双方を退役する。

## 2. 撤去インベントリ

### 削除（ファイルごと）

- データ/catalog: `engine/jquants_to_catalog.py` / `engine/nautilus_catalog_loader.py` /
  `engine/kernel/bars.py`（catalog pyarrow reader・等価突合用）/ `engine/nautilus_adapter.py`
  （nautilus `Bar`→`KlineUpdate`・legacy 分岐専用）。
- backtest/replay: `engine/nautilus_backtest_runner.py`（#68）/ `engine/strategy_runtime/replay_runner.py`
  （nautilus `BacktestEngine`）/ `engine/strategy_runtime/engine_runner.py`（legacy `engine_run`）/
  `engine/strategy_runtime/catalog_data_loader.py`（catalog ローダ・`normalize_granularity` 正本は
  `engine/kernel/duckdb_bars.py` 側へ既に移行済み）。
- 旧 live 実行 stack（D1）: `engine/live/nautilus_exec_client.py` / `nautilus_data_client.py` /
  `nautilus_risk_config.py` / `bar_supply.py` / `gui_bridge_actor.py`（#68 GUI bridge actor）/
  `engine/strategy_runtime/instrument_factory.py`（nautilus instrument）。
- spike/CLI: `spike/fixtures/strategies/spike_buy_sell.py` / `spike_bar_consumer.py`（kernel twin
  `kernel_spike_buy_sell.py` 等が既存）/ `engine/strategy_replay/cli.py`（dead CLI）/
  `spike/kernel_golden/run_oracle.py`（nautilus oracle capture）/ `spike/s0_backtest.py`。
- テスト: `tests/test_kernel_bars.py` / `tests/test_replay_bar_streaming.py` /
  `tests/test_nautilus_exec_client_cancel_ack.py` / `tests/test_gui_bridge_positions.py`。
- C# throwaway scaffold（D2）: `Assets/Scripts/ReplayChart/`（ReplayChartHarness / ReplayPanelsHarness /
  ReplayRunLifecycle 等 #68 駆動分）/ `Assets/Editor/ReplayChartDecodeProbe.cs` /
  `ReplayPanelsDecodeProbe.cs` / `S1AdapterSmokeProbe.cs`。

### 改修（nautilus を剥がして残す）

- `engine/_backend_impl.py`: module-level の `nautilus_catalog_loader`/`jquants_to_catalog`/
  `catalog_data_loader` import を撤去。`start_engine` のレガシー catalog 分岐（duckdb_root が無い経路）を削除し、
  DuckDB 経路（`_start_engine_duckdb`）のみ残す。#68 メソッド群（`start_nautilus_replay`/`pause_backtest`/
  `resume_backtest`/`step_backtest`/`set_replay_speed`）と `_backtest_*` state を削除。`nautilus_adapter` 依存を外す。
- `engine/live/engine_controller.py`: `NautilusLiveEngineController` クラス（遅延 nautilus import・削除モジュール
  参照）を除去。kernel 経路の controller は維持。
- `engine/strategy_runtime/strategy_loader.py`: `base_cls is None` 時の nautilus `Strategy` fallback を除去
  （本番は常に `base_cls=engine.kernel.strategy.Strategy` を渡すため fallback は dead）。
- `engine/kernel/sink.py` ほか: 削除モジュールへの **docstring/コメント参照**を更新（コード結合は無い）。

### 維持（既に nautilus-free・要 import）

- `engine/kernel/*`（runner/duckdb_bars/broker/sink/strategy/risk/orders/portfolio/instrument_id/live/*）。
- `engine/strategy_runtime/replay_kernel_observer.py`（production observer・nautilus 参照は docstring のみ）。
- `engine/live/{safety_rails,strategy_log,...}`（nautilus 参照は docstring/型ヒント注のみ）。
- import-purity 検出基盤（`spike/kernel_golden/purity.py` の `leaked_nautilus_modules` 等）。

## 3. `Bar` dataclass の consolidation

`Bar`（`instrument_id, ts_event_ns, open, high, low, close, volume`）は `engine/kernel/bars.py` と
`engine/kernel/duckdb_bars.py` に**構造同一で二重定義**されていた。本番 runner（`engine/kernel/runner.py`）は既に
`duckdb_bars.Bar` を使う一方、`broker/sink/strategy/live/driver.py` は `bars.Bar` を import していた（duck-type で
動作）。`bars.py` 削除に伴い `duckdb_bars.Bar` を唯一の正本とし、importer 4 本＋`tests/test_replay_review_fixes.py`
を `from engine.kernel.duckdb_bars import Bar` へ付け替える。

## 4. golden / 等価突合の真値化

- ADR-0006 の通り **nautilus oracle 経路（`run_oracle.py`）を退役**。#24 取得済み golden は**凍結 fixture（回帰）**
  として残し、`tests/test_kernel_subprocess_matches_committed_golden`（nautilus-free）が byte-identical を担保。
- findings 0017 §5 の **skip-if-absent 等価突合**（`tests/test_kernel_duckdb_bars.py` の DuckDB vs catalog reader
  比較）は、catalog reader 削除後に**凍結期待値の純粋回帰**へ転換する（catalog 有無ガードを撤去）。
- `tests/test_kernel_live_step1.py` の「nautilus `Strategy` base で kernel twin を読むと 0 subclass」比較テストは、
  nautilus 不在で恒久的に意味を失うため削除（kernel twin が nautilus-free でロードされる本質テストは維持）。

## 5. import-purity gate（AC#4）

`tests/test_gate_import_purity.py` を、Safety Rails gates + `engine.kernel.runner` に加え、**production DuckDB
Replay 起動点の import 連鎖**（`engine.kernel.duckdb_bars` / `engine.strategy_runtime.replay_kernel_observer` /
`engine.kernel.runner` ＝ `_start_engine_duckdb` が組む依存）を fresh subprocess で import し、`sys.modules` に
`nautilus_trader*`/`nautilus_pyo3` が載らないことを assert するよう拡張する。Live 経路は既存
`tests/test_kernel_live_purity.py`（full LiveAuto roundtrip + register-path）が担保し維持。

## 6. 依存

`python/pyproject.toml` の `nautilus-trader==1.226.0` を削除し `uv lock` を更新（nautilus 抜きで解決が通る）。
`pyarrow` は kernel/bars.py 退役後も `_backend_impl` の catalog scan（instrument listing・**pure-pyarrow・
nautilus 非依存**）で使うため依存維持（pyproject のコメントを実態に更新）。

## 7. 残存影響（#50 scope 外・要追跡）

- `_backend_impl` の `list_all_listed_symbols` / `_list_instruments_local` / `_resolve_date_bounds_from_catalog`
  / `_scan_catalog_instruments` は parquet catalog を pyarrow で直接走査する（nautilus 非依存）。#49 後の production
  は DuckDB ルートを使うため `catalog_path` 経路は実質 dead だが、UI の instrument 列挙に影響するため #50 では
  撤去せず残置。DuckDB ベース listing への統一は将来スライス。
- C# `ScenarioStartup` 本番経路（`start_engine`）は #68 scaffold 削除の影響を受けない（無傷を確認）。
- C# 共有 decoder（`ReplayBarDecoder` / `ReplayPanelDecoder`）と `ReplayEventSink` は production kernel-sink
  decode 経路と共有のため温存。削除した throwaway 6 ファイルの非コメント相互参照はクラスタ内に閉じており
  本番 C# への compile 依存は無い（コメント/文字列リテラルのみ）ことを確認済み。
- dev spike `spike/s1_adapter_smoke.py`（#68 `start_nautilus_replay` の CPython smoke）と、それを参照する
  `spike/viz_source.py` / `spike/s2spike_live_loop.py` は **dev scaffold**（テスト/本番非依存・pytest 対象外）の
  ため #50 では温存。退役した seam を呼ぶので実行は失敗するが runtime/AC には影響しない。将来 spike 整理で撤去。
- 既存 `tests/test_login_subprocess_env.py::test_login_env_propagates_site_packages` は #50 と無関係の
  **pre-existing macOS 失敗**（Windows 風 "X:" パスを `os.pathsep=":"` が分割する）。HEAD でも失敗を確認済み。

## 8. 検証結果（2026-06-15）

- `uv lock` が nautilus 抜きで解決（nautilus-trader v1.226.0 + 推移依存 click/fsspec/msgspec/portion/
  sortedcontainers/tqdm/uvloop を除去）。`uv.lock` に nautilus 参照ゼロ。
- nautilus を **物理アンインストール**した venv で pytest 全スイート **256 passed**（唯一の失敗は上記
  pre-existing login テスト）。lazy import すら残っていないことを証明。
- import-purity gate（Replay+Live 全 runtime 経路）/ live purity / 凍結 golden 回帰（`run_kernel --assert-pure`
  が byte-identical contract を出力）すべて GREEN。engine/spike/tests に実 `import nautilus` 文ゼロ。
</content>
</invoke>
