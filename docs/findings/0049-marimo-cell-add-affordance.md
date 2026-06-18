> ⚠️ **SUPERSEDED / 前提誤り（2026-06-18 owner 指摘）**: 本 findings は「Strategy Editor = 単一テキストバッファ、[+] は
> `@app.cell` を buffer に**追記**」という**誤った前提**で書かれている。正しいモデルは **`1 cell = 1 Strategy Editor`**
> （marimo `cell-array.tsx`: 各 cell が独立エディタの縦スタック）。[+] は**新しい cell-editor を生成**すべきで、テキスト追記ではない。
> やり直しの引き継ぎ: `%TEMP%\handoff-issue81-cell-as-editor.md`。本文 Q1–Q8 は誤前提の上に立つため大半が無効。

# findings 0049 — #81 strategy editor: marimo cell 追加 UI（画面右下 [+]）

issue #81（#76 / ADR-0012 follow-up・S6b 非ブロッキング）。
方針: **ADR-0012（target authored モデル = marimo cell-DAG）/ findings 0010（Strategy Editor 編集コア）/
findings 0044（WYSIWYR）/ findings 0046（marimo-embed thin-drain runtime）/ findings 0025 §8（adopt 不変）**。
移植元 affordance = marimo frontend `edit-app.tsx` AddCellButtons（3D モードの画面固定 [+]）。
`grill-with-docs`（2026-06-18）で Q1–Q8 を導出。backcast に FLOWS.md は無いため本 findings が設計＋検証の正本。
ADR-0012 は参照のみ（書き戻さない）。

## スコープ

Strategy Editor 上で marimo `@app.cell` skeleton を最小操作で挿入する画面固定 [+]。**任意バッファ（空含む）を
runnable marimo へ bootstrap する**ことに閉じる。**非目標**: full notebook UI / セル個別実行・出力 / live reactive 統合 /
命令型戦略の削除 / File→New 既定を marimo テンプレに変える（= S6b の責務）。

## 設計台帳（Q1–Q8・実ソース裏取り付き）

### Q1 — [+] の帰属：画面右下に固定した1個（per-window ではない）
本番編集エディタは1枚に固定（adopt 済み `strategy_editor:region_001`）。File 系（New/Open/Save/Save As）が全部
`WINDOW_ID = "strategy_editor:region_001"` 直結で2枚目を引く経路が無い（`BackcastWorkspaceRoot.cs:42,309,404,1568`）。
よって「どのエディタへ挿すか」の問いは生じず、移植元 marimo（1ノートブック・画面固定1個）と同型。

### Q2 / Q3b — 挿入アンカー：run-guard の手前／無ければ EOF（フッタ検知付き）
dispatch は `marimo._ast.load.load_app`（marimo 構造化パーサ・`_backend_impl.py:443,452`）で `.py` を app 化する。
`parse_body` は `is_run_guard`（`if __name__ == "__main__": app.run()`）で**収集を break**（`marimo/_ast/parse.py:656-678,
1031-1033`）。**run-guard より後ろの `@app.cell` は無登録で silent に消える**（verified silent corruption）。アンカーを
marimo の収集終端と一致させ、appended セルが必ず収集されるようにした。今日の backcast 生成形（フッタ無）では EOF と一致。

### Q3a — skeleton：`def _():` ＋ 全 `_` ローカル ＋ `_qty = 0.0` 既定 no-op ＋ 3 API 全部入り
反復挿入で marimo の **multiple-definition** に当たらないよう、全代入を `_` プレフィックス（**cell-local**）にした。
marimo `is_local`（`marimo/_ast/variables.py:62`: 単一 `_` 始まり・ダンダー `__` は除外）が per-cell に mangle するので、
同一 skeleton を N 回挿しても衝突しない（**ダンダーにしてはいけない**）。`_qty = 0.0` 既定は `submit_market` で no-op
（`cell_api.py:55`: `0.0`/`-0.0` は無発注）＋ cold compile では inert stub seed（`thin_drain` / `marimo_strategy.py:61-64`）
の二重安全＝**足しただけでは絶対に誤発注しない**。未使用 `_bar`/`_pf` は **意図した discoverability seed**（ruff F841 は
`_` 始まりを除外＝lint クリーン）。canonical 自由参照形（引数ではなく `# noqa: F821` 付き free ref）は
`test_marimo_strategy_adapter.py:43-49` 準拠。セル間は **2 空行**（marimo 正準）。

