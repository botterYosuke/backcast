---
status: accepted
---

# 埋め込み Python ランタイム（CPython + venv）の物理配置と解決方式

Step 1（#3 / ADR-0001 decision 7・8）で、Unity に埋め込む CPython 3.13.13 ランタイム＋venv を
**どこに置き、Editor（dev）と standalone build（deploy）の両方でどう resolve するか**を確定する。
`grill-with-docs`（2026-06-12）で導出。S0 spike（#2）が絶対パス hardcode で残した
「StreamingAssets で relativize」TODO（`Assets/Scripts/S0Spike/S0SpikeHarness.cs` / `Assets/Editor/S0EditorProbe.cs`）の
正式な解。

関連: ADR-0001（decision 7 engine 所有・移植・pin / decision 8 単一 adapter）。

## Context

埋め込み venv は **数万ファイル**（`.py` / native `.so`/`.pyd` / `.pyc` / `.dist-info`）。pythonnet は
Mono バックエンド上で `PythonEngine.PythonHome` と `PYTHONPATH` から stdlib・venv site-packages・
project root を解決し、`nautilus_pyo3` の native ローダ（Rust core）を `dlopen` する。wheel は
**OS 別**（`macosx_*` / `win_amd64`）で native ローダが別物のため、venv は **deploy OS ごとにビルド**する
（ADR-0001 配布節）。

S0 はこれを全て **絶対パス hardcode**（`/Users/sasac/backcast/python/.venv/...`）で通し、
「Step1 #3: relativize via StreamingAssets」を明示的に先送りしていた。本 ADR がその負債を
**実抽象**として解消する。

### 踏んではいけない地雷：venv を `Assets/` 配下に check-in する

「StreamingAssets に同梱」という言葉は、リポジトリの `Assets/StreamingAssets/` に venv を
check-in する失敗を誘発する。これは不可：

- Unity の AssetDatabase が数万ファイルを import 対象として走査し、**1 ファイル 1 `.meta` の爆発**＋
  Editor の import 時間が破滅的に伸びる。nautilus の native `.so` まで asset 扱いされる。
- 「**ビルド成果物**の StreamingAssets に入れる」ことと「**リポジトリの** `Assets/StreamingAssets/` に
  venv を check-in する」ことは別物で、後者だけが地雷。

## Decision

1. **物理配置（dev / repo）**：venv は今のまま `python/`（`Assets/` の外）に置く。Editor の AssetDatabase に
   一切触らせない。`import` root は **`engine` のまま**（TTWR からの移植で ~90 ファイルの import churn ゼロ）。
   pyproject の **dist 名のみ** placeholder `python` から実名 `backcast-engine` に実体化する
   （import package 名 ≠ dist 名なので `import engine` は不変）。
2. **ビルド時コピー**：`IPostprocessBuildWithReport`（Unity build post-process）で、その deploy OS 用の
   venv＋CPython ランタイムを **ビルド出力の `StreamingAssets/`** へコピーする。ADR-0001 の
   「verbatim copy で standalone が動く」利点は **check-in ではなくビルドフック**で得る。
3. **runtime 解決は 1 箇所に集約**：`PythonRuntimeLocator` が `Application.isEditor` で分岐する —
   - **Editor（dev）** → `python/.venv`（editable / project root を `sys.path`）
   - **build（deploy）** → `Application.streamingAssetsPath` 基準
   desktop standalone（Windows/Mac）では StreamingAssets は実ファイルパスなので
   `dlopen(libpython / native .so)` が通る。
4. **基準は StreamingAssets、sidecar は保険**：desktop 配布なら StreamingAssets 基準（`_Data` 内・自動探索）が
   素直。venv の hot-swap 運用要件が出たら exe 隣の sidecar（差し替え容易・要配置ステップ）に倒す。
   いずれも post-process コピーで自動化できるため、sidecar の「auto-copy されない」欠点は消える。
5. **OS 別 venv は deploy OS でビルド**：Windows leg は #2 の Windows 8-byte 再ビルド（standard wheel）が前提。
   → shippable standalone build の検証は **#2 Windows leg の下流**に来る。

### slice-1（#3）が持つ範囲 vs 後送り

「resolution の抽象化」と「shippable standalone build の検証」を分ける：

- **slice-1 が持つ**：`PythonRuntimeLocator`（Editor/build 分岐の実装）を入れ、**Editor playmode の dev パス**で
  Replay tracer を緑にする。S0 の絶対パス hardcode 負債を「べた書き」ではなく実抽象として解消する。
- **後続スライス / 別 issue に倒す**：build post-process コピー＋実際に standalone exe を起動して
  「bundled venv から Replay が動く」検証。重く、OS 別 venv ＝ Windows では #2 Windows 8-byte 再ビルドが
  前提なので、自然に **#2 Windows leg の下流**に束ねる。
- **#3 AC「Unity 単体で Replay 完結」は Editor playmode で満たす**（ADR-0001 移行順序の #3 は「Replay parity」
  という**挙動ゲート**であり、shippable build を gating 条件にしていない）。

## Considered Options

- **採用：venv は `Assets` 外 + ビルドフックで StreamingAssets へコピー + `PythonRuntimeLocator` で
  Editor/build 分岐**。AssetDatabase 爆発を回避しつつ、standalone でも verbatim copy で動く。
- **不採用：venv を `Assets/StreamingAssets/` に check-in**。`.meta` 爆発・Editor import 破綻（上記地雷）。
- **保険として留保：exe 隣の sidecar dir**。hot-swap 運用には素直だが、desktop 配布では StreamingAssets 基準で
  足り、追加の配置ステップが要る。resolution は同型なので要件が出たら倒せる。

## Consequences

- slice-1 のゴールは `PythonRuntimeLocator` 抽象 + Editor playmode の Replay tracer 緑まで。S0 の hardcode 負債は
  ここで解消する（#3 で踏み倒さない）。
- shippable standalone（bundled venv 起動）の検証は **未消化ゲート**として #2 Windows leg の下流に残る。
- Android 等 StreamingAssets が実ファイルでない（APK 内）ケースは**射程外**（deploy = Windows/Mac desktop）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
slice 内で確定する下位事実（resolver の関数名・コピー対象の正確なパス等）は本 ADR に書き戻さず、
当該スライスの `docs/findings/` に記録し本 ADR を「方針: ADR-0002」として参照する。
