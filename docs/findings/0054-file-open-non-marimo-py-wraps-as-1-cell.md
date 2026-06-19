# 0054 — File→Open of a non-marimo `.py` wraps as 1 cell (#86)

owner-locked 2026-06-19 (grill-with-docs). 関連: #86 / #80 (CLOSED) / findings 0050（cell-as-floating-window）/
0051（File→Open bare strategy・layout 有無分岐）/ ADR-0013（notebook aggregate）.

## 背景 — UI と実体の食い違い

`File→Open` のダイアログ filter は `Strategy (*.py)` で「Python ファイル全般」を選べるように見えるが、
production の Open は `_coordinator.Open` → `MarimoNotebookDocument.Open` → `_synth.Decompose(content)` で、
`Decompose` の正体は `PythonnetMarimoSynthesizer` → `WorkspaceEngineHost.DecomposeCells` →
`engine.strategy_runtime.cell_synthesis.decompose_json` の **`load_app` ベース**。`load_app` は marimo の
`app = marimo.App()` を必要とするので、`python/strategies/v19/v19_morning.py` のような **imperative な
`Strategy` サブクラス `.py`** は `None` 返り → C# が `null` 受け → `Open` が `Fail("not a marimo notebook")`
で abort → メニューに `Open: 'v19_morning.py' not a marimo notebook` が出る。

ユーザー目線では「アプリ内エディタなのに Python ファイルが開けない」挙動になり、findings 0051 が `.py` 全般を
扱えるようにした File→Open 一本化の趣旨にも背く。issue #86 がこれを別件として切り出した。

## 決定（設計の木）

**File→Open は任意の `.py` を開く。非 marimo `.py` は 1-cell として wrap して bound する（option (A)）。**

### D1. wrap 方針 — body 生本文・name = `_`（anonymous）

集約 Open で `Decompose` が `null` を返したとき、`fail-soft abort` ではなく:

- `cells = [new Cell(body=<原 .py 全文>, name="_", config="{}")]` の **1 cell** にして cell list を差し替え
- `_path = full`, `_dirty = false`, `_openedOrSaved = true` で **bound + clean + supplyable**

cell 名は marimo の新セル既定（`_` = anonymous）に倣う（owner: 「`_config()` じゃなくていい」）。
`generate_filecontents` の wrap 出力は naturally `@app.cell\ndef _():\n    <indented body>\n    return (defs,)`
の形になる。owner 指示「先頭に `@app.cell\ndef _config():` をつける」は wrap の見え方の例示で、cell 名そのものは
任意。

### D2. Save は marimo 形式に書き換える（destructive・owner 公認）

`File→Save` は `_coordinator.Save()` → `_notebook.Save()` → `_synth.Synthesize(_cells)` →
`generate_filecontents` を必ず通る。よって非 marimo `.py` を開いて編集 + Save した結果は **on-disk が marimo
notebook（`import marimo` + `app = marimo.App()` + `@app.cell def _():` + `if __name__ == "__main__": app.run()`）
に上書き**される。

owner はこの destructive 上書きを **明示的に公認**: 「Save したとき、ファイルを marimo 形式に書き換えてよい？」
→ 「はい、上書きしてよい」（2026-06-19 grill 回答 2）。issue #86 受け入れ条件案 §「破壊的な marimo 変換をしない」
は owner の明示判断で override される。

### D2a. Open 直後の wrap-mode signal（F3 / 2026-06-19 code-review MEDIUM）

§D2 の destructive 変換を user に黙ってやらせないため、Open 直後の toast と aggregate state に
wrap-mode の signal を出す:

- `MarimoNotebookDocument.WrapMode`（public read-only bool）が wrap leg の Open 成功で `true`、
  valid-marimo Open / `Save` / `SaveAs` / `ResetUnboundEmpty` で `false`。dirty refuse や path/IO
  fail-soft では触らない（既存 state を維持）。
- `BackcastWorkspaceRoot.OnFileOpen` の toast を分岐:
  - clean marimo Open: `"Opened v19_morning.py"`
  - wrap leg: `"Opened v19_morning.py (wrap mode — Save will convert to marimo)"`
  - 後置の `" (no saved layout)"` は従来どおり末尾に並ぶ（wrap hint → layout hint の順）。

