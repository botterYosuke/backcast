# 0098 — Open 層を marimo 専用にする（findings 0054 の自動 wrap を反転・#113）

owner all-in 指示で実装（2026-06-24 / grill-with-docs + behavior-to-e2e）。親: #112（ADR-0025 D4 の
`NOT_A_MARIMO_NOTEBOOK` run/materialize 契約）。反転対象: findings 0054（File→Open の非 marimo `.py` を 1-cell
自動 wrap）。関連: ADR-0013（notebook aggregate・immutable）/ #86 / #87（SaveGuard）。

## 背景 — 「marimo or error」を入口（Open 層）へ前倒し

#112 の設計ドリル Q5 で「detect-first 分岐を捨て、marimo を型として強制する」が確定し、run/materialize は
`build_live_marimo_loader`（`engine.strategy_runtime.live_cell_runtime`）が非 marimo を `NOT_A_MARIMO_NOTEBOOK`
で弾く。一方 **Open 層**は findings 0054 §D1 で「非 marimo `.py` を 1-cell wrap して開く」設計のままだった。
#113 はこの自動 wrap を退役させ、Open でも「marimo or error」を強制する。これで editor が marimo 専用で
あることが open〜run で一貫する。

## 決定（設計の木）

**Open 層を run 層の `build_live_marimo_loader` の鏡像にする。** 非 marimo は明示エラー、broken syntax は
SyntaxError 由来の別エラー、valid marimo は無回帰。

### D1. wrap 退役 — `Decompose==null → Fail`（aggregate）

`MarimoNotebookDocument.Open` の null 分岐 `?? new List<Cell> { new Cell(content, "_", "{}") }` を撤去。
`_synth.Decompose(content, out string decErr)` が null を返したら `return Fail(decErr ?? "not a marimo notebook")`。
**全失敗 leg で `_cells`/`_path`/`_dirty` を一切触らない**ので、編集中（dirty 含む）バッファは失敗 Open で
決して消えない（構造的保証）。valid marimo（Decompose 非 null）は従来どおり置換。空 list（valid header だが
0 cell）も `Fail("not a marimo notebook")`（`load_app` の `ensure_one_cell` で実 marimo は不達だが防御）。

### D2. 非 marimo vs broken-syntax を区別（run 層と同一文言）

Python seam `cell_synthesis`:
- `load_app_from_text(py, *, raise_syntax_error=False)`: `load_app` の例外を仕分け。**SyntaxError** は
  `raise_syntax_error=True` で伝播、それ以外（`NonMarimoPythonScriptError`/`MarimoFileError`/empty=None）は `None`。
  per-cell RUN 系 caller（notebook_session / _backend_impl）は default（fail-soft None）のまま無改変。
- `decompose_json` は `raise_syntax_error=True` で呼ぶ → 非 marimo=`None`、broken=SyntaxError 伝播。

C# seam:
- `WorkspaceEngineHost.DecomposeCells(py, out string error)`: None→`"not a marimo notebook"`、PythonException が
  `"SyntaxError"` を含む→`"syntax error: <msg>"`、Python 未 init→`"python not ready"`。
- `IMarimoSynthesizer.Decompose(string py, out string error)`（実装: Pythonnet / Fake）。
- これで `NOT_A_MARIMO_NOTEBOOK`（run）↔ `"not a marimo notebook"`（open）、`SyntaxError`（run）↔
  `"syntax error: …"`（open）が対応＝同じ区別が open〜run で立つ（AC#1/#2）。

### D3. `discardDirty` / #86 F1 dirty-gate / `WrapMode` を退役

これらは **wrap leg 専用の付帯機構**だった:
- `MarimoNotebookDocument`: `_wrapMode`/`WrapMode`、`Open(path, discardDirty)` の dirty-gate（`if (_dirty && !discardDirty) Fail(...)`）。
- `NotebookCellCoordinator.Open(path, positions, discardDirty)` の透過。
- `BackcastWorkspaceRoot.DoFileOpen` の `discardDirty:true` と wrap-hint toast（`WrapMode ? " (wrap mode …)"`）。

