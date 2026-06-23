# findings 0089 — marimo per-cell RUN は戦略の `__file__` anchor を持たず cell-adjacent artifact 解決が壊れる

> 出荷前確認（owner HITL・2026-06-23）で発見。`v19_morning_cell.py` を Unity Replay モードで「実ユーザー操作と
> 同じ状況」で回したら **約定ゼロ**になった。`behavior-to-e2e` の RED→GREEN として記録。方針参照: ADR-0011・ADR-0016。

## 症状（ユーザー操作）

`v19_morning_cell.py` を開き committed シナリオ（universe 52・Minute・2025-01-06..10）で戦略セルの ▶ RUN を押すと:

- 実 Unity 埋め込みカーネルでは `__file__` が **`None`** → `_artifacts` セルの `Path(__file__).resolve().parent / "artifacts"`
  が **`TypeError: argument should be a str or an os.PathLike object … not 'NoneType'`** で落ちる
  （plain-Python の marimo は `__file__` を cwd 由来の str にするので `FileNotFoundError` になる。どちらも壊れている）
- → universe/scorer artifact が読めず `score_v19_rows` 不発 → **約定ゼロ・run_summary なし**（トップレベルは `ok:true`＝静かな無）

## 真因

| | |
|---|---|
| `_artifacts` セル | cell 隣接 `artifacts/` を `Path(__file__).parent / "artifacts"` で自己ロード（`V19_ARTIFACTS_DIR` override 可）|
| marimo **per-cell `run_cell`** 経路 | `load_app_from_text(source_text)` で source を**テキスト**として受け、戦略のディスク位置を一切受け取らない → `__file__` が anchor されない |
| 命令型 strategy 経路 | `strategy_loader.py:87` が `module.__file__ = str(original_path)` を**明示設定**するので `Path(__file__).parent` 相対が効く |
| 非対称性 | 命令型は `__file__` を設定するが、**marimo per-cell 経路は等価処理を欠く**。v19 の marimo cell は per-cell 経路を通るので壊れる |

### ADR-0011 の前提との関係（#79 調査）

ADR-0011（proposed・cwd を戦略 dir に合わせる #79）の Context は **「`Path(__file__).parent / ...` 相対は別経路で
正しく戦略の隣を指す（… v19 の `universe.json` 解決が依存）。`__file__` 相対は効くが cwd 相対は効かない」** と前提していた。
本 finding はこの前提を **marimo per-cell 経路で falsify** する（その経路では `__file__` 相対も効かない）。#79 は cwd 相対
（`to_csv('aaa.csv')`）が対象で命令型経路に scope され、しかも未実装（`grep chdir python/engine/` = 0）なので、本バグは
**#79 では直らない別不具合**。根は共通＝「per-cell 経路に戦略のディスク anchor が無い」。ADR-0011 の Context にこの例外を注記
（proposed のため in-place・decision の逆転ではない）。

## 修正（principled・最小）

戦略の canonical `.py` パスを per-cell RUN 経路へ配線し、marimo cell globals の `__file__` を設定（`strategy_loader.py:87`
を鏡映）。`_apply_inject` が cold-run **前**に `k.globals.update(inject)` するので、artifact セルが正しい `__file__` で走る。

- **Python**: `DataEngineBackend.run_cell(source, idx, scenario_json, strategy_path=None)` →
  `inject = {**(inject or {}), "__file__": str(strategy_path)}`（`strategy_path` 真値時のみ・全 press で・空文字は falsy で無注入）。
  `inproc_server.run_cell` / `backend_service.run_cell` も `strategy_path` を素通し。
- **C#**: `NotebookRunController`（`_strategyPathProvider`）→ `NotebookRunRequest.StrategyPath` → `NotebookRunLane` →
  `HostNotebookCellExecutor.Run(…, strategyPath)` → `WorkspaceEngineHost.InvokeRunCell`（4-arg PyString marshaling）。
  provider は `BackcastWorkspaceRoot.BuildNotebookStrategyPath`＝#78 `EditorFileProvider.TryGetStrategyFile`
  （未バインドエディタ→null→backend は `__file__` を既定のまま＝#78 fail-closed gate と整合）。

## RED→GREEN ゲート

- **Python e2e**（`python/tests/test_v19_morning_cell_replay.py`・実 mount skip-if-absent）: 実 v19 cell を `run_cell`
  経由で実 NAS データ駆動。`strategy_path` 配線前は **RED**（`TypeError: run_cell() got an unexpected keyword 'strategy_path'`／
  __file__ 未注入で FileNotFound）、配線後 **GREEN**（fills>0・run_summary・IDLE）。`1 passed in ~6s`。
- **C# AFK**（`Assets/Tests/E2E/Editor/V19ReplayLiveE2ERunner.{cs,md}`・実 mount skip-if-absent）: `InitializePython("MOCK")`
  で実 Python を立て、実 NAS データで実 cell を本番 4-arg `InvokeRunCell` marshaling 経由で駆動。
  - RED（inject を無効化）: `[E2E V19-REPLAY FAIL] run never finalized a run_summary`（`__file__=None`→`TypeError`）。
  - GREEN: `[E2E V19-REPLAY PASS] … fills_count=2`（2025-01-06 単日・1 round-trip）。
  - 注: `InitializePython("MOCK")` runner は shutdown segfault で **exit=139** だが verdict は PASS/FAIL タグで判定（#107 と同型）。
  - ⚠️ NAS（`/Volumes/StockData/jp`）を読むため Unity は**サンドボックス無効**で起動する（子プロセスが `/Volumes` を継承する必要）。

## 2 ゲート分割（behavior-to-e2e）

- C# press→controller→lane→executor の **wiring** は Python-FREE の `NotebookToHakoniwaJourneyE2ERunner` が別途ゲート（regression 確認済み・PASS）。
- 本 finding の新規価値＝「実 Python で実 cell が実 artifact を `__file__` 解決して実約定する」**DATA 縦串**（#99 で退役した実カーネル Replay ゲートの席を実データで再縫合）。
