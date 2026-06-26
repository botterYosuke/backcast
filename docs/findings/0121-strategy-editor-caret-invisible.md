# 0121 — Strategy Editor: コードセルで点滅カーソル（caret）が見えない（#149・#119 TMP/SDF 移行後の回帰）

## 症状（owner HITL 2026-06-26）

Strategy Editor のコードセルにフォーカスして編集できる状態でも、**テキストカーソル（caret）が見えない**。
文字入力・Backspace は効く（＝フォーカスはできている）が、編集位置を示す点滅カーソルが**どの zoom でも**
描画されない（owner スクショ＝Game Scale 1.3× で `aaaaaa`/`b`/`c` が白く表示されているのに caret 無し）。
`StrategyInputField` は #119（findings 0096 D5）で `UnityEngine.UI.InputField` → `TMP_InputField` へ移行しており、
その移行で発生した回帰。

## 症状からの初動仮説は外れた（記録）

issue 本文の仮説（caretWidth / customCaretColor / caretColor の既定依存）と「zoom で細くなりすぎる」説を最初に
検討したが、**いずれも primary cause ではなかった**:

- 既定 `caretWidth=1` は MIN_ZOOM 0.2× で sub-pixel になり得る（AC#2 の zoom 視認性に**は**関係する）が、owner は
  **「いつも見えない」（拡大表示 1.3× でも見えない）**と回答 → zoom-thinness は primary ではない。
- caret 色は `customCaretColor=false` の既定で `textComponent.color`（テーマ text 色・alpha=1 不透明）に追従する。
  `PythonSyntaxMeshEffect` は per-glyph の `colors32` だけ書き換え `textComponent.color` には触れない → caret 色は
  正しく不透明。
- `textViewport` は builder（`StrategyEditorContentBuilder.cs`）が割当済み（RectMask2D 在）。

＝**AC 記載の 3 不変条件（width>0・alpha>0・textViewport 割当）は既定のままで既に満たされていた**ので、それらを
額面で assert する gate は **vacuous（既に GREEN）**で不具合を検出できない。「症状で真因 class を当てるな」の典型
（owner に AskUserQuestion 1 問＝「いつも／zoom 時だけ」で primary cause の class を切った）。

## 根本原因（コード調査で一意確定）

`TMP_InputField` は caret の CanvasRenderer（`m_CachedInputRenderer`＝子 GameObject "Caret"）を
**たった 1 箇所、`OnEnable` でのみ生成する**（`com.unity.ugui@2.0.0/Runtime/TMP/TMP_InputField.cs:1172`）。しかも
`m_CachedInputRenderer == null && m_TextComponent != null` のときだけ。caret mesh を描く `GenerateCaret` の経路は
renderer が null だと早期 return（`UpdateGeometry`/`SetMesh` 経路 `:3769`）＝**caret は一切描かれない**。

`StrategyEditorContentBuilder.Build` は `StrategyInputField` を
`new GameObject("StrategyCodeInput", …, typeof(StrategyInputField), …)` の**コンストラクタ**で足す。Play mode では
この時点で `OnEnable` が**同期的に**走る——だが `input.textComponent = text;` は**その次の行**で配線される。よって
**最初の（唯一の）OnEnable が `textComponent==null` で走り**、caret renderer は生成されず、以後の再 enable も無いため
**caret は永久に不可視**（文字入力は caret renderer 不要なので効く＝症状に完全一致）。

## 修正

### (1) 根本原因＝OnEnable の順序（`StrategyEditorContentBuilder.cs`）

**編集器サブツリーを INACTIVE で組み、textComponent を field の最初の enable より前に配線**する。`StrategyInputField`
を GameObject コンストラクタの type list から外し、inputGo を `SetActive(false)` してから子（TextArea/Text/
Placeholder）と field を組んで配線し、最後に `SetActive(true)` で OnEnable を**textComponent ありで 1 回だけ**走らせる
（Play mode→caret CanvasRenderer 生成）。これは「activate 前に依存を満たす」Unity の prefab 流儀そのもの＝事後の
re-cycle（`enabled=false→true`・余分な OnEnable(null) を残す）より深い altitude（simplify レビュー Altitude/Efficiency
両エージェントが指摘・OnDisable/OnEnable の churn と redundant な最初の OnEnable(null) を構造的に消す）。

