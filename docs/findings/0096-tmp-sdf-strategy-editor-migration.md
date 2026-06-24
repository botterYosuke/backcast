# 0096 — Strategy Editor の TMP(SDF) 移行（#117–121 設計の木）

InfiniteCanvas のズーム（`Content.localScale` 0.2–5×）で Strategy Editor のテキストがぼやける根本原因は、
legacy uGUI `Text`/`InputField`＋ダイナミックフォントを transform スケールで拡大していること。グリフは表示
ピクセルサイズで atlas にラスタライズされ、親 localScale を考慮しないため拡大でビットマップが引き伸ばされる。
TMP(SDF) はシェーダが輪郭を再構成するため transform スケール非依存で鮮明。本エピック（#117–121）はこの移行。

## 方針（grill 確定・load-bearing 決定）

- **D1 フォント = Cascadia Mono（SIL OFL 1.1）の SDF `TMP_FontAsset`**。owner 選択（OFL 等幅を新規採用）。
  システムの `C:\Windows\Fonts\CascadiaMono.ttf` を `Assets/Fonts/` に複製（OFL.txt 同梱・再配布可）。
  hard-to-reverse（フォント binary＋atlas パラメータをコミット）＝採用根拠を本 finding に固定。
  - atlas: samplingPointSize 90 / padding 9 / 1024² / `GlyphRenderMode.SDFAA` / `AtlasPopulationMode.Dynamic` /
    multiAtlas。SDF は解像度非依存なので 5× でも 0.2× でも鮮明。Dynamic は将来の CJK/IME fallback を
    re-bake 無しで chain できる（owner は CJK fallback 無しを選択＝現状 IME/CJK レンダリングは HITL のまま）。
  - 置き場: `Assets/TextMesh Pro/Resources/Fonts & Materials/CascadiaMono SDF.asset`（Resources 配下）。
    runtime/probe ロードパス＝`Fonts & Materials/CascadiaMono SDF`（`TmpFoundationSetup.CascadiaSdfResourcesPath`）。
- **D2 TMP Essential Resources を headless import**（`TMP_PackageResourceImporter.ImportResources(true,false,false)`）。
  ⚠️ `AssetDatabase.ImportPackage` は **async**＝`-quit` が import 完了前に終了すると `TMP_Settings.instance` が
  null で `CreateFontAsset` が落ちる。`importPackageCompleted` callback を待ってから font asset 生成（`-quit` 無し
  で callback から `EditorApplication.Exit`）。`Assets/Editor/TmpFoundationSetup.cs` が正本（idempotent）。
- **D3 console / text-plain の rich-text エスケープ = TMP `<noparse>`**（UGUI の `<`→`&lt;` から変更）。
  理由: TMP は entity を decode しない（`&lt;` を**そのまま**表示＝regression）。TMP の正道は `<noparse>…</noparse>`
  で囲んで一切パースさせ、OUR `<color=#ffa01c>` だけ外側に置く。user payload 中の literal `</noparse>` は
  guard を閉じてしまうので zero-width break で無害化。`&` は UGUI 同様**触らない**（findings 0079 D6 を継承）。
- **D4 構文ハイライト = TMP `textInfo.meshInfo[].colors32` の per-glyph 書換**（`OnPreRenderText`／ForceMeshUpdate 後）。
  TMP は `BaseMeshEffect` を使わないので `PythonSyntaxMeshEffect`（旧 `ModifyMesh`）を作り直す。rich-text タグを
  本文に挿入しない設計意図（caret/選択/index 不変）は維持。可視行ウィンドウは TMP の `firstVisibleCharacter`/
  `maxVisibleCharacters` から再導出。複数 material（fallback グリフ）時の meshInfo 分割に対応。
- **D5 編集面 = `TMP_InputField`＋`TMP_Text`**。legacy `InputField` の `m_DrawStart`（`VisibleDrawStart`）は TMP に
  無いので、可視行ウィンドウは TMP_InputField の textComponent から取り直す。text-sync コア（onValueChanged→
  SetBody→dirty・EditHistory・undo/redo・blur→restage・MultiLineNewline）は AFK 担保、caret/選択/IME は HITL。

## 採番の訂正（issue 本文の stale）

