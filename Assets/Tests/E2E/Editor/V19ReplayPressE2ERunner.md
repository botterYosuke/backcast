# V19ReplayPressE2ERunner — 台本（実ボタン press 単一縦串 / 実データ Replay E2E 仕様・観測点・合格条件）

`V19ReplayPressE2ERunner.cs` が自動検証する **実 mount・実ボタン press 単一縦串** E2E の台本。実装者は `.cs` と本 `.md` を
セットで読む。設計の木・RED→GREEN は [`docs/findings/0090`](../../../../docs/findings/0090-v19-press-driven-replay-single-vertical.md)
（前段の `__file__` anchor は [`0089`](../../../../docs/findings/0089-v19-marimo-cell-file-anchor.md)）、方針は ADR-0016。

> ⚠️ MOCK データではなく **owner の実 J-Quants DuckDB**（`BACKCAST_JQUANTS_DUCKDB_ROOT=/Volumes/StockData/jp`）を直接読む。
> `InitializePython("MOCK")` は live venue ではなく Replay server を立てる＋実 marimo / 実 executor を ready にするため。
> mount 不在は **SKIP**（PASS 扱い）＝偽 GREEN を作らない。NAS を読むため Unity は**サンドボックス無効**で起動する。

## なぜこの台本が要るか（既存 2 ゲートの継ぎ目）

per-cell RUN は `V19ReplayLiveE2ERunner`（実 Python/NAS/約定だが **press 配線をバイパス**＝`InvokeRunCell` 直叩き）と
`NotebookToHakoniwaJourneyE2ERunner`（実 press 配線だが **fake executor**・同期レーン）の **2 ゲートに分割**されていた。
owner 要求「バイパスではなくユーザー操作と同じ状況」は、両ゲートが跨がない継ぎ目——**実 decompose→synthesize 往復・
実 worker thread 上の実 Python・実ファイルでの `__file__` 解決**——を 1 本で踏むことを求める。本台本はその唯一の単一縦串。

## 対象ストーリー（owner が手で開いて押すのと同じ）

owner が `v19_morning_cell.py` を **開き**（実 marimo が cell へ分解）、開いた doc から **scenario panel が seed** され、
bt.replay セルの **▶ を押す** → 実 worker lane の実 Python が実 source を実 NAS minute bars で駆動し、cell 隣接 artifacts を
`__file__` 相対で自己ロードして **実約定**（top-k 10:00 entry / 14:55 flatten）する。

## アーキテクチャ前提（production 配線を fake へ差し替えない）

- `OpenScene(BackcastWorkspace)` で実 root を editmode 合成 → `root._host.InitializePython("MOCK")` を **BuildWorkspace の前**に
  呼び、実 server / 実 `PythonnetMarimoSynthesizer` / 実 `HostNotebookCellExecutor` / `NotebookRunLane(startWorker:true)` /
  `NotebookRunController` が ready な状態で production の `BuildWorkspace` を走らせる（Journey と違い **fake synthesizer も
  fake executor も注入しない**）。
- ドキュメントは production の `_coordinator.Open(cellPy)` で開く（実 `decompose_json`）。`ReseedFromEditor` →
  `SeedScenarioFromEditor` が v19 sidecar（`v19_morning_cell.json` の `scenario` キー）から `_scenario` を populate。
- press は bt.replay セルの **実 `_cellRunButtons[region]` の `.onClick.Invoke()`**＝production の onClick gate
  （`_isOwner && _host.ServerReady` → `_notebookRun.RunCell`）を通る。`_isOwner=true` を立てる。
- 完了待ちは main の `DrainAndRoute` pump（batchmode は Update が無いので手で回す）。約定は production poll cache
  `host.RunSummaryJson`（`LiveRpcLanes.get_run_summary_json`）で観測。

## 自動検証する範囲

| Action ID | 行動 | 入口 | 観測点 | 合否の意味 |
|---|---|---|---|---|
| `V19PRESS-01` | 実 marimo で v19 を開く（decompose） | `NotebookCellCoordinator.Open` → `_synth.Decompose` → `host.DecomposeCells` | `Open(cellPy)`==true・cell 数 ≥ 1 | 往復の入口（分解）が通った |
| `V19PRESS-02` | 開いた doc から scenario panel が seed | `ReseedFromEditor` → `SeedScenarioFromEditor` → `_scenario.PopulateFrom` | `_scenario.Universe.Count > 0` | sidecar→scenario の手渡しが通った |
| `V19PRESS-03` | strategyPath leg が実 v19 path を返す | `BuildNotebookStrategyPath` → `EditorFileProvider.TryGetStrategyFile` → `_notebook.TryGetStrategyFile` | `== cellPy`（非 sentinel・非 null） | 0089 の `__file__` leg が実ファイルで非空虚解決 |
| `V19PRESS-04` | bt.replay セルの実ボタン press → RUNNING | `Button.onClick` → `RunCell` → guard（`drivesReplay && scenarioJson`） | press 後 `IsBacktestRunning`・glyph ▶→■ | 実 onClick gate→controller→guard が通った |
| `V19PRESS-05` | worker lane の実 Python が実 source を実バーで駆動 | `NotebookRunLane`(worker) → `HostNotebookCellExecutor` → `InvokeRunCell` | timeout 内に run 完了・hung でない | 実 worker thread の実 marimo session（build+run 1 スレッド契約）が通った |
| `V19PRESS-06` | 実約定（top-k 10:00 / 14:55） | `engine.last_run_summary` → `host.RunSummaryJson` | `fills_count`（or `trade_count`）`> 0` | 往復 source が artifacts を解決し実エンジンを駆動して実約定した |

## 自動検証しない範囲（carve-out・理由併記）

- **実 GPU での base tile / chart のピクセル・色・レイアウト** → **HITL専用**（`-nographics` は GPU 無し）。
- **エンジン数値 parity**（byte-identical fills 等） → **対象外**: `test_v19_replay_core.py` / `test_v19_marimo_parity.py`。
- **走行中スナップショットの逐次 tile 更新の C# 縫い目** → **対象外**: `NotebookToHakoniwaJourneyE2ERunner`（本縦串は最終 run_summary の fills を gate）。

## 観測点 / 実行

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod V19ReplayPressE2ERunner.Run -logFile <abs>
# expect: [E2E V19-PRESS PASS] ... fills_count=N / verdict はタグで判定（exit=139 は MOCK shutdown segfault＝環境ノイズ）
# mount 不在: [E2E V19-PRESS SKIP]（PASS 扱い）
# ⚠️ 実 NAS を読むため Bash サンドボックス無効で起動（/Volumes 継承）。確認は Bash `grep -a "E2E V19-PRESS"`
```

## RED→GREEN litmus（delete-the-production-logic）

- findings 0089 の `__file__` inject を外す → worker の実 cold-run で `_artifacts` が FileNotFound → 約定ゼロ → V19PRESS-06 RED。
- decompose→synthesize 往復が壊れ bt.replay セルが消える/壊れる → synthesized source が bt を駆動せず → V19PRESS-05/06 RED。
- worker lane の build+run 同一スレッド契約が崩れる → `ContextNotInitializedError` → run_cell ok=false → V19PRESS-06 RED。
- onClick gate / running guard を壊す → V19PRESS-04 RED。

カバー状態: `V19PRESS-01..06` = `自動(E2E済)`（実 mount 時）。mount 不在 CI では SKIP。
