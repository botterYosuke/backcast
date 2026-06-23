# StrategyEditorNotebookE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`StrategyEditorNotebookE2ERunner.cs`（第二波で実装）が自動検証する **Strategy Editor / marimo notebook
サーフェス**（cell-DAG 著作表面）の台本。実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、
**この サーフェスでユーザーができる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。
Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「per-cell RUN で notebook が**実エンジン**を駆動し、結果が **Hakoniwa の base tile に逐次出る**」縦串は *Journey E2E*
> （[`NotebookToHakoniwaJourneyE2ERunner`](./NotebookToHakoniwaJourneyE2ERunner.md)）が担う（旧 chart-OHLC 串
> `ReplayToHakoniwaE2ERunner` は #99 / ADR-0017 で退役。その縦串の席を新 Hakoniwa＝dockable floating window モデルで継いだ）。
> 本 Surface 台本は「セル著作操作が正しい aggregate 状態遷移・window lifecycle を起こすか」と per-cell RUN の**制御ロジック**
> （index→窓 routing・running guard・glyph・stale badge）までを fake executor で観測する（engine DATA は Python e2e）。
> ▶ Run（per-cell RUN）のゲート評価は本 Surface（S13/S14/S16/S17）に統合済み（旧 global ▶ Run の別 Surface RunButtonE2ERunner は #95 Phase 6 sunset で retire・findings 0075 §3c）。

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
| STRATEGY-11 | 単一セル時の host-API placeholder hint | `NotebookCellCoordinator.cs:226`→`UpdatePlaceholders`→`StrategyEditorView.cs:191`→`SetPlaceholderHint` | CellCount==1 のときのみ `HostApiHint` を表示・本体には焼き込まない（findings 0050） | Section20: 1 セルで placeholder active＋text==`HostApiHint`・2 セルで非 active・1 セルへ戻すと復活・どの遷移でも `Cell.Body` 空（hint 非焼き込み）を反射 assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S20) |
| STRATEGY-12 | 供給可能性が著作操作で反転 | `MarimoNotebookDocument.cs:168`→`TryGetStrategyFile` | body 編集／add／delete のいずれでも dirty → not supplyable（5条件、CONTEXT「供給可能」） | 編集・add・delete 各々の後 `TryGetStrategyFile` false、Save 後 true を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S4,S10) |
| STRATEGY-13 | セル窓を move / drag で移動 | `FloatingWindowController`（共有 drag ロジック） | drag で window rect 更新・位置は `CapturePositions`（cell-order parallel・regenerate from live）→layout sidecar | 窓 move ロジックは **FloatingWindow Surface 台本**で観測。本台本は `CapturePositions` の cell-order 整合のみ assert | 自動(E2E済)（CapturePositions cell-order は S12。move/drag 本体は FloatingWindow 台本） | `StrategyEditorNotebookE2ERunner`(S12) |
| STRATEGY-14 | セル窓を click-to-front（z-order） | `FloatingWindowController`（共有 raise ロジック） | クリックで対象 window を最前面へ・他窓の相対 z 保持 | 共有 floating-window ロジックなので **FloatingWindow Surface 台本**へ参照を張る（実 raycast は HITL） | 対象外（floating window 共有ロジック＝FloatingWindow 台本が正本。重複自動化しない） | — |
| STRATEGY-15 | File→New が notebook を 1 空セルへ reset | `BackcastWorkspaceRoot.cs:1526`→`OnFileNew`→`NotebookCellCoordinator.cs:125`→`New`→`ResetUnboundEmpty` | region_001 in-place reset・region_002+ despawn・unbound・1 空セル（MenuBar の `MENU-02` と同一 notebook 効果） | notebook 効果は **[MenuBarE2ERunner](./MenuBarE2ERunner.md) `MENU-02`** が正本。本台本は `coordinator.New()`→CellCount==1・!IsBound を assert | 自動(E2E済) | `MenuBarCutoverProbe` `StrategyEditorNotebookE2ERunner`(S10) |
| STRATEGY-16 | File→Open が `.py` を N セル窓へ分解 | `NotebookCellCoordinator.cs:117`→`Open`→`SyncWindowsToNotebook` | aggregate `Open` が cell list 置換・window を cells に一致再構築・sidecar 位置適用・fail-soft 非破壊 | open 後の window↔cell 整合は **MenuBar `MENU-04` / Journey** が縦串。本台本は `SyncWindowsToNotebook` の orphan 一掃を assert。**非 marimo `.py` の 1-cell wrap policy（#86）の正本ゲートは [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) `OPEN-NM-01..04`**。**dirty な File→Open の未保存ガード（Save/Discard/Cancel）＋ discard 認可（discardDirty）の横断配線は [FileNavGuardE2ERunner](./FileNavGuardE2ERunner.md) `FILEGUARD-05..07,09`（#87）** | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10,S12) |
| STRATEGY-17 | File→Save が N セル → 1 `.py` 合成 | `NotebookCellCoordinator.cs:132`→`Save`→`MarimoNotebookDocument.cs:87`→`Save` | 順序付きセルを synthesizer で 1 `.py` 合成・atomic temp+replace・dirty クリア・name/config opaque 往復 | Save→Open round-trip で body+name+config 一致を assert（layout sidecar は MenuBar `MENU-05`） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) |
| STRATEGY-18 | InputField 直接タイプ / IME / caret 同期 | `StrategyEditorView.cs:102,124`（uGUI InputField boundary） | キー入力→InputField.text→onValueChanged 同期、IME 合成、`_suppress` 中の自己 write 無視 | — | HITL専用（uGUI InputField・IME・実キーボードの編集フィール＝findings 0010 §9） | `Strategy Editor HITL` |
| STRATEGY-19 | per-cell ▶ RUN ボタン（adopted+spawned 両窓に在る） | `BackcastWorkspaceRoot.cs`→`WireCellRunButton`→`StrategyEditorWindowFrame.cs`→`EnsureRunButton` | adopted region_001 ＋ spawned region_002 の title bar に ▶ RUN を find-or-create（idempotent・X ボタンと同型・ADR-0016 D2） | Section13 で両 region に `EnsureRunButton` 非null・2 回目呼出が同一 Button（重複生成しない）を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S13) |
| STRATEGY-20 | per-cell RUN 押下 → 出力がその窓に出る | `EnsureRunButton` onClick→`NotebookRunController.cs`→`RunCell`→`NotebookRunLane`→`StrategyEditorView.cs`→`SetOutput` | 押した cell＋reactive 下流の出力が各 cell index の窓へ routing・別窓を上書きしない（engine 非接続の純粋計算「土台」・ADR-0016 D1/D2）。marimo reactive の正しさ（下流再計算/上流非依存 cell 不変）は **Python pytest** が正本 | Section13: press region_001→region_001="out-0" ＆ region_002="down-1"（下流 routing）、press region_002→region_002="out-1" ＆ region_001 不変（index→window routing・1 窓に collapse しない）を assert | 自動(E2E済)（C# routing。reactive 正しさは `python/tests/test_notebook_interactive_run.py`） | `StrategyEditorNotebookE2ERunner`(S13) |
| STRATEGY-21 | `bt.replay()` press → committed scenario が backend に渡り cell が RUNNING（▶→■） | `NotebookRunController.cs:58`→`RunCell`（`drivesReplay=true`）→`_btRunActive=true`＋`onRunningChanged`→`StrategyEditorWindowFrame.SetRunButtonGlyph(■)` | `bt.replay` を含む source の press で `scenarioJsonProvider` が呼ばれ scenario が executor へ届く・running guard が立つ・▶→■ トグル（ADR-0016 D3 / findings 0073 §P4-3） | Section14: press 後 executor が受領した scenario JSON ＝ 提供 JSON、`run.IsBacktestRunning==true`、`runningEvents[0]==R1:True`、glyph=="■" を assert | 自動(E2E済)（C# routing。real bt drive は `python/tests/test_notebook_replay_afk.py`） | `StrategyEditorNotebookE2ERunner`(S14) |
| STRATEGY-22 | replay 走行中の 2nd RUN は reject（queue ではない） | `NotebookRunController.cs:60`（`_btRunActive` 早期 return） | 走行中の press は executor を呼ばず onError でメッセージ・queue しない（ADR-0016 D3「即時拒否」） | Section14: 1st press で running→2nd press 後 `exec.Calls` 不変・`errors` に message が積まれている | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S14) |
| STRATEGY-23 | ■ press → force-stop 要求→ result drain で guard 解除＋▶ 復帰 | `NotebookRunController.cs:96`→`StopRunning`→`_onStop`、`ApplyResult` で `_btRunActive=false`＋`onRunningChanged(false)` | ■ press で `_onStop` が呼ばれ、completed result の drain で running guard が解除され ▶ に戻る・新 press 受理 | Section14: `run.StopRunning()` 後 `stopCount==1`、`DrainAndRoute` 後 `IsBacktestRunning==false` ＆ glyph=="▶"、その後の RunCell が再受理 | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S14) |
| STRATEGY-24 | `bt.step()` press → running guard / ▶→■ を活性化させない | `NotebookRunController.cs:73`（`drivesReplay` のみが guard を立てる・`drivesStep` 単独は activate しない） | step は一発操作で意図的に press 跨ぎ stateful（cell 著者が `bar = bt.step()` を書いた意味）。guard / ▶→■ を立てると次の press が拒否されて意図に反する（findings 0074 / ADR-0016 D3 の文脈読み替え） | Section15: `bt.step` only source の press 後、`run.IsBacktestRunning==false`・`runningEvents` 空・glyph=="▶" のまま、executor が scenario JSON を受領していること | 自動(E2E済)（C# routing。real bt persistence は `python/tests/test_notebook_step_afk.py`） | `StrategyEditorNotebookE2ERunner`(S15) |
| STRATEGY-25 | step cell の連打は受理される（per-press の counter signal が累加） | `NotebookRunController.cs:73`（step は guard 非活性なので 2 連打が両方通る） | step persistence の C# 側可観測：押すたびに executor が呼ばれ stub counter が +1 し、SetOutput がその文字を window に書き戻す | Section15: 1st press 後 output=="1"、2nd press 後 `exec.Calls==2` ＆ output=="2"（guard が活性化しないので 2nd も到達） | 自動(E2E済)（real bt cache 持続性は pytest） | `StrategyEditorNotebookE2ERunner`(S15) |
| STRATEGY-26 | scenario 未 commit の `bt.step()` press → guidance text が cell output に出る | `NotebookRunController.cs:73`（provider が空文字 → ScenarioJson=null）→ backend `NoScenarioBacktester` placeholder（real path）／stub executor の fail-closed branch（AFK） | scenario が commit されていない状態で bt cell を押すと、cell output に `RuntimeError: ...commit the startup panel first...` の guidance text が出る（Phase 3 `submit_market` context-out fail-closed と同型） | Section15: provider を `""` にして press → `views[R1].CurrentOutput` が `RuntimeError` ＋ `commit the startup panel` を含む | 自動(E2E済)（C# routing。real placeholder は `python/tests/test_backtester_phase5.py` / `test_notebook_step_afk.py`） | `StrategyEditorNotebookE2ERunner`(S15) |
| STRATEGY-27 | 編集/blur → 編集 cell＋下流窓が stale badge（amber） | `BackcastWorkspaceRoot.cs:371/535`→`EditCommitted`→`NotebookRunController.cs:117`→`Restage`→`NotebookRunLane.cs:86`→`HostNotebookCellExecutor.cs:41`→`notebook_restage` | edit/blur で restage RPC が stale index 集合を返し、`ApplyResult`（`NotebookRunController.cs:172`）が index→region map・`SetRunButtonStale`（`StrategyEditorWindowFrame.cs:159`）で該当窓を amber tint。編集 cell の下流窓も amber（marimo `set_stale` 下流伝播は pytest `test_notebook_stale.py` が正本） | Section16: `_StaleExecutor` が `[0,1]` 返す→btn1/btn2 とも amber を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S16) |
| STRATEGY-28 | press で押した窓の stale クリア（下流は amber 残） | `NotebookRunController.cs:172`→`ApplyResult`（ran cell の index を stale 集合から drop） | press した cell が走ると result の stale 集合から pressed index が落ち、その窓だけ amber→green、still-stale 下流窓は amber のまま（無関係 press で下流を誤クリアしない） | Section16: `RunCell(R1)` 後 btn1 green ＆ btn2 amber を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S16) |
| STRATEGY-29 | 走行中の第二 per-cell RUN → 通知ライン block popup（非ブロック press は無通知） | `NotebookRunController.cs:60`（`_btRunActive` early-return）→ onError → `BackcastWorkspaceRoot.cs:383`→`_menuBarView.ShowMessage("Run cell: "+msg)` | bt.replay 走行中の 2nd per-cell RUN だけが `already running` を通知ラインへ・成功（非ブロック）press は無通知（not-owner / server-not-ready は root button-wire gate `cs:371/535` の別レーン＝HITL） | Section17: 成功 press で `notifications==0`、blocked 2nd press で exactly 1 ＆ `already running` を含む | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S17) |
| STRATEGY-30 | document badge: Open→basename 常時可視 / New→Untitled | `MarimoNotebookDocument`（CurrentPath/IsBound/IsDirty）→`BackcastWorkspaceRoot.cs:1532`→`DocumentBadgeText`→`MenuBarView.cs:122`（`_docBadge`・左寄せ別レーン）→`:172` refresh | bound notebook は basename 常時可視・unbound（File→New `ResetUnboundEmpty`）は `Untitled`。document-identity badge は venue/mode/message badge（`MenuBarView.cs:119-123`）とは**別レーン**＝#90 AC4「Run-disabled reason と非矛盾」を構造的に満たす | Section18: fresh→`Untitled`・SaveAs/Open→`doc18.py`・ResetUnboundEmpty→`Untitled` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S18) |
| STRATEGY-31 | document badge: 編集→`* name.py` / Save→`*` 消える | 同上（`IsDirty` が `* ` prefix を駆動・`DocumentBadgeText` `cs:1542-1543`） | 未保存編集で `* ` prefix・Save で dirty クリア→`* ` が消える | Section18: body 編集後 `* doc18.py`・Save 後 `doc18.py` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S18) |
| STRATEGY-32 | rich output routing: plain / markdown / html table | `NotebookRunController.cs:198`→`SetOutput(Output,Mimetype,Data)`→`StrategyEditorView.cs:117`→per-mimetype 分岐（`:123-141`） | text/plain→verbatim Text・text/markdown→rich-text subset（`<b>`）・text/html `<table>`→pipe rows。marimo `FormattedOutput(mimetype,data)` 生成（matplotlib→image/png・mo.md→markdown・df→html）は pytest `test_notebook_rich_output.py` が正本 | Section19: 各 mimetype で `OutputIsImage==false` ＆ pane 内容（`hello` / `<b>` 含む / `\|` 含む）を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S19) |
| STRATEGY-33 | rich output routing: image/png→RawImage / 未対応→labelled fallback | `StrategyEditorView.cs:169`→`TryDecodeImage`（image/png→`Texture2D.LoadImage`→RawImage・`StrategyEditorContentBuilder.cs:116` が RawImage 兄弟を配線） | image/png は RawImage へ routing（decode 可時 active）、未対応 mimetype は `[mime]` labelled plain fallback。**image decode 能力 probe で分岐**: decode 可（HITL/GPU）＝RawImage active／decode 不可（headless batch）＝mimetype 伝播のみ AFK 確認（`[image/png]` ラベルが routing 非 collapse を証明）＋RawImage active は HITL 降格（S3/S8 と同型） | Section19: decode probe で分岐 assert・unsupported→`[application/json]` label | 自動(E2E済)（image decode active は HITL 降格） | `StrategyEditorNotebookE2ERunner`(S19) |
| STRATEGY-34 | console: stdout/stderr セグメント表示 / stderr amber / arrival 順保持 | `NotebookRunController.cs:198`→`SetConsole(ConsoleSegment[])`→`StrategyEditorView.SetConsole`→`BuildConsoleRichText`（stderr を `<color=#ffa01c>` でラップ） | `print('a')` で console 表示・`print(file=sys.stderr)` で amber `<color=...>` タグ付き・arrival 順は Python `_BridgedConsoleHook` が `host.stream.cell_id` で per-cell に保ち隣接 same-stream を collapse（marimo `collapseConsoleOutputs.tsx` parity） | Section21: stdout-only でカラータグなし / 混在で `<color=` 含む / `o1<e1<o2` の index 順保持 | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S21) ＋ `test_notebook_console.py` |
| STRATEGY-35 | dynamic layout: 出力 0 → 出力ブロック非表示 / editor が body 全高 | `StrategyEditorContentBuilder.cs`：body の `VerticalLayoutGroup` ＋ 各 block の `LayoutElement`（editor: flex=1 / 出力: pref=0 deactivated） | rich + console いずれも空 → 両ブロックの GameObject deactivate → LayoutGroup が skip → editor が body 全高を占有 | Section21: 初期＝両 visible=false / paint 後の空 SetOutput+SetConsole で再び両 visible=false | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S21) |
| STRATEGY-36 | dynamic layout: rich のみ populated → console 非表示 | `SetOutput` で rich block active / `SetConsole(null)` で console block deactivate（独立駆動） | rich-only 出力で console 不表示・互いに独立 | Section21: rich populated＋console 空で `RichBlockVisible==true && ConsoleBlockVisible==false` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S21) |
| STRATEGY-37 | dynamic layout: 両ブロック populated / 各ブロック cap=body*0.45 | `ApplyRichBlockSize` / `ApplyConsoleBlockSize` が `LayoutElement.preferredHeight = min(natural, body*OutputBlockMaxFractionOfBody)`、RectMask2D が overflow をクリップ → window 不伸長 | rich + console 両方表示・各 preferredHeight ≤ body * 0.45 + 1px（rebuild rounding tolerance） | Section21: 両 populated で両 visible=true ＋ `richLE.preferredHeight ≤ cap && conLE.preferredHeight ≤ cap` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S21) |
| STRATEGY-38 | cell rebind: 別 cell に bind 直し時に rich+console 両方クリア | `StrategyEditorView.Bind(cell)` が `SetOutput(null) + SetConsole(null) + SyncFromCell()` を順に呼ぶ | Bind 前は両 populated・Bind 後は両 visible=false（前回 cell の出力は新 cell に滲まない） | Section21: paint→`view.Bind(new Cell("y=1"))` で `RichBlockVisible==false && ConsoleBlockVisible==false` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S21) |
| STRATEGY-39 | escape regression: `&` リテラルが entity escape されない | `StrategyEditorView.EscapeForUguiRichText` が `<` のみ escape（UGUI は entity を decode しないので `&`→`&amp;` は字面のまま画面に出る pure regression・findings 0079 §6 D6） | `print("a & b")` 相当の stdout segment で `CurrentConsoleText` がリテラル `a & b` を含み `&amp;` を含まない | Section22: `exec.SetConsole({stdout,"a & b\n"})`→`view.RunCell→DrainAndRoute`→`CurrentConsoleText` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-40 | multi-cell routing: pressed cell + reactive descendant が**それぞれの**窓に paint・互いに滲まない | `NotebookRunController.ApplyResult` 200/206 行が `cells[co.Index]` → `RegionOf` で per-ran index→region routing | exec が `(idx=0, "from-c0")` と `(idx=1, "from-c1")` を返す press で、R1 console=from-c0／R2 console=from-c1、互いに leak しない | Section22: `exec.SetMulti((0,"from-cell-0"),(1,"from-cell-1"))`→`run.RunCell(R1)`→`view1.CurrentConsoleText`/`view2.CurrentConsoleText` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-41 | re-press REPLACE: 同 cell 再 press で**前回 text が混ざらない** | `StrategyEditorView.SetConsole` が `_consoleText.text = BuildConsoleRichText(...)` で**置換**（append ではない） | "round1" press→"round2" press 後、`CurrentConsoleText` が `round1` を**含まず** `round2` を含む | Section22: 2 連 press で 1st payload が 2nd 後の text から消失 | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-42 | re-press EMPTY: 同 cell の 2nd press が空 segment 列なら**ブロックが隠れる** | `SetConsole(null\|empty)` の早期 return が `_consoleBlock.gameObject.SetActive(false)` | 非空 press→ConsoleBlockVisible=true、空 press→ConsoleBlockVisible=false | Section22: non-empty→empty 連 press 後 `ConsoleBlockVisible==false` | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-43 | overflow → 本物の ScrollRect（findings 0079 §6 D5） | `StrategyEditorContentBuilder.BuildOutputBlock` が ScrollRect + Viewport + Content + 縦 Scrollbar を組み、`StrategyEditorView.ApplyBlockSize` が Block.LE.preferredHeight を min(Content.preferredHeight, body*0.45) で cap | 多行 payload（40 行）で `scroll.content.rect.height > scroll.viewport.rect.height + 1px`・`verticalNormalizedPosition = 0f / 1f` 設定が往復で stick | Section22: 40-line payload→content>viewport を assert＋`scroll.verticalNormalizedPosition`設定の round-trip を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-44 | bodyH==0 first-frame race: 初回描画でも block が見える | `StrategyEditorView.ApplyBlockSize` が `bodyH==0` ブランチで natural drives（cap collapse 回避） | adopted window のサイズ未確定状態（`ForceRebuildLayoutImmediate` priming なし）で press→`ConsoleBlockVisible==true`・`preferredHeight > 0` | Section22: adoptRoot を resize する**前に** press して `ConsoleBlockVisible` ＋ `LE.preferredHeight > 0` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-45 | `</color>` 注入耐性: amber wrapper を user payload が閉じない | `EscapeForUguiRichText` が `<` → `&lt;` 変換で全 stderr segment を escape してから `<color=#ffa01c>...</color>` で囲む | stderr segment に `</color>` を仕込んでも `CurrentConsoleText` に `&lt;/color>` が出現・OUR `<color=...>...</color>` wrapper が破られない（escaped が wrapper の内側） | Section22: stderr {"start","</color>middle","end"} payload で `amberOpen < escapedLeak < amberClose` の順序を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-46 | dormant-reuse race: AddCell/DeleteCell が in-flight run を drop（findings 0079 §6 D7） | `NotebookCellCoordinator.ListMutated` event を `NotebookRunController.Invalidate` に wiring。AddCell / DeleteCell / SyncWindowsToNotebook で generation bump → 古い request の result は drain 時に dropped | press R1（cellA）→DeleteCell(R1)→AddCell(cellC 再 bind R1)→DrainAndRoute で `ConsoleBlockVisible==false`・stale stdout が cellC の view に leak しない | Section22: synchronous lane で press→DeleteCell→AddCell→DrainAndRoute の sequence で view の console 不在を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S22) |
| STRATEGY-47 | #112 mode-aware launcher: LiveAuto+接続で per-cell RUN が **live run を起動**（Replay backtest lane ではなく `onLiveLaunch`） | `NotebookRunController.RunCell` が `liveLaunchActive()` を見て `LaunchLive`（▶→■ optimistic・`onLiveLaunch(region)`）へ分岐（ADR-0025 D3） | Auto mode で press→`onLiveLaunch` 1 回・executor 0 回・`IsBacktestRunning==false`・`IsLiveRunLaunched==true`・glyph ■ | Section23: fake live callback で `liveLaunches==[R1]`・`exec.Calls==0`・■ を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S23) |
| STRATEGY-48 | live run 走行中の 2nd press は **拒否**（二重起動なし） | `LaunchLive` が `_liveRunRegion != null ∨ liveRunActive()` で reject（`onError`） | live active 中に press→2nd `onLiveLaunch` 無し・error message 出る | Section23: 2nd press 後 `liveLaunches.Count==1`・`errors.Count>0` を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S23) |
| STRATEGY-49 | ■ → live stop（backtest ForceStop ではなく `onLiveStop`）・terminal で ▶ 復帰 | `StopRunning` が `_liveRunRegion != null` なら `onLiveStop`（stop_live_strategy）。`SyncLiveRunButton` が `liveRunActive()` false で ■→▶（ADR-0025 D3/D5） | ■ press→`onLiveStop` 1 回・`btForceStops==0`、Sync 後 `IsLiveRunLaunched==false`・glyph ▶ | Section23: `liveStops==1`・`btForceStops==0`・Sync 後 ▶ + `R1:False` event を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S23) |
| STRATEGY-50 | 同一 press が Replay mode では backtest を駆動（dispatch は mode 条件） | `liveLaunchActive()` false → 既存 backtest path（`_lane.Submit`） | Replay mode で press→executor 駆動・live 起動なし | Section23: `autoMode=false` で press→`exec.Calls>0`・`liveLaunches.Count==1`(不変) を assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S23) |

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
  **Section20 で昇格**（実 `StrategyEditorView` の `_placeholder` を反射読みし、CellCount 1↔2 遷移で hint 付与/解除＋body 非焼き込みを assert）。
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
- **STRATEGY-11（placeholder hint）litmus**: `NotebookCellCoordinator.UpdatePlaceholders` の `single ? HostApiHint : null` の
  `single`（CellCount==1）ゲートを外して常に `HostApiHint` を渡すと Section20 の「2 セルで非 active」assert が落ちる／hint を
  `Cell.Body` へ seed すると「body 空」assert が落ちる。
