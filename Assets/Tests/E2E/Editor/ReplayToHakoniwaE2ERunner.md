# ReplayToHakoniwaE2ERunner — 台本（E2E 仕様 / 観測点 / 合格条件）

`ReplayToHakoniwaE2ERunner.cs` が自動検証する E2E の台本。実装者は `.cs` と本 `.md` をセットで読む。
これは調査メモではなく、**E2E の仕様・観測点・合格条件を定義する正本**。背景の調査・設計経緯は
`docs/findings/0052-e2e-story-replay-to-hakoniwa.md` を参照。

## 対象ストーリー（7 ステップ）

1. アプリ起動（Unity）
2. メニュー File → Open
3. `.json`（sidecar 付き `.py`）を開く
4. Strategy Editor で Python ソースを編集
5. 再生（▶）ボタンを押す
6. python engine/kernel が replay を再生
7. hakoniwa（箱庭）が更新される

## アーキテクチャ前提

- C#↔Python 境界は **gRPC ではなく pythonnet（同一プロセス直接呼び出し）**。状態は `LiveRpcLanes` が
  `get_state_json()` を 50ms ポーリングして読む（`WorkspaceEngineHost`）。
- **batchmode では `WorkspaceOwnership.ShouldClaim` が Python 初期化をスキップする**不変条件があるため、
  Runner は `host.InitializePython()` を**直接呼んで**所有権の門を迂回する（`KernelTeardownProbe` が
  `PythonEngine.Initialize` を直呼びするのと同じ正当手）。

## 自動検証する範囲（この Runner がゲートする）

- **steps 2-3**: `OnFileOpen`(StubFileDialog → fixture `.py`) → inline SCENARIO から universe を seed →
  `chart:8918.TSE` タイルが spawn される（#60 の universe→tile 配線）。
- **steps 5-6**: production の run 呼び `host.TryStartRun` で**実 Python kernel の replay** が走り、合成
  DuckDB の 50 バーが streaming される（exactly-once）。
- **step 7（縫い目）**: 実 `BackcastWorkspaceRoot.Update()` を pump し、kernel の `get_state_json` が
  `InstrumentOhlcDecoder.Decode → ChartView.Render` を通って箱庭チャートタイルに**データとして**届く。

## 自動検証しない範囲

- **実ピクセルの見た目**（ローソクの色・位置・軸ラベルの鮮明さ等の視覚的正しさ）。`-nographics` は GPU が
  無く、本 Runner は描画を**データ層**で観測する。実ピクセルは引き続き **owner HITL**（実 Play）。
- **step 4 の editor/notebook 合成経路**（marimo cell synth → 戦略 `.py` 解決）。これは
  `BackcastWorkspaceProbe` S9/S11 がカバー。本 Runner は実 kernel を確実に走らせるため、戦略は
  kernel-native fixture を host へ直接渡す（notebook の fake synth を経由しない）。
- データ忠実性（実 DuckDB の中身の正しさ）。本 Runner は合成 DuckDB を使う（owner mount に触れない）。

## 観測点

| step | 観測 | 合否の意味 |
|---|---|---|
| 1 | `host.PythonInitialized && host.ServerReady`、ログ `[WorkspaceEngineHost] live-configured server built; …` | server 構築済み |
| 2-3 | `_currentLayoutPath` ＝開いた `.py`、`_scenario.Universe.Ids == ["8918.TSE"]`、`_chartViews` に `8918.TSE` | open→seed→tile spawn |
| 6 | `host.RunFinished && host.StartError == null`、`InstrumentOhlcDecoder.Decode(host.LatestStateJson, "8918.TSE").Ohlc.Count == 50` | kernel が 50 バーを state JSON へ（exactly-once） |
| 7 | root の private `_chartRendered["8918.TSE"] == 50`（Render 後にのみ set）＋ `ChartView.FirstCandle(true) != null`（実際にローソク geometry を建てた） | 実描画経路が全バーを描いた |

`_chartRendered` と `FirstCandle` の二点で「Update の描画ループが走った」かつ「ChartView が実際に
描画した」を担保する（delete-the-production-logic litmus: `Update` の Render 呼びや `ChartView.Render`
本体を消すとどちらかが必ず落ちる）。

## 合格条件

- ログに `[E2E REPLAY→HAKONIWA PASS] real kernel streamed 50 bars into the hakoniwa chart tile via the production render path.`
- プロセス exit code 0（`-quit` 併用、self-failing gate）。`error CS\d+` が 0 件。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod ReplayToHakoniwaE2ERunner.Run -logFile <log>
```

このマシンの Unity: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
compile だけ先に通すゲート: `-executeMethod` を外して同コマンド（`error CS\d+` 0 件＋`return code 0` を確認）。

## 失敗時に確認するログ・代表的な原因

- **PASS 行が出ない / `[E2E REPLAY→HAKONIWA FAIL] <msg>`**: ログ末尾の `FAIL` メッセージが原因を直接示す。
  Unity ログは UTF-8（`→` を含む）なので **ripgrep ベースで grep**（PowerShell `Select-String` は取りこぼす）。
  flush race: `Application will terminate with return code` を flush 完了 sentinel として待ってから読む。
- **`File→Open did not seed the universe …`**: fixture の inline SCENARIO が読めていない／`coordinator.Open`
  が失敗。fixture が `python/spike/fixtures/strategies/kernel_spike_buy_sell.py` のままか確認。
- **`run failed: load_replay_data …`**: 合成 DuckDB が見つからない。`BACKCAST_JQUANTS_DUCKDB_ROOT` の
  os.environ ハードセット（`BuildSyntheticDuckDb`）と temp `stocks_daily/8918.duckdb` の生成を確認。
- **`kernel streamed N bars … expected 50`**: replay window（2024-10-01〜2025-01-10, Daily）と合成バー数
  （50, 2024-10-01 起点）の不整合、または streaming の prime/skip。`test_replay_duckdb_kernel_afk.py` と突合。
- **`Update() never rendered …` / `_chartRendered != 50` / `FirstCandle null`**: state は届いたが描画経路が
  走っていない。`_isOwner=true` 設定漏れ（`Update` の owner ガードで early-return）か、`Update` の pump
  回数不足（`SETTLE_FRAMES`）を疑う。
- **segfault / GIL stall / crash dump**（`%LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp`）: Mono teardown。
  `host.Stop()` の force_stop + lanes/launcher join 規律（`KernelTeardownProbe` と同型）を確認。

## 命名規約

E2E 回帰ゲートは `Assets/Tests/E2E/Editor/<ScenarioName>E2ERunner.cs` ＋ `<...>E2ERunner.md` に置く。
`Probe`（`Assets/Editor/*Probe.cs`）は探索・一時検証の名前として残し、回帰ゲート化したものを `E2ERunner`
へ昇格させる。正式な規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。
