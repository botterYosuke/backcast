# findings 0071 — #95 Phase 2「土台」: per-cell RUN（純粋計算 reactive 実行）の設計＋ゲート

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）。本 findings は **#95 Phase 2** の下位決定（ADR が「Phase 2 / 6 の findings に固定」と委譲した *全 cell 窓の RUN ボタン C# 配線詳細* と *出力捕捉 seam*）を会話で消えないように固定する。ADR-0016 / findings 0070 / ADR-0012 / ADR-0013 は immutable（書き戻さない）。

> Phase 2 のスコープ（ADR-0016 D11 / D2）: **すべての cell 窓（adopted `region_001` ＋ spawned `region_002+`）に RUN ボタンを持たせ、押すと押した cell＋reactive 下流が DAG 順で純粋計算（engine 非接続）として再実行され、出力が窓にテキストで出る**。`bt` ハンドル（Phase 3）・実 backtest 駆動（Phase 4/5）・実行状態 UI / rich output（Phase 6）は **範囲外**。現行の title-bar `StrategyEditorRunButton`（旧 single Run）の撤去も **Phase 6 sunset** であり Phase 2 では退役しない（ADR-0016 D2 は supersede を宣言するが UI 撤去は Phase 6）。

---

## owner HITL（2026-06-20）— RUN の対象 = 「編集中の内容をそのまま」

設問: 「セル窓の RUN ボタンを押したとき、何を走らせるか？」

**owner 判断: 編集中の内容をそのまま走らせる（保存不要）**。marimo 本来の挙動に最も近く、探索（土台）層の UX として正しい。

帰結（binding）:
- per-cell RUN は **保存（Save）を前提にしない**。`MarimoNotebookDocument` の供給可能 5 条件（bound∧not-dirty∧…）は **engine backtest run（golden 再現性のため disk 由来が必須）専用の契約**であり、純粋計算の per-cell run には課さない。
- C# は press のたびに **live cell から `.py` ソースを合成**（既存 `IMarimoSynthesizer.Synthesize(live cells)`）してソース文字列を Python へ渡す。disk パスは使わない（engine run の path-based seam とは別経路）。
- **却下**: 「保存済みファイルの内容を走らせる（毎回 Save 必須）」— 実装は path-based ブリッジ流用で軽いが、探索のたびに Ctrl+S を強いる UX で土台層の telos に反する。

## 設計の木（Phase 2 下位決定）

### P2-1 — Phase 2 の正体は marimo `Runner` の interactive 駆動（findings 0070 F1 を実装）
「土台」= `HeadlessKernel` の reuse では届かない（F1）。実体は **既 import の marimo `Runner`（`thin_drain.py:68` = `marimo._runtime.runner.cell_runner.Runner`）を、押した cell を root に in-proc 駆動し、出力を捕捉して窓に出す net-new 配線**。HTTP サーバーも自前 DAG エンジンも書かない（F2）。

- 新規 module `engine/strategy_runtime/notebook_session.py`（marimo を top-import するが、`_backend_impl` からは **lazy-import** ＝ offline import-purity 不変・F4 / ADR-0012 §4）。
- `Runner.compute_cells_to_run(graph, {pressed}, set(), "autorun")` が「押した cell ＋ reactive 下流（＋ stale 上流）」を topo 順で返す＝AC「下流再計算 / 上流非依存 cell は再計算されない」の構造的根拠（pressed の子孫でない cell は list に入らない）。
- 出力捕捉: `Runner(roots=…, execution_context=kernel._install_execution_context, …)` で各 cell を実行（`run_all()`）。`RunResult.output`（last-expr 値）＋ `RunResult.accumulated_output`（`mo.output.replace/append`）を text に整形（`marimo._output.formatting.try_format` → text/plain、非 text は `repr`）。`HeadlessKernel`（thin_drain）の RuntimeContext を流用（`_execute_hot_cell` と同じ「kernel 実行 context 内で動かす」規律・D6）。

