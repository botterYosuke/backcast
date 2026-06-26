# 0117 — Strategy Editor: Escape を押すと編集が全部消えて編集が終了する（#148 の姉妹バグ）

## 発見の経緯（2026-06-26）

#148（Enter が改行にならず編集が終了する／findings 0116）の修正が確実に治ったかを検証する過程で、owner が
「ソースコードエディタとして致命的な不具合が他にないか」を確認。**#148 と完全に同型の継ぎ目**（新 Input System ×
TMP_InputField の EventSystem handler dispatch）に、もう一つの致命的バグが残っていた。

#148 は EventSystem の **Submit** action（Enter）だったが、本件は **Cancel** action（Escape）。Submit handler を
潰しても Cancel handler は別経路なので残っていた。

## 症状

Strategy Editor のコードセルにフォーカスして編集中に **Escape を押すと、focus してから打った編集が全部消え、
編集が終了する**（フォーカスが外れる）。Enter の blur（#148）より重い——**サイレントなデータ消失**。

## 根本原因（コード調査で一意確定）

- production の EventSystem は `BackcastWorkspaceSceneBuilder.cs:47` で `InputSystemUIInputModule`（新 Input System・
  既定 actions）。その既定 `Cancel` action は **Escape にバインド**されている（#148 の `Submit`=Enter と同じ既定 actions）。
- フォーカス中に Escape を押すと、module は `ICancelHandler.OnCancel` をフォーカス中の field に dispatch する。
- `TMP_InputField.OnCancel`（`com.unity.ugui@2.0.0/Runtime/TMP/TMP_InputField.cs:4505-4516`）は
  `m_WasCanceled = true` を立てて `DeactivateInputField()` を呼ぶ。
- `DeactivateInputField`（`:4420-4464`）は `:4436` で `if (m_WasCanceled && m_RestoreOriginalTextOnEscape && …) text = m_OriginalText;`
  ——`m_RestoreOriginalTextOnEscape` は既定 `true`（`:854`）。
  → Escape で **focus 時点のテキストに revert ＋ blur**（focus 後の編集が全消失・編集終了）。

アプリ側に Escape を先取りして field の Cancel を抑える handler は無い（`BackcastWorkspaceRoot.cs:1932` 等の
`kb.escapeKey.wasPressedThisFrame` poll は EventSystem dispatch とは独立で、focus 中 field の OnCancel は依然 dispatch される）。

## 修正（owner 決定 2026-06-26：Esc は何もしない＝編集を続ける）

`StrategyInputField`（#16 以来の「editor 固有 input 挙動の seam」・#148 で OnSubmit を override 済み）に
`OnCancel` を override し、**MultiLineNewline のとき cancel を消費**（revert/deactivate しない・編集とフォーカス保持）。
single-line は既定の cancel/revert を維持（検索/名前欄は Escape で破棄してよい）。#148 の OnSubmit fix と完全な鏡像。

```csharp
public override void OnCancel(BaseEventData eventData)
{
    if (lineType == LineType.MultiLineNewline)
    {
        CancelConsumedCount++;
        eventData?.Use();   // handled — Escape must not revert/blur the multiline code editor
        return;
    }
    base.OnCancel(eventData);
}
```

`CancelConsumedCount` は AFK 可観測 seam（`SubmitConsumedCount` と同型の "probe observability" field）。

## ゲート（behavior-to-e2e 2 ゲート分割）

| 半分 | gate | 内容 |
|---|---|---|
| 決定論（AFK） | `StrategyEditorNotebookE2ERunner` Section28 / **STRATEGY-60** | 実 builder 産 field で (a) `lineType==MultiLineNewline`、(b) `((ICancelHandler)field).OnCancel(evt)`→`CancelConsumedCount==1`（消費＝revert/deactivate せず）、(c) 負コントロール single-line `CancelConsumedCount==0`（消費は multiline 限定）。`[E2E STRATEGY-60 PASS]` |
| 実キーストローク（HITL） | **STRATEGY-18**（`Strategy Editor HITL`） | 実 Escape で編集が消えず・フォーカスが残る。`-batchmode -nographics` には EventSystem focus が無いので owner 目視 |

real keystroke→revert/focus は headless で駆動不能なので HITL に分離（純 C# の OnCancel 決定だけを AFK が pin）。

### RED→GREEN litmus

- **RED**: `StrategyInputField.OnCancel` override の本体（`if (lineType==MultiLineNewline){…return;}`）を撤去
  → base TMP `OnCancel` が `m_WasCanceled`＋無条件 deactivate（focus 済みなら revert）→ `CancelConsumedCount` は
  増えず 0 → Section28 (b) が `multiline OnCancel did NOT consume the Cancel …` で FAIL。
- **GREEN**: override 適用後、Section28 (a)(b)(c) すべて PASS・`[E2E STRATEGY-60 PASS]`。

delete-the-production-logic を通る（override を消す＝バグが戻る＝gate が落ちる）。負コントロール (c) で
「全 cancel を握り潰す」vacuous 化も防ぐ（single-line は消費しない）。

**実走で RED→GREEN を実証（2026-06-26）**:
- **GREEN**（override 適用）: `[E2E STRATEGY-60 PASS] multiline code editor consumes the EventSystem Cancel …`・
  rollup `8 PASS / 0 FAIL / 0 SKIP`（STRATEGY-53..60）・`error CS` 0・exit 0。
- **RED**（override の consume 枝をコメントアウト）: `[E2E STRATEGY NOTEBOOK FAIL] S28b: multiline OnCancel did
  NOT consume the Cancel …`・rollup から **STRATEGY-60 が消えて `7 PASS`**（失敗で手前 abort＝per-id タグ未到達＝
  「id 不在＝そのステップ未到達」の規約どおり）。→ 復元で GREEN に戻ることを確認。

## 再走

```
& .\scripts\run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
# 期待: [E2E STRATEGY-60 PASS]（＋STRATEGY-59 含む既存 PASS）、exit 0、error CS 0 件
```

## #148 検証結果（本調査の出発点）

`StrategyEditorNotebookE2ERunner.Run` を実走し `[E2E STRATEGY-59 PASS]`・rollup 7 PASS / 0 FAIL / 0 SKIP・
`error CS` 0 件を確認＝#148 の修正は確実に GREEN。その上で本件（Escape）を同型バグとして発見・修正・gate 化した。

## 関連

- `Assets/Scripts/StrategyEditor/StrategyInputField.cs`（OnSubmit=#148 ＋ OnCancel=本件 fix）
- `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.{cs,md}`（Section28 / STRATEGY-60）
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`（STRATEGY-01..60 / 行数 60）
- `docs/findings/0116-strategy-editor-enter-stays-newline.md`（#148・姉妹バグ）
- com.unity.ugui@2.0.0 `TMP_InputField.cs:4505-4516`（OnCancel）/ `:4420-4464`（DeactivateInputField の revert・`:4436`）
