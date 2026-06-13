# Strategy Editor Findings: editable Python code buffer（lexical highlight / undo-redo / 実 .py save / provider seam / open-file persist）

- Issue: #16 (S8 UI shell — Strategy Editor: code buffer / syntax highlight / undo-redo)・親 #1 (Epic)・**#3 の外**
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted, self-protection 節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）
- 配置の根拠: ADR-0003 self-protection 節（capability surface の具体項目など下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0003」として参照）。`strategyEditors` 永続化は ADR-0003 decision 2 が**明示列挙した** capability surface 項目（「（後で）Strategy Editor の開いていたファイル等」）の additive 実体。**新規 ADR は起こさない**（additive・version 戦略で reverse 可能、mesh-coloring 等は UI 実装詳細で「非可逆」を満たさない）。
- 先行: #9（Replay tracer / 0001）, #10（Replay chart / 0002）, #11（Replay panels / 0003）, #12（Replay layout / 0004）, #13（infinite canvas / 0006）, #14（Hakoniwa split-grid / 0007）, **#15（floating windows / 0008-floating-windows）**
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ uGUI 2.0（legacy `UnityEngine.UI`・**TMP 非導入**）/ Input System 1.19.0
- 設計確定: `grill-with-docs`（2026-06-13、owner インタビュー・9 問）。参照実装の capability parity 対象 = `/Users/sasac/marimo/frontend`（**CodeMirror 6**・`defaultHighlightStyle`）の `EditorState` / Python language extension / `history`+`keymap` の**関心分離**（実装方式 Lezer parser ではなく AC 上必要な視覚 capability に合わせる）。

> **状態: 設計ロック済み・実装前（このドキュメントは grill 成果）。** 実装結果・ゲートログは §11 に後追記。

---

## 0. スコープと段階づけ（owner 確定 2026-06-13）

#16 は #15 が frame だけ立てて deferred した **strategy_editor floating window の実 content**。editable Python buffer +
lexical syntax highlight + undo/redo + **実在 `.py` への save** + open-file path の layout persistence + 保存済みパスを
Replay/Live に渡す **durable provider API** まで。

### 採用（4 AC + provider seam）

- editable `.py` code buffer（strategy_editor window 内）
- Python **lexical** syntax highlight（意味解析ではない）
- undo / redo
- 開いていたファイル（path）を #12 layout schema へ persist
- 編集→保存→**provider API から strategy_file path を取得**できる durable seam（`_load_strategy` がパスを開く＝供給するのはソース文字列ではなく**保存済みパス**）

### 不採用（= #16 外・別 slice）

- **Run ボタン / 実 `start_nautilus_replay` 呼出 / Replay・Live lifecycle wiring**（run-UI の責務。現 Replay 起動は throwaway harness が定数 `STRATEGY_FILE` を渡しているだけで durable orchestration ではない）
- file-open **picker/dialog** / 新規ファイル作成 / multi-file・tabs
- autocomplete / LSP / linting / 意味解析 / Python parser / 実行検証
- SCENARIO sidecar（`<strategy>.json`）の読み書き・複製・検証（engine が読む。editor は触れない）
- Strategy Editor 常時最前面 pin（TTWR #90・#15 が既に out 宣言）
- resize gesture（#15 が将来 slice 宣言）
- caret / scroll / selection の永続化（path のみ persist。「等」は将来 additive）

## 1. editor architecture（owner 確定 2026-06-13）— pure core authoritative + legacy InputField

CM6 parity = `EditorState`（state authority）/ language extension（highlight）/ `history`+`keymap`（分離）の関心分離を、
本リポジトリの「pure AFK-authoritative core + thin Unity boundary（HITL）」規律へ写像する。

- **入力面: legacy `UnityEngine.UI.InputField`** — caret / selection / IME / clipboard / マウス選択を所有。**core へ raw key を
  転送しない**（surface 自前再実装は射程外。IME＝日本語コメント/文字列のため InputField の IME を使う）。