### Q3c — bootstrap：空→生成 / marimo→append / 非 marimo→拒否（三分岐）
`@app.cell` はモジュールレベル `app`（`import marimo` + `app = marimo.App()`）が無いと無効。三分岐:

| バッファ | [+] |
|---|---|
| 空／空白のみ | **Bootstrapped**: ヘッダ＋最初のセルを生成（新規エディタ→[+]→save/run 可） |
| marimo app（検知 True） | **Appended**: アンカーへセル append |
| 非空かつ非 marimo | **RefusedNotMarimo**: 無変更＋ notice「cell 追加は marimo 戦略が対象です」 |

C# 検知 `MarimoCellInsert.LooksLikeMarimoApp` は Python 権威 `is_marimo_app_source`（`strategy_kind.py:48-93`）の
**保守的ミラー**（モジュールレベルの `import marimo` ＋ `App(...)` 構築の両方／**迷ったら marimo 扱いせず拒否**）。
破壊的 false-positive（命令型へ append → dispatch fork で dead code 化）を構造的に潰す。drift は層3共有 fixture で機械検出。

### Q4 / Q5 — 通知＝`ShowMessage` 流用 / 実行中も [+] 通す
notice は `_menuBarView.ShowMessage`（既存 File 操作と同一面・`BackcastWorkspaceRoot.cs:311,316,322`）。
編集は **run-gate されない**（`:739` の `if (_host.IsRunning) return;` は OnRun 二重起動ガード・編集ブロックではない／
`StrategyEditor/` に readOnly/interactable/IsRunning 連動なし）。[+] append は**バッファ編集**バケツ（タイプ編集と同扱い）
＝実行中も通す・無効化しない。WYSIWYR（findings 0044）どおり次の Run が拾う。marimo の `isAppInteractionDisabled` parity は
live-reactive 前提なので転用不可。

### Q6 — 単一 undo Tx / dirty / `0.0` 選択 / 挿入後フォーカス
`StrategyEditorView.InsertMarimoCell` は純粋 `MarimoCellInsert.Build` の結果を**プログラム的 paste**として既存経路に乗せる。
多文字変更は `EditHistory.Classify` で一律 `Standalone`（`EditHistory.cs:168`）＝直前タイプ run と merge されず・自分も独立
＝**Ctrl+Z 一発で挿入前へ**。must-do 2点: ①挿入後にスナップショット `_prevText/_prevAnchor/_prevFocus` を新状態へ更新
（怠ると次タイプが stale baseline で Record）②`Record` の after-caret = 実際に set する選択（redo が同じ選択を復元）。
`_qty = 0.0` の `0.0` を**選択状態**にして即上書き可（snippet tab-stop）。フォーカスは host が reveal 後に `FocusSelection`。

### Q7 — 画面固定 chrome / Content 前面・modal 背面 / 挿入成功時 reveal
[+] は **runtime-built**（HITL で owner が (B) を選択 2026-06-18：当初 scene-authored 案だったが、シーン再生成の手間を避け
Play しただけで出る方式に変更。secret modal と同じく `BackcastWorkspaceRoot.BuildAddCellButton` が chrome Canvas（footer の
parent）直下・bottom-right・footer の上に Play 時生成し onClick→`OnAddCell` を配線）。位置はコード定数で調整可。
z 帯 = Content 前面・secret modal 背面（secret modal は別 front canvas）。`region_001` は adopt 不変（findings 0025 §8）で常に存在・隠せるが
破棄されない。挿入成功時に `FloatingWindowController.Show(WINDOW_ID)` で reveal＋raise（**`Show = SetActive(true) +
SetAsLastSibling` の2手**——`BringToFront` は `SetActive` を呼ばない・`FloatingWindowController.cs:141`）。RefusedNotMarimo は
ウィンドウ操作なし（hidden 状態尊重・notice のみ）。

