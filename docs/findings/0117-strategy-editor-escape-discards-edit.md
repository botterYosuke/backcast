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

## ⚠️ HITL 続報（2026-06-26）— OnCancel 消費だけでは不十分（第二の revert 経路）

owner HITL で「入力 → ESC → Settings dialog 表示 → 内容が消える」が**まだ再現**（治ってない）。
コミット `6ff73ae`（OnCancel 消費）は **2 つある Escape→revert 経路のうち 1 つしか塞いでいなかった**:

1. `ICancelHandler.OnCancel`（`TMP_InputField.cs:4505`）＝ EventSystem の Cancel action 経路。**← 0117 の OnCancel 消費で対応済み。**
2. `OnUpdateSelected`→`KeyPressed`→`case KeyCode.Escape`（`TMP_InputField.cs:2276-2280`）＝ IMGUI 風 Event key pump 経路。
   `m_WasCanceled=true`＋`return EditState.Finish` → 呼び出し元が `DeactivateInputField`（`:2380` 付近）→ `text=m_OriginalText`
   で revert。**← OnCancel 消費では塞がらない残存経路。これが「治ってない」の真因。**

`InputSystemUIInputModule` は Escape を **両経路に**流す（Cancel action＝1／キーボードを IMGUI Event queue に forward＝2）ので、
multiline editor では**両方**を抑止しないと revert が残る。#148（Enter）が OnSubmit だけで足りたのは、経路 2 の Enter が
`MultiLineNewline` 枝で**改行挿入**（deactivate しない・`:2263`）だから——Escape は経路 2 が deactivate+revert なので非対称。

→ 完全修正（経路 2 の抑止）は別 issue で追跡（`StrategyInputField` の `OnUpdateSelected` seam で multiline 時に Escape Event を
握り潰す等）。本 0117 の OnCancel 消費＋Section28 は**経路 1 の回帰ゲートとして有効なので維持**（必要だが不十分）。

## ✅ 経路 2 修正（#150・2026-06-26）— OnUpdateSelected で key-pump Escape を握り潰す

上の「別 issue」が **#150**。経路 2 を `StrategyInputField` に塞いだ。

### 修正

`StrategyInputField`（OnSubmit=#148 / OnCancel=path 1 を override 済み）に **`OnUpdateSelected` override** を追加。
MultiLineNewline のとき IMGUI key pump を**自前所有**し、Escape の key event を `TryConsumeKeyPumpEscape` で
**握り潰して `base.KeyPressed` に到達させない**＝ `m_WasCanceled`/`EditState.Finish`/`DeactivateInputField` の revert+blur が
そもそも起きない。Escape 以外の event は base の pump（`TMP_InputField.cs:2356-2413`）を 1:1 で鏡映して再処理する
（`protected` な `KeyPressed`/`SendOnSubmit`/`SelectAll`/`ForceLabelUpdate` を使用）ので、通常の編集・カーソル移動・
Windows IME は不変。single-line は TMP 既定 pump を維持（検索/名前欄は Esc で cancel してよい）。

```csharp
public bool TryConsumeKeyPumpEscape(Event e)   // multiline かつ Escape KeyDown のときだけ swallow（カウント）
{
    if (lineType == LineType.MultiLineNewline && e != null
        && e.rawType == EventType.KeyDown && e.keyCode == KeyCode.Escape)
    { EscapeKeyPumpConsumedCount++; return true; }
    return false;
}

public override void OnUpdateSelected(BaseEventData eventData)
{
    if (lineType != LineType.MultiLineNewline) { base.OnUpdateSelected(eventData); return; }
    if (!isFocused) return;
    bool consumedEvent = false;
    while (Event.PopEvent(_keyPumpEvent))
    {
        var t = _keyPumpEvent.rawType;
        if (t == EventType.KeyUp) continue;
        if (t == EventType.KeyDown)
        {
            consumedEvent = true;
            if (TryConsumeKeyPumpEscape(_keyPumpEvent)) continue;   // ← THE FIX（Escape を base に渡さない）
            var st = KeyPressed(_keyPumpEvent);
            if (st == EditState.Finish) { if (!wasCanceled) SendOnSubmit(); DeactivateInputField(); break; }
            ForceLabelUpdate(); continue;
        }
        if ((t == EventType.ValidateCommand || t == EventType.ExecuteCommand)
            && _keyPumpEvent.commandName == "SelectAll") { SelectAll(); consumedEvent = true; }
    }
    if (consumedEvent) { ForceLabelUpdate(); eventData?.Use(); }
}
```

**設計上のキモ**:
- `Event.PopEvent` は破壊的 pop で push-back API が無いため、escape だけ抜いて base に渡す事ができない＝multiline は
  pump を**自前所有**するしかない。`UpdateLabel`（`:3503`）は flag 無関係に `m_TextComponent.text` 再代入＋caret 表示を
  常に行い、private `m_IsTextComponentUpdateRequired` は「次フレームの canvas update が必ずやり直す追加 ForceMeshUpdate」
  を 1 回だけ gate するだけなので、`ForceLabelUpdate()` で忠実。