- **文書 authority: UnityEngine-free `StrategyDocument`** — source / path / dirty / save + provider を所有。`onValueChanged` で即時同期。
- **履歴: UnityEngine-free `EditHistory`** — transaction = `{beforeText, afterText, beforeSel(anchor,focus), afterSel}`。境界が
  選択位置を取得・復元。`InputField.KeyPressed` には undo/redo 実装が無いため、⌘/Ctrl+Z・⌘/Ctrl+Shift+Z・Ctrl+Y を history へ接続。
- **highlight: rich-text tag ではなく頂点色変更** — pure `PythonHighlighter`（source → token span）+
  `PythonSyntaxMeshEffect : BaseMeshEffect`（span → `UIVertex.color`）を**同じ Text geometry** に適用。元テキストへ tag を挿入
  しないため文字 index・caret・selection が壊れない。
- **display-window mapping**: InputField は表示文字列を内部（`m_DrawStart/m_DrawEnd`）で切り詰める。mesh effect は
  full-source span ↔ visible-offset の対応が必要。**reflection で private field を読まない** — 境界で解決できない場合は薄い
  `StrategyInputField : InputField` を設けて表示範囲を公開する。
- **TMP 非導入**（現 package 依存になく、この slice のための UI stack 追加は不要）。

### sibling rich-text overlay を不採用にした理由（owner）

別 overlay Text ではスクロール・折返し・IME composition・caret 位置を InputField の内部表示範囲と同期できない。よって
overlay ではなく**単一 Text の mesh vertex coloring** に置換し、caret/selection は InputField authority のまま。

## 2. `PythonHighlighter` 仕様（owner 確定 2026-06-13）— lexical tokenizer

意味解析ではなく **lexical**（AC「Python syntax highlight」に十分。LSP/parser は射程外）。builtin（`print`/`len`）は
**distinct class にしない**（shadow 可能で固定リスト着色は擬似意味分類・CM6 parity 上も不要）。

**token class（ロック）**: `Keyword` / `String` / `Comment` / `Number` / `Decorator` / `Definition`（def/class 直後の識別子）/ `Default`
（operator/punctuation は Default）。

**lexer 不変条件（ロック）**:

- span は **UTF-16 の (start, length)**（C# string / InputField index と一致）。
- span は**昇順・非重複・範囲内**。
- string / comment 内では他 token を生成しない。
- prefix は大文字小文字許容・**有効な組合せだけ**認識（`f`/`r`/`b` 等）。**f-string は全体を String**。
- triple quote（`"""`/`'''`）は改行をまたぐ。**未閉鎖なら EOF まで String**。
- `# comment` は EOL まで。
- decorator = 論理行先頭の空白後の `@` から **dotted name** まで。
- def / class と名前の間の空白・改行は許容（ただし comment/string をまたがない）。
- `\` continuation 自体は **Default**（特別状態を持たせない）。
- 数値 = binary/octal/hex・指数・虚数接尾辞・underscore を対象。

**AFK fixture**: keyword/string/comment/number/decorator/def 名 + 未閉鎖 triple string + f-string + `print = 1`（builtin 扱いしない）
+ `# "not a string"`（Comment のみ）+ `"not # comment"`（String のみ）+ Unicode identifier 前後の offset 不破壊 + 昇順/非重複 invariant。

## 3. `EditHistory` 仕様（owner 確定 2026-06-13）— 境界ベース coalescing

time-based idle flush は不採用（AFK 非決定的）。**境界ベース coalescing**・depth cap **200**（unbounded 不要・超過時最古 undo を drop）。

- 連続 single-char 挿入を coalesce。**連続 Backspace / Delete も方向別に coalesce**。
- **newline は直前 group を閉じ、newline 自体を standalone transaction**。
- paste / selection replace / IME commit など **multi-char change は standalone**。
- undo 後の fresh edit は **redo stack 全消去**。
- **save は group boundary だが history は消さない**（save 後も undo 可）。
- **file open / reload は history 全消去**。
- **no-op**（text・selection とも不変）は不記録。

