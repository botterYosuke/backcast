# findings 0018 — DuckDB reader: 分足＋複数銘柄 universe（時刻順マージ）（#48）

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。本書は #48 の下位確定事実を
記録し、ADR-0006 を「方針: ADR-0006」として参照する（ADR-0006 は自己保護のため編集しない）。
前提スライスは findings 0017（#47・日足/単一銘柄 tracer）。

## 0. ゴール

#47 で通した DuckDB 直読み経路を **分足**と **複数銘柄 universe** へ広げる薄い拡張。日足/単一銘柄と
**同一 reader** で分足を読み（`Date`+`Time` から決定的に ts 構成）、universe の各銘柄を時刻順に
stable マージして kernel へ単一ストリームで供給し run を完走させる。nautilus は全経路で非 import。

## 1. データ源（実機確認・grill 2026-06-15）

- 分足 = `<root>/stocks_minute/<symbol>.duckdb`・table `stocks_minute`。ファイルは **4 桁 symbol で命名**
  （`8918.duckdb`）だが、**table 内の Code は 5 桁 LocalCode のみ**（`89180`・4 桁は 0 件）。
  列: `Date`(DATE)・`Time`(VARCHAR `'09:00'`=bar 開始ラベル)・`Open/High/Low/Close`(**DOUBLE**)・
  `Volume`(BIGINT)・`Value`。PK = (Date, Time, Code)。
- 日足（findings 0017）との差: table 名 / `Date` 型(TIMESTAMP→DATE) / `Time` 列の有無 /
  OHLC 型(BIGINT→DOUBLE) / Code 桁数(4桁混在→5桁のみ)。reader がこれを吸収する。
- 8918 分足 2024-04-01 = 119 本（09:00〜15:00）。1301 分足 2024-01-04 = 59 本、1305 = 133 本。

## 2. linchpin — 分足 ts の (h,m,59,999999) JST 合成（日足 15:30 と対）

DuckDB の `Time` は **bar 開始ラベル**（`'09:00'`）。一方 catalog 生成元
`engine/jquants_loader.py:24` は `datetime.combine(d, dt_time(h, m, 59, 999999), JST)` ＝
**bar 終了**（その分の最終マイクロ秒）を ts として焼く。

→ **分足 reader は `Date`+`Time` から `(h, m, 59, 999999)` Asia/Tokyo を合成して UTC ns へ変換必須**。
これは日足の「`Date`→15:30 JST 合成」と同型の linchpin。`'09:00'` をそのまま 09:00:00 にすると
catalog 生成規約とズレる。`test_minute_ts_is_bar_end_convention` で「09:00 → 09:00:59.999999 JST、
かつ 09:00:00 では**ない**」を assert。分足 catalog は不在のため、独立オラクル＝この生成規約そのもの。

## 3. 銘柄行選択 — 両対応フォールバック（#48 grill 決定）

Issue 本文の「4 桁 Code のみ」は**分足には適用不能**（分足は 5 桁 LocalCode のみ）。grill で
**両対応フォールバック**を採用:

- 要求 symbol に対し **4 桁 `Code = symbol` を優先**（日足は 4 桁 `8918` 採用＝凍結 golden 維持）。
- 4 桁が無ければ **5 桁 LocalCode**（`length(Code)=5 AND substr(Code,1,4)=symbol`）へフォールバック
  （分足は `89180`）。
- 日足の 4 桁/5 桁混在ファイルでは 4 桁が勝つので**二重計上しない**（単一 Code 系列のみ採用）。
- 5 桁候補が複数で 4 桁も無い曖昧ケースは **fail-closed**（`ValueError`）。ファイルはあるが該当行
  ゼロなら `[]`、ファイル欠落は `FileNotFoundError`。
- 実装は `duckdb_bars._resolve_code()`。日足の `test_five_digit_localcode_excluded`（4桁優先）と
  分足の `test_minute_code_fallback_uses_localcode`（5桁フォールバック）で双方向を固定。

## 4. 複数銘柄 universe — 時刻順 stable マージ

`merge_bars_by_ts(bar_lists)` = 各銘柄（既に ts 昇順）を universe 順に連結し `sorted(key=ts)`。
Python の `sorted` は **stable** なので、**同一 ts では入力＝universe 順を保持**する（先頭銘柄が先）。
`load_universe_bars(data_root, instrument_ids, *, start, end, granularity)` が各銘柄を読みマージ。