### P2-2 — 持続カーネル・1 worker スレッド・RUN は順番待ち（findings 0070 F5 / ADR-0016 D9）
marimo Kernel/RuntimeContext は **スレッドローカル**（thin_drain.py:152）。よって:
- **1 ノートにつき marimo Kernel を 1 個だけ持続**させ、**専用 worker スレッド 1 本**（Unity 本体とは別）で build も run も行う（同一スレッド不変条件）。これが「engine は worker に 1 個」（D9）＝Phase 3+（`bt`）と同じ土台。
- ソースが変わったら（編集後の press）**そのスレッド上で session を rebuild**（旧 HeadlessKernel teardown → 新規 → 全 cell cold-run で graph + globals 確立 → 押した cell を root に Runner 駆動）。cold-run 済みなので上流は stale でなく、press は「pressed＋子孫」だけに絞られる。ソース不変なら session を再利用（globals 持続）。
- replay 走行中（Phase 4）の別 RUN は **順番待ち**（エンジン=1=1 スレッド）。Phase 2 の純粋計算は速いが、同じ lane に乗せて将来の running guard と整合させる。

> live-edit の staleness を「編集した上流 cell を stale 表示・押すまで再計算しない」と忠実再現するのは Phase 6（per-cell idle/running/stale UI）。Phase 2 は rebuild-on-change で**正しい結果**を出すが、編集 cell は rebuild の cold-run で 1 度走る（出力は press した cell＋子孫のぶんだけ窓へ返すので、押していない cell の表示は変わらない）。決定論的純粋計算なので結果は同値。

### P2-3 — C# 配線（X ボタンと同型）
- **`StrategyEditorWindowFrame.EnsureRunButton(windowRoot, font)`**: idempotent find-or-create（`EnsureCloseButton` と同型）。title bar 内、X の左隣に ▶。adopted（serialized scene が先行）と spawned の両方で同じボタンになる。
- **per-cell 出力ペイン**: cell 窓 body を「エディタ（上）＋ 出力テキスト（下）」に分割。`StrategyEditorContentBuilder` が両方を組み、`StrategyEditorView.SetOutput(text)` で出力を反映。窓ごとに `ViewFor(region)` で引けるので index→region→view の経路で出力を配る。
- **`NotebookRunLane`**: 専用 worker スレッド 1 本＋request/result の thread-safe キュー。`INotebookCellExecutor`（`NotebookRunResult Run(string source, int pressedIndex)`）を注入。実 impl は `WorkspaceEngineHost` 経由で `Py.GIL()` + `_server.InvokeMethod("run_cell", source, index)`。root が `Update()` で result を drain し index→region で各窓へ配る。**executor を注入可能にすることで AFK probe は Python-FREE な fake を差せる**。
- 押下経路: `EnsureRunButton.onClick → root.OnRunCell(regionId) → 該当 Cell の index = Notebook.IndexOf(cell) ＋ live source 合成 → lane.Submit(req) → [worker] executor.Run → result enqueue → Update drain → ApplyRunResult（index→region→view.SetOutput）`。

### P2-4 — Python backend メソッド（marimo-free seam を守る）
`_backend_impl.DataEngineBackend.run_cell(self, source: str, pressed_index: int) -> dict` を追加し `inproc_server` / `backend_service` 経由で公開。**marimo / notebook_session の import は メソッド内 lazy**（`_backend_impl` は offline gate が import する seam ＝ module-load で marimo を引いたら RED）。戻り値 `{"ok": bool, "ran": [{"index": int, "output": str}], "error": str|null}`。

## ゲート（RED→GREEN）

