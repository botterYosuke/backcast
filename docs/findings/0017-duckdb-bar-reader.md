# findings 0017 — DuckDB 直読み bar reader → kernel Replay 1本通し（#47）

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。本書は当該スライス（#47）の
下位確定事実を記録し、ADR-0006 を「方針: ADR-0006」として参照する（ADR-0006 は自己保護のため編集しない）。
上位の kernel tracer は findings 0008（#24）、その方針は ADR-0004（案 C）。

## 0. ゴール（再掲）

Replay の市場データ源を nautilus `ParquetDataCatalog`（precision-baked `fixed_size_binary`）から
**J-Quants DuckDB の直読み**へ置換する最初の tracer bullet。8918.TSE 日足を **nautilus 非ロード**で端から端まで
走らせ、#24 の凍結 golden と一致させ、DuckDB 読み出しの faithfulness を data-equivalence で証明する。

## 1. データ源（実機確認・grill 2026-06-15）

- ルート = `/Volumes/StockData/jp`（owner 所有・env `BACKCAST_JQUANTS_DUCKDB_ROOT` で上書き可）。
- 銘柄別日足 = `<root>/stocks_daily/<code>.duckdb`・table `stocks_daily`。
- 列マッピング: `Open/High/Low/Close/Volume` は **BIGINT（生 OHLCV）** → float に cast（catalog decode が
  golden に `8.0` 等の float を焼いているため一致させる）。`Adjustment*` 列は当面ほぼ NULL → **不使用**。
- master = `<root>/listed_info.duckdb`・table `listed_info`。**Code は全件 4 桁**（13268 行）。
  `8918` の市場 = MarketCode `0112`／スタンダード。市場名（プライム/スタンダード/グロース/その他/
  TOKYO PRO MARKET）は全て東証区分 → suffix は一律 `.TSE`。
- 4桁/5桁: 日足ファイルは 4 桁 Code（`8918`・4650 行）と 5 桁 LocalCode（`89180`・8 行）が混在。
  **要求 instrument と一致する 4 桁 Code 行のみ採用**し 5 桁行は捨てる（`WHERE Code = '<symbol>'` で自然に除外）。

## 2. linchpin — ts_event の 15:30 JST 合成（#47 最重要事実）

DuckDB の日足 `Date` は **深夜 0:00**（TIMESTAMP）で格納される。一方 catalog/凍結 golden の `ts_event` は
**15:30 JST = 06:30 UTC**（出所 = catalog 生成元 `engine/jquants_loader.py:14`
`datetime.combine(d, time(15,30), ZoneInfo("Asia/Tokyo"))`・全 68 本均一を実測）。

→ **DuckDB reader は `Date` の日付に 15:30 Asia/Tokyo を合成して UTC ns へ変換必須**。これを再現しないと
`ts_event` がズレ、凍結 golden の `fills[].timestamp_ms`（約定 bar の同一 bar close ts）が不一致になり
golden gate も data-equivalence も FAIL する。テストで `duckdb_bar.ts_event_ns == catalog_bar.ts_event_ns`
を全 68 本 assert（`test_matches_catalog_including_ts_event`）。

## 3. 実装インベントリ（GREEN 2026-06-15）

- **`engine/kernel/duckdb_bars.py`**（新規・nautilus-free）: `Bar`（findings 0008 の Bar と同形・自己完結
  ＝#50 の catalog 撤去後も生存）/ `load_bars(data_root, instrument_id, *, start, end)`（`duckdb.connect(read_only)`
  → `SELECT … WHERE Code=? [AND Date>=?][AND Date<=?] ORDER BY Date` → 15:30 JST 合成）/ `daily_db_path()`。
- **`engine/kernel/runner.py`**: KernelRunner の bar 源を `engine.kernel.bars`（catalog pyarrow）→
  `engine.kernel.duckdb_bars` へ差し替え。コンストラクタ引数 `catalog_path` → **`data_root`** に改名。
- **`spike/kernel_golden/scenario.py`**: `DUCKDB_ROOT`（env 上書き可・既定 `/Volumes/StockData/jp`）を追加。
  `CATALOG` は **data-equivalence の比較源**と oracle 源として残置（撤去は #50）。
- **`spike/kernel_golden/run_kernel.py`**: KernelRunner に `data_root=scenario.DUCKDB_ROOT` を渡す
  ＝**golden gate（verify_golden / `--assert-pure`）の kernel 経路が DuckDB 源になる**（AC#3）。
- **`engine/kernel/bars.py`（catalog pyarrow reader）は無改修で残置**。data-equivalence テストの比較対象。
  `test_kernel_bars.py` も catalog reader の unit test として温存（#50 で両者撤去）。
- **`pyproject.toml`**: `duckdb>=1.5` を直接依存に追加。

## 4. data-equivalence の正本（grill で確定・catalog 非依存）

- **凍結 fixture = 全 68 本**（`tests/fixtures/duckdb_bars_8918_daily_golden.json`・catalog 由来の既知正解）。
  first/last だけでなく **全行 OHLCV+ts** を固定（8918 は単調でないため中間破損も検知）。golden と同じ
  「凍結契約」idiom。catalog が #50 で消えてもこの fixture で永久回帰できる（mount があれば突合・無ければ skip）。
- catalog 突合（`test_matches_catalog_including_ts_event`）は **skip-if-absent**。`ts_event_ns` 含め全 68 本一致。

## 5. テスト（`python/tests/`・standalone & pytest 両対応）

- **`test_kernel_duckdb_bars.py`**（新規）: 68 本 / 凍結 fixture 全突合 / catalog 全突合（ts 含・skip-if-absent）/
  5 桁 LocalCode 除外 / **DuckDB 読み経路の import-purity**（subprocess で `nautilus_trader*` 未ロードを確認）。
