# findings 0008 — Backcast Execution Kernel: 最初の tracer bullet（#24）

方針: **ADR-0004 案 C**（pure-Python Backcast Execution Kernel）。本書は当該スライスの下位確定事実を記録し、
ADR-0004 を「方針: ADR-0004」として参照する（ADR-0004 は自己保護のため編集しない）。

## 0. ゴール（再掲）

`spike_buy_sell` 相当の薄い縦スライスを新 kernel で 1 本通し、Nautilus oracle と golden 一致させ、
Windows-Mono で clean teardown する。汎用エンジン完成ではなく、次の 3 仮説の検証:
- Nautilus 無しで golden parity を達成できる。
- Windows-Mono で clean teardown（Rust core 不在）。
- 既存 gates と既存 sink 契約を再利用できる。

## 1. 切り離すべき Nautilus 結合（grill で確定）

kernel を Mono プロセスで Rust core 抜きに走らせるため、以下 3 つの結合を断つ:

1. **gates**: `engine/live/safety_rails.py:30` の `from nautilus_trader.live.config import LiveRiskEngineConfig`
   は **import するだけで `nautilus_trader.core.nautilus_pyo3`（Rust core .pyd）をロードする**（実測 True）。
   → `to_live_risk_engine_config()` を新 module `engine/live/nautilus_risk_config.py` の関数
   `build_live_risk_engine_config(limits, instrument_ids) -> LiveRiskEngineConfig` に分離。呼び出しは
   `NautilusLiveEngineController._do_attach()`（`engine_controller.py`）からのみ。これにより
   `safety_rails.py` / `pre_trade_gate.py` / `post_trade_gate.py` は **nautilus import 禁止**＝import-pure に。
   純粋ロジック（`check_pre_trade`/`check_post_trade`/`evaluate_pre_trade`/`evaluate_post_trade`/各純関数）は不変。
2. **Strategy base**: `spike_buy_sell.py` は `nautilus_trader.trading.strategy.Strategy` を継承（Rust core を引く）。
   → kernel 固有の `Strategy` base（`on_start`/`on_bar`/`on_tick`/`on_order`/`on_stop`）を新設し、
   **kernel 版 spike_buy_sell の twin** を author。**nautilus 版 `spike_buy_sell.py` は oracle として温存**。
3. **bar/instrument loading**: `nautilus_catalog_loader.load_bars` / `make_equity_instrument` は Rust core を引く。
   → kernel は **同一の catalog parquet を `pyarrow` で直接読む**（`s0_backtest.py` の footer 読みと同型）。
   同じ parquet を読むため OHLC 値は oracle と一致する。

## 2. parity 契約（既存 Nautilus replay path から確定）

`engine/strategy_runtime/replay_runner.py` の venue/engine 構成より:
- account = `CASH`, OMS = `NETTING`, base = `JPY`, starting balance = `10_000_000`（scenario）。
- instrument = `Equity`（`make_equity_instrument`）: `price_precision=1` / `price_increment=0.1` /
  `lot_size=100` / **maker・taker fee = 0**（Nautilus `Equity` 既定）→ commission 項なし。