これで「clean marimo を開いた」と思って Ctrl+S して on-disk が §D2 の destructive 変換に飲まれる
事故が防げる（toast に「Save will convert」と明示）。`WrapMode` は coordinator を貫通しない
（coordinator 経由ではなく `_notebook.WrapMode` を直参照） — wrap-mode は file semantics の話で
window orchestration の altitude ではないため（ADR-0013）。

### D3. Save 前 vs Save 後の Run 挙動差（重要・user-facing）

Open 直後 (Save 前): on-disk は元の imperative `.py` のまま。Run は `_make_strategy_factory` →
`is_marimo_app_file(spath)` が `False` → `strategy_loader.load(...)` の imperative 経路で
`inspect.getmembers(module, inspect.isclass)` から `Strategy` サブクラスを抽出 → **正常に走る**。

Save 後: on-disk は marimo 形式に変換済み。Run は marimo 分岐 → `MarimoStrategy(app=...)` → cell DAG を
`submit_market` 駆動で走らせる。**だが** 元の `class V19MorningStrategy(Strategy):` は `def _():` 関数本体に
押し込まれて *function-local* 化し、module 属性として顔を出さない。`MarimoStrategy` は cell の
`submit_market(qty)` 呼び出しを駆動するので、元の `Strategy` サブクラスの `on_bar` ロジックは**一度も実行
されない**（silent no-op に近い）。

これは「imperative 戦略 → cell-DAG への一方向マイグレーション補助ツール」として割り切る。user が wrap 後の
cell 本体を marimo cell-DAG 形式（`submit_market` を直接呼ぶ等）に書き換えて初めて Run が意味を持つ。
**この事実は user 文化として広報されるべき**（findings 本文 + コミットメッセージで surface する）。

### D3a. Run 時の SyntaxError surface（broken-syntax `.py` の wrap-Open）

`Decompose == null` 経由で wrap される `.py` には imperative `Strategy` サブクラスを欠く
**broken-syntax** ファイルも含まれる（owner 決定 2026-06-19: wrap-Open は構文有無を問わず通す
＝「Open も Run も通す、Run 時のエラーを user に見せる」）。Open は body をそのまま 1-cell に
詰めて成功するが、Run（`start_engine`）は `_select_replay_strategy` →
`strategy_loader.load` → `importlib` の `exec_module` で **`SyntaxError` を発生**させる。
これは load 経路で既に user-visible envelope に乗る:

- `strategy_loader.load`（[`python/engine/strategy_runtime/strategy_loader.py:88-93`]）が
  `except Exception` で握り、`StrategyLoadError("failed to import <path>:\n<traceback>")` に
  **traceback ごと wrap** する。
- `_start_engine_duckdb`（[`python/engine/_backend_impl.py:840-851`]）の `except Exception` が
  `BacktestRunResult(success=False, error_code="STRATEGY_LOAD_ERROR", error_message=str(exc))`
  に変換する（`_select_replay_strategy` の docstring が "SyntaxError" を明示的に契約として
  宣言済み — line 434-437）。
- C# 側は `WorkspaceEngineHost.Launcher`（[`Assets/Scripts/Live/WorkspaceEngineHost.cs:299-305`]）
  が `_startError = "start_engine: STRATEGY_LOAD_ERROR " + error_message` を立て、
  `BackcastWorkspaceRoot.Update`（[`Assets/Scripts/Live/BackcastWorkspaceRoot.cs:946-947`]）が
  `_menuBarView.ShowMessage("Run failed: ...")` + `Debug.LogError("[BackcastWorkspaceRoot] FAIL: ...")`
  でメニュー通知ラインに traceback 込みで surface する。

つまり **追加配線は不要** — 案 A の「Run 時のエラーを user に見せる」は既存の
`STRATEGY_LOAD_ERROR` パイプ（imperative load 失敗用）が SyntaxError もそのまま運ぶ。
F1 の dirty gate で wrap が拒否される clean window から外れない限り、broken-syntax を
Open → Run しても crash や silent no-op ではなく user-visible エラーになる。

### D4. fail-soft 経路は path/IO エラーのみ

`MarimoNotebookDocument.Open` で false を返すケースは：