wrap 退役で **valid marimo は dirty を無条件置換（不変）／非 marimo・broken は失敗で buffer 保全（自動）**となり、
aggregate に dirty 特別扱いが残らないため全て撤去。**dirty 喪失保護は SaveGuard（`GuardThenProceed`）が引き続き担う**
（#87 の Save/Discard/Cancel モーダルは健在 = FILEGUARD-05/07/08/09）。退役したのは aggregate レベルの
discard-authorization seam のみで、#87 のユーザー向け保護契約は失われない。

> 余談: #86 以前の Open は非 marimo に「is not a notebook」を出していた（findings 0054 D4 が wrap 化で LastError 経路へ
> 置換）。#113 はその拒否を `"not a marimo notebook"` として実質復元しつつ、broken-syntax を区別する点で上書きする。

## 退役した経路／テストの棚卸し（AC#3）

| 退役 | 反映 |
|---|---|
| `MarimoNotebookDocument` wrap leg / `WrapMode` / `discardDirty` / F1 dirty-gate | 撤去（D1/D3） |
| `NotebookCellCoordinator.Open` の `discardDirty` | 撤去 |
| `BackcastWorkspaceRoot` の `discardDirty:true` / wrap-hint toast | 撤去（toast は layoutHint のみ） |
| `FakeMarimoSynthesizer`（seam double） | `Decompose(py, out error)` 化・`SyntaxErrorDetail` leg 追加・no-marker seeding leniency は据置（probe seeding 用） |
| `test_marimo_cell_synthesis_golden.py::test_entry_point_decompose_fail_soft_on_broken_py` | `…_non_marimo_returns_none` に改名・非 marimo=None / broken=SyntaxError raise を assert |
| `StrategyEditorNotebookE2ERunner` S10 wrap/F3/F1/F1-DISCARD | 非 marimo reject / broken distinct / valid 無回帰へ書換 |
| `FileNavGuardE2ERunner` FILEGUARD-06（wrap discard relax）/ 07（WrapMode precondition） | 06 = 非 marimo reject + buffer 保全 / 07 = WrapMode assert 削除 |
| `FileOpenNonMarimoE2ERunner`(.cs/.md) | wrap release-gate → marimo-or-error reject release-gate（OPEN-NM-01..04 反転・per-id rollup タグ追加） |

## テスト方針（behavior-to-e2e / RED→GREEN）

### 層3 pytest（seam 正本）
`test_marimo_cell_synthesis_golden.py::test_entry_point_decompose_non_marimo_returns_none`:
非 marimo（imperative class / comment-only）→`None`、broken→`pytest.raises(SyntaxError)`、valid marimo→JSON（不変）。
**RED→GREEN litmus**: `decompose_json` の `raise_syntax_error=True` を外すと broken が `None` に戻り `raises` が落ちる。

### 層1 AFK（純 C# aggregate・`StrategyEditorNotebookE2ERunner` S10）
非 marimo（`FailDecompose`）→`Open false`+`LastError=="not a marimo notebook"`+未 bound+buffer 非破壊；
broken（`SyntaxErrorDetail`）→`Open false`+`LastError` が `"syntax error"` 始まり（distinct）；
dirty 非 marimo→失敗で buffer 保全；valid marimo→無回帰で開ける。
**litmus**: wrap leg を戻すと非 marimo section が `Open()==true` で RED。

### 層1.5 AFK release-gate（実 on-disk）
`FileOpenNonMarimoE2ERunner`（OPEN-NM-01..04）: 実 `v19_morning.py` reject / broken distinct / valid 無回帰+Run-gate /
path-IO fail-soft。per-Action-ID タグ `[E2E OPEN-NM-0N PASS]` を rollup に出す。

### 層1 AFK（実 root 配線・`FileNavGuardE2ERunner` FILEGUARD-06）
dirty 非 marimo File→Open → SaveGuard prompt → Discard → marimo-only Open が reject、buffer（`dirty_work`）保全、未 bind。
**litmus**: wrap leg を戻すと buffer が file body に置換され RED。

### 層4 HITL
実 workspace で `v19_morning.py` を File→Open → 「marimo notebook ではありません」相当のメニュー通知、編集中バッファ
不変。broken-syntax の marimo ファイルで SyntaxError 由来通知。正常 notebook は無回帰で開ける。

## ADR 判断

新規 ADR は起こさない（可逆・aggregate + seam の局所変更）。run 層の方針正本は **ADR-0025 D4**（immutable・参照のみ）。
本 slice の正本は本 findings。findings 0054 は §「#113 で反転」を追記して履歴として残す（下記）。