- **STRATEGY-19/20（per-cell RUN）litmus**: `StrategyEditorWindowFrame.EnsureRunButton` を消すと STRATEGY-19 が落ちる／
  `NotebookRunController.ApplyResult` の index→region routing を「常に cells[0] の region」へ collapse させると STRATEGY-20 が
  落ちる（**実証済み RED→GREEN**・findings 0071: `cells[co.Index]`→`cells[0]` で `region_001 did not show its own (cell 0)
  output, got [down-1]` を確認）。reactive 正しさ（pressed 子孫が走る/独立 cell が走らない）の litmus は Python pytest 側
  （`compute_cells_to_run` autorun を縮める/広げると RED）。
- **STRATEGY-21/22/23（bt.replay() control）litmus**: `NotebookRunController.RunCell` の `_btRunActive` early-return を消すと
  STRATEGY-22 が落ちる（2nd RUN が executor に到達する）／`drivesReplay` の判定を「常に false」にすると STRATEGY-21 が落ちる
  （running guard / glyph トグルが立たない）／`ApplyResult` の `_btRunActive=false` 解除を消すと STRATEGY-23 が落ちる（drain 後も
  RUNNING のまま・▶ に戻らない）。real bt drive・pacing・cross-thread stop は Python pytest 側（`test_notebook_replay_afk.py`）。
