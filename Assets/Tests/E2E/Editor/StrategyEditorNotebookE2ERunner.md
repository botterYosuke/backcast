# StrategyEditorNotebookE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`StrategyEditorNotebookE2ERunner.cs`（第二波で実装）が自動検証する **Strategy Editor / marimo notebook
サーフェス**（cell-DAG 著作表面）の台本。実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、
**この サーフェスでユーザーができる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。
Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「保存済み `.py` を run して kernel が replay→箱庭更新する」縦串は *Journey E2E*（`ReplayToHakoniwaE2ERunner`）が
> 担う。本 Surface 台本は「セル著作操作が正しい aggregate 状態遷移・window lifecycle を起こすか」までを観測する。
> ▶ Run ボタンのゲート評価は別 Surface（[RunButtonE2ERunner](./RunButtonE2ERunner.md)）の責務。

## 対象サーフェス

Strategy Editor の **cell-as-floating-window** サーフェス（#81・ADR-0013・findings 0050）。1 セル = canvas 上の
1 floating window（`strategy_editor:region_NNN`）で、窓に映るのは**セル本体だけ**。N 個のセル窓は**ノート全体で 1 つの
`.py`** を成す。三層: pure core `MarimoNotebookDocument`（ノート集約 = `IStrategyFileProvider`）/ `Cell`（1 論理セル・
body+name+config）、orchestration `NotebookCellCoordinator`（add/delete/open/save → window lifecycle、region_id↔Cell
binding）、boundary `StrategyEditorView`（InputField↔Cell.Body の同期・undo/redo キー・syntax mesh）。本体↔`.py` の
合成/分解と DAG 解析は **Python(marimo) 純正**で、C# は空間 UI のみ（CONTEXT「cell window / marimo notebook」）。

## 対象ユーザー行動