**tie 安定性の歯止め（grill 歯止め1）**: 日足 universe `[8918, 1301]`（共有 8 営業日は全 bar が
15:30 で ts 完全一致＝衝突最大）を **`[8918,1301]` と `[1301,8918]` の両方向**流し、同一 ts での
並びが**反転する**ことを assert（`test_merge_preserves_universe_order_on_ties`）。片方向だけだと
「instrument_id ソート」でも偶然 PASS するため、反転 assert が stable=入力順保持の本当の証明。
マージ総数 == 各銘柄本数の和（脱落/重複なし）も固定。

## 5. kernel run（AC#2）— 分足 × universe × run 完走

`KernelRunner` に `instrument_ids: list[str]` と `granularity` を **加算的に**追加（既存
`instrument_id` は単一銘柄 back-compat として温存・`[instrument_id]` に正規化）。`run()` は
`load_universe_bars` で読み込む。venue は universe 全銘柄で単一であることを要求（混在は `ValueError`）。
既存の per-bar ループ（`reference_price=bar.close` / `last_prices[bar.instrument_id]`）は元から
multi-instrument 安全なので無改修。

**消費の歯止め（grill 歯止め2）**: 分足 universe `[1301, 1305]`/2024-01-04 を `KernelRunner` で完走し、
「例外なく完走」だけでなく **per-instrument の on_bar 回数 == 各銘柄本数**・`result.bars` ＝
sink 受領数 ＝ 両銘柄本数の和 を assert（`test_minute_universe_run_consumes_every_instrument`）。
片方の silent drop が「完走」で素通りするのを防ぐ。これは #48 固有の新経路（分足×マージ×kernel）を
同時に踏む唯一のテスト。

## 6. 実装インベントリ（GREEN 2026-06-15）

- **`engine/kernel/duckdb_bars.py`**: `_Granularity`（table==サブディレクトリ名・SELECT 列・order_by・
  row→Bar ビルダ）/ `load_bars(..., granularity="Daily")` / `merge_bars_by_ts` / `load_universe_bars` /
  `db_path(.., granularity)` ＋ `daily_db_path`（#47 back-compat alias）/ `_resolve_code`（§3）/
  `_minute_to_ts_event_ns`（§2）。
- **`engine/kernel/runner.py`**: `instrument_ids` / `granularity` を加算追加・venue 単一性検査・
  bar 源を `load_universe_bars` へ。
- **`tests/fixtures/duckdb_bars_8918_minute_20240401_golden.json`**（119 本・**独立生成**＝raw SQL ＋
  jquants_loader.py:24 規約。reader 非経由なので reader 回帰を検知）。
- テスト: `test_kernel_duckdb_bars.py`（分足 4 件追加 ＋ purity を minute/universe へ拡張）/
  新規 `test_kernel_duckdb_universe.py`（merge tie 反転・総数・分足 universe run 消費）。

## 7. PASS ログ（2026-06-15・macOS・`/Volumes/StockData/jp` mounted）

```
$ .venv/bin/python tests/test_kernel_duckdb_bars.py
[KERNEL DUCKDB BARS PASS] DuckDB direct-read == frozen golden == catalog (ts incl.)

$ .venv/bin/python tests/test_kernel_duckdb_universe.py
[KERNEL DUCKDB UNIVERSE PASS] daily tie stable=input-order; minute universe run consumes both

$ .venv/bin/python -m pytest tests/test_kernel_duckdb_bars.py tests/test_kernel_duckdb_universe.py -q
12 passed

$ .venv/bin/python -m spike.kernel_golden.verify_golden     # #47 単一銘柄 daily 経路の回帰
[VERIFY GOLDEN PASS] kernel contract matches committed golden

$ .venv/bin/python -m pytest -q
3 failed, 234 passed
```

> **3 failed は #48 と無関係の既存失敗**（findings 0017 §6 と同一・本機 nautilus が high-precision
> ビルドで 8-byte catalog を読めない ①`test_kernel_bars` ②`test_oracle_subprocess`、③`test_login_subprocess_env`
> は Windows `X:` パスを macOS pathsep で split する OS 依存）。いずれも DuckDB 経路非接触。
> 新規 7 件で 227→234 passed。

## 8. 再走手順