- **STRATEGY-24/25/26（bt.step() persistence）litmus**: `NotebookRunController.RunCell` の `drivesReplay` 判定（`bt.step` 単独で
  guard を立てない条件）を `drivesBacktest` に戻すと STRATEGY-24/25 が落ちる（step press でも guard が立ち 2 連打目が reject
  される）／scenario unset（`""`）で provider を返した時に backend が `NoScenarioBacktester` を inject する分岐を消すと
  STRATEGY-26 が落ちる（output に NameError が出る or output が空）。real bt cache 持続性・scenario commit reset・terminal
  finalize は Python pytest 側（`test_notebook_step_afk.py`）。
- **STRATEGY-27/28（per-cell stale badge）litmus**: `NotebookRunController.ApplyResult` の `RegionOf(cells[idx])` を `cells[0]` へ collapse すると
  STRATEGY-27 の「下流 region_002 も amber」が落ちる（全 stale が窓0へ寄る）／fake の Run が pressed index を stale 集合から drop しないと
  STRATEGY-28 の「press 後 region_001 green」が落ちる（amber 残留）。real marimo 下流 stale 伝播（`set_stale`）は Python pytest 側（`test_notebook_stale.py`）。
- **STRATEGY-29（block popup）litmus**: `NotebookRunController.RunCell` の `if (_btRunActive)` guard を消すと 2nd press が走り通知が出ず STRATEGY-29 が落ちる／
  通知を無条件化すると成功 press でも誤通知で落ちる。real cross-thread stop は Python pytest 側（`test_notebook_replay_afk.py`）。
