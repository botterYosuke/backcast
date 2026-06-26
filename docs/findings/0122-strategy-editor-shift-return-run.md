# 0122 — Strategy Editor: コードセルを Shift+Return で再生（Jupyter/marimo 流ショートカット・#164）

## 要望（owner 2026-06-26）

Strategy Editor のコードセル（`StrategyInputField` = `TMP_InputField` / `MultiLineNewline`）にフォーカスがある状態で
**Shift+Return** を押すと、そのセルの **▶ 再生ボタンをクリックしたのと完全に同一**に動作してほしい（Jupyter notebook /
marimo 流のセル実行ショートカット）。Jupyter 準拠で **Ctrl+Return（Win）/ Cmd+Return（mac）**、および各キーの
**テンキー Enter（KeypadEnter）** も同じ再生に割り当てる。**改行は挿入しない**（plain Enter は従来どおり改行）。

関連: findings 0116（Enter は改行・OnSubmit 消費）/ 0117（Escape 破棄しない・OnCancel 消費＋key-pump 所有）。本件は
findings 0117 の **path 2（IMGUI key-pump 所有）と完全に同型の継ぎ目**に乗る——Escape を握り潰したのと同じ pump で、
今度は「修飾付き Return を握り潰して run を発火する」。

## 設計の凍結決定（grill 2026-06-26・コード裏取り済み）

### D1 — 完全同一（▶ クリックと寸分違わぬ挙動）

ショートカットは ▶ ボタンの **`onClick.Invoke()`** を呼ぶ＝再生ポリシーを一切複製しない。`WireCellRunButton` が張る
onClick リスナは `if (_isOwner && _host.ServerReady) _notebookRun?.RunCell(regionId)` で**実行可否ゲートを内包**し、
`SetCellRunButtonState(running:true)` が onClick を `StopRunning()` に張り替える**自己トグル**（▶↔■）なので、
`onClick.Invoke()` は「アイドルなら RunCell・実行中なら StopRunning」を自動的に履行する。よって完全同一が
**policy 重複ゼロ**で保証される（実行中（■）に押せば停止トグル）。

> 注: D1 が言及する `RunReadinessViewModel` は **title-bar Run（#95 P6 で退役）** の readiness brain であり、
> per-cell RUN の実ゲートは onClick リスナ内の `_isOwner && _host.ServerReady`。`onClick.Invoke()` がこれを継承する
> ので、ショートカットは現行ボタンが課す可否ゲートをそのまま尊重する。

### D2 — キー範囲

Shift+Return ＋ Ctrl+Return（Win）/ Cmd+Return（mac）＋ 各キーの **テンキー Enter（`KeyCode.KeypadEnter`）**。セルは
独立浮遊ウィンドウでセル間ナビが無いので、Jupyter の「下のセルへ（Shift）／その場で（Ctrl/Cmd）」区別は構造上
no-op に collapse し、全部「フォーカス中セルをその場で実行」。Alt+Enter（下にセル挿入）は該当機能が無いので除外。

### D3 — 検知/抑止の場所（pump 1 箇所・poll と二重化しない）

Undo/Redo（`StrategyEditorView.Update()`）は Input System ポーリングで成立する（Ctrl 押下中キーはポンプで非印字）が、
**Shift+Return はポンプ側で改行印字される**（`TMP_InputField.cs:2263` の MultiLineNewline 枝が Return を改行挿入）ため、
Escape（findings 0117 path 2）と同型で **`StrategyInputField.OnUpdateSelected` の IMGUI key-pump で握りつぶす**必要がある。
**検知＋改行抑止＋発火を pump 内 1 箇所**で行い、`Update()` poll と二重シームにしない。**plain Return（修飾なし）は
trigger にせず改行のまま**（pump で base.KeyPressed に渡して改行を挿入させる）。

