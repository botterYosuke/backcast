---
status: accepted
---

# ADR-0015: E2E Runner の配置とスクリプト命名規約

> 関連: [findings 0052](../findings/0052-e2e-story-replay-to-hakoniwa.md)（本規約を生んだ初の E2ERunner・調査経緯） / [台本＋実行ファイル](../../Assets/Tests/E2E/Editor/)（`ReplayToHakoniwaE2ERunner.{cs,md}`） / [ADR-0009](0009-scene-authored-workspace-composition-root.md)（runner が headless で合成・駆動する composition root） / [ADR-0001](0001-unity-pythonnet-embedded-frontend.md)（runner が直接 claim する pythonnet interpreter）

> 採番: ADR は findings（0052）とは独立系列。直前の最大が 0014 のため本 ADR は 0015。

Unity アプリの主要ストーリーを目視以外で確認するため、headless runner 型の E2E テストを継続的に拡充する。その配置と命名を固める ADR。

## Status

Accepted（2026-06-19）

## Context

Unity アプリには「起動 → File→Open → .json を開く → Strategy Editor で編集 → 再生 → kernel が replay → 箱庭更新」のような主要ストーリーがあり、これらを人手の目視に頼らず回帰ゲート化したい（findings 0052）。

これまで探索用・回帰用の検証コードが `Assets/Editor/*Probe.cs`（例: `BackcastWorkspaceProbe`, `KernelTeardownProbe`, `ReplayChartDecodeProbe`）に混在しており、**どれが恒久的に運用する E2E ゲートで、どれが一時的な探索コードなのか**が名前と置き場所から判別できなかった。findings は調査ログであって、E2E の台本（仕様・観測点・合格条件）の正本としては流用しづらい。

加えて、これらの runner は Unity Editor を `-executeMethod` で起動する self-failing gate であり、Unity Test Framework（NUnit、EditMode/PlayMode）のテストとは実行形態が異なる。両者を区別して扱える構造が要る。

## Decision

回帰ゲートとして**継続運用する** E2E は、以下の形式で配置・命名する。

```text
Assets/Tests/E2E/Editor/<ScenarioName>E2ERunner.cs
Assets/Tests/E2E/Editor/<ScenarioName>E2ERunner.md
```

- `*.cs` は Unity Editor の `-executeMethod <ScenarioName>E2ERunner.Run` から起動できる runner とする（標準の起動形）。self-failing gate（PASS でログに `[... PASS]`、FAIL のみ `EditorApplication.Exit(1)`、`-quit` 併用で pass=exit 0）。
- `*.md` は同じシナリオの台本（対象ストーリー・自動検証する/しない範囲・観測点・合格条件・実行コマンド・失敗時の切り分け）を記述し、`*.cs` と同じ場所に置いて実装者がセットで読めるようにする。
- 置き場所の `Editor` リーフフォルダにより `Assembly-CSharp-Editor` に入る（runner は `UnityEditor` / pythonnet を使うため editor アセンブリ必須）。

探索・一時検証のコードは引き続き `Assets/Editor/*Probe.cs`（`Probe`）と呼んでよい。**継続的な回帰ゲートに昇格したものは `E2ERunner` として `Assets/Tests/E2E/Editor/` へ移す**（実例: `ReplayToHakoniwaProbe` → `ReplayToHakoniwaE2ERunner`、findings 0052）。

`docs/findings/NNNN` は調査ログ・設計決定の記録として残し、E2E の台本そのものは `Assets/Tests/E2E` 側のテスト資産として扱う。findings には必要なら台本へのリンクだけ残す。

## Consequences

- E2E テストと台本を同じ場所で管理できる（`Runner.cs` と `Runner.md` がセット）。
- `docs/findings` は調査ログ、`Assets/Tests/E2E` は検証資産、と役割が分かれる。
- 新しい E2E を追加するときの「どこに置く？名前は？起動形は？」の判断が不要になる。
- Unity Test Framework の NUnit テストとは別に、headless runner 型 E2E を明示的に扱える（将来 `Assets/Tests/` 配下に NUnit 用 asmdef を足す場合も、`E2E/Editor/` の runner は `-executeMethod` 起動として独立に共存できる）。
- 既存の `Assets/Editor/*Probe.cs` は探索用として残置（一括移行はしない）。回帰ゲート化のタイミングで個別に昇格させる。
- runner が依存する headless 駆動の作法（batchmode の `WorkspaceOwnership` Python スキップを `host.InitializePython` 直呼びで迂回、合成 DuckDB の env 注入、実 `Update()` の pump、データ層での観測）は findings 0052 / 台本 / memory に記録し、本 ADR は配置・命名のみを規定する。