- #121 本文は「findings 0093 に RED→GREEN」と書くが、**0093 は #122（in-proc tkinter login）が既に使用済み**。
  TMP zoom 回帰ゲートは **本 finding 0096** に記録する（#121 実装時に §gate を追記）。

## スライス分解（issue 順）と HITL 境界

| slice | 範囲 | headless 担保（AFK/probe） | HITL（owner） |
|---|---|---|---|
| #117 | TMP Essential + Cascadia SDF asset | compile clean・asset 生成・既存 E2E 不変 | — |
| #118 | 出力ペイン（rich/console）TMP_Text | Section20/21 を TMP assert へ更新・amber/noparse・空折りたたみ/cap/ScrollRect | 5× ズーム鮮明（スクショ） |
| #119 | 編集面 TMP_InputField+TMP_Text・placeholder | text-sync/undo/redo/blur/MultiLineNewline（StrategyEditorNotebook） | caret/選択/IME 合成 |
| #120 | 構文ハイライト TMP recolor | token グリフ色≠base・Default 不変（probe を TMP 版へ） | スクロール整合 |
| #121 | zoom 鮮明性 回帰ゲート | TMP/SDF 経路 assert・transform スケール非依存・delete-the-logic litmus | 5× スクショ添付 |

## #117 着地（実装済み・GREEN）

- `Assets/Editor/TmpFoundationSetup.cs`（headless 2-in-1: essentials import → SDF asset 生成）。
- `Assets/Fonts/CascadiaMono.ttf` + `Assets/Fonts/OFL.txt`、`Assets/TextMesh Pro/…/CascadiaMono SDF.asset`。
- 見た目・挙動・既存 E2E 不変（描画方式は #118 まで legacy のまま）。compile clean（`error CS` 0）。

## #118 着地（実装済み・GREEN）

- 出力ブロックの `rich.Text` / `console.Text` を `TextMeshProUGUI`（SDF・Cascadia）へ。`StrategyEditorContentBuilder
  .BuildOutputBlock` が `EditorTmpFont()`（`Resources.Load("Fonts & Materials/CascadiaMono SDF")` ＋ TMP default
  fallback）でロード。`OutputBlock.Text` 型 → `TMP_Text`。`textWrappingMode=Normal`／`overflowMode=Overflow`／
  `TextAlignmentOptions.TopLeft`（Obsolete な `enableWordWrapping` は不使用）。
- `StrategyEditorView`: `_output`/`_consoleText` → `TMP_Text`、`Initialize` 引数型を `TMP_Text` に、`supportRichText`
  → `richText`。**console エスケープを `<noparse>` 方式へ**（`NoParse()`／literal `</noparse>` は ZWSP で break、
  `&` 不変）。markdown/html 投影（`<b>/<i>/<color>/|`）は TMP もパースするので不変。
- probe: `StrategyEditorNotebookE2ERunner` の STRATEGY-34（`<EOF>`→`<noparse>` で literal 生存・`&lt;` 不在）と
  STRATEGY-45（user `</color>` は `<noparse></color>middle</noparse>` で guard・amber opens==2）を TMP semantics へ
  更新。**AFK GREEN**（`[E2E STRATEGY NOTEBOOK PASS]` exit=0）。編集面 Text/InputField は #119 まで legacy のまま。
- HITL 残（owner）: 5× ズームで出力テキストが鮮明（スクショ）。

## #119 / #120 / #121 実装ガイド（次スライス・設計の木は凍結済み）

> **コードリーディングで判明した設計精緻化（#120 を単純化）**: legacy `InputField` は focus 中に textComponent の
> **文字列を可視行ウィンドウ `[m_DrawStart, m_DrawEnd)` に truncate** するため、mesh effect は `displayStart` オフセットを
> LIVE に足して full-source index を復元していた（`StrategyInputField.VisibleDrawStart`／findings 0010 §8-11）。
> **TMP_InputField は textComponent に full text を保持し、スクロールは `firstVisibleCharacter`/vertical offset で行う**
> （文字列を truncate しない）。よって TMP 版 recolor は `textInfo.characterInfo[i].index`（＝**そのまま full-source
> index**）→ token 着色で済み、`displayStart` オフセット機構が**不要**になる（`VisibleDrawStart` プロバイダは廃止）。

