---
status: proposed
---

# 戦略 run 中の作業ディレクトリ（cwd）を「実行する `.py` のディレクトリ」に合わせる（TTWR ADR-0021 §Consequences の cwd 方針を上書き）

> **実装状態（2026-06-17）**: 本 ADR は **proposed**（未実装）。#78（WYSIWYR 配線）スライスの grill で導出したが、cwd の
> `os.chdir`/restore（Decision §2）は **`python/engine/` にまだ存在しない**。追跡は issue #79（reopen 済み・#78 が
> blocked-by）。実装してから `accepted` に昇格する。誤って「済み」と読まないこと。

`grill-with-docs`（2026-06-17）で導出。戦略コード内の **裸の相対パス I/O（例 `df.to_csv('aaa.csv')`）がどこに保存されるか**を固定する。
上位方針として **ADR-0001（in-proc 埋め込み・同一 PID）** を参照し、移植元 **TTWR ADR-0021（`0021-strategy-identity-is-source-path-not-code-cache.md`）** の
**identity 決定（`__file__`=source）は踏襲しつつ、その Consequences が定めた cwd 方針（「cwd は GUI のリポであって戦略のものではない／戦略は `__file__` 相対で解決し cwd は使わない」）のみを backcast 向けに上書き**する。TTWR ADR-0021 自体は別リポの固定 oracle であり編集しない。

## Context

- Python は pythonnet で Unity プロセスに埋め込まれ実行される（同一 PID・ADR-0001）。コードのどこにも `os.chdir` は無く、戦略実行時の cwd は **Unity 起動時の cwd（Editor 実行ではプロジェクトルート `<repo>`）のまま**。
- よって戦略が `df.to_csv('aaa.csv')` のような **裸の相対パス**を書くと、出力は**戦略 `.py` の隣ではなくリポ直下**に落ちる（owner の直感と相違・本 grill の発端）。
- `Path(__file__).parent / ...` 相対は別経路で正しく戦略の隣を指す（`strategy_loader.py` が `__file__`=source に設定・TTWR ADR-0021 ①の踏襲・v19 の `universe.json` 解決が依存）。つまり **`__file__` 相対は効くが cwd 相対は効かない**という二分が存在していた。
- TTWR ADR-0021 は cache≠source の split を前提に「cwd は GUI リポのまま・戦略は `__file__` で解決」と決めていた。一方 **#78（owner 確定 2026-06-17）は cache 機構を採らず、provider が返す本物の `.py` を直接実行**する（dirty なら Run 封鎖）。cache/source の split が無いため、TTWR ADR-0021 の identity 論点は backcast には適用されず、**残る論点は cwd 方針だけ**になった。
- engine 自身の I/O（`paths.py` の `artifacts_root()` は `REPO_ROOT`=`__file__` 基準の絶対パス）も Unity の I/O（`Application.persistentDataPath`・sidecar の絶対 path）も **すべて cwd 非依存（絶対）**であることを確認済み。よって実行中に cwd を変えても engine/Unity 本体の I/O は壊れない。

## Decision

1. **戦略 run の実行中、プロセスの cwd を「実行する `.py` のディレクトリ」に切り替える。** 基準は #78 の provider が返す canonical な `.py` path の親ディレクトリ（`Path(strategy_path).parent`）。これにより裸の相対 I/O（`to_csv('aaa.csv')` 等）も `Path(__file__).parent` も**両方とも戦略ファイルの隣**を指す。
2. **境界は run スコープ一括**：`load()`（＝`exec_module` で module-level コード実行）の直前に `os.chdir`、`runner.run()`（Live は live ループ）を抜けた `finally` で**元の cwd へ restore**。Replay（`_backend_impl._start_engine_duckdb`）と Live（`strategy_host`）の両経路を**単一の共有コンテキストマネージャ**で包み、素の `os.chdir` を散らさない。例外時も restore する。
3. **`__file__`=source は維持**（TTWR ADR-0021 ①の踏襲）。cwd と `__file__` の両方が戦略ディレクトリを指す superset とする。
4. **未保存（source path 無し）時の cwd は定義しない**＝#78 の fail-closed gate（provider が supplyable でなければ Run 封鎖）により、**保存済み path が無ければそもそも走らない**ので、run 中の cwd は常に実在する source dir。将来 dirty 実行を許す決定をするまで、source 無し時の cwd フォールバックは必要としない。

## Considered Options

- **採用：run スコープで cwd=戦略dir に chdir（restore 付き）**。owner の直感（`to_csv('aaa.csv')` が戦略の隣に落ちる・「その場所で実行している」モデル）に一致。全 I/O が絶対パスのため保持中の競合リスクが構造的に低い。
- **不採用：TTWR ADR-0021 の cwd 方針踏襲（chdir せず・戦略は `__file__` 相対を契約）**。parity は最も素直だが、裸の相対 I/O がリポ直下に落ちる非直感を残す。owner が明示的に「Open した path で実行したことにしたい」と要求したため却下。
- **不採用：各コールバック（on_start/on_bar/on_stop）周りだけ save→chdir→restore**。global cwd の窓は最小化できるが per-bar オーバーヘッドと複雑さが増す。全 I/O 絶対の本コードベースでは run スコープ一括で十分。

## Consequences

- **戦略リポへ出力が書かれ得る**（TTWR ADR-0021 ③が git 汚染・書込権限・名前衝突を理由に避けた領域）。backcast は直感性を優先しこれを**意図的に許容**する。`.gitignore` 運用や出力先の指針はユーザー側の責務。
- **chdir はプロセスglobal で engine は daemon thread（ADR-0001）**。run 中に Unity メインスレッドが cwd 相対 I/O をすると影響するが、Unity 側 I/O は全て絶対パスのため実害は低い。restore は `finally` で保証する。
- 実装スライス・AFK ゲート・検証項目は本 ADR に重複させず、対応 issue の `docs/findings/` に記録し本 ADR を「方針: ADR-0011」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。移植元 **TTWR ADR-0021 は別リポの固定 oracle**であり、本 ADR は TTWR ADR-0021 を編集・supersede するものではなく、backcast 固有の cwd 方針を **TTWR ADR-0021 の Consequences から逸脱して**定めるもの。下位の実装事実は対応 issue の `docs/findings/` に記録する。
