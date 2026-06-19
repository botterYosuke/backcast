# findings 0052 — E2E ストーリー: File→Open から箱庭更新まで（調査・決定の記録）

> **台本（E2E 仕様・観測点・合格条件）は移動しました** →
> [`Assets/Tests/E2E/Editor/ReplayToHakoniwaE2ERunner.md`](../../Assets/Tests/E2E/Editor/ReplayToHakoniwaE2ERunner.md)
> （実行ファイル `ReplayToHakoniwaE2ERunner.cs` と同じ場所。実装者はセットで読む）。
> 本 findings には調査経緯と設計決定だけを残す。

## 背景（なぜこの E2E が要るか）

「起動 → File→Open → .json を開く → Strategy Editor で編集 → 再生 → kernel が replay → 箱庭更新」という
ストーリーを**人間の目視なしで確認**したい、という調査依頼から始まった。

調査の結論: この 7 ステップは **Python 半分（step 4/6/7 のデータ）と Unity 半分（step 1-5・3 の chart
spawn）が別々のゲートで守られていた**が、両者をつなぐ **「再生→箱庭更新」の縫い目（step 7: kernel の
`get_state_json` が `InstrumentOhlcDecoder → ChartView.Render` を通って箱庭に届く）だけが未ゲート**
（従来は owner HITL 任せ）だった。

| 検証面 | 既存ゲート |
|---|---|
| 1 起動・server 構築 | `BackcastWorkspaceProbe` S1-2 ／ CI Stage4 Player smoke |
| 2-3 File→Open・sidecar | `BackcastWorkspaceProbe` S14 |
| 3 universe→chart spawn | `BackcastWorkspaceProbe` S10 |
| 4 editor seed・Run ゲート | `BackcastWorkspaceProbe` S11 |
| 5 Run-Commit 配線 | `BackcastWorkspaceProbe` S9 |
| 6 kernel replay（データ） | `test_replay_duckdb_kernel_afk.py` |
| 6 kernel teardown（Mono） | `KernelTeardownProbe` / `test_kernel_teardown_mono.py` |
| **7 箱庭描画への接続** | **← `ReplayToHakoniwaE2ERunner` で新設** |

## 決定

- owner 判断で **「実 kernel で本物の通し」** を採用（軽量版＝state JSON 注入で描画契約だけ見る案は不採用）。
- アーキ前提: C#↔Python は pythonnet 直呼び。batchmode は `WorkspaceOwnership` が Python をスキップするため、
  Runner は `host.InitializePython()` を直接呼んで迂回（`KernelTeardownProbe` 同型）。合成 DuckDB は
  `BACKCAST_JQUANTS_DUCKDB_ROOT` 注入で owner mount に触れない。
- 実 Unity.exe headless で **PASS**（exit 0、CS エラー 0）を確認済み。再走手順・観測点・失敗時の切り分けは
  Runner.md を参照。
- 命名規約: 回帰ゲート化した E2E は `Assets/Tests/E2E/Editor/<Scenario>E2ERunner.{cs,md}`。`Probe` は
  探索・一時検証用に残す。→ [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) で規約化。