```csharp
// StrategyInputField を constructor type list から外す
var inputGo = new GameObject("StrategyCodeInput",
    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
inputGo.transform.SetParent(body, false);
inputGo.SetActive(false);                 // ← 最初の enable より前に配線するため inactive で組む
// … 子（TextArea/Text/Placeholder）を組む …
var input = inputGo.AddComponent<StrategyInputField>();   // inactive → premature OnEnable なし
input.textViewport = areaRt;
input.textComponent = text;               // … lineType/characterLimit/richText …
input.caretWidth = CaretWidthPx;          // (2) zoom 生存（既定 1 は 0.2× で sub-pixel）
input.customCaretColor = true;            // (2) 明示・不透明（text 色の dim に従わない）。色は ApplyTheme が所有
inputGo.SetActive(true);                  // ← THE FIX: OnEnable が textComponent ありで 1 回走る → caret 生成
```

`OnDisable`（`:1233`）が `m_CachedInputRenderer` を破棄せず `.Clear()` するだけ、という TMP 仕様も裏取り済み
（再 enable でも一度作った renderer は残る）が、本修正は**そもそも最初の OnEnable で textComponent を見せる**ので
再 enable に頼らない。

### (2) 明示 caret 構成（zoom 生存＋色の堅牢化）

- `caretWidth = CaretWidthPx`（=6・builder の `public const`）。solid caret quad は InfiniteCanvas zoom
  （`Content.localScale`）でスケールするので既定 1 は MIN_ZOOM 0.2× で sub-pixel。`CaretWidthPx` は 0.2–5× で
  視認できる太さ（HITL tunable・gate は `>1` だけを構造的に pin）。
- `customCaretColor = true`。caret が将来の text 色 dim / recolour にサイレントに従わないよう明示固定（`textComponent.color`
  への自動追従を切る）。**caret 色の所有は `StrategyEditorView.ApplyTheme` 1 箇所に集約**（`_input.caretColor = c.text(α=1)`）。
  `ApplyTheme` は `Initialize` から（Build が返る前に）1 回呼ばれ、以後テーマ切替ごとにも呼ばれるので、builder は caretColor を
  重ねて seed しない（同フレームで上書きされる dead code になるため）——builder が持つのは `caretWidth` と `customCaretColor`
  フラグだけ（ApplyTheme はこの 2 つを触らない）。

## ゲート（behavior-to-e2e 2 ゲート分割）

| 半分 | gate | 内容 |
|---|---|---|
| 決定論（AFK） | `StrategyEditorNotebookE2ERunner` Section30 / **STRATEGY-62** | 実 builder 産 field で (a) `lineType==MultiLineNewline`、(b) `textViewport` 割当＋RectMask2D 在、(c) **`OnEnableCount>0` かつ `TextComponentReadyAtLastEnable`**（最新 OnEnable が textComponent を見た＝caret 生成の前提条件・**THE FIX**）、(d) `caretWidth>=CaretWidthPx`（`CaretWidthPx>1`）、(e) `customCaretColor` ＆ `caretColor.a>0`、(f) 非 vacuity（textComponent 無しで enable した control は false）。`[E2E STRATEGY-62 PASS]` |
| 実カーソル（HITL） | **STRATEGY-18**（`Strategy Editor HITL`） | 実際にコードセルにフォーカス→編集位置に点滅 caret が見える・InfiniteCanvas zoom 0.2–5× でも視認できる |

