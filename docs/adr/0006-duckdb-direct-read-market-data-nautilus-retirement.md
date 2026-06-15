---
status: accepted
decision: J-Quants DuckDB を Replay の市場データ源として直読みし、nautilus を runtime から完全退役する
accepted-date: 2026-06-15
accepted-by: owner
relates-to: ADR-0004（案 C・pure-Python Backcast Execution Kernel）を Replay データ経路へ延伸し、
  その「nautilus を standalone CPython oracle として温存」consequence を本 ADR で supersede する
---

# Replay の市場データを J-Quants DuckDB から直読みし、nautilus を runtime から完全退役する

`grill-with-docs`（2026-06-15・#29 HITL で `CatalogPrecisionMismatchError` を実機で踏んだ直後）で確定。
ADR-0004（案 C）は execution を pure-Python kernel に置換したが、**Replay のデータ経路は依然 nautilus
`ParquetDataCatalog`**（`jquants_to_catalog.py` で書き・`nautilus_catalog_loader.py` で読み）に依存し、その
on-disk 形式は `nautilus_pyo3` の `HIGH_PRECISION` ビルドに焼かれた `fixed_size_binary` で、**読込にも
nautilus Rust core を Mono プロセスへロードする**。これは ADR-0004 が構造的に消したはずの「Rust core を
Mono に入れない」制約を Replay で破る。**Decision = Replay の市場データを `/Volumes/StockData/jp/` 配下の
銘柄別 J-Quants DuckDB（`stocks_daily/<code>.duckdb` / `stocks_minute/<code>.duckdb` / `listed_info.duckdb`）
から `duckdb` で直読みし、中間 catalog を持たず kernel へ bar を渡す。これに伴い nautilus を runtime から
完全に退役させる**（`accepted` 2026-06-15・owner 確定）。用語は CONTEXT.md「[[市場データソース（J-Quants
DuckDB 直読み）]]」。

## Context（#29 HITL で顕在化した事実）

1. #29 production Replay は `start_engine` → `catalog_data_loader.load_bars_for_scenario` →
   `nautilus_catalog_loader.load_bars`（`ParquetDataCatalog.query` + `nautilus_pyo3.PRECISION_BYTES`）→
   `engine_runner.run` → `replay_runner` の **nautilus BacktestEngine**。data も execution も nautilus。
2. 共有 catalog（`/Volumes/StockData/artifacts/jquants-catalog`）は standard-precision（8-byte）。本機の
   nautilus wheel は high-precision（16-byte）。`fixed_size_binary` 幅不一致で `query()` 直前に
   `CatalogPrecisionMismatchError`（preflight が無ければ Rust 内 SIGABRT）。**catalog 形式が nautilus
   ビルドに data を縛る**ことが実機で確定。
3. 「nautilus-free」を謳う `kernel/bars.py` すら、nautilus の on-disk エンコード（int64×1e9 を 8-byte
   binary）を手で内面化しており、形式 couple は解けていない。
4. 一方 owner 所有の **J-Quants DuckDB** が既に存在: 銘柄別 1 ファイル、`Open/High/Low/Close` は普通の
   数値（日足 BIGINT・分足 DOUBLE）、`listed_info` の市場は全て東証。precision バイナリ無し・追加変換不要。

## Decision

- **データ源 = J-Quants DuckDB 直読み**。Replay 実行時に `duckdb` で銘柄別ファイルを直接クエリし kernel へ
  bar を渡す。**中間 parquet catalog を生成・保持しない**（第二の真実源と変換ズレを作らない）。
- **値 = 生(raw) OHLCV**。調整列（`Adjustment*`）は当面ほぼ NULL のため使わない（split 調整は将来スライス）。
- **銘柄ID = `<code>.TSE`**（master の市場は全て東証）。日足ファイルに混在する 5 桁 LocalCode（`13010`）は
  捨て、**master と一致する 4 桁 Code のみ採用**。
- **スコープ = bars（日足・分足）のみ**。`stocks_board`（歩み値/板）は将来スライス。
- **nautilus を runtime から完全退役**。`jquants_to_catalog.py` / `nautilus_catalog_loader.py` /
  `/Volumes/StockData/artifacts` parquet catalog / nautilus BacktestEngine 経路（`replay_runner`）を廃止し、
  Replay 実行は kernel へ移す。最終的に `nautilus-trader` を依存から外す。
- **golden oracle 退役**。ADR-0004 の「nautilus を比較 oracle として温存」consequence を supersede し、#24 で
  取得済み golden を**凍結 fixture（回帰）**として残す（新規 capture に nautilus を起こさない）。新データの
  faithfulness は **既知銘柄の data-equivalence チェック**（8918.TSE 日足の本数・OHLCV 等）で担保する。

## Considered Options

- **(A) DuckDB 直読み（採用）** — 変換ゼロ・nautilus 排除・precision 問題消滅。
- **(B) DuckDB → 新 parquet catalog を一度ビルド** — 中間形式を噛ませる。変換ステップ・第二の真実源・ビルド
  手順が増える。データが既にクリーンで銘柄別なので利得が無い → 不採用。
- **(C) nautilus standard-precision wheel を再ビルドして共有 catalog に合わせる**（当日のエラーメッセージが
  示唆した回避策）— precision ビルド地獄を温存し、ADR-0004 の「Rust core を Mono に入れない」に反する →
  不採用。
- **(D) nautilus を oracle のため runtime に残す** — 退役対象の precision/ビルド負担がそっくり残る → 不採用
  （凍結 fixture ＋ data-equivalence で代替）。

## Consequences

- catalog を捨てると #29 の nautilus Replay 経路は読む先を失うため、**Replay 実行の kernel 移行が必須化**する
  （ADR-0004 案 C の Replay への延伸＝やり残しの完了）。
- strategy API: 現 spike fixtures は `nautilus_trader.trading.strategy.Strategy` 等を import する。runtime から
  nautilus を消すには **kernel-native strategy API**（既存 `kernel_*` twin 系）へ寄せる必要がある（移行スコープ）。
- `catalog_path`（[[catalog_path（環境/配置の関心・scenario 外）]]）の解決は DuckDB ルート（`/Volumes/StockData/jp`
  等）を指す env/config へ読み替える。scenario sidecar には引き続き焼かない。
- 立花/kabu の instrument master・market→suffix（#36/#45）とは別系統（あちらは live venue master・本 ADR は
  Replay の過去データ源）。本 ADR は両者を統合しない。
- 実装は複数スライスに分割（DuckDB bar reader / Replay 実行の kernel 化 / nautilus 依存撤去・パッケージ削除 /
  #29 panel・HITL の新ソース再緑化）。各スライスの下位事実は当該 `docs/findings/` に記録し本 ADR を参照する。

## 自己保護

本 ADR の Decision が確定したら固定する。覆す場合は本ファイルを編集せず supersede する新規 ADR を起こす。
スライス内で確定する下位事実（DuckDB reader の列マッピング・分足タイムゾーン処理・kernel strategy loader 詳細等）は
本 ADR に書き戻さず当該スライスの `docs/findings/` に記録し、本 ADR を「方針: ADR-0006」として参照する。
