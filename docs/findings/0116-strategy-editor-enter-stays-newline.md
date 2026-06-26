# 0116 — Strategy Editor: Return が改行にならず編集が終了する（#148）

## 症状（owner 報告 2026-06-26）

Strategy Editor のコードセルにフォーカスして **Return を押しても改行が入らない**。owner HITL 確認:

- 通常文字（`abc` 等）の入力・Backspace は**正常**（Return だけ効かない）。
- Return を押すと**フォーカスが外れて編集が終了**する（カーソルが消える／blur）。

## 根本原因（コード調査で一意確定）

production の構成自体は「Enter=改行」になるべき正しい構成だった——単純な config 回帰ではない:

- `StrategyEditorContentBuilder.cs:126` … `input.lineType = MultiLineNewline`（#16 以来不変・#119 TMP 移行でも維持）。
- `StrategyInputField.cs` … 空サブクラス（Enter を奪う key handler 無し）。
- `StrategyEditorView.Update()` … Undo/Redo のみ。アプリ全体に Enter/Submit を消費するグローバルハンドラ無し（grep 0 件）。

真因は **新 Input System × TMP_InputField の継ぎ目**:

1. production の EventSystem は `BackcastWorkspaceSceneBuilder.cs:47` で `InputSystemUIInputModule`（新 Input System）。
   その既定 actions の **`Submit` は Enter にバインド**されている。
2. フォーカス中に Enter を押すと、module は IMGUI key pump とは**別に** `ISubmitHandler.OnSubmit` を dispatch する。
3. `TMP_InputField.OnSubmit`（`com.unity.ugui@2.0.0/Runtime/TMP/TMP_InputField.cs:4484-4503`）は
   **`DeactivateInputField()` を無条件で呼ぶ**——`MultiLineNewline` フィールドを除外しない（`:4501`）。
   → Enter で editor が blur（focus 喪失・編集終了）。
4. 改行自体は `OnUpdateSelected` の key pump（`TMP_InputField.cs:2255-2263` の MultiLineNewline 枝）が
   挿入するが、3. の deactivate が同フレームで focus を奪うため、ユーザーには「改行できず編集が終わる」と見える。

owner 症状（他文字は打てる＝key pump は生きている／Return だけ blur＝Submit action 経路）と完全一致。

## 修正

`StrategyInputField`（#16 以来「将来の editor 固有 input 挙動の seam」として用意された空サブクラス）に
`OnSubmit` を override し、**MultiLineNewline のとき submit を消費**（deactivate しない・focus 保持）。
single-line は既定の submit/deactivate を維持（Enter で commit する用途を壊さない）。

```csharp
public override void OnSubmit(BaseEventData eventData)
{
    if (lineType == LineType.MultiLineNewline)
    {
        SubmitConsumedCount++;
        eventData?.Use();   // handled — Submit は multiline code editor を blur してはならない
        return;
    }
    base.OnSubmit(eventData);
}
```

改行の挿入は TMP の key pump がそのまま行う（こちらは「余計な deactivate を止める」だけ＝最小・idiomatic）。

## ゲート（behavior-to-e2e 2 ゲート分割）

| 半分 | gate | 内容 |
|---|---|---|
| 決定論（AFK） | `StrategyEditorNotebookE2ERunner` Section27 / **STRATEGY-59** | 実 builder 産 field で (a) `lineType==MultiLineNewline`、(b) `((ISubmitHandler)field).OnSubmit(evt)`→`SubmitConsumedCount==1`（消費＝deactivate せず）、(c) 負コントロール single-line `SubmitConsumedCount==0`（消費は multiline 限定）。`[E2E STRATEGY-59 PASS]` |
| 実キーストローク（HITL） | **STRATEGY-18**（`Strategy Editor HITL`） | 実 Return が見た目に改行を入れ、フォーカスを保つ。`-batchmode -nographics` には IMGUI key pump / 実フォーカスが無いので owner 目視 |

`SubmitConsumedCount` は AFK 可観測 seam（production の OnSubmit を実呼びして「消費枝を通ったか」を assert）。
real keystroke→focus は headless で駆動不能なので HITL に分離（純 C# の OnSubmit 決定だけを AFK が pin）。

### RED→GREEN litmus

- **RED**: `StrategyInputField.OnSubmit` override の本体（`if (lineType==MultiLineNewline) {…return;}`）を撤去
  → base TMP `OnSubmit` が無条件 deactivate → `SubmitConsumedCount` は増えず 0 → Section27 (b) が
  `multiline OnSubmit did NOT consume the Submit …` で FAIL。
- **GREEN**: override 適用後、Section27 (a)(b)(c) すべて PASS・`[E2E STRATEGY-59 PASS]`。

delete-the-production-logic を通る（override を消す＝バグが戻る＝gate が落ちる）。負コントロール (c) で
「全 submit を握り潰す」vacuous 化も防ぐ（single-line は消費しない）。

## 再走

```
& .\scripts\run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
# 期待: [E2E STRATEGY-59 PASS] と既存 STRATEGY-* PASS、exit 0、error CS 0 件
```

## 関連

- `Assets/Scripts/StrategyEditor/StrategyInputField.cs`（fix）
- `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.{cs,md}`（Section27 / STRATEGY-59）
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`（STRATEGY-01..59 / 行数 59）
- com.unity.ugui@2.0.0 `TMP_InputField.cs:2255-2263`（多行 Return=改行）/ `:4484-4503`（OnSubmit 無条件 deactivate）