**snapshot 方式（owner 補正）**: `onValueChanged` は before-selection を渡さないため、境界が常に直前の `{text, anchor, focus}`
snapshot を保持し `previous snapshot + current InputField state → EditHistory.Record(...)`。undo/redo による `InputField.text`
更新中は **suppression flag** で再記録を防ぐ。

**AFK**: coalesce 挿入 / 方向別 BS/Del / newline standalone / multi-char standalone / undo・redo / redo clear / save 後 undo 可 /
open 後 history empty / no-op 不記録 / 201 件投入で最古 drop。

## 4. file モデル（owner 確定 2026-06-13）

- **`Open(path)`**: 実在の通常ファイル ∧ 拡張子 `.py` のみ成功。canonical absolute path 保持。失敗時は document 不変。
  UTF-8 読込。成功時 dirty=false ∧ history clear。（新規作成・picker は射程外。）
- **`Save()`**: 同一パス上書きのみ。UTF-8 **atomic write**（同一 dir の一時ファイルから置換）。成功時のみ dirty=false。失敗時は
  text/path/dirty/history 維持。save は history boundary だが undo 可能履歴は残す。
- **存在しない外部書換え**は保証しない（必要なら将来 hash/mtime 契約を追加）。
- **sidecar**: #16 は `<strategy>.json` を一切読み書き・複製・検証しない。`load_scenario()` は sidecar 優先・無ければ `.py` 内
  inline `SCENARIO` に fallback し、現行フィクスチャは inline なので作業コピーは sidecar なしで Replay 可能。

## 5. provider seam（owner 確定 2026-06-13）

- **`IStrategyFileProvider`**（`StrategyDocument` が実装）: `bool TryGetStrategyFile(out string path)` は次を**すべて**満たすときだけ
  true + canonical absolute path:
  1. path バインド済み
  2. `dirty == false`
  3. 直近の Open または Save が成功済み
  4. canonical absolute `.py`
  5. 呼出時点でも通常ファイルとして存在
  （dirty 時は **false**＝stale パスを返さない。「provider が返すパス = ディスク内容が buffer と一致」を構造保証。）
- **`StrategyProviderRegistry`**（案A・薄い durable lookup）: window ID による Register/Unregister、`TryGet(windowId, out provider)`、
  登録済み ID の**決定的列挙（window ID の ordinal 昇順）**、duplicate ID は登録拒否。
  **active/current/default を選ばない・lifetime を所有しない・filesystem/save/layout を扱わない**（multi-instance の選択は run-UI 責務）。

**AFK**: dirty→false / save 後 true+path / その path を読むと buffer 一致 / 5 条件 / unbound→false / registry の dup 拒否・unregister・
複数 instance lookup・決定的列挙。

## 6. content の組み込み（owner 確定 2026-06-13）— controller 境界は不変

**`FloatingWindowController` に content factory を追加しない**（#15 が「catalog/spec は content factory を所有しない」とロック・
controller は既に *window* factory 注入済み）。Strategy Editor content は **caller の window factory が `kind == "strategy_editor"` の
とき組み立てる**（または controller 外の content registry で合成）。durable な content builder が window body に
InputField + Text + `PythonSyntaxMeshEffect` + view を配線し、`StrategyProviderRegistry` へ register/unregister する。

## 7. layout persistence（AC4・owner 確定 2026-06-13）— additive・v1 据え置き

```
[Serializable] class StrategyEditorState { string id; string filePath; }
class LayoutDocument { int version;                        // CURRENT_VERSION = 1（据え置き）
                       List<PanelLayout> panels;
                       CanvasView canvasView;              // #13
                       List<FloatingWindowLayout> floatingWindows;   // #15
                       List<StrategyEditorState> strategyEditors; }  // #16 additive（別 dimension）
```

- `FloatingWindowLayout` は**変更しない**。`id` で strategy_editor window と関連付ける別 dimension。
- persist は **canonical absolute filePath のみ**（buffer/dirty/history/caret/selection/scroll は保存しない）。
- `Default()` 空リスト・v1 据え置き・`Clone()`/`StructurallyEqual()` に含む（equality は id 突合・list 順非依存・null↔empty 同値）。
- **Sanitize（LayoutStore）**: null / 空 id / 空 path を drop・duplicate id は first-wins・**orphan entry や存在しない path は保持**。
  LayoutStore は **filesystem 確認も canonical 化もしない**。