セル本体のコード編集（Cell.SetBody → notebook dirty → EditHistory 記録）、Undo/Redo（Cmd/Ctrl+Z 系・per-cell history）、
構文ハイライト（lexical token・pure-UI mesh colouring）、[+] でセル追加（region_002+ spawn / dormant region_001 reuse）、
[X] でセル削除（region_001 は hide-dormant・region_002+ は despawn・最後の 1 セルは削除不可）、単一セル時の host-API
placeholder hint、窓の move / click-to-front（floating window 共有ロジック）。File→New/Open/Save はメニューバー入口の
notebook 効果なので参照行として載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| STRATEGY-01 | セル本体のコードを編集 | `StrategyEditorView.cs:102`→`OnValueChanged` | `EditHistory.Record`（snapshot 前後）＋`Cell.SetBody`→aggregate dirty（`MarkDirty`）＋`Retokenize` | 反射で `OnValueChanged` 駆動→`UndoCount`≥1・`Notebook.IsDirty`・`Cell.Body` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S2,S10) |
| STRATEGY-02 | Undo（Cmd/Ctrl+Z） | `StrategyEditorView.cs:139`→`DoUndo`（key: `:136`） | `EditHistory.Undo`→`ApplyTextAndSelection`（`_suppress` で再録防止）→`Cell.SetBody`・anchor/focus 復元 | `DoUndo` 反射 invoke 後の `Cell.Body`／selection を assert（key 経路は HITL） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S2) |
| STRATEGY-03 | Redo（Cmd/Ctrl+Shift+Z・Ctrl+Y） | `StrategyEditorView.cs:144`→`DoRedo`（key: `:134-135`） | `EditHistory.Redo`→`ApplyTextAndSelection`、fresh edit 後は redo 枝クリア | `DoRedo` 反射 invoke 後の body／RedoCount を assert（key 経路は HITL） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S2) |
| STRATEGY-04 | 構文ハイライト（lexical token 計算） | `StrategyEditorView.cs:117`→`Retokenize`→`PythonHighlighter.Tokenize` | token の ascending/non-overlap/in-range・keyword/string/comment/number/decorator/definition 分類 | `PythonHighlighter.Tokenize` を直接 assert（純ロジック） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S1) |
| STRATEGY-05 | ハイライトの**実 mesh 着色**（視覚） | `PythonSyntaxMeshEffect.ModifyMesh` | glyph 頂点色が token 色・Default 不変・scroll 追従・tag 非注入 | non-scroll は real Text で assert、scroll 着色は HITL | HITL専用（visible-range scroll 着色は実 TextGenerator/GPU 前提・findings 0010 §9） | `StrategyEditorNotebookE2ERunner`(S8) `Strategy Editor HITL` |
| STRATEGY-06 | [+] でセル追加（region_002+ spawn） | `BackcastWorkspaceRoot.cs:590`→`OnAddCell`→`NotebookCellCoordinator.cs:64`→`AddCell` | `Notebook.AddCell`→dirty・`AllocRegion`→`SpawnAuto`・`Track`・`Bind`・front 化・`UpdatePlaceholders` | coordinator `AddCell` 後に `RegionOf(cell)==region_002`・window 存在・CellCount+1・dirty を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-07 | [+] が dormant region_001 を再利用 | `NotebookCellCoordinator.cs:69`（`_region001Dormant` 分岐） | dormant 時は新規 spawn せず region_001 を `RevealAt`（**新 cascade 位置**・旧 hidden 座標は使わない findings 0050 trap2） | 1セル化→[X]で region_001 dormant→[+]で `RegionOf(c)==region_001` ＆ 再 active を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-08 | [X] でセル削除（region_002+ despawn） | `BackcastWorkspaceRoot.cs:548`→`OnDeleteCell`→`NotebookCellCoordinator.cs:92`→`DeleteCell` | `Notebook.RemoveCell`→dirty・region_002 は `Close`（despawn+deregister）・`Untrack` | `DeleteCell(region_002)` 後に window 不在・CellCount-1 を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-09 | [X] でセル削除（region_001 は hide-dormant） | `NotebookCellCoordinator.cs:99`（`regionId==AdoptedRegionId` 分岐） | region_001 は never-Destroy → `Hide`＋`_region001Dormant=true`＋`Bind(null)`（破棄しない・ADR-0013 Decision4） | 2セルで `DeleteCell(region_001)`→shell 残存・`activeSelf==false` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-10 | [X] で最後の 1 セル削除（≥1 ガードで拒否） | `MarimoNotebookDocument.cs:80`（`_cells.Count<=1`） | `RemoveCell` false・notebook 不変・shell 保全（marimo canDelete=!hasOnlyOneCell） | 単一セルで `DeleteCell` が false・CellCount==1・region_001 残存を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10,S12) |
| STRATEGY-11 | 単一セル時の host-API placeholder hint | `NotebookCellCoordinator.cs:223`→`UpdatePlaceholders`→`StrategyEditorView.cs:80`→`SetPlaceholderHint` | CellCount==1 のときのみ `HostApiHint` を表示・本体には焼き込まない（findings 0050） | CellCount 遷移で hint の付与/解除を反射 assert（hint text が `Cell.Body` に入らない） | 要新規自動化 | — |
| STRATEGY-12 | 供給可能性が著作操作で反転 | `MarimoNotebookDocument.cs:168`→`TryGetStrategyFile` | body 編集／add／delete のいずれでも dirty → not supplyable（5条件、CONTEXT「供給可能」） | 編集・add・delete 各々の後 `TryGetStrategyFile` false、Save 後 true を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S4,S10) |
| STRATEGY-13 | セル窓を move / drag で移動 | `FloatingWindowController`（共有 drag ロジック） | drag で window rect 更新・位置は `CapturePositions`（cell-order parallel・regenerate from live）→layout sidecar | 窓 move ロジックは **FloatingWindow Surface 台本**で観測。本台本は `CapturePositions` の cell-order 整合のみ assert | 自動(E2E済)（CapturePositions cell-order は S12。move/drag 本体は FloatingWindow 台本） | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-14 | セル窓を click-to-front（z-order） | `FloatingWindowController`（共有 raise ロジック） | クリックで対象 window を最前面へ・他窓の相対 z 保持 | 共有 floating-window ロジックなので **FloatingWindow Surface 台本**へ参照を張る（実 raycast は HITL） | 対象外（floating window 共有ロジック＝FloatingWindow 台本が正本。重複自動化しない） | — |
| STRATEGY-15 | File→New が notebook を 1 空セルへ reset | `BackcastWorkspaceRoot.cs:1526`→`OnFileNew`→`NotebookCellCoordinator.cs:125`→`New`→`ResetUnboundEmpty` | region_001 in-place reset・region_002+ despawn・unbound・1 空セル（MenuBar の `MENU-02` と同一 notebook 効果） | notebook 効果は **[MenuBarE2ERunner](./MenuBarE2ERunner.md) `MENU-02`** が正本。本台本は `coordinator.New()`→CellCount==1・!IsBound を assert | 自動(E2E済) | `MenuBarCutoverProbe` `StrategyEditorNotebookE2ERunner`(S10) |
| STRATEGY-16 | File→Open が `.py` を N セル窓へ分解 | `NotebookCellCoordinator.cs:117`→`Open`→`SyncWindowsToNotebook` | aggregate `Open` が cell list 置換・window を cells に一致再構築・sidecar 位置適用・fail-soft 非破壊 | open 後の window↔cell 整合は **MenuBar `MENU-04` / Journey** が縦串。本台本は `SyncWindowsToNotebook` の orphan 一掃を assert。**非 marimo `.py` の 1-cell wrap policy（#86）の正本ゲートは [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) `OPEN-NM-01..04`** | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10,S12) |
| STRATEGY-17 | File→Save が N セル → 1 `.py` 合成 | `NotebookCellCoordinator.cs:132`→`Save`→`MarimoNotebookDocument.cs:87`→`Save` | 順序付きセルを synthesizer で 1 `.py` 合成・atomic temp+replace・dirty クリア・name/config opaque 往復 | Save→Open round-trip で body+name+config 一致を assert（layout sidecar は MenuBar `MENU-05`） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) |
| STRATEGY-18 | InputField 直接タイプ / IME / caret 同期 | `StrategyEditorView.cs:102,124`（uGUI InputField boundary） | キー入力→InputField.text→onValueChanged 同期、IME 合成、`_suppress` 中の自己 write 無視 | — | HITL専用（uGUI InputField・IME・実キーボードの編集フィール＝findings 0010 §9） | `Strategy Editor HITL` |