| 層 | gate | 観測（AC） |
|---|---|---|
| Python seam | `python/tests/test_notebook_interactive_run.py`（pytest・`@pytest.mark.marimo`） | ① 押した cell の出力捕捉（last-expr ＋ `mo.output`）② **下流が再計算される**（pressed の子孫が ran に入り再評価）③ **上流非依存 cell は再計算されない**（pressed の子孫でない cell は ran に入らない・recorder の呼び出し回数で証明）④ ソース変更で rebuild、不変で再利用 |
| Python drift | `test_notebook_interactive_run.py::test_marimo_private_api_drift_gate`（新 seam に co-located・ADR-0012 §3。0070 F1 が示唆した「thin_drain test に追加」より、Runner interactive 経路＋出力捕捉を *使う* 本 seam の隣に置く方が結束的） | marimo upgrade で `Runner.__init__` params / `Runner.run_all` / `RunResult.accumulated_output` / `CellOutputList.stack` / `try_format` / `Kernel._install_execution_context` 等が rename されたら RED |
| Python purity | `test_strategy_runtime_offline.py`（既存・無改変で GREEN 維持） | `_backend_impl` import で marimo が漏れない（run_cell の lazy-import を構造的に強制） |
| C# / Unity | `StrategyEditorNotebookE2ERunner` に **STRATEGY-19 / STRATEGY-20** section 追加（Python-FREE・fake executor） | 19: **adopted＋spawned の両窓に RUN ボタンが在る**（`EnsureRunButton` find-or-create）20: **press → fake 出力が *その窓* の出力ペインに出る**（index→region 配線・press region_002 が region_001 を更新しない） |

**RED→GREEN litmus**:
- `EnsureRunButton` を消す → STRATEGY-19 RED。
- index→region 配線（`ApplyRunResult` の routing）を「常に region_001 へ」に壊す → STRATEGY-20 RED（press region_002 が region_001 を更新してしまう）。
- `compute_cells_to_run` の autorun を「pressed 単独」に縮める → pytest ② RED（下流が ran に入らない）。
- pressed の子孫判定を「全 cell」に広げる → pytest ③ RED（上流非依存 cell の recorder が回る）。
- `run_cell` を `_backend_impl` の module-top で `import marimo` → offline gate RED。

## 実装着地＋code-review 反映（2026-06-20）

全ゲート GREEN で着地: pytest 11（output 捕捉・下流再計算・独立 cell 不変・rebuild/reuse・fail-soft・thread-guard・drift gate）＋ AFK `StrategyEditorNotebookE2ERunner` S13（STRATEGY-19/20/20c）PASS ＋ C# compile 0 error。`/code-review` high で検出した Medium を全て修正:

- **in-flight result の誤 routing**（press 後 File→Open/New で notebook が差し替わると stale result が新 doc の同 index cell に塗られる）→ **generation token**: `NotebookRunRequest/Result.Generation`・controller の `_generation`・`Invalidate()`（root が New/Open の *成功時* に呼ぶ＝fail-soft Open では呼ばない）・`ApplyResult` が gen 不一致を drop。AFK S13c で litmus 化。
- **thread-local kernel の無防備な singleton 再利用** → `NotebookSession` に owner-thread assert（2nd thread は fail-closed・pytest 化）。
- **`_load_app_from_source` が `cell_synthesis.decompose_json` の temp-file ロジックを複製** → `cell_synthesis.load_app_from_text` に抽出して両者で共有。
- 軽微: GIL「main 常時 free」の過大コメントを正直化（Python work は GIL 直列）・非 owner press の無駄な main-thread synthesize を `_isOwner && ServerReady` で guard・dead `try_format` import 除去・stale `OnRunCell` コメント修正・unmappable index を Python 側で filter。

## 本 finding が**やっていない**こと（Phase 2 範囲外）
- `bt` ハンドル / 実 backtest 駆動 / golden parity（Phase 3–5）。
- per-cell idle/running/stale 表示・block popup・`mo.md`/table/chart rich output（Phase 6）。
- 現行 title-bar `StrategyEditorRunButton`（旧 single Run）の撤去（Phase 6 sunset）。
- live-edit staleness の忠実再現（編集 cell を押すまで stale 保持）。Phase 2 は rebuild-on-change で同値結果（Phase 6 で staleness UI）。