- **STRATEGY-30/31（document badge #90）litmus**: `MarimoNotebookDocument.Open` が bind を止める（CurrentPath null 据え置き）と Open 後 badge が
  `Untitled` へ落ちて STRATEGY-30 が落ちる／`DocumentBadgeText` の `* ` prefix を消すと STRATEGY-31 が落ちる。
- **STRATEGY-32/33（rich output routing）litmus**: controller `ApplyResult` の Mimetype passthrough を外すと `[image/png]` ラベルが消え
  STRATEGY-33（AFK leaf）が落ちる／全 mimetype を Text へ collapse すると RawImage 非 active で STRATEGY-33（HITL leaf）が落ちる。real
  `FormattedOutput` 生成は Python pytest 側（`test_notebook_rich_output.py`）。
- **STRATEGY-34（console interleave + stderr amber）litmus**: `BuildConsoleRichText` の stderr 分岐（`<color=#ffa01c>` ラップ）を外すと
  amber が無くなり S21 の `<color=` 含有 assert が落ちる。`_BridgedConsoleHook._append` の adjacent-same-stream collapse を外しても
  arrival 順は保たれる（stream switch で必ず新セグメント）。real per-cell capture の正本は Python pytest 側（`test_notebook_console.py`）。
- **STRATEGY-35/36/37（dynamic layout）litmus**: 出力ブロックの `gameObject.SetActive(false)` を消すと空状態でも visible=true が残り
  STRATEGY-35 が落ちる／`ApplyRichBlockSize` / `ApplyConsoleBlockSize` の cap を外すと preferredHeight が natural 値そのままになり
  body の 0.45 を超えて STRATEGY-37 が落ちる／rich/console を片方の SetActive で連動させると STRATEGY-36 が落ちる。