- 既存テストを DuckDB 源へ移行＋**real-data skip-if-absent** ガード追加（repo 慣習）:
  `test_kernel_risk_gate.py`（4 箇所 `data_root=scenario.DUCKDB_ROOT`）/ `test_kernel_golden_cpython.py`
  （kernel leg のみ skip 化・oracle leg は catalog のまま）/ `test_kernel_teardown_mono.py`。

## 6. PASS ログ（2026-06-15・macOS・`/Volumes/StockData/jp` mounted）

```
$ .venv/bin/python tests/test_kernel_duckdb_bars.py
[KERNEL DUCKDB BARS PASS] DuckDB direct-read == frozen golden == catalog (ts incl.)

$ .venv/bin/python tests/test_kernel_risk_gate.py
[KERNEL RISK GATE PASS] allowlist denies via kernel path; control fills

$ .venv/bin/python -m spike.kernel_golden.verify_golden
[VERIFY GOLDEN PASS] kernel contract matches committed golden

$ .venv/bin/python tests/test_kernel_teardown_mono.py        # run_kernel --assert-pure 経由
[KERNEL TEARDOWN PASS] kernel subprocess exits 0, Rust-core-free, golden match   (rc=0)

$ .venv/bin/python -m pytest -q
3 failed, 227 passed
```

> **3 failed は #47 と無関係の既存失敗**（clean `main` でも同一・stash 検証済み）:
> ① `test_kernel_bars` ② `test_kernel_golden_cpython::test_oracle_subprocess` は **本機 nautilus が
> high-precision(16-byte) ビルドで 8-byte catalog を読めない**（`CatalogPrecisionMismatchError`＝#29/#34・
> ADR-0006 が退役対象とする当該問題そのもの）。③ `test_login_subprocess_env` は Windows パス `X:` を
> macOS の pathsep `:` で split する OS 依存失敗。いずれも DuckDB 経路・本スライスの変更に非接触。
>
> **本スライスの payoff の実証**: nautilus oracle が catalog を読めない本機でも、**kernel golden gate は
> DuckDB 源で GREEN**（verify_golden PASS）＝データ経路が precision-baked catalog から解放されたことの実機証拠。

## 7. 再走手順

```
# 前提: /Volumes/StockData/jp が mount 済み（無ければ DuckDB 依存テストは skip）。
#       別ルートは env: BACKCAST_JQUANTS_DUCKDB_ROOT=/path/to/jp
cd python

# data-equivalence（DuckDB == 凍結 fixture == catalog・ts 含）
.venv/bin/python tests/test_kernel_duckdb_bars.py

# kernel tracer が DuckDB 源で凍結 golden と一致（AC#3・read-only）
.venv/bin/python -m spike.kernel_golden.verify_golden

# import-purity ＋ clean teardown（run_kernel --assert-pure・nautilus 不在）
.venv/bin/python tests/test_kernel_teardown_mono.py

# 凍結 fixture の再生成（catalog 由来の既知正解から・catalog 在庫時のみ）
.venv/bin/python -c "import json,os; from engine.kernel.bars import load_bars; \
b=load_bars('spike/fixtures/jquants-catalog','8918.TSE',start='2024-10-01',end='2025-01-10'); \
json.dump({'instrument_id':'8918.TSE','start':'2024-10-01','end':'2025-01-10','granularity':'Daily', \
'source':'derived from committed catalog parquet (#24 known-good) for #47 data-equivalence','bar_count':len(b), \
'bars':[{'ts_event_ns':x.ts_event_ns,'open':x.open,'high':x.high,'low':x.low,'close':x.close,'volume':x.volume} for x in b]}, \
open('tests/fixtures/duckdb_bars_8918_daily_golden.json','w'),indent=2)"
```

## 8. AC 対応表

| AC | 状態 | 実現 |
| --- | --- | --- |
| ① DuckDB 直読みで動き `nautilus_trader*` 非 import（本経路で確認） | ✅ GREEN | `verify_golden` PASS ＋ `test_kernel_teardown_mono`（`--assert-pure` rc=0・Rust-core 不在）＋ `test_duckdb_read_path_is_nautilus_free` |
| ② 8918.TSE 日足を読み 4 桁 Code 採用・5 桁 LocalCode 除外 | ✅ GREEN | `test_window_bar_count`(68) ＋ `test_five_digit_localcode_excluded` |
| ③ 既存 kernel tracer が DuckDB 源で完走し凍結 golden と一致 | ✅ GREEN | `verify_golden`（kernel==committed golden・sink 順序/fill/position/PnL/cash/equity） |
| ④ data-equivalence（本数・raw OHLCV が期待値一致） | ✅ GREEN | `test_matches_frozen_fixture`（全68本）＋ `test_matches_catalog_including_ts_event`（ts 含・skip-if-absent） |
| ⑤ PASS ログ＋再走手順を findings 記録（ADR-0006 参照） | ✅ GREEN | 本書 §6/§7 |

## 9. 後続スライス（本スライス非目標）

- #49: production Replay（`replay_runner` の nautilus BacktestEngine 経路）を kernel へ移行。
- #50: catalog parquet / `jquants_to_catalog.py` / `nautilus_catalog_loader.py` / `engine.kernel.bars` /
  `test_kernel_bars.py` 撤去、`nautilus-trader` 依存削除。
- 分足（`stocks_minute`・`engine/jquants_loader.py:24` の `(h,m,59,999999)` JST 規約）・複数銘柄・
  `stocks_board`（歩み値/板）・`stocks_trades` は将来スライス。
```
