# 0122 — Strategy Editor: Shift+Return でセル再生（Jupyter/marimo 流ショートカット）

**状態**: 設計凍結（grill 2026-06-26・実装前）。著者: owner HITL + grill-with-docs。
**関連**: findings 0116（Enter は改行のまま）/ 0117（Escape は破棄しない・キーポンプ所有）/ 0121（caret 可視）。
CONTEXT.md L515「per-cell RUN」グロッサリーに alias を追記。ADR は不要（キーボード挙動 0116/0117 と同様 findings が正本）。

## 何をするか

コードセル（`StrategyInputField` = `TMP_InputField` / `MultiLineNewline`）にフォーカスがある状態で
**Shift+Return**（および Jupyter notebook の `Ctrl/Cmd+Return`、テンキー Enter 含む）を押すと、
そのセルの **▶ 再生ボタンをクリックしたのと完全に同一**の動作をする。改行は入れない。

## 設計の木（凍結済み下位決定）

### D1 — 中核の挙動: ▶ ボタンと完全同一（owner 決定 2026-06-26）
Shift+Return = ▶ クリックと **完全同一**。
- 実行可否ゲート（`RunReadinessViewModel`: running → no-strategy → invalid-scenario → not-owner）を尊重。
- 実行中（■ 状態）に押したら **停止トグル**になる（`StopRunning()`）。
- **改行は挿入しない**（Jupyter/marimo 流。plain Enter は従来どおり改行）。
- 別案「実行のみ・停止しない」は却下（owner が停止トグルを明示許容）。

### D2 — キー範囲: Jupyter notebook と同じ（owner 決定 2026-06-26）
Jupyter notebook の実行系キーをすべて再生に割り当てる:
- **Shift+Return**（ユーザー明示）
- **Ctrl+Return（Win）/ Cmd+Return（mac）**（Jupyter の "run in place"）
- 上記いずれも **テンキー Enter（KeypadEnter）** を含む。

backcast のセルは**各自が独立した浮遊ウィンドウ**でセル間ナビゲーションが無い
（`NotebookCellCoordinator` / `MarimoNotebookDocument`: ordered `List<Cell>` だが空間的な前後は無い）。
したがって Jupyter の「Shift+Enter=実行して下のセルへ」「Ctrl+Enter=その場で実行」の区別は **構造上 no-op** に collapse し、
3 キーすべて「**フォーカス中セルをその場で実行**」になる。
Alt+Enter（実行して下にセル挿入）は該当機能が無いので **除外**。

### D3 — 検知と改行抑止は「IMGUI キーポンプ」で行う（コードで裏取り）
Undo/Redo（findings 既存）が `StrategyEditorView.Update()` の Input System ポーリング*だけ*で成立するのは、
**Ctrl 押下中のキーがポンプ側で印字されない**から。一方 **Shift+Return はポンプ側で改行として印字される**
（`TMP_InputField.KeyPressed` の `MultiLineNewline` 分岐, TMP_InputField.cs:2263）。
よって Escape（findings 0117 path 2）と同型で、**`OnUpdateSelected` のキーポンプで握りつぶさないと改行が入る**。

→ 検知＋改行抑止＋発火を **すべてキーポンプ内の 1 箇所**で行う（poll と二重シームにしない）。
`StrategyInputField.TryConsumeKeyPumpEscape` の隣に `TryConsumeKeyPumpRun(Event e)` を足し、
`e.rawType==KeyDown && (e.keyCode==Return||KeypadEnter) && (e.shift||e.control||e.command)` で true。
**plain Return（修飾なし）は trigger にせず従来どおり改行**（重要な不変条件）。

### D4 — 再生の経路: フォーカスセルの `btn.onClick.Invoke()`（完全同一の保証手段）
▶ ボタンは状態に応じて onClick を `RunCell(regionId)` ↔ `StopRunning()` に張り替える自己トグル
（`BackcastWorkspaceRoot.SetCellRunButtonState`）。D1「完全同一」を policy 重複なしで保証する最短経路は、
ショートカットが**そのセルのボタンの `onClick.Invoke()` を呼ぶ**こと
（現在張られているリスナ=RunCell か StopRunning がそのまま発火 → owner/ServerReady ゲートも停止トグルも自動一致）。

配線:
- `StrategyInputField` が `Action RunShortcutRequested` を公開し、キーポンプで trigger 時に発火。
- `StrategyEditorView` が `_input.RunShortcutRequested` を購読し、自身の event として relay（`EditCommitted` と同型）。
- `BackcastWorkspaceRoot.WireCellRunButton(windowRoot, regionId)` で、その region の view の relay を購読し
  `_cellRunButtons[regionId]?.onClick.Invoke()` を呼ぶ。フォーカス中セル = 選択中フィールド = その region。

### D5 — キーリピート debounce（held で run→即 stop を防ぐ）
held Shift+Enter は KeyDown を反復送出する。D4 の `onClick.Invoke()` は実行開始で onClick が `StopRunning` に
張り替わるため、**リピートで「実行→即停止」**が起こり得る。
→ **物理 1 押下につき 1 回だけ発火**（latch を張り、Return の KeyUp で re-arm）。
キーポンプは現状 KeyUp を早期 continue するので、Return/KeypadEnter の KeyUp だけは latch クリアに使う。

### D6 — 検証ゲート（behavior-to-e2e で著す）
findings 0116/0117 の STRATEGY-59/60/61 と同型の AFK seam:
- `StrategyInputField` に `RunShortcutConsumedCount` と `TryConsumeKeyPumpRun(Event)` を公開。
- AFK ゲート（新 Section ≒ STRATEGY-63、`StrategyEditorNotebookE2ERunner.cs`）が合成 `Event` を直接食わせて assert:
  - Shift+Return / Ctrl+Return / Cmd+Return / Shift+KeypadEnter → swallow & `RunShortcutRequested` 発火 & counter++。
  - **plain Return → trigger しない（改行のまま）**。
- relay→`onClick.Invoke()` の配線到達も assert（root レベル）。
- 実キーストローク→実 backtest の通しは HITL（STRATEGY-18。-batchmode -nographics に IMGUI ポンプ/フォーカス無し）。

## 触ったファイル（実装時の想定）
- `Assets/Scripts/StrategyEditor/StrategyInputField.cs` — `TryConsumeKeyPumpRun` / `RunShortcutRequested` / `RunShortcutConsumedCount` / latch、`OnUpdateSelected` の KeyDown 分岐に分岐追加。
- `Assets/Scripts/StrategyEditor/StrategyEditorView.cs` — relay event。
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` — `WireCellRunButton` で relay→`onClick.Invoke()` 購読。
- `Assets/Scripts/.../StrategyEditorNotebookE2ERunner.cs` — STRATEGY-63 section。