> 物理窓 ≠ 論理セル（ADR-0013 Decision4）: region_001 は never-Destroy の adopted shell。**notebook は常に ≥1 セル**
> （aggregate の ≥1 guard）。1 ノート = 1 `.py`（合成/分解は marimo 純正）。供給可能 = path バインド済 ∧ not dirty ∧
> 直近 Open/Save 成功 ∧ canonical absolute `.py` ∧ 呼出時点で実在（CONTEXT「strategy file provider」）。

## 観測点（詳細）

- **STRATEGY-01/02/03（編集・Undo・Redo）**: `EditHistory` は **per cell window**（marimo の独立 CodeMirror history）。
  `OnValueChanged` は snapshot モデル（変更前 `{text,anchor,focus}` を保持）で `Record`、`Bind(cell)` で history を
  `Clear`。`StrategyEditorNotebookE2ERunner` Section2 が boundary coalescing（typing run 合体・newline standalone・directional
  delete・paste standalone・redo-clear・save-boundary・cap-200）の正本。Undo/Redo の **キー経路**（Cmd/Ctrl 判定・
  `Update`）は HITL（実 Keyboard）、**ロジック**（`DoUndo`/`DoRedo`→`ApplyTextAndSelection`）は反射 invoke で自動。
- **STRATEGY-04/05（ハイライト）**: lexical token 計算（`PythonHighlighter.Tokenize`・S1）は純ロジックで自動。
  実 mesh 着色は non-scroll を real Text 頂点色で assert（S8）、**visible-range scroll 着色は HITL**（findings 0010 §9）。
- **STRATEGY-06〜10（add/delete/≥1 ガード）**: `StrategyEditorNotebookE2ERunner` Section12 が REAL（bare-RT）window で
  cell0→region_001 / AddCell→region_002 spawn / DeleteCell 振り分け（despawn region_002・hide-dormant region_001）/
  ≥1 guard / dormant reuse / CapturePositions cell-order を assert。Section10 は aggregate 側 ≥1 guard。
