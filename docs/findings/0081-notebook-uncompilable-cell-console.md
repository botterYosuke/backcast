# 0081 — 構文エラーのセルが console にエラーを出さず KeyError で落ちる

方針: findings 0079（#102 console）の follow-up。0080（context thread）の修正で happy path が動くようになり
表面化した**別の既存バグ**。ADR 無改変。

## 症状（owner HITL 2026-06-21, findings 0080 の実機検証中に発見）

セルに **構文エラー**（`print('1234)` ＝ 閉じ引用符欠落）を入れて ▶ を押すと、フッターに
`Run cell: KeyError: 'c0'` が出て **console にエラーメッセージが出ない**。
（**ランタイムエラー**＝`1/0` / `raise` は既に正しく console stderr にトレースバックが出る。壊れているのは
**コンパイル時失敗のセルだけ**。）

## 根本原因

marimo の `Kernel._maybe_register_cell(cid, code, stale)` は `(old_children, error)` を返し、コンパイル失敗時
（`SyntaxError`）は **`error=MarimoSyntaxError(...)` を返してセルをグラフに登録しない**（`runtime.py:836-887`）。
`IncrementalNotebookSession._restage` は**この戻り値の error を無視**していたため：
- セルは `k.graph.cells` にも `k.graph.parents` にも入らない（`c0 in graph.cells == False`）。
- `self._codes[cid]` は設定される → `run_pressed` は `pressed_cell_id in self._codes` を満たし `_run` へ進む。
- `_run` が `Runner(roots={cid})` → `compute_cells_to_run` → `dataflow.transitive_closure` →
  `graph.parents['c0']` で **`KeyError: 'c0'`**（`cell_runner.py:189` / `dataflow/__init__.py:51`）。
- KeyError は `_run` から伝播 → `_backend_impl.run_cell` の except が `{"ok":False,"error":"KeyError: 'c0'"}`
  に変換 → フッター表示。エラーメッセージ（SyntaxError）は破棄。

## fix（`notebook_session.py`）

- `IncrementalNotebookSession.__init__` / `close`: `self._cell_errors: dict[cid, Error] = {}` を追加。
- `_restage` の登録ループ: `_, err = k._maybe_register_cell(...)` の **error を捕捉**。非 None なら
  `self._cell_errors[cid]=err`、コンパイル成功なら pop（＋従来どおり set_stale）。削除セルも pop。
- `run_pressed`: `_run` の前に `pressed_cell_id in self._cell_errors` なら **`_compile_error_result(cid)`**
  を返す（`_run` に到達させない＝KeyError 回避）。
- `_compile_error_result`: ランタイムエラーと**同じ ran 行の形**を返す — top-level `error=None`・`ok=True`、
  行は `ok=False`、`console=[{"stream":"stderr","text": err.msg}]`（C# が amber で console に描画）。
  編集して有効コードに戻すと `_restage` が error を pop し、通常実行に復帰。

## ゲート（RED→GREEN）

- **Python e2e**（`test_notebook_console.py::test_uncompilable_cell_surfaces_its_error_to_the_console`）:
  valid `print('1234')`（console=stdout）→ `print('1234)`（**SyntaxError**）→ valid `print('recovered')`。
  - **RED**（fix 前）: 2 番目で `run_pressed` が `KeyError: 'c0'` を raise。
  - **GREEN**（fix 後）: top.error=None・row.ok=False・console stderr に `SyntaxError ...`、3 番目で復帰。
  - delete-the-production-logic litmus: `run_pressed` の `_cell_errors` 短絡を消すと即 `KeyError`。
- **C# 側**: console passthrough（`HostNotebookCellExecutor.ExtractConsole` → `SetConsole`）は既存・無改変。

## 教訓（[[behavior-to-e2e]] に反映済み）

happy path が直って初めて「エラー入力」系の隣接バグが表面化する。**seam を触る操作の全列挙**には
*失敗系の入力*（構文エラー・ランタイムエラー・空セル）も含めること。