```
# 前提: .env の BACKCAST_JQUANTS_DUCKDB_ROOT が指す DuckDB root が mount 済み（直書きしない・
#       engine.paths が .env / process env から読む）。未設定なら DuckDB 依存テストは skip。
cd python
.venv/bin/python tests/test_kernel_duckdb_bars.py        # 日足＋分足 equivalence＋purity
.venv/bin/python tests/test_kernel_duckdb_universe.py     # tie 反転＋分足 universe run

# 分足 fixture の再生成（独立: raw SQL ＋ catalog-gen ts 規約・reader 非経由）。
# root は engine.paths.jquants_duckdb_root() 経由（直書きせず .env から）・未設定なら明示失敗。
.venv/bin/python - <<'PY'
import duckdb, json
from datetime import datetime, time as dt_time
from zoneinfo import ZoneInfo
from engine.paths import jquants_duckdb_root
JST = ZoneInfo("Asia/Tokyo")
_root = jquants_duckdb_root()
if _root is None:
    raise SystemExit("set BACKCAST_JQUANTS_DUCKDB_ROOT (.env) — no hardcoded fallback")
ROOT = str(_root)
SYMBOL, DAY = "8918", "2024-04-01"
con = duckdb.connect(f"{ROOT}/stocks_minute/{SYMBOL}.duckdb", read_only=True)
rows = con.execute("SELECT Date,Time,Open,High,Low,Close,Volume FROM stocks_minute "
    "WHERE length(Code)=5 AND substr(Code,1,4)=? AND Date=? ORDER BY Date,Time", [SYMBOL, DAY]).fetchall()
con.close()
bars=[]
for d,t,o,h,l,c,v in rows:
    hh,mm=map(int,t.split(":"))
    ts=int(datetime.combine(d,dt_time(hh,mm,59,999999),tzinfo=JST).timestamp()*1_000_000_000)
    bars.append({"ts_event_ns":ts,"open":float(o),"high":float(h),"low":float(l),"close":float(c),"volume":float(v)})
json.dump({"instrument_id":"8918.TSE","granularity":"Minute","date":DAY,
  "source":"independent raw-SQL read + jquants_loader.py:24 (h,m,59,999999) JST ts convention for #48 minute data-equivalence",
  "bar_count":len(bars),"bars":bars}, open("tests/fixtures/duckdb_bars_8918_minute_20240401_golden.json","w"), indent=2)
print(len(bars))
PY
```

## 9. AC 対応表

| AC | 状態 | 実現 |
| --- | --- | --- |
| ① 分足を DuckDB から読み `Date`+`Time` から決定的に ts 構成（日足と共通 reader） | ✅ GREEN | `load_bars(granularity="Minute")`・`_minute_to_ts_event_ns`（§2）/ `test_minute_window_bar_count`・`test_minute_ts_is_bar_end_convention` |
| ② 複数銘柄 universe を時刻順マージ（同一 ts 入力順保持）し kernel が消費して run 完走 | ✅ GREEN | `merge_bars_by_ts`/`load_universe_bars`/`KernelRunner(instrument_ids=..)` / `test_minute_universe_run_consumes_every_instrument`（両銘柄消費）・`test_merge_preserves_universe_order_on_ties`（反転 tie） |
| ③ 分足・複数銘柄ともに `nautilus_trader*` 非 import | ✅ GREEN | `test_duckdb_read_path_is_nautilus_free`（minute + universe load を subprocess で purity 確認） |
| ④ data-equivalence（分足・複数銘柄の本数・OHLCV・マージ順） | ✅ GREEN | `test_minute_matches_frozen_fixture`（119本・独立 fixture）・`test_merge_total_count_is_sum_of_instruments`・`test_merge_preserves_universe_order_on_ties` |
| ⑤ PASS ログ＋再走手順を findings 記録（ADR-0006 参照） | ✅ GREEN | 本書 §7/§8 |

## 10. 後続スライス（本スライス非目標）

- multi-instrument の mark-to-market 評価（universe の post-trade rail は run-start MARKET 価格を
  各 instrument に要する・findings 0017 §runner 注記）。本スライスは「run 完走」までで rails 評価の
  多銘柄正確性は対象外。
- `stocks_trades`（歩み値）・`stocks_board`（板）の直読み。
- #49（production Replay の kernel 移行）・#50（catalog / nautilus 依存撤去）。