- **#119 編集面**:
  - `StrategyInputField : InputField` → `TMP_InputField` ベースへ（`VisibleDrawStart`/`m_DrawStart` は撤去）。
  - content builder の editor `Text` → `TMP_Text`（textComponent）、`Placeholder` → `TMP_Text`、`StrategyCodeInput` の
    `StrategyInputField` を TMP 版へ。`lineType = TMP_InputField.LineType.MultiLineNewline`、`characterLimit=0`。
  - `StrategyEditorView`: `_input` 型 → `TMP_InputField`、`_placeholder` → `TMP_Text`。`onValueChanged`/`onEndEdit`/
    `selectionAnchorPosition`/`selectionFocusPosition`/`caretPosition`/`text`/`isFocused` は TMP_InputField に同名で在。
    `ApplyTheme` の `_input.textComponent.color`／`img` 背景は TMP textComponent へ。SetBody→dirty・EditHistory・
    undo/redo・blur→restage は不変。
  - probe: `StrategyEditorNotebookE2ERunner` の編集系（text-sync/undo/redo/placeholder STRATEGY-11）を TMP 参照へ。
  - HITL: caret/選択/IME 合成。
- **#120 構文ハイライト**:
  - `PythonSyntaxMeshEffect : BaseMeshEffect`（`ModifyMesh`）を**廃し**、TMP の per-glyph recolor へ:
    `text.ForceMeshUpdate()` 後 or `TMP_TextChangedEvent` 購読で `textInfo.characterInfo[i]`（`.index` full-source・
    `.isVisible`・`.materialReferenceIndex`・`.vertexIndex`）を走査し `textInfo.meshInfo[matIdx].colors32[vi+0..3]`
    を token 色で書換 → `text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32)`。rich-text タグ非挿入は維持。
    複数 material（fallback グリフ）の meshInfo 分割に対応。`displayStart` 機構は不要（上記精緻化）。
  - probe: Section8（旧 mesh colouring）を TMP `colors32` assert へ移行（token グリフ色≠base・Default 不変）。
  - HITL: スクロール整合。
- **#121 zoom 鮮明性 回帰ゲート**（findings は**本 0096** に §gate を追記＝issue 本文の「0093」は #122 使用済み）:
  - 新 `*E2ERunner`: 編集面・出力面の Text が `TMP_Text`（legacy `Text`/`InputField` でない）・font が SDF `TMP_FontAsset`
    （material/atlas 保持）・zoom（Content.localScale）変更で fontSize が transform スケール非依存。delete-the-logic
    litmus（legacy Text に差し戻すと RED）。
  - HITL: 5× ズームのスクショを findings に添付。

## #119 着地（実装済み・GREEN）

- `StrategyInputField : InputField` → **`: TMP_InputField`**（空サブクラス＝named editing-surface seam）。`VisibleDrawStart`/
  `m_DrawStart` 撤去。
- `StrategyEditorContentBuilder.Build` 編集ブロックを TMP 化: `StrategyCodeInput`(TMP_InputField) → `TextArea`(RectMask2D
  viewport) → `Text`(`TextMeshProUGUI`+`PythonSyntaxMeshEffect`) ＋ `Placeholder`(`TextMeshProUGUI`)。`input.textViewport`/
  `textComponent`/`placeholder`/`lineType=MultiLineNewline`/`characterLimit=0`/`richText=false` を配線。editor font は
  `EditorTmpFont()`（Cascadia SDF・default fallback）。`SetDisplayStartProvider` 配線は撤去。
- `StrategyEditorView`: `_input` 型 `InputField`→`TMP_InputField`、`_placeholder` `Text`→`TMP_Text`、`Initialize` 引数型を
  TMP へ。text-sync/undo/redo/blur/MultiLineNewline コアは不変（onValueChanged/onEndEdit/selection*/caretPosition/text/
  isFocused/textComponent は TMP_InputField に同名で在）。
- probe: `StrategyEditorNotebookE2ERunner` の placeholder reflection helper（`_placeholder`）を `TMP_Text` へ。Build 経由の
  全 section（10–23）は編集を `Cell.SetBody`（model）で駆動するため不変で GREEN。