IMGUI `Event.modifiers`（`EventModifiers.Shift|Control|Command`）は forward された Return KeyDown に乗る——これは base
TMP pump が既に Shift+Return を識別している経路と同じ。よって pump 内で `e.keyCode ∈ {Return, KeypadEnter}` かつ
修飾フラグありを「run shortcut」と判定できる。

### D4 — 再生の経路（フォーカスセルのボタンへ relay）

▶ ボタンは状態に応じて onClick を `RunCell`↔`StopRunning` に張り替える自己トグル。完全同一（D1）を policy 重複なしで
保証するため、ショートカットは**フォーカスセルのボタンの `onClick.Invoke()`** を呼ぶ。配線:

```
StrategyInputField.RunShortcutRequested  (key-pump が発火)
   → StrategyEditorView.RunShortcutRequested  (relay: field の event を再公開)
   → BackcastWorkspaceRoot.WireCellRunButton(regionId) が購読
   → _cellRunButtons[regionId]?.onClick.Invoke()
```

`StrategyEditorView` は `_input` を `TMP_InputField` 型で持つが実体は `StrategyInputField` なので、`Initialize` で
`_input as StrategyInputField` を購読し（`OnDestroy` で解除）、自身の `RunShortcutRequested` へ relay する。root は
`WireCellCloseButton` / `WireCellRunButton` と同じ箇所で view（`ViewFor(regionId)`）の event を購読する。view は
spawn 1 回・1 view あたり 1 購読（adopted region_001 は never-Destroy で起動時 1 回・spawned は窓ごとに新 view）なので
二重購読しない。

### D5 — リピート debounce（物理 1 押下 = 1 発火）

held Shift+Enter で「run→即 stop」が起きないよう **1 物理押下 = 1 発火**。`_runShortcutArmed` latch を張り、修飾付き
Return/KeypadEnter の KeyDown で armed なら発火＋disarm、**Return/KeypadEnter の KeyUp で re-arm**。held 中の KeyDown
リピートは（armed=false なので）再発火しないが、**swallow は latch 無関係に常時**行う（held でも改行を撒かない）。

### D5 への補足 — swallow と発火の分離

`TryConsumeKeyPumpRun(Event e)` の戻り値 `true/false` は「この KeyDown を base に渡さず握り潰すか」を表す。修飾付き
Return/KeypadEnter の KeyDown は**常に true（swallow）**＝改行抑止。発火（`RunShortcutConsumedCount++` ＋
`RunShortcutRequested?.Invoke()`）は **armed のときだけ**。KeyUp（Return/KeypadEnter）は re-arm の副作用だけ起こして
false を返す（base pump の `if (KeyUp) continue;` がそのまま処理）。plain Return の KeyDown は false＝改行へ。

### D6 — 検証（AFK seam）

findings 0116/0117 の STRATEGY-59/60/61 と同型。production の swallow/発火述語 `TryConsumeKeyPumpRun(Event)` と
counter `RunShortcutConsumedCount` を公開し、合成 `Event` を直接食わせる **新 Section31 ≒ STRATEGY-63**:

