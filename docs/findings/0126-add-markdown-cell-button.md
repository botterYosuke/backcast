# 0126 — [m] Add Markdown ボタン（マークダウンセルを spawn する・#179）

**Issue**: #179（GitHub。findings 番号 0126 とは別物——issue#126 は ADR-0026 startup 退役で無関係）。
**状態**: 設計凍結＋実装済み（grill-with-docs・2026-06-27）。レビュー待ち。
**関連**: findings 0123（rich output サンプル・`text/markdown` 描画経路）/ findings 0075（Phase 6 rich output `{mimetype,data}` 契約・per-cell stale）/ findings 0124（種付き New `ObserveSeedBody`）/ ADR-0013（cell-as-floating-window）。
**ADR は不要**: 容易に可逆（小さな additive ボタン）・marimo 本家忠実で驚きが小さい・トレードオフは本 finding に記録（3 条件のうち「hard to reverse」を欠く）。CONTEXT「マークダウンセル / [m] Add Markdown ボタン」項に glossary 化。

## 何をするか

右下の **[+] Add Cell ボタンの真上**に 2 つ目のボタン **[m]** を足す。[m] は **マークダウンを書くための cell**（`mo.md(r"""…""")` 種付き）を 1 枚 spawn する。`/marimo` の「マークダウンセル」に相当する affordance を backcast の cell-DAG オーサリング表面へ足すもの。

## marimo 本家のマークダウンセル（grill で裏取り）

marimo に**マークダウン専用 cell 型は無い**。本体が `mo.md(r"""…""")` の普通の cell を、フロントエンドが「マークダウンセル」として扱う:

- 判定（`_convert/common/format.py::get_markdown_from_cell`）: `cell.refs == {"mo"}` かつ `cell.defs` が空 → `extract_markdown(code)` で中の文字列を取り出す。
- 正準形（`_ast/codegen.py::construct_markdown_call`）: 3 行 `mo.md(r"""` / `<本文>` / `""")`（prefix `r`・三連引用符）。`markdown_to_marimo` が生成。
- フロントエンドの**マークダウン言語アダプタ**が、合致 cell では Python エディタの代わりに**マークダウン直接編集モード**（WYSIWYG・ラッパを隠す）を出す。新規マークダウンセルは**素の `mo`**（別途 `import marimo as mo` cell 前提）を挿入する。

## backcast との差（設計の前提）

backcast の Unity Strategy Editor は**素の Python テキスト欄＋▶ per-cell RUN 出力ペイン**で、本家の WYSIWYG マークダウン編集モードは**無い**。したがって「マークダウンを書く StrategyEditor」=**`mo.md(...)` 種付き cell を開き、▶ で rendered markdown を出す**が現実的な等価物（findings 0123 の `text/markdown` 描画経路を再利用＝net-new 描画ゼロ）。

## 設計の木（凍結済み下位決定）

### D1 — [m] の中身（owner 決定）
`mo.md(r"""…""")` 種を入れた**普通の cell**。[+] と同じ「cell を 1 枚足す」操作で、中身が空 Python の代わりに markdown 雛形になっているだけ。**新 cell 型・新ウィンドウ種は作らない**（KIND_STRATEGY_EDITOR のまま）。

### D2 — 素の `mo`（本家 parity・owner 決定）
種は**素の `mo`**（`_mo` ローカル import ではない）。owner は marimo 本家と同じ形を選択。よって `import marimo as mo` cell が無いと ▶ で `NameError: mo`。
→ **[m] は共有 `import marimo as mo` cell を冪等に用意してから種 cell を足す**。既存に `mo` を定義する cell（行 `import marimo as mo` を含む cell）があれば再利用、無ければ 1 枚追加。
- **却下**: cell 内に `import marimo as mo` を同梱する案 → [m] 2 回押下で `mo` の二重定義になり marimo がエラー（cell-level def はグローバル def）。共有 import cell＝本家の notebook 構成と同じ。
- **却下**: `_mo` 自己完結（findings 0123 サンプルの作法）→ owner が本家 parity を明示選択したため不採用（self-contained で常時 VALID だが本家 `refs=={mo}` 判定に乗らない。backcast にその判定/WYSIWYG は無いので実害は無かったが、owner 選択を尊重）。

### D3 — ▶ で NameError にならない根拠（コードで裏取り）
production の per-cell RUN は `IncrementalNotebookSession.run_pressed`（`notebook_session.py`）で、**押した cell＋stale な上流 ancestors＋reactive 下流**を `compute_cells_to_run(..., "autorun")` で走らせる（docstring L399-402・L471）。
- `mo.md(...)` は `mo` を **ref**、`import marimo as mo` は `mo` を **def** → def/ref エッジで import cell は**上流 ancestor**。
- fresh notebook では import cell は **stale**（足した直後・未実行）→ ▶ 押下で **import cell が先に走り、次に md cell**。`mo` 定義済み。✔
- globals は編集跨ぎで **persist**（`IncrementalNotebookSession` は rebuild しない）ので、以降 md cell を編集・再 press しても `mo` は生存。✔
- メモリ「seed は presence でなく end-to-end VALID で」（[[seed-default-must-be-valid-not-present]]）を、D2 の共有 import cell 担保で満たす。

### D4 — 作成直後は手動 ▶（owner 決定）
[m] は cell を開くだけ。ユーザーが markdown を編集して ▶ を押すと rendered 表示になる（[+] と同じ振る舞い・配線最小）。本家は作成瞬間に rendered だが、それは WYSIWYG フロントエンドの機能で backcast には無い。**自動 ▶ はしない**（coordinator→RunController の追加配線を持たない）。