- blur も同時に抑止できる（deactivate 自体が起きない）。Settings dialog は **独立 keyboard poll**
  （`BackcastWorkspaceRoot.DriveSettings` の `kb.escapeKey.wasPressedThisFrame`・EventSystem selection を**奪わない**）で
  開くので、field を active のまま保っても焦点を奪い合わない＝AC「ESC で Settings は出る」を回帰させない。
- 既知の逸脱: OSX の composition 抑止 micro-branch（`:2370-2375`・private `m_IsCompositionActive`/`compositionLength`
  にアクセス不可）は省略。owner は Windows・IME は STRATEGY-18 の HITL に残す。

### ゲート（STRATEGY-61 / Section29）

real keystroke→OnUpdateSelected は headless で駆動不能（IMGUI pump も focus も無い）。よって **production の swallow 述語
`TryConsumeKeyPumpEscape` を直接叩き**、さらに **base の Escape 処理（`ProcessEvent`+`DeactivateInputField`）を「swallow されない
経路でだけ」走らせる非 vacuous モデル**で判定する:

| # | assert | 役割 |
|---|---|---|
| a | 実 builder 産 field の `lineType==MultiLineNewline` | config 不変条件（flip→RED） |
| b | multiline: `TryConsumeKeyPumpEscape(escKeyDown)`→true・`EscapeKeyPumpConsumedCount==1`・swallow したので base モデル未実行＝`text` 保持 | THE FIX＋delete-the-production-logic |
| c | 負: single-line は swallow されず → `ProcessEvent`+`DeactivateInputField` が走り `text` が `m_OriginalText` に revert | 既定 cancel 維持＋**base が実際に revert する事の証明**（b の非 vacuity 担保） |
| d | 負: 非 Escape キー（`KeyCode.A`）は swallow しない・counter 不変 | escape-gated（blanket swallow でない＝typing 不破壊） |

field の「focus 後に編集した」状態は reflection で TMP の activate を最小再現（`m_AllowInput=true`・`m_OriginalText`＝revert 先・
`m_WasCanceled=false`）。`restoreOriginalTextOnEscape` は本修正では**触らない**（既定 true のまま）ので、base モデルの revert が
忠実に再現される。

#### RED→GREEN litmus

- **RED**: `TryConsumeKeyPumpEscape` の `MultiLineNewline` 枝を撤去（常に false）→ Section29 (b) が `!swallowed`→ base モデル
  （`ProcessEvent`+`DeactivateInputField`）が `text=m_OriginalText` に revert → `S29b: multiline edit was lost on Escape` で FAIL・
  rollup から **STRATEGY-61 が消える**。
- **GREEN**: override 適用後 Section29 (a)(b)(c)(d) すべて PASS・`[E2E STRATEGY-61 PASS]`。
- blanket 化（`return true`）の vacuity も (c)（single-line が revert しない）・(d)（非 Escape も swallow）で RED になる。

### 完全修正後の経路マップ

| 経路 | trigger | 抑止 | gate |
|---|---|---|---|
| path 1 | EventSystem Cancel action → `ICancelHandler.OnCancel` | `OnCancel` override で消費（6ff73ae） | STRATEGY-60 / Section28 |
| path 2 | IMGUI key pump → `OnUpdateSelected`→`KeyPressed(Escape)` | `OnUpdateSelected` override で swallow（#150） | STRATEGY-61 / Section29 |

これで multiline code editor の Escape は **両経路とも** revert/blur しない（編集とフォーカスを保持）。実 keystroke の
最終確認は owner HITL（STRATEGY-18・「入力 → ESC → Settings 表示 → 内容が残る」）。

### 再走（#150 後）

```
& .\scripts\run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
# 期待: [E2E STRATEGY-61 PASS]（＋STRATEGY-59/60 含む既存 PASS）、exit 0、error CS 0 件
```

## #148 検証結果（本調査の出発点）

`StrategyEditorNotebookE2ERunner.Run` を実走し `[E2E STRATEGY-59 PASS]`・rollup 7 PASS / 0 FAIL / 0 SKIP・
`error CS` 0 件を確認＝#148 の修正は確実に GREEN。その上で本件（Escape）を同型バグとして発見・修正・gate 化した。

## 関連

- `Assets/Scripts/StrategyEditor/StrategyInputField.cs`（OnSubmit=#148 ＋ OnCancel=path1 ＋ OnUpdateSelected/TryConsumeKeyPumpEscape=path2/#150）
- `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.{cs,md}`（Section28+29 / STRATEGY-60+61）
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`（STRATEGY-01..61 / 行数 61）
- `docs/findings/0116-strategy-editor-enter-stays-newline.md`（#148・姉妹バグ）
- com.unity.ugui@2.0.0 `TMP_InputField.cs:4505-4516`（OnCancel）/ `:4420-4464`（DeactivateInputField の revert・`:4436`）