| # | assert | 役割 |
|---|---|---|
| a | 実 builder 産 field の `lineType==MultiLineNewline` | config 不変条件（flip→RED） |
| a2 | `StrategyInputField` が `OnUpdateSelected` を override（wiring 構造ガード） | 述語が pump から呼ばれている保証（findings 0117 S29a と同型） |
| b | Shift+Return / Ctrl+Return / Cmd+Return / Shift+KeypadEnter 各 KeyDown → `TryConsumeKeyPumpRun`=true（swallow）・各 1 発火で counter が累積 | THE FIX＋キー範囲 D2 |
| c | plain Return（修飾なし）KeyDown → false（**非 swallow＝改行**）・counter 不変 | plain Enter は改行（D3）・非 vacuity |
| d | held: 同じ Shift+Return KeyDown 連打 → swallow は続くが発火は 1 回（latch）／Return KeyUp で re-arm → 次の KeyDown で再発火 | debounce D5 |
| e | relay 到達: field.`RunShortcutRequested` 購読 → 合成 Event 投入で購読側が呼ばれる（StrategyEditorView relay → onClick.Invoke 等価・**手で WireCellRunButton を鏡映**） | 配線 D4（field→view relay の単体） |
| g | **実 `BackcastWorkspaceRoot` を合成（S25 harness）して production の `WireCellRunButton` を実走**: `BuildWorkspace` 後 `ViewFor(region_001).RunShortcutRequested != null`（配線済み）＋実 field の Shift+Return が `_cellRunButtons[region_001].onClick` に到達 | 配線 D4（root→button assignment の回帰ガード・(e) の hand-mirror 穴を塞ぐ） |
| f | 負: single-line は swallow されず（修飾付き Return でも false）・counter 不変 | swallow は multiline 限定（blanket でない） |

> (e) は field→view relay を単体で固定するが `WireCellRunButton` を手で鏡映するので production の assignment（`BackcastWorkspaceRoot.cs:1043-1049`）が壊れても緑のまま——この穴を **(g)** が実 root 実走で塞ぐ（review 2026-06-26）。実 IMGUI pump が `TryConsumeKeyPumpRun` を**呼ぶ**こと自体は headless にフォーカス/Event queue が無く実走不可で、(a2) 構造ガード＋ STRATEGY-18 HITL に委ねる残（sibling S27–S30 と同じ境界）。

実キーストローク→実 backtest の通しは HITL（**STRATEGY-18**・batchmode に IMGUI ポンプ/フォーカス無し）。

#### RED→GREEN litmus

- **RED**: `TryConsumeKeyPumpRun` の run 判定本体（`MultiLineNewline ∧ Return/KeypadEnter ∧ 修飾`）を撤去（常に false）
  → (b) が `!swallowed`／counter 0 → `S31b: modified Return was NOT consumed/fired` で FAIL・rollup から STRATEGY-63 が消える。
- **GREEN**: 実装適用後 (a)(a2)(b)(c)(d)(e)(f) すべて PASS・`[E2E STRATEGY-63 PASS]`。
- blanket 化（修飾チェック撤去で plain Return も swallow）は (c) が「plain Return が改行にならず swallow された」で RED。
- multiline ゲート撤去（single-line も swallow）は (f) が RED。
- latch 撤去（held で毎フレーム発火）は (d) が「連打で counter > 1」で RED。

## 実装ファイル

- `Assets/Scripts/StrategyEditor/StrategyInputField.cs`
  （`RunShortcutRequested` / `RunShortcutConsumedCount` / `TryConsumeKeyPumpRun` ＋ key-pump 配線・1-press latch）
- `Assets/Scripts/StrategyEditor/StrategyEditorView.cs`（relay）
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`（`WireCellRunButton` で購読→`onClick.Invoke()`）
- `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.{cs,md}`（Section31 / STRATEGY-63）
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`（STRATEGY-01..63 / 行数 63）
- `CONTEXT.md`（per-cell RUN グロッサリーに Shift+Return alias 追記）

## 再走

```
& .\scripts\run-live-e2e.ps1 -CompileOnly                              # error CS 0 件
& .\scripts\run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
# 期待: [E2E STRATEGY-63 PASS]（＋STRATEGY-59/60/61/62 含む既存 PASS）、exit 0、error CS 0 件
```

## 関連

- `docs/findings/0116-strategy-editor-enter-stays-newline.md`（#148・Enter=改行・OnSubmit 消費）
- `docs/findings/0117-strategy-editor-escape-discards-edit.md`（#148/#150・Escape 保持・**key-pump path 2 所有の原型**）
- com.unity.ugui 2.0.0 `TMP_InputField.cs:2255-2263`（多行 Return=改行）/ `:2356-2413`（base OnUpdateSelected pump）