**なぜ実 caret は HITL か**: caret CanvasRenderer の生成は `OnEnable` 内で `if (Application.isPlaying)`-gated
（`TMP_InputField.cs:1170`）。`-batchmode -nographics` の AFK は EditMode（`isPlaying==false`）なので caret renderer は
そもそも生成されない（TMP caret/mesh trap・findings 0096）。だが **OnEnable 自体は EditMode でも走る**ので、gate は
caret 生成が依存する**正確な前提条件**（「最新 OnEnable が non-null textComponent を見た」）を pin できる。
`StrategyInputField` に observability seam（`OnEnableCount` / `TextComponentReadyAtLastEnable`・`SubmitConsumedCount`
等と同型）を足し、OnEnable override で記録する。

### RED→GREEN litmus（実走で実証 2026-06-26）

- **RED**（fix 前・field を active で構築し配線前に enable していた当時のコード）: 実 builder 産 field の唯一の
  OnEnable（コンストラクタ時）が `textComponent==null` で走った → `TextComponentReadyAtLastEnable==false` →
  `[E2E STRATEGY NOTEBOOK FAIL] S30c: the editor field's latest OnEnable saw a NULL textComponent → TMP never
  creates its caret CanvasRenderer (TMP_InputField.cs:1172) → caret invisible at every zoom.`。
  ＝**不具合を決定論ハーネスで再現**（owner HITL「いつも見えない」を機械が裏取り）。`OnEnableCount>0` で
  「OnEnable が走らなかったから false」という偽陰性も排除。
- **GREEN**（fix 後）: build-inactive で field の最初の OnEnable が textComponent ありで走り
  `TextComponentReadyAtLastEnable==true`・`caretWidth=6`・`customCaretColor`・全 assert PASS →
  `[E2E STRATEGY-62 PASS]`、rollup `[PASS] STRATEGY-62`・`10 PASS / 0 FAIL / 0 SKIP`、exit 0、`error CS` 0。

delete-the-production-logic を通る（builder を field active 構築＝配線前 enable に戻すと S30c が RED）。非 vacuity:
(c) は textComponent 無し control が false ＝定数 true でない、(d) は既定 1<6 で RED、(e) は既定 `customCaretColor=false`
で RED。AC 記載の他不変条件（textViewport・alpha>0）は既定で満たされる**前向きガード**として併置（regression 用）。

### 他 TMP_InputField を壊さない（AC#4）

修正は `StrategyEditorContentBuilder`（編集器のみを組む）＋`StrategyInputField.OnEnable` override（base.OnEnable を
呼ぶだけの observability 追記＝無害）に閉じる。Unity 通常経路で組まれる他 field（textComponent が prefab で enable 前に
配線済み）は無改変。既存 STRATEGY-59/60/61（Enter/Escape）も全 PASS で回帰なし。

## 再走

```
& .\scripts\run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
# 期待: [E2E STRATEGY-62 PASS]（＋STRATEGY-59/60/61 含む既存 PASS）、exit 0、error CS 0 件
```

## 関連

- `Assets/Scripts/StrategyEditor/StrategyInputField.cs`（#149 OnEnable observability＝`OnEnableCount`/`TextComponentReadyAtLastEnable`）
- `Assets/Scripts/StrategyEditor/StrategyEditorContentBuilder.cs`（fix＝編集器サブツリーを inactive で組み textComponent 配線後に `SetActive(true)` ＋ `CaretWidthPx`/`customCaretColor`・caret 色は ApplyTheme 所有）
- `Assets/Scripts/StrategyEditor/StrategyEditorView.cs`（`ApplyTheme` で caretColor 同期）
- `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.{cs,md}`（Section30 / STRATEGY-62）
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`（STRATEGY-01..62 / 行数 62）
- com.unity.ugui@2.0.0 `TMP_InputField.cs:1172`（caret renderer 生成・OnEnable のみ・textComponent gated）/ `:1170`（isPlaying gate）/ `:3769`（null renderer 早期 return）/ `:1233`（OnDisable は renderer 破棄せず Clear）
- findings 0096（#117–121 TMP/SDF 移行・TMP mesh trap）/ 0116（#148 Enter）/ 0117（#150 Escape・同型の継ぎ目）