### D5 — 種文字列
```
mo.md(r"""
# 見出し

本文をここに書く。
""")
```
- 末尾改行なし（cell 本体の正準保存形＝`ObserveSeedBody` と同じ慣習・synth→decompose の byte 冪等を保つ）。
- column-0 保存（synth が `def _():` ラッパと 4-space indent を付与・`mo.md` が dedent して描画）。▶ で即なにか描画される＝D3 の VALID 担保と合わせて「すぐ使える雛形」。

### D6 — 配置と可視性
- [m] は [+] と同じ `_addCellOverlay`（screen-space overlay・bottom-right）に同居し、[+] の**真上**（y を 1 段上げる）。
- 同じ overlay の子なので **`LiveManual` 非表示**を [+] と共有（findings 0110・`_addCellOverlay.SetActive` 1 箇所が両方を制御）。グリフは `m`。

## 実装スコープ（この finding の正本）

既存 seam で完結。`MarimoNotebookDocument.AddCell(body=…)` は**既に種本体を受ける**ので document 変更は不要。spawned cell 窓は既に ▶ を得る（`BackcastWorkspaceRoot.WireCellRunButton`）ので run 配線も不要。

1. **`NotebookCellCoordinator`**: 定数 `MoImportBody = "import marimo as mo"` と `MarkdownSeedBody`（D5）、メソッド `AddMarkdownCell()`（① `mo` を def する cell が無ければ **窓付き coordinator `AddCell(MoImportBody)`** で 1 枚足す ② `AddCell(MarkdownSeedBody)` で種 cell を窓化・前面化・`ListMutated`）。**import cell は `_notebook.AddCell`（窓無し・raw document seam）ではなく窓付き `AddCell` で足す**——独自の窓/▶/✕ を得て tracked になり、`AddCell`/`SyncWindowsToNotebook`/`Open` の cell↔window 全単射が保たれ `CapturePositions` が窓無し cell を見ない（実装で発覚した正しい形・元の「`_notebook.AddCell`」記述は誤り）。冪等 import 担保＝既存 `_notebook.Cells` の本体を走査して `import marimo as mo` を**定義する行**の有無で判定（C# は def/ref 解析を再実装しない＝text scan・[[ttwr-parity-first]]）。判定は **regex `^import\s+marimo\s+as\s+mo\b`**（whitespace 変種・trailing comment・combined import `import marimo as mo, pandas as pd` を許容＝false-negative で二重定義しない）＋ **`mo.md(` で始まる markdown cell は skip**（本文/コードフェンス中の `import marimo as mo` を誤検出して real import を抑止しない＝false-positive 回避）。残余: 非 markdown コードcell の文字列内に同行があるケースは text-scan の限界（marimo の def/ref が run-time の正本）。
2. **`BackcastWorkspaceRoot`**: `BuildAddCellButton` 内に [m] ボタンを追加（[+] の真上）し `OnAddMarkdownCell` → `_coordinator?.AddMarkdownCell()` を配線。

## ゲート（behavior-to-e2e で著した・着地済み 2026-06-27）

- **Python**（`python/tests/test_notebook_markdown_cell.py`・`@pytest.mark.marimo`）: 種 cell（`MarkdownSeedBody`）＋共有 import cell（`MoImportBody`）を `IncrementalNotebookSession.run_pressed` で per-cell RUN すると、押した md cell の **stale 上流 ancestor（import cell）が autorun で先に走り**、md cell が `text/markdown` を産み見出しが出る（findings 0123 の `test_rich_output_sample` と同型 seam）。**RED→GREEN**: import cell を抜いて md cell を単独 press すると `NameError`（`test_litmus_bare_mo_without_import_cell_nameerrors`）＝D3 の delete-the-production-logic litmus。3 tests GREEN。
- **AFK**（`StrategyEditorNotebookE2ERunner` Section33 / **STRATEGY-66**）: 実 `NotebookCellCoordinator`（bare-RT harness・Section12 同型）で `AddMarkdownCell()` が (a) `mo.md(` 種 cell＋**窓付き** import cell 1 枚を用意（両方 windowed＝bijection）(b) [m]×2 で import cell が増えない (c) combined import を再利用（duplicate def 無し）(d) markdown 本文中の import 行を誤検出しない、を coordinator primitive 直叩きで gate。litmus は `.md` の自動判定節（`EnsureMoImportCell` 撤去→(a) RED／窓無し `_notebook.AddCell` に戻す→(a) windowed RED／`DefinesMoImport` を旧 line-exact に戻す→(c)/(d) RED）。
- rollup: 両ゲートとも Action-ID（pytest=`marimo` marker／runner=`[E2E STRATEGY-66 PASS]`）で `run-all-tests.ps1` rollup に載る。

## 未決・HITL 残

- **実画素レンダ**（rendered markdown が窓に見える）は findings 0123 と同じく HITL（headless で TMP SDF mesh は検証不能・STRATEGY-18）。AFK は coordinator の冪等・種窓化まで、Python は payload 産出＋no-NameError まで。
- **ボタン click→`OnAddMarkdownCell` の UI 配線**（実 EventSystem raycast）は [+] ボタンと同様 AFK 非対象（headless に実 click 無し）＝コード読解＋共有 `BuildOverlayButton`（[+]/[m] が styling/lifecycle を分岐しない）で担保。実 [m] クリックの spawn 目視は owner HITL。