| 原因 | LastError |
|---|---|
| `path == null/empty` | `"no path"` |
| `Path.GetFullPath` 例外 | `"bad path"` |
| 拡張子 != `.py` | `"not a .py"` |
| `File.Exists(full)` == false | `"file missing"` |
| `File.ReadAllText` 例外 | `"read failed"` |
| 非 marimo `.py` ＋ `IsDirty == true`（#86 F1） | `"dirty workspace — Save or File→New before opening a non-marimo .py"` |

`Decompose == null`（= 非 marimo / broken syntax）は **failure ではなく policy** に降格＝1-cell wrap。
**ただし `IsDirty == true` のときは wrap も拒否**（#86 F1）：未保存セルを別ファイルの 1-cell wrap で暗黙上書きしないよう、aggregate が dirty gate を立てて fail-soft する。valid marimo `.py`（Decompose 非 null）は dirty でも従来通り置換する（user の明示的「別 notebook に切替」意図）。discard-confirm modal は別 slice の UX 改善（本 fix の射程外）。

`BackcastWorkspaceRoot.OnFileOpen` のメッセージは
`"Open: '<name>' " + (LastError ?? "could not be opened")` に更新（旧 "is not a notebook" は不正確になった）。

### D5. Python seam は不変

`engine.strategy_runtime.cell_synthesis.decompose_json` の契約「非 marimo / broken → `None`」は **変えない**。
理由: ① seam を fan-in する他の caller（`test_marimo_cell_synthesis_golden.py:243` の fail-soft 契約 test 等）が
依存。② wrap policy は document aggregate の責務であり、編集器ドメインに留めるのが ADR-0013 の altitude 原則に
忠実（`generate_filecontents`/`load_app` のラッパ層は薄いまま）。③ fake と prod の seam 挙動差（fake は arbitrary
text を 1-cell wrap、prod は null）を**集約レイヤで吸収**することで両系統が同じ Open 出力に収斂し、`FakeMarimoSynthesizer.FailDecompose` の意味付けが「production の null leg を再現する」と明確化される。

## 射程外（別スライス）

- **広報・ドキュメンテーション**: 「Save すると marimo 形式に変換される」を menu hint / first-Save 警告 modal /
  README に出すかは UX slice として別件（最小実装は findings 本節と commit message のみ）。
- **imperative → cell-DAG 自動変換**: AST で top-level classes/funcs を別 cell に切り出す等の "smart wrap" は、
  marimo cell 契約（`submit_market` を呼ぶこと）に届かないので Run 挙動を変えない。手動書き換えが正。
- **Save As の path picker filter**: 現状 `Strategy (*.py)` のまま。`marimo notebook (*.py)` 等への renaming は
  option (B) 側の話で本 issue では採用していない。

## テスト方針（CLAUDE.md / behavior-to-e2e）

### 層1 AFK（純粋 C#・`StrategyEditorProbe` S10）

旧 fail-soft test を **wrap 検証に置換**:
- `FakeMarimoSynthesizer { FailDecompose = true }`（prod 由来の null leg を再現）+
  `class V19MorningStrategy(Strategy):` を含む raw `.py` を temp に書く → `Open` 成功 ∧ `CellCount == 1` ∧
  `Cells[0].Body == 生本文` ∧ `Name == "_"` ∧ `IsBound && !IsDirty` ∧ `TryGetStrategyFile == true`.