### Q8 — 4層検証
1. 純粋 AFK（`StrategyEditorProbe.Section10_CellInsertPure`）: 三分岐・run-guard アンカー・反復スペーシング・`0.0` 選択・検知ミラー。
2. view AFK（`Section11_CellInsertView`・実 InputField）: 単一 undo・dirty・InputField mirror・**Save ラウンドトリップ**・refuse は clean。
3. pytest 共有 fixture（`test_marimo_cell_insert_golden.py`・`python/tests/fixtures/marimo_cell_insert/`）: C# `Build` 出力＝golden を
   両側が読む。`is_marimo_app_source` True ＋ `load_app` がセルを**収集**（footer ケースで appended セルが drop されない実証）。
4. HITL: 実 workspace で右下 [+] クリック→focus＋`0.0` 選択＋スクロール、reveal-on-insert、[+] で足したセルを Replay run（AC5）。

注: behavior-to-e2e は Bevy/gRPC（TTWR）専用で backcast には FLOWS.md 無し。#81 は機能追加（bug-fix RED ではない）。

## 成果物

- `Assets/Scripts/StrategyEditor/MarimoCellInsert.cs`（純粋コア・UnityEngine-free）
- `Assets/Scripts/StrategyEditor/StrategyEditorView.cs`（`InsertMarimoCell` / `FocusSelection`）
- `Assets/Scripts/FloatingWindow/FloatingWindowController.cs`（`Show(id)`）
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`（`BuildAddCellButton` runtime 生成 / `OnAddCell`）
- `Assets/Editor/StrategyEditorProbe.cs`（Section10/11）
- `python/tests/test_marimo_cell_insert_golden.py` ＋ `python/tests/fixtures/marimo_cell_insert/`

## code-review(high) ハードニング（2026-06-18）

- **docstring 偽陽性（修正済み）**: `LooksLikeMarimoApp`/`RunGuardAnchor` は triple-quote（`"""`/`'''`）region を
  跨いでスキップする。実 `import marimo` ＋ モジュール docstring 内に例示 `marimo.App()` を持つ命令型ファイルが
  marimo 誤判定 → `@app.cell` append → 未定義 `app` 参照で破壊、を防ぐ（Python AST 権威は Constant 扱い）。probe S10j。
- **単一行 run-guard（修正済み）**: `IsRunGuardHeader` は `if __name__ == "__main__": app.run()`（インライン body）も
  アンカーとして認識（`EndsWith(":")` → `IndexOf(':')>=0`）。手書き単一行フッタの後ろにセルが落ちて `load_app` に
  収集されない silent drop を防ぐ。probe S10k。
- **`Inserted` フォールバック（修正済み）**: `_qty = ` 不在時は length=3 のゴミ選択ではなく collapsed caret を返す。
- **共有ヘルパ抽出**: `StrategyEditorView.ApplyTextAndSelection` を undo/redo（`ApplyHistoryState`）と
  `InsertMarimoCell` で共有（InputField↔document↔snapshot 結合の単一所有）。`FloatingWindowController.Show` は
  `BringToFront` を合成（raise 意味論を1箇所に）。
- **既知の safe-direction parity gap（未修正・無害）**: ① `from marimo import App as X` ＋ bare `X()` ②
  `app = (\n  marimo.App()\n)` の括弧跨ぎ構築 — どちらも C# ミラーは**過剰拒否**（notice のみ・corruption なし）。
  Python 権威は受理する。canonical 形（`import marimo` / `app = marimo.App()`）では生じない。必要になれば asname 追跡を足す。

## 検証状況（2026-06-18）

- 層3 pytest: `uv run --group spike pytest tests/test_marimo_cell_insert_golden.py` → **6 passed**
  （`test_append_before_run_guard_is_collected` が run-guard アンカーの実証＝appended セルを load_app が収集）。
- `Build` の byte-exactness: 純粋ロジックを Python へ port し golden 3 本と一致を確認（**ALL OK**）。
- 層1/2 Unity AFK probe（`StrategyEditorProbe.Run` / 6000.4.11f1 headless）: **実機 PASS**（exit 0・`[STRATEGY EDITOR PASS]` に
  `marimo cell-insert #81`（Section10/11）を含む・`error CS` 0 件＝新規 `.cs` コンパイル成功・既存 Section1–9 も GREEN＝AC3 リグレッションなし）。
- 層4 HITL（実 workspace で右下 [+]→focus→`0.0` 選択→Replay run・AC5）: **owner 実行待ち**。