- **STRATEGY-11（placeholder）**: `single = CellCount==1` のときだけ `HostApiHint` を全セル窓へ付与し、それ以外は null。
  hint は placeholder Graphic で、**`Cell.Body` には決して書き込まない**（seed 焼き込み禁止・findings 0050）。
  既存 Probe に直接の assert が無いため**要新規自動化**（CellCount 1↔2 遷移で hint 付与/解除を反射確認）。
- **STRATEGY-12（供給可能）**: dirty source は body 編集（`Cell.SetBody`→`MarkDirty`）AND 構造変化（add/delete）。
  「窓を足したが未保存」が正しく not-supplyable へ落ちる（aggregate が dirty を持つ・CONTEXT「供給可能」）。
- **STRATEGY-13/14（窓 move / front）**: floating window 共有ロジック。move/front 本体は **FloatingWindow Surface
  台本**が正本（重複自動化しない）。本台本は `CapturePositions` が cell-order parallel に live から再生成される点だけ assert。

## 自動判定（合格条件）

- ログに `[E2E STRATEGY NOTEBOOK PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、
  `error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E STRATEGY NOTEBOOK FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `MarimoNotebookDocument.RemoveCell` の `_cells.Count<=1` ガードを消すと
  STRATEGY-10 が落ちる／`Cell.SetBody` の dirty hook を消すと STRATEGY-12 が落ちる／`NotebookCellCoordinator.DeleteCell`
  の region_001 分岐（hide vs close）を入れ替えると STRATEGY-08/09 が落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `StrategyEditorNotebookE2ERunner` S1/S2 | EditMode・pure ロジック | STRATEGY-01〜04 の highlighter / history を昇格元として流用 |
| `StrategyEditorNotebookE2ERunner` S4/S10 | pure aggregate | STRATEGY-12/15/17 の supplyable・round-trip・≥1 guard を昇格 |
| `StrategyEditorNotebookE2ERunner` S8 | real Text mesh | STRATEGY-05 の non-scroll 着色（scroll は HITL に残す） |
| `StrategyEditorNotebookE2ERunner` S12 | batchmode・bare-RT window | STRATEGY-06〜10/13 の coordinator window lifecycle の正本 |
| `MenuBarCutoverProbe` | batchmode・root 合成 | STRATEGY-15（File→New reset）は MenuBar `MENU-02` と共有。正本は MenuBar 側 |
| `Strategy Editor HITL` | HITL ハーネス | STRATEGY-05(scroll)/18 の編集フィール・IME の視覚確認用に**探索 Probe として残す** |

## 将来の `StrategyEditorNotebookE2ERunner.cs` 実装方針（第二波）

> **実装済み（第二波8本目・findings 0061）**: `StrategyEditorNotebookE2ERunner`（旧 `StrategyEditorProbe`）を git mv＋改名で昇格
> （assert verbatim 移送・S1-12 に Covers 付与）。実際の経路は下記「軽量経路」（Section12 の bare-RT `FloatingWindowController`＋
> `FakeMarimoSynthesizer`）で、ComposeRoot 反射合成は未使用。STRATEGY-11（placeholder hint）は実 view harness を要するため
> 未昇格（要新規自動化のまま据え置き）。

- `StrategyEditorNotebookE2ERunner` Section12 と同型に **実 `BackcastWorkspaceRoot` を反射合成**（`ComposeRoot`: `OpenScene` →
  `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`）か、または coordinator を bare-RT
  `FloatingWindowController` で直接組む（Section12 の軽量経路）。Python-FREE が既定（cell 合成/分解は
  `FakeMarimoSynthesizer`・marimo round-trip 契約を共有）。
- ファイルダイアログは `StubFileDialog`、メニュー操作は `OnFileNew`/`OnFileOpen`/`OnFileSave` を反射 invoke、
  セル操作は `OnAddCell`/`OnDeleteCell` を反射 invoke。Undo/Redo は `StrategyEditorView.DoUndo`/`DoRedo` を直呼び
  （キー経路は HITL）。private 状態（`_region001Dormant`・`BoundCell`・`Notebook.IsDirty`）は coordinator の
  public（`RegionOf`/`CellOf`/`CapturePositions`）と reflection で観測。
- セクション構成は操作一覧表の `自動(*)` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す `Execute()`
  （null=PASS）パターン。teardown は spawned GameObject の `DestroyImmediate`。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod StrategyEditorNotebookE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