- **F1 (#86 review・dirty 拒否)**: 同じ `FailDecompose=true` synth で `AddCell()` + `Cells[0].SetBody(...)` で dirty 状態を作り、別 raw `.py` を temp に書いて `Open` → **`false`** ∧ `LastError != null` ∧ `CellCount`/`Cells[i].Body` が Open 前と byte-for-byte 同一 ∧ `IsDirty` 保持。直後に**新しい** `MarimoNotebookDocument`（clean）で同じ raw `.py` を `Open` → 既存 wrap 経路が green（clean 経路の回帰防止）。
- **F3 (#86 code-review MEDIUM・wrap-mode signal)**: wrap leg の Open 成功で `WrapMode == true`、valid-marimo round-trip Open で `WrapMode == false`、clean wrap → `SaveAs(temp)` 成功で `WrapMode == false`（destructive marimo 変換後に stale な warning が残らない）。toast 分岐 (`"(wrap mode — Save will convert to marimo)"`) はそれ自身が aggregate state を反映する pure projection なので、aggregate flag を lock すれば toast 文言も lock される。

### 層1.5 AFK release-gate（実 on-disk v19_morning.py を開く E2E runner）

[`Assets/Tests/E2E/Editor/FileOpenNonMarimoE2ERunner.cs`](../../Assets/Tests/E2E/Editor/FileOpenNonMarimoE2ERunner.cs)
（台本: [`.md`](../../Assets/Tests/E2E/Editor/FileOpenNonMarimoE2ERunner.md)）は層 1 の合成 fixture を **実在の
`python/strategies/v19/v19_morning.py` の on-disk テキスト**に置き換え、Run-gate 解禁まで延伸する release-gate。
4 section（OPEN-NM-01..04）:

- `OPEN-NM-01` … 実 v19_morning.py の 1-cell wrap（テスト側が独立に読んだ生本文と body byte-for-byte 等値＋
  `class V19MorningStrategy` 包含で非 vacuous）
- `OPEN-NM-02` … wrap 直後に production と同じ `NOTEBOOK_ID` で `RegistryStrategyFileProvider` を引き、
  v19_morning.py の絶対パスが返る（= ▶ ボタンが押せる）。body 編集→false / SaveAs→true で 5 条件の dirty 反転も固定
- `OPEN-NM-03` … wrap → `SaveAs(temp)` → fresh `MarimoNotebookDocument(FailDecompose=false)` で再 Open →
  body verbatim（loss-less 一方向マイグレーション・findings 0054 D2）
- `OPEN-NM-04` … path/IO エラー（empty / wrong ext / file missing）は fail-soft 維持（D4 縮退範囲）

実行: `Unity.exe -batchmode -nographics -quit -projectPath . -executeMethod FileOpenNonMarimoE2ERunner.Run`。
PASS log: `[E2E FILE OPEN NONMARIMO PASS] real v19_morning.py wrapped as 1 cell + run-gate open + lossless SaveAs + path/IO fail-soft preserved`。
delete-the-logic litmus: `MarimoNotebookDocument.Open:149-150` の `?? new List<Cell> { NewCell(content, "_", "{}") }` を
消すと OPEN-NM-01 が確定的に落ちる。

### 層3 pytest（既存契約・回帰防止）

`test_marimo_cell_synthesis_golden.py::test_entry_point_decompose_fail_soft_on_broken_py` は **不変**。
`decompose_json("...not valid python...")` → `None` の契約は seam に残し、wrap は C# 層の policy。

**F2 追加**: `test_marimo_strategy_adapter.py::test_dispatch_surfaces_syntax_error_to_strategy_load_error`
は `_select_replay_strategy` 経路で broken-syntax `.py` を投げ、
`StrategyLoadError("failed to import ...\nSyntaxError: ...")` が
raise されることを assert する。これは §D3a の Run-time SyntaxError surface 契約を
Python 層で locks する（C# 層は既存の `_startError` パイプを再利用するので追加 probe 不要）。
delete-the-logic litmus: `strategy_loader.py:90-93` の `except Exception: raise StrategyLoadError(...)`
を `pass` に潰すと `SyntaxError` が raw で漏れ、テストが確定的に落ちる。

### 層4 HITL

実 workspace で `python/strategies/v19/v19_morning.py` を File→Open → 1 cell window が中央に出現し、
本文がそのまま見える → そのまま Run（imperative 経路で走る）→ 編集後 Save → on-disk が marimo 形式に変換
されていることを owner が確認。（HITL 必須項目ではないが、production assertion として記録する。）

## code-review(simplify) 観点

- 「`Decompose` の null は fail-soft」と「non-marimo は 1-cell wrap」は責務が異なる。前者は seam の契約（broken 入力で
  例外を投げない宣言）、後者は document aggregate の policy（任意の `.py` を編集可能にする）。同じ層で混ぜると
  symbiotic に絡んでテストが弱くなる。集約 Open に `?? new List<Cell> { new Cell(content, "_", "{}") }` の 1 行を
  足すだけで分離が保たれる。
- `LastError` は wrap 後はクリアされる（Open 全体が成功するため）。「non-marimo notebook」の文字列を UI に出さない:
  もう正確でない。

## ADR 判断

新規 ADR は起こさない（可逆・aggregate 1 メソッド + 1 probe section の局所変更・findings 0050 / 0051 の延長線）。
正本は本 findings。ADR-0013 は immutable（書き換えない）。findings 0050 は参照のみ。