### restore semantics（owner 補正・full-replacement）

既存 editor へ layout 再適用時、`Open(missingPath)` 失敗で以前の document が残ると layout と表示が食い違うため:

- **state なし**: editor を **unbound empty** へ reset。
- **state あり**: まず unbound empty へ reset してから `Open(filePath)`。
- **Open 成功**: disk 内容・dirty=false・history empty。
- **Open 失敗**: window は残り **unbound empty** のまま（persisted entry は document から削除しない）。
- この reset は新規ファイル作成ではなく **restore 境界専用の「content 未復元」状態**（通常の `Open(path)` 失敗時 document 不変契約は維持）。
- **unbound empty** は freshly-spawn 直後（まだ何も Open していない strategy_editor）の初期状態でもある。

## 8. durable / throwaway 構成（owner 確定 2026-06-13）

durable **`Assets/Scripts/StrategyEditor/`**（pure core + Unity boundary）:

| 型 | 役割 | 層 |
|---|---|---|
| `PythonHighlighter` | source → token span（lexical・**AFK 権威**・UnityEngine-free） | pure core |
| `EditHistory` | 境界ベース undo/redo stack（**AFK 権威**・UnityEngine-free） | pure core |
| `StrategyDocument` | source/path/dirty/save・`IStrategyFileProvider` 実装（System.IO 可・UnityEngine-free） | pure core |
| `IStrategyFileProvider` | 明示的 provider 契約（`TryGetStrategyFile`） | seam |
| `StrategyProviderRegistry` | window id → provider lookup（**AFK 権威**・UnityEngine-free） | seam |
| `PythonSyntaxMeshEffect` | `BaseMeshEffect`・span → `UIVertex.color` | Unity boundary |
| `StrategyInputField`（必要時） | `InputField` 派生・visible 表示範囲公開（reflection 回避） | Unity boundary |
| `StrategyEditorView` | MonoBehaviour 境界。InputField 配線・`onValueChanged`→document/history・undo/redo key・mesh effect・registry 登録 | Unity boundary |
| content builder | window body に editor subtree を構築（caller window factory が kind 判定で呼ぶ） | Unity boundary |

schema 拡張（`Assets/Scripts/Layout/`）: `LayoutDocument.cs`（`StrategyEditorState` + `strategyEditors` + Default/Clone/StructurallyEqual/Find）・
`LayoutStore.cs`（`NormalizeStrategyEditors` を Sanitize に追加・probe 用 public）。

throwaway: `StrategyEditorHitlHarness`（Editor-menu spawn・`persistentDataPath` 作業コピーを開く・編集/highlight/undo/redo/save/Load）・
`Assets/Editor/StrategyEditorHitlMenu.cs`・`Assets/Editor/StrategyEditorProbe.cs`（7 セクション AFK ゲート）。

## 9. ゲート（owner 確定 2026-06-13）— AFK 権威 7 セクション + HITL 目視

### AFK probe 7 セクション

1. **PythonHighlighter** — §2 fixture + ascending/非重複/範囲内 invariant。
2. **EditHistory** — §3 全境界 + 201 件 cap drop。
3. **StrategyDocument file モデル** — Open(実.py)→dirty=false/history clear、Open(非.py/欠落)→失敗・document 不変、Save→atomic
   write・dirty=false・disk==buffer、**置換失敗時に既存ファイル内容保持**。
4. **provider** — dirty→false / save 後 true+path / path 読むと buffer 一致 / 5 条件 / unbound→false。
5. **StrategyProviderRegistry** — register/unregister・dup 拒否・ordinal 昇順決定的列挙・複数 instance lookup。
6. **layout round-trip（実 JsonUtility serialization gate）** — strategyEditors を mutate（2 editor + orphan + missing path）→save→
   fresh load→loaded==mutated / sanitize / 旧 #15 sidecar 後方互換（strategyEditors 無し→空・panels/canvasView/floatingWindows 維持）/
   on-disk JSON テキスト proof / Clone/StructurallyEqual。
