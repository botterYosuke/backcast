# 0097 — issue #58: 市場休止日（OHLCV=0 bar）が `OhlcPoint(price>0)` 検証で Replay run をクラッシュさせる

- **状態**: 実装＋pytest ゲート GREEN（RED→GREEN を本 doc に記録）。実 venue / Unity 不要（純 Python seam）。
- **gate**: `python/tests/test_replay_no_trade_day.py`（4 本）
  - `test_no_trade_day_does_not_crash_replay_run` — 合成 DuckDB（中間に全ゼロ bar）→ `DataEngine.load_replay_data` → `start_engine` を端から端まで駆動し run 完走を assert（issue 提案そのまま）。
  - `test_loader_drops_all_zero_day_but_keeps_partial_corruption_loud` — 全ゼロ行は drop、`close=0 & volume>0` の半破損行は **残す**（fail-loud）。
  - `test_minute_no_trade_day_also_dropped` — drop は粒度非依存（Minute も）。
  - `test_priming_skips_leading_no_trade_day` — ctor 注入 provider（`core.py` priming 経路）でも初日が無取引日で落ちない。
- **fix**:
  - `python/engine/kernel/duckdb_bars.py` — `is_no_trade_bar(...)`（単一述語）＋ `load_bars` で全ゼロ行を drop（log 1 行）。
  - `python/engine/core.py` `_prime_provider_locked` — 先頭の無取引日 tick を skip。

## 症状

J-Quants DuckDB の Daily/Minute に無取引日（市場休止）の OHLCV 全 0 行が含まれると、その bar が reducer に
流れた瞬間に `OhlcPoint` の `open/high/low/close > 0` 検証で `ValidationError`（4 errors）が発生し、KernelRunner の
run が halt → `start_engine` が `RUN_FAILED` を返す。

実例: `8918.TSE` を 2020-10-01（東証システム障害＝終日全銘柄売買停止）を含む期間で Replay すると、その行が
`Open=High=Low=Close=Volume=0` で記録されているため落ちる。

## 根本原因（コードで裏取り）

- 本番クラッシュ経路（provider なし）: `start_engine` → `runner.py` `load_universe_bars` → `stepper._open_bar`
  → `ReplayKernelObserver.push_bar` → `apply_replay_event` → `reducer.apply_event:90` → `OhlcPoint(open=0,…)`。
- `engine/kernel/duckdb_bars.py` は生 OHLC をそのまま読む（無取引日の除外なし）。
- reducer は個々のゼロ項目を price へ carry-forward する（`open if open>0 else price`）ので、実際に 0 が `OhlcPoint`
  へ届くのは **`close (= price) == 0` のときだけ**。つまり厳密なクラッシュ条件は `close <= 0` 一点。
- priming 経路（`core.py` `_prime_provider_locked`）は ctor に provider を注入したときだけ通る test/後方互換の入口。
  本番 Replay（DuckDB→kernel）は provider を組まないので通らないが、初日が無取引日なら同じ形で落ちる。

## 設計判断（owner 確定）

1. **A案: loader で skip**（reducer ではなく per-instrument chokepoint `load_bars` で除外）。本番 runner と
   backtester の両方を 1 箇所でカバーし、exactly-once 不変条件（ohlc 本数 == stream bar 本数）を保つ（両方 1 本減る）。
2. **skip判定 = 全項目ゼロのみ**（`open==high==low==close==volume==0`）。J-Quants の市場休止記録の形だけを drop し、
   `close=0 & volume>0` のような半破損行は従来どおり検証で落として **fail-loud**（データ異常に気づける）。
3. **priming も直す**（手を抜かない完成形）。同じ述語を priming にも適用。priming の tick は OHLC のみで volume を
   持たないため、OHLC 4 項目ゼロで判定。

## RED → GREEN

- **RED（fix 前）**: 合成 DuckDB に全ゼロ bar を 1 行混ぜて `start_engine` を走らせると `RUN_FAILED` ＋
  `4 validation errors for OhlcPoint (open/high/low/close Input should be greater than 0 [input_value=0.0])`
  ——issue 本文と寸分違わぬ症状を本番経路で再現。ゲートは当初 `xfail(strict=True)` で RED を固定。
- **GREEN（fix 後）**: 4 本すべて PASS。`xfail` を撤去し enforcing ゲート化。
- **delete-the-production-logic litmus**: loader filter / priming guard を外すと即 `RUN_FAILED`（= ゲートは production
  ロジックに依存）。

## 回帰（既存不変条件）

- `test_replay_duckdb_kernel_afk.py` の exactly-once（ohlc == streamed）は維持（drop は kernel/reducer 到達前なので
  chart と stream が同数減る）。
- 実データテスト `test_kernel_duckdb_bars.py::test_five_digit_localcode_excluded` は no-trade 行を期待 count から
  差し引くよう更新（`AND NOT (Open=0 AND High=0 AND Low=0 AND Close=0 AND Volume=0)`）。実 `8918.TSE` 全履歴では
  全ゼロ行が 5 行 drop された（4650→4645）。