- HITL 残（owner）: caret/選択/IME 合成。

## #120 着地（実装済み・GREEN）

- `PythonSyntaxMeshEffect : BaseMeshEffect`(`ModifyMesh`) を **`: MonoBehaviour`** へ。`[RequireComponent(typeof(
  TextMeshProUGUI))]`。`TMP_Text.OnPreRenderText` を OnEnable で購読（OnDisable で解除）し、colour 計算後・canvas upload
  前（`GenerateTextMesh`: OnPreRenderText → `m_mesh.colors32=…` → `SetMesh`）に `textInfo.meshInfo[matIdx].colors32[vi+0..3]`
  を token 色で上書き＝追加の `UpdateVertexData` 不要・全 TMP 再生成で自動再適用。`SetTokens` は `ForceMeshUpdate()` で
  即時 recolour（font null 時は skip＝fontless harness の TMP warning 回避）。
- **`characterInfo[i].index` が full-source index**（TMP は full text 保持・truncate しない）→ `displayStart` オフセット機構が
  不要（`SetDisplayStartProvider`/`VisibleDrawStart` 廃止・findings 0096 §#120 精緻化どおり）。複数 material（fallback グリフ）は
  per-char `materialReferenceIndex` で meshInfo を引き multi-atlas safe。
- probe Section8 を TMP へ: 実 `TextMeshProUGUI`＋`ForceMeshUpdate` → `textInfo.meshInfo[].colors32` を full-source index で
  assert（keyword/definition/number/comment は token 色、`(` は Default 不変、tag 非注入）。**static-atlas font**（TMP default
  LiberationSans SDF）を使い batchmode `-nographics` で決定論的に mesh が populate（Dynamic Cascadia はラスタ遅延）。
  recolour の **mapping は font 非依存**、production SDF font は #121 が別途 gate。
- 旧 `GlyphColor(VertexHelper,…)` helper 撤去。`ThemeProbe`/`ThemeHitlHarness` の syntax montage も `TextMeshProUGUI` へ移行
  （effect が TMP_Text を要求するため・HITL/AFK 双方 compile GREEN）。
- HITL 残（owner）: スクロール整合。

## #121 着地（実装済み・GREEN）

- 新 `Assets/Tests/E2E/Editor/StrategyEditorZoomCrispnessE2ERunner.{cs,md}`（ZOOM-01..04）。`StrategyEditorContentBuilder.
  Build` で実編集器を組み、(01) editor subtree に legacy `UnityEngine.UI.Text`/`InputField` ゼロ＋`StrategyInputField is
  TMP_InputField`、(02) editor textComponent＋placeholder が SDF（atlasRenderMode に "SDF"＋atlas material shader が
  "Distance Field"）、(03) 出力 2 ペイン（rich/console）も SDF、(04) ancestor `localScale=5×` で `TMP_Text.fontSize` 不変
  （SDF がズームを供給・bitmap 再ラスタでない）。delete-the-logic litmus＝legacy Text/InputField へ戻すと ZOOM-01 RED、非 SDF
  font で ZOOM-02/03 RED。
- HITL 残（owner）: 5× ズームの鮮明スクショ（編集面・出力面）を本 finding に添付。

## §gate（#121 zoom 回帰ゲートの正本 — issue 本文「0093」は #122 使用済みで stale）

| Action ID | 検証 | litmus |
|---|---|---|
| ZOOM-01 | 編集面 TMP・legacy bitmap 面ゼロ | ◎ legacy Text/InputField へ戻すと RED |
| ZOOM-02 | editor font(textComponent+placeholder) が SDF | ◎ 非 SDF font で RED |
| ZOOM-03 | 出力面(rich/console) が SDF | ◎ 非 SDF font で RED |
| ZOOM-04 | fontSize が transform scale 非依存（5× ancestor） | scale 機構の invariant 文書化ガード |

- VISUAL 鮮明性（5× スクショ）は HITL（headless は画素サンプル不可）。本ゲートは「SDF パイプライン全面・legacy bitmap 面ゼロ」の
  構造的前提＝鮮明性の機構を固定する。