7. **restore semantics（実 window/controller）** — full-replacement（state なし→unbound empty / state あり→reset 後 Open / 成功→disk・
   dirty=false・history empty / 失敗→unbound empty・**Open 失敗後も window 登録が残る**・entry 不削除）。
8. **mesh 着色（非スクロール・real-component）** — ASCII 固定 font fixture で Text mesh を強制生成 → 既知 token の glyph quad vertex
   color / Default token 色不変 / whitespace・newline は glyph 無し前提で index mapping 不破壊 / token 更新後 mesh 再構築 / source へ
   rich-text tag 不混入。**surrogate pair・IME composition・visible range スクロール切替は HITL**（vertex 数と UTF-16 index が単純対応
   しないため）。batchmode で mesh 生成不能だった場合のみ HITL へ降格し未自動化リスクを §11 に明記。

（#12–#15 回帰は各既存 probe を個別実行して確認。）

### HITL gate（owner 起動・menu spawn・**Mac leg**）

working-copy fixture を開く→色分けが見える（keyword/string/comment/number/decorator/def 名）→タイプで再着色→**undo/redo がキーで効く
（macOS: ⌘+Z・⌘+Shift+Z 必須）**→**IME で日本語をコメントに入力**できる→Save→Load round-trip（path 復元・再読込）→スクロール時も色が
ズレない。`Ctrl+Y` は Windows leg まで未検証として扱う。

## 10. leg / ADR 配置

- **Mac leg のみ**（#12–#15 と同じ）。Windows leg は **deferred**（Ctrl+Y redo 検証含む）。
- ADR-0003 self-protection 節に従い **新規 ADR を起こさない・ADR-0003/0001 を編集しない**。本 findings が下位事実を記録し ADR を
  「方針」として参照（#13/#14/#15 と同パターン）。

## 11. 実装結果（ゲートログ・Mac leg, 2026-06-13）

durable 9 ファイル（`Assets/Scripts/StrategyEditor/`）+ schema 拡張（`Layout/` 2 ファイル）+ throwaway 3 ファイルを直接実装
（pair-relay/parallel は使わず — 単一言語 C#・Python 非依存・仕様完全確定。#12/#13/#14/#15 と同じ Unity スライス逸脱理由・CLAUDE.md 規約）。

成果物:
- **durable pure core**: `PythonSyntaxToken.cs`（enum + span struct）/ `PythonHighlighter.cs`（lexical tokenizer・AFK 権威）/
  `EditHistory.cs`（境界 coalescing・cap 200・snapshot Record）/ `StrategyDocument.cs`（Open/Save atomic・`IStrategyFileProvider` 実装）/
  `IStrategyFileProvider.cs` / `StrategyProviderRegistry.cs`（ordinal 列挙）/ `StrategyEditorRestore.cs`（full-replacement）。
- **durable Unity boundary**: `PythonSyntaxMeshEffect.cs`（`BaseMeshEffect`・span→`UIVertex.color`・displayStart は live provider）/
  `StrategyInputField.cs`（`InputField` 派生・`protected m_DrawStart` を `VisibleDrawStart` で公開・reflection 不要）/
  `StrategyEditorView.cs`（InputField sync・snapshot Record・undo/redo key・registry 登録）/ `StrategyEditorContentBuilder.cs`
  （caller window factory が kind 判定で呼ぶ content builder・effect に display-start provider を配線）。
  **`StrategyInputField` は HITL Step-5 で必要と判明**（当初「multiline は full text 保持で displayStart=0」と仮定したが、
  **focused multiline InputField は `m_TextComponent.text` を可視行窓 `[m_DrawStart, m_DrawEnd)` に切り詰める**＝`UpdateLabel`・
  `InputField.cs:2559/2563`。scroll は `onValueChanged` を発火せず `m_DrawStart` だけ動くため、effect は mesh-build 時に
  **live で** `m_DrawStart` を読む必要がある。owner が §1 で予約した fallback path を発動）。