- **STRATEGY-38（rebind clear）litmus**: `StrategyEditorView.Bind` の `SetConsole(null)` 呼び出しを消すと、前 cell の console が
  新 cell へ滲み出して S21 の post-Bind visibility=false assert が落ちる（`SetOutput(null)` 単体は既存テストでカバー）。
- **STRATEGY-39（`&` 二重エスケープ regression）litmus**: `EscapeForUguiRichText` に `Replace("&","&amp;")` を**復活**させると、`a & b` payload が `a &amp; b` で
  paint され S22 の `Contains("&amp;")` 否定 assert が落ちる（UGUI は entity を decode しないため pure regression・findings 0079 §6 D6）。
- **STRATEGY-40（multi-cell routing）litmus**: `NotebookRunController.ApplyResult` の `cells[co.Index]` を `cells[0]` に collapse すると、R2 への
  descendant 出力が R1 へ寄り S22 の `view2 missing 'from-cell-1'` assert が落ちる（STRATEGY-20 の親戚だが multi-ran 経路を pin）。
- **STRATEGY-41（re-press replace）litmus**: `StrategyEditorView.SetConsole` を `_consoleText.text += BuildConsoleRichText(...)`（append）に変えると S22 の
  `Contains("round1")` 否定 assert が落ちる。
