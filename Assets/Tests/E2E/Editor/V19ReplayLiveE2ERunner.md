# V19ReplayLiveE2ERunner — 台本（実データ Replay LIVE E2E 仕様 / 観測点 / 合格条件）

`V19ReplayLiveE2ERunner.cs` が自動検証する **実 mount Replay** E2E の台本。実装者は `.cs` と本 `.md` をセットで読む。
設計の木・RED→GREEN は [`docs/findings/0089`](../../../../docs/findings/0089-v19-marimo-cell-file-anchor.md)、方針は ADR-0011 / ADR-0016。

> ⚠️ これは MOCK データではなく **owner の実 J-Quants DuckDB**（`BACKCAST_JQUANTS_DUCKDB_ROOT=/Volumes/StockData/jp`）を
> 直接読む。`InitializePython("MOCK")` は live venue を立てず Replay server を立てるためだけ。mount 不在の環境は **SKIP**（PASS 扱い）
> ＝偽 GREEN を作らない。NAS を読むため Unity は**サンドボックス無効**で起動する（子プロセスが `/Volumes` を継承する必要）。

## 対象ストーリー

ユーザーが `v19_morning_cell.py` を開き、committed シナリオ（universe 52・Minute・1 営業日に絞る）で戦略セルの ▶ RUN を押す
＝per-cell RUN で marimo cell が実エンジンを実バーで駆動し、cell 隣接 artifacts を `__file__` 相対で自己ロードして実約定する。

## アーキテクチャ前提

- `WorkspaceEngineHost` を単体 new し `InitializePython("MOCK")` で Python を直 claim（batchmode の `WorkspaceOwnership`
  スキップを正当に迂回・`TachibanaLiveE2ERunner` / `KernelTeardownProbe` と同型）。render 経路（scene）は組まない。
- 駆動は本番 per-cell RUN の executor が叩く `WorkspaceEngineHost.InvokeRunCell(source, idx, scenario_json, strategy_path)`
  の **4-arg pythonnet marshaling**。`strategy_path`=cell の canonical `.py` パス → backend が marimo cell globals の
  `__file__` を設定（findings 0089 の修正）。`load_replay_data` は `jquants_duckdb_root()`（os.environ）から実 NAS を読む。
- C# 側 press→controller→lane→executor の **wiring** は別ゲート（Python-FREE の `NotebookToHakoniwaJourneyE2ERunner`）。本 runner は
  **DATA 半分**（実 Python / 実バー / 実 artifact 解決 / 実約定）に集中する＝behavior-to-e2e の 2 ゲート分割。

## 自動検証する範囲

| Action ID | 行動 | 観測点 | 合否の意味 |
|---|---|---|---|
| `V19REPLAY-01` | 実 mount で実 v19 cell を per-cell RUN 駆動 | `run_cell` JSON `ok=true` | 経路が通った |
| `V19REPLAY-02` | cell が artifacts を `__file__` 相対で自己ロード | `ran[*].output` に `FileNotFoundError`/`No such file` が**無い** | __file__ anchor 修正が効いている（litmus: inject を外すと `TypeError`/FileNotFound で RED）|
| `V19REPLAY-03` | 実バーで実約定（top-k 10:00 entry / 14:55 flatten） | `run_summary.fills_count`（or `trade_count`）`> 0` | 戦略が実エンジンを駆動し約定した |

## 自動検証しない範囲

- **C# press→controller→lane の wiring**（`NotebookToHakoniwaJourneyE2ERunner` がカバー）。
- **箱庭/チャートの実ピクセル**（owner HITL）。
- **エンジン faithfulness の数値 parity**（`test_v19_replay_core.py` / `test_v19_marimo_parity.py` がカバー）。

## 観測点 / 実行

```
<Unity> -batchmode -nographics -quit -projectPath . \
        -executeMethod V19ReplayLiveE2ERunner.Run -logFile <abs>
# expect: [E2E V19-REPLAY PASS] … fills_count=N / verdict はタグで判定（exit=139 は MOCK shutdown segfault＝環境ノイズ）
# mount 不在: [E2E V19-REPLAY SKIP]（PASS 扱い）
```

カバー状態: `V19REPLAY-01..03` = `自動(E2E済)`（実 mount 時）。mount 不在 CI では SKIP。