- **durable schema**: `Layout/LayoutDocument.cs`（`StrategyEditorState` POCO + `strategyEditors` additive field + Default 空 + Clone/StructurallyEqual/FindStrategyEditor・null↔empty coalesce）・`Layout/LayoutStore.cs`（`NormalizeStrategyEditors` を Sanitize に追加・public）。
- **throwaway**: `StrategyEditorHitlHarness.cs`（Editor-menu spawn・persistentDataPath 作業コピー・Open/Save/Undo/Redo/Save+Load Layout）・
  `Assets/Editor/StrategyEditorHitlMenu.cs`（`Tools > Backcast > Strategy Editor HITL`）・`Assets/Editor/StrategyEditorProbe.cs`（8 セクション AFK ゲート）。

ゲート（VERBATIM, `UNITY_EXIT=0`, CS エラー 0・自ファイル警告 0, `-batchmode -nographics`）:
```
[STRATEGY EDITOR PASS] lexical highlighter (keyword/string/comment/number/decorator/definition; triple-unterminated, f-string whole, comment-vs-string, print-not-builtin, surrogate offset, ascending/non-overlap invariant) + edit history (boundary coalescing: insert run / directional backspace+delete / newline standalone / multi-char standalone / redo-clear / save-boundary-undoable / open-clears / no-op-skip / cap-200 drop-oldest) + file model (Open .py-only/existing, atomic UTF-8 save, replace-failure preserves on-disk) + provider 5-condition (dirty->false, save->path, unbound->false) + registry (dup-reject, unregister, ordinal enumeration, multi-instance) + layout round-trip (REAL JsonUtility, additive strategyEditors, on-disk text proof, sanitize null/empty/dup/orphan/missing-path, back-compat, Clone/StructurallyEqual) + restore full-replacement on real windows (state-none->unbound, present->reset+Open, Open-failure keeps window) + non-scroll mesh colouring (real Text, token glyph vertex colour, Default unchanged, no tag injection) — Unity-owned, ADR-0003 capability parity, under Unity Mono
```

**mesh AFK fallback は発生せず**（残存リスク無し）: Section 8 は `-nographics` でも実 `TextGenerator` が glyph を生成し、可視 glyph 数 == synthetic rank が一致した（whitespace-skipping 前提を実 generator で裏取り。fallback 警告ログ無し）。同様に Section 3 の atomic 置換失敗は read-only ディレクトリで実際に強制でき、既存ファイル内容保持・dirty 維持を assert（force できなかった場合の soft-skip 警告も出ていない）。

**#12/#13/#14/#15 回帰（各 probe 個別実行）**: 全て `UNITY_EXIT=0` で GREEN 継続 —
- `ReplayLayoutProbe`: `[REPLAY LAYOUT PASS]` / `InfiniteCanvasProbe`: `[INFINITE CANVAS PASS]` /
- `HakoniwaProbe`: `[HAKONIWA PASS]` / `FloatingWindowProbe`: `[FLOATING WINDOW PASS]`
（schema に `strategyEditors` を additive 追加しても旧 sidecar 後方互換・既存 round-trip が無傷であることを確認）。

**HITL（owner 目視・Mac leg, 2026-06-13）**: 1 巡目で **Step 1-4/6/7 PASS**（色分け視認・タイプ再着色・⌘+Z/⌘+Shift+Z undo/redo・
IME 日本語コメント・Save/Load Layout round-trip・pan/zoom 追従）、**Step 5（スクロール時の色ズレ）FAIL**。
→ 原因 = focused multiline InputField の可視行窓切り詰め（上記）。`StrategyInputField` + effect live display-start provider で修正。
AFK gate（8 section）+ #12-#15 回帰は修正後も `UNITY_EXIT=0` GREEN 継続。**2 巡目で Step 5 PASS**（owner 再目視・長い
コードを scroll しても色が文字に追従）。→ **§9 HITL gate 全項目 GREEN（Mac leg）＝ #16 の AFK + HITL 両ゲート達成**。
Windows leg（Ctrl+Y redo 含む）は deferred。