- **STRATEGY-42（re-press empty hides）litmus**: `SetConsole` の `null\|empty` 早期 return から `_consoleBlock.gameObject.SetActive(false)` を消すと S22 の
  post-empty `ConsoleBlockVisible==false` assert が落ちる。
- **STRATEGY-43（overflow scroll）litmus**: `BuildOutputBlock` から ScrollRect を `RectMask2D`-only 構成に**戻す**と S22 の `scroll==null` ／
  `content > viewport` ／ `verticalNormalizedPosition` round-trip assert のいずれかが落ちる（findings 0079 §6 D5 を逆退させない pin）。
- **STRATEGY-44（first-frame paint）litmus**: `ApplyBlockSize` の `bodyH==0` ブランチ（`bodyH > 0f ? min(...) : natural`）を `bodyH * cap` 一本に戻すと
  S22 の `preferredHeight > 0` assert が落ちる（cap が 0 へ collapse する）。
- **STRATEGY-45（`</color>` 注入耐性）litmus**: `EscapeForUguiRichText` の `<` → `&lt;` 置換を消すと、user の `</color>` が**実際の閉じタグ**として paint され
  S22 の `Contains("&lt;/color>")` assert が落ちる（amber wrapper も実際に破られる）。
- **STRATEGY-46（dormant-reuse race）litmus**: `BackcastWorkspaceRoot.cs:388` の `_coordinator.ListMutated += () => _notebookRun.Invalidate();` を消す
  か、`NotebookCellCoordinator` の AddCell/DeleteCell から `ListMutated?.Invoke();` を抜くと、stale stdout が cellC の view へ leak し S22 の
  `Contains("stale-from-A")` 否定 assert が落ちる（generation bump が DeleteCell+AddCell で発火することを構造的に pin・findings 0079 §6 D7）。

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
> `FakeMarimoSynthesizer`）で、ComposeRoot 反射合成は未使用。STRATEGY-11（placeholder hint）は **#95 E2E 仕上げで Section20 として
> 昇格済み**（bare-RT＋実 `StrategyEditorView` の `_placeholder` 反射読み・findings 0076）。本台本の `要新規自動化` 残はゼロ。

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