- venue は **`FillModel` 無し・adaptive ordering 無し**＝Nautilus 既定。**oracle 実測**（record from oracle）で
  fill 模型を確定: `MARKET` を `on_bar(N)` 中に submit すると **同一 bar N の CLOSE（LAST 価格）で即時・全量約定**、
  `OrderFilled.ts_event = bar N の ts_event`（slippage 0・commission 0）。
  証拠: BUY submit @bar#3 → fill ts=bar#3 ts・price=8.0(=bar#3 close)。SELL submit @bar#40 → fill ts=bar#40 ts・
  price=8.0(=bar#40 **close**。bar#40 open=9.0 なので open ではない)。
  > spike fixture の docstring「fills next bar open」は**誤り**。oracle 実測が正（ts が submit bar と一致＝同一 bar fill）。
- CASH account の equity = 現金残高のみ（建玉評価額は equity に含めない＝`account.balance_total`）。
  BUY: `cash -= qty×px`、SELL: `cash += qty×px`。realized PnL = 終端 flat 時 `final_cash − initial_cash`。
- 各 bar の equity point = その bar の fill 反映後の cash（on_equity は post-fill を読む。max_drawdown=800 がこれを裏付け）。
- sink 1 bar あたりの emission 順 = `push_bar(N) → [fill あれば push_order → push_portfolio]`。
- run_complete summary（`{fills_count, equity_points, max_drawdown, sharpe, sortino}`）は kernel が
  `engine.strategy_runtime.summary.equity_curve_stats`（stdlib only・nautilus-free）を**再利用**して bit-identical に再現。

scenario（`spike/fixtures/strategies/spike_buy_sell.py`・schema v2）:
`instruments=["8918.TSE"]`, `2024-10-01`→`2025-01-10`, `Daily`, `initial_cash=10_000_000`,
BUY 100 @ bar 3, SELL 100 @ bar 40。
catalog: `python/spike/fixtures/jquants-catalog/data/bar/8918.TSE-1-DAY-LAST-EXTERNAL/*.parquet`。

> **bar 数（owner 合意 2026-06-13・issue #24 訂正済み）**: canonical scenario は単一銘柄 8918.TSE・**68 daily bars**
> （catalog の窓もちょうどこの 68 本）。issue 旧文の「204 bars」は **S0 fixture の 68 bars × 3 銘柄が混入した転記ミス**
> で `68 bars` に訂正（issue #24 本文＋訂正コメント）。multi-instrument は本 issue の明示 non-goal＝follow-up。
> bar 数は golden に**実測で記録**する。

## 3. EventSink 契約（既存 sink・C# decoder 無改修で読める＝AC#4）

`engine/live/gui_bridge_actor.py` より、kernel の `EventSink` が emit すべき JSON:
- `push_bar`: `{price, timestamp, timestamp_ms, history[], ohlc_points[], per_instrument{}}`
- `push_order`: `{symbol, client_order_id, venue_order_id, strategy_id, side, status, qty, price, timestamp_ms}`
  — **FILLED のみ push**（GUI bridge は `OrderFilled` だけ転送）。
- `push_portfolio`: `{buying_power, equity, positions:[{symbol,qty,avg_price}], orders[]}`
  — `positions_open()` 相当（closed は脱落＝終端 FLAT）。
- `push_run_complete(run_id, summary)`: `summary = {fills_count, equity_points, max_drawdown, sharpe, sortino}`。

## 4. golden gate の構造（grill で確定）

- **commit frozen golden + dual-gate**、oracle と kernel は**別プロセス**で実行。
- Standalone CPython gate: oracle subprocess → normalized / kernel subprocess → normalized /
  `oracle == committed`（golden 陳腐化検出）/ `kernel == committed`。
- Windows-Mono gate: kernel のみ / `kernel == committed` / `nautilus_trader*` 未ロード（`sys.modules` 全走査・
  可能なら `.pyd` 不在）/ clean teardown・exit 0・heartbeat 正常。
- golden 形式: 正規化 contract（§0 の 6 項目）＋ provenance（nautilus version・`PRECISION_BYTES`・
  strategy/catalog/scenario の sha256）。float は **price precision に従う整数 raw 値 or decimal 文字列**で保存し
  binary float 許容誤差比較を避ける。
- golden は**計算せず oracle 経路から記録**（自己参照回避）。`capture`（明示生成）/`verify`（read-only・差分で失敗）。

## 5. スコープ（tracer-thin・grill で確定）

**作る**: Strategy `on_start`/`on_bar`/`on_order`（`on_tick`/`on_stop` は予約口・`on_stop` は正常完走で 1 回呼ぶ）/
OrderEngine `submit→(pre-trade gate)→ACCEPTED→次 bar open で FILLED` ＋ 同一 order id 重複防止 /
RiskEngine denial 経路（gate violation → `DENIED/REJECTED` + `on_order` + sink。AC#3 の gate 発火担保）/
Portfolio / ReplayBroker（同一 bar close fill・§2）/ EventSink / BUY-SELL 一往復・終端 FLAT。

**follow-up へ送る（非目標）**: partial fill / user・venue cancel / venue reject / tick 駆動 fill / order modify /
複数注文の高度な競合 / native rail（`max_order_value`・`max_orders_per_minute`）の pure-Python 実装
（tracer では 0=無効。**「live-only」ではなく未実装＝Live 接続前に kernel 側で実装が必要**）。

## 6. 実装インベントリ（GREEN 2026-06-13）

kernel（`engine.kernel.*`・すべて nautilus-free）:
`bars.py`（pyarrow reader・raw int64/1e9 decode）/ `orders.py`（OrderEngine・dup-guard・pre-trade rail・
DENIED/ACCEPTED/FILLED）/ `portfolio.py`（CASH cash 会計・netting position・flip 時 avg_px は fill 価格で再起算）/
`risk.py`（**RiskEngine = pre/post 両 rail の集約**。`evaluate_pre_trade`/`evaluate_post_trade` を呼ぶ）/
`broker.py`（ReplayBroker・同一 bar close fill）/ `strategy.py`（Strategy base・5 hooks・register 注入）/
`sink.py`（EventSink・GuiBridgeActor 契約）/ `runner.py`（**EventLoop は `KernelRunner.run` の per-bar streaming
loop として実装**＝単一銘柄・時刻順・決定的。**post-trade rail を毎 bar 評価**し、違反で run 停止＝`stopped_reason`。
issue の「EventLoop」コンポーネントはこれ）。`LiveBroker` は follow-up（Replay のみ）。

gate 分離: `engine/live/nautilus_risk_config.py::build_live_risk_engine_config`（Nautilus 専用）。
`safety_rails.py`/`pre_trade_gate.py`/`post_trade_gate.py` は nautilus import 撤去（import-pure）。
caller: `engine_controller.py::_do_attach`。

oracle twin: `spike/fixtures/strategies/kernel_spike_buy_sell.py`（oracle = `spike_buy_sell.py` 温存）。

golden gate（`spike/kernel_golden/`）:
`scenario.py`（共有定数）/ `normalize.py`（正規化 contract・volatile id 除去）/ `run_oracle.py`（oracle subprocess・
contract+provenance を stdout）/ `run_kernel.py`（kernel subprocess・`--assert-pure` で nautilus 不在検査）/
`capture_golden.py`（**oracle subprocess から golden 生成・明示実行のみ**）/ `verify_golden.py`（read-only・kernel
比較）/ `golden.json`（committed・provenance: nautilus 1.226.0・PRECISION_BYTES=8・strategy/catalog/scenario sha256）。

tests（`python/tests/`・standalone 実行可＝`python tests/<x>.py`、pytest でも可）:
`test_gate_import_purity.py` / `test_kernel_bars.py` / `test_kernel_risk_gate.py` /
`test_kernel_golden_cpython.py`（subprocess 隔離 dual-gate）/ `test_kernel_teardown_mono.py`。

Unity-Mono probe（editor-only throwaway・`-executeMethod <Probe>.Run` で batchmode 実行）:
- AC② teardown: `Assets/Editor/KernelTeardownProbe.cs`（`S0EditorProbe` 同型）。
- AC④ C# decode: `Assets/Editor/KernelSinkDecodeProbe.cs`（`ReplayPanelsDecodeProbe` の kernel 版・C# `ReplayEventSink`
  を kernel push_target に渡し無改修 decoder で VALUE 照合）。Python 口 = `run_kernel.run_into(push_target)`。

実行手順:
```
# golden 再生成（oracle が必要・standalone CPython）— レビュー対象の変更
python -m spike.kernel_golden.capture_golden
# kernel verify（read-only・nautilus 不要）
python -m spike.kernel_golden.verify_golden
```

## 7. Windows-Mono 実走ゲート（AC② — GREEN 実証済み 2026-06-13）

- **AC② = Unity-Mono batchmode の実 teardown：GREEN**。`Assets/Editor/KernelTeardownProbe.cs`
  （`S0EditorProbe` と同型・worker が `Py.GIL()` で kernel 駆動、main は `BeginAllowThreads` で GIL-free heartbeat）を
  `-executeMethod KernelTeardownProbe.Run` で実行。probe は kernel tracer を走らせ → `verify_golden.verify()` で
  committed golden 照合 → `leaked_nautilus_modules(sys.modules)==0` で Rust core 不在を確認 →
  **`PythonEngine.Shutdown()` + process exit**（nautilus 版が SIGSEGV した箇所）を通す。
  - **結果（Windows 11 / Unity 6000.4.11f1 Mono / CPython 3.13.11 / pythonnet）**: `[KERNEL TEARDOWN PASS]`・
    **exit 0**・**新規 `%LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp` 無し**・`Shutdown OK (clean teardown)`・
    heartbeat 生存（GIL stall 無し）・`Exiting batchmode successfully` / leaked weakptr 無し。
  - 過去の nautilus 版は同一 `Shutdown`/process-exit 経路で**必ず** SIGSEGV（s0-result §1.2/§1.3、dump `10148` 他）→
    今回ゼロ。**ADR-0004 案 C の中核仮説（Rust core 排除で多重 CRT/FLS teardown crash が構造的に消える）を実機実証**。
  - 証跡: `C:/tmp/kernel_probe_run.log`（compile health は `C:/tmp/kernel_probe_compile.log`・return code 0）。
  - 補足: kernel は pyarrow（Arrow C++ native .pyd 15+）をロードするが、それらの CRT は teardown を割らなかった
    （実証済み）。headless 自動分 `test_kernel_teardown_mono.py`（subprocess exit 0＋Rust core 不在＋golden）は
    この構造保証の回帰ガードとして残す。

## 7c. AC④ — C# decoder 実読込（GREEN 実証済み 2026-06-13）

- **AC④ = 既存 C# decoder が kernel sink を無改修で読む：GREEN**。`Assets/Editor/KernelSinkDecodeProbe.cs`
  （`ReplayPanelsDecodeProbe` の kernel 版）が kernel の `push_target` に **C# `ReplayEventSink` をそのまま渡し**
  （kernel sink は RustBacktestSink を duck-type）、kernel を走らせて push_bar/push_order/push_portfolio/
  push_run_complete を C# sink のキューへ流す → **無改修の `ReplayBarDecoder.Decode` / `ReplayPanelDecoder.
  DecodeOrder|DecodePortfolio|DecodeRunResult`** で drain・decode し VALUE assert。Python 側口は
  `spike.kernel_golden.run_kernel.run_into(push_target)`。
  - **結果**: `[KERNEL SINK DECODE PASS] bars=68 ordersPushed=2 portfoliosPushed=2 fills=2`・exit 0・compile error 無し。
    JsonUtility は key 不一致で silent zero-fill するため **count ではなく値**（Side∈{BUY,SELL}・Status=FILLED・
    Qty>0・Price>0・open Position(qty>0,avg_price>0)・Equity>0・FillsCount=2・EquityPoints=68・bar Price>0）で gate。
  - ⇒ kernel の EventSink payload は既存 Replay sink 契約と同一で、**C# decoder は無改修で読める**ことを実機実証。
  - 証跡: `C:/tmp/kernel_sink_decode.log`。
  - 注: order payload の `client_order_id`/`venue_order_id`/`strategy_id` は実装固有で oracle と値が異なる（golden は
    volatile id を除いた正規化 contract で gate）。C# decoder はキーで bind するため値差は無関係＝無改修で読める。

## 7b. 残 manual-gate
- なし（AC④ は §7c で GREEN。kernel を本番 C# adapter に結線する slice で live 経路の回帰は別途）。

## 8. AC 対応表

| AC | 状態 | 実現 / 残 |
| --- | --- | --- |
| ① golden 6 項目一致（CPython） | ✅ GREEN | `test_kernel_golden_cpython`（oracle==committed==kernel・subprocess 隔離・正規化 contract） |
| ② Windows-Mono clean teardown | ✅ **GREEN** | **`KernelTeardownProbe.Run` 実走で exit 0＋新規 crash dump 無し＋Shutdown clean＋heartbeat 生存**（§7・2026-06-13）。回帰ガード `test_kernel_teardown_mono` |
| ③ gates 発火 | ✅ GREEN | `test_kernel_risk_gate`（pre allowlist deny ＋ post MTM daily-loss halt）＋ `test_gate_import_purity` |
| ④ sink JSON 同一（C# 無改修） | ✅ **GREEN** | **`KernelSinkDecodeProbe.Run` 実走で無改修 `ReplayBarDecoder`/`ReplayPanelDecoder` が kernel sink を VALUE decode**（bars=68・orders=2 BUY+SELL FILLED・open Position・Equity>0・FillsCount=2）（§7c・2026-06-13） |
| ⑤ golden script+手順を findings 記録／GREEN で ADR-0004 案 C を accepted | ✅ GREEN | 本書 §6＋capture/verify script。**ADR-0004 案 C `accepted` 昇格済み**（2026-06-13・owner） |

> **判定（2026-06-13）**: **AC① ② ③ ④ ⑤ すべて GREEN**（実機実証）。tracer bullet 完了。ADR-0004 案 C は `accepted`。
> kernel を本番 C# adapter に結線する live 経路の回帰は別 slice（#24 非目標）。
