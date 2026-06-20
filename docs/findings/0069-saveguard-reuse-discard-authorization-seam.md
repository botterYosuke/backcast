# findings 0069 — issue #87: SaveGuard を File 操作へ再利用（slice 2: MarimoNotebookDocument discard 認可シーム）

**日付**: 2026-06-20
**issue**: #87（#89 で作った終了確認 SaveGuard を File→New / File→Open の未保存ガードへ再利用する）
**対象サーフェス**: 純コア集約 `MarimoNotebookDocument`（`Open` の #86 F1 dirty-refuse ガード）
**台本**: `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.md`（STRATEGY-16）／ゲート: `StrategyEditorNotebookE2ERunner.cs` S10

## 背景 — slice の位置づけ

#87 は #89 の `SaveGuardController`（pure な Save/Discard/Cancel 判定・findings 0068）を **File→New / File→Open** の
未保存ドキュメントガードへ再利用するエピック。

- **slice 1（済）**: `QuitConfirm* guard` を `SaveGuard*` へ rename（終了専用名 → File 操作でも使える reusable nav guard）。
  #89 characterization（QUIT-01..10）は rename 後も全件再 GREEN（commit `f57b80f`）。
- **slice 2（本 findings）**: `MarimoNotebookDocument` 側に **discard 認可シーム**を開ける。

## 何を変えるか — #86 F1 ガードの認可付き緩和

`MarimoNotebookDocument.Open(string path)` は #86 F1 で「dirty な notebook に非 marimo `.py` を開こうとしたら REFUSE」
する（未保存編集を 1-cell wrap で無言上書きしない fail-soft）。これは集約が不変条件を守る hard guard で、上位 UX が
「破棄してよい」とユーザー承認を取った場合でも緩める術が無かった（0054 §F1 が「discard-confirm modal は higher-layer
UX slice・out of scope」と据え置いていた箇所）。

slice 2 は `Open` に **`discardDirty`（既定 false）** を足す:

```csharp
public bool Open(string path, bool discardDirty = false)
...
    if (_dirty && !discardDirty) return Fail("dirty workspace — Save or File→New before opening a non-marimo .py");
```

- `discardDirty:false`（既定）= **F1 不変**。既存呼び出し（coordinator・restore・全 probe/runner）は挙動も契約も不変。
- `discardDirty:true` = 上位の **SaveGuard「Discard」判定**を得た呼び出しだけが渡す。dirty な `_cells` を破棄して
  新ファイルを 1-cell wrap・bind・clean 化する（既存 wrap 経路へ素通し）。**集約は pre-clear しない**（呼び出し側で
  `ResetUnboundEmpty` してから `Open` する案を退け、`Open` 内で原子的に discard+wrap する＝中途半端な空状態を作らない）。

## owner / 設計判断（grill 確定）

1. **pre-clear せず `Open(py, discardDirty:true)` で F1 を緩める**（集約が discard+wrap を原子的に行う）。
2. **既定は F1 維持**（`discardDirty=false`）＝認可は呼び出し側の明示 opt-in。無認可の File→Open は従来どおり refuse。
3. SaveGuard→Discard→`Open(discardDirty:true)` の **上位配線（BackcastWorkspaceRoot の File→Open フック）は slice 3**。
   本 slice は集約シームのみ。

## RED→GREEN

gate: `StrategyEditorNotebookE2ERunner.cs` の S10（#81 notebook aggregate）に **F1-DISCARD leg（nb7）** を追加。

- **RED**: production の param 追加前に characterization を先に著す → compile-only ゲートで
  `error CS1739: The best overload for 'Open' does not have a parameter named 'discardDirty'`（exit 1）。
- **GREEN**: `Open` に `discardDirty=false` を足し guard を `_dirty && !discardDirty` に緩める →
  compile-only `error CS\d+` 0 件 → `-executeMethod StrategyEditorNotebookE2ERunner.Run` が exit 0 + `[E2E STRATEGY NOTEBOOK PASS]`。

**非 vacuous litmus（two stakes, mutually protective）**:
- guard の `&& !discardDirty` を消す（常に refuse）→ **F1-DISCARD leg（nb7）が RED**（認可 discard が拒否される）。
- `_dirty` guard を丸ごと消す（常に wrap）→ **F1-refuse leg（nb5）が RED**（無認可 dirty Open が上書きしてしまう）。

F1-DISCARD leg の assert: dirty 2-cell → `Open(rawPath2, discardDirty:true)` が true・CellCount==1・body==新ファイル本文
（古い dirty cell が残っていない）・name=="_"・IsBound・!IsDirty・WrapMode・LastError==null・supplyable。

## 再走手順（AFK）

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod StrategyEditorNotebookE2ERunner.Run -logFile <log>
# expect: [E2E STRATEGY NOTEBOOK PASS] ... / exit=0
# compile-only ゲート: -executeMethod を外した同コマンドで `error CS\d+` が 0 件。
# Unity ログは UTF-8 → bash `grep -a "[E2E STRATEGY NOTEBOOK PASS]"`（recompile-skip: .cs 編集直後の初回は
# コンパイルで終わり実行されない＝2 回目で走る）。Unity = C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe。
```

## 据え置き / 次 slice

- **slice 3**: `BackcastWorkspaceRoot` の File→Open フックを SaveGuard 経由にし、`Discard` 判定時に
  `_coordinator.Open(py, positions, discardDirty:true)` を呼ぶ配線（coordinator passthrough を含む）。
  ユーザー操作行 STRATEGY-16 の台本カバー更新（discard UX 行）はこの wiring slice で行う（今は集約シームのみで
  user-reachable でないため台本 action table は据え置き）。
- File→New 側（`ResetUnboundEmpty`）は dirty でも破壊しない（unbound 空へ reset するだけ）ので F1 のような refuse は無い；
  SaveGuard の絡みは「dirty で File→New したら Save/Discard を問う」UX のみ（slice 3 で配線）。

## 追記（2026-06-20・slice 3: `BackcastWorkspaceRoot` の File→New/Open ガード配線）

slice 2 の集約シーム（`Open(discardDirty)`）の上位配線を実装した。#89 の終了確認 SaveGuard を File 操作へ一般化し、
**dirty な File→New / File→Open が即実行せず Save/Discard/Cancel を挟む**ようにした。

### 何を配線したか

- **`_guardProceed`（deferred `Action`）**: dirty で defer された「続行アクション」（Quit / New / Open）を 1-shot で保持。
- **`GuardThenProceed(Action)`**: clean（または未配線）→ `proceed()` を即実行 / dirty → `OpenSaveGuard(proceed)` で modal へ defer。
  File→New（`OnFileNew`→`DoFileNew`）と File→Open（`OnFileOpen`→`DoFileOpen`）の入口。picker は guard の **前**（cancel は no-op）。
- **`OnGuardSave/OnGuardDiscard/OnGuardCancel`**（旧 `OnQuit*` を rename・一般化）: overlay ボタンに配線。Save→実 Save/SaveAs→
  `ResolveSave/ResolveSaveAs`→認可されたら `RunGuardProceed()`（=`_guardProceed`）/ Discard→即 `RunGuardProceed()` / Cancel→
  `_guardProceed=null`。終了確認（`OnWantsToQuit`→`OpenSaveGuard(ConfirmAndQuit)`）も同じ seam を共有（#89↔#87 統合）。
- **`DoFileOpen` は `_coordinator.Open(py, positions, discardDirty:true)`** を呼ぶ。`DoFileOpen` に到達した時点で guard は既に
  「clean / 保存済 / 明示 Discard」のいずれかで discard を認可しているので、`discardDirty:true` は常に正しい（guard が認可そのもの）。

### owner-veto supersession（owner 確定・confirmation #1）

旧挙動「valid marimo `.py` への File→Open は dirty でも黙って切替」（#86 §F1 が valid-marimo を refuse 対象外にしていた、
CONTEXT L389 旧文）を **#87 で意図的に廃止**。File→Open は valid-marimo 切替も含め、dirty なら必ず確認を出す（marimo/非marimo
一様）。集約 F1（`MarimoNotebookDocument.Open` の dirty-refuse）は **slice 2 のまま不変**——supersede したのは **root/UX 層**で、
root が aggregate を呼ぶ前に SaveGuard で包む。`FakeMarimoSynthesizer`（`FailDecompose=false`）の Decompose 成功経路＝この
silent-switch 経路が、`FileNavGuardE2ERunner` `FILEGUARD-07` で「dirty なら確認必須」として固定される。

### 同 slice の必須回帰修正（grill で捕捉）

`AuthorToRunJourneyE2ERunner` `JOURNEY-AUTHOR-02` は dirty な notebook に `OnFileNew` を invoke し **即 clear** を assert していた。
#87 で File→New が dirty 時に defer するため、この assert は壊れる。修正: invoke 後に **(a) defer の非空虚 assert**（cell 数==2 の
まま＝clear されていない）→ **(b) `OnGuardDiscard` invoke**（Discard で続行）→ **(c) clear assert**（cell 数==1）。

### 他の reflective 呼出し側の監査（不変）

`OnFileNew/OnFileOpen` を反射 invoke する他の probe/runner——`BackcastWorkspaceProbe` S14 / `MenuBarCutoverProbe` /
`RunButtonE2ERunner` / `ReplayToHakoniwaE2ERunner` / `LayoutPersistenceJourneyE2ERunner`——はすべて **clean notebook 上**で
invoke する（新規 compose の `MarimoNotebookDocument` は constructor が 1 空セルを `AddCell` 経由でなく直接足すので `_dirty=false`、
Open/Save 後も clean）。clean→`RequestProceed`=Proceed→即実行なので **挙動・契約とも不変**（FILEGUARD-08 がこの「過剰発火しない」
不変を固定）。`OnQuit*`→`OnGuard*` の rename も root 内のみの参照で probe/runner 非反射＝安全。

### 新規ゲート

- **`FileNavGuardE2ERunner.cs`（`FILEGUARD-01..09`）** + 台本 `FileNavGuardE2ERunner.md`。Python-FREE・実 root 反射駆動。
  defer / Cancel 据え置き / Discard 続行 / Save 続行 / データ保護 Abort / clean 素通し / valid-marimo も確認（owner-veto）を観測。
- STRATEGY-16 行（`StrategyEditorNotebookE2ERunner.md`）に discard UX 横断配線への相互参照を追記。

### RED→GREEN / litmus

- **RED**: production 配線前に runner を著すと `OnGuardSave/OnGuardDiscard/OnGuardCancel`/`GuardThenProceed`/`DoFileOpen` 未定義で
  compile-only ゲートが `error CS\d+`。
- **GREEN**: 配線後 compile-only `error CS\d+` 0 件 → `-executeMethod FileNavGuardE2ERunner.Run` が exit 0 + `[E2E FILE-NAV-GUARD PASS]`。
- **delete-the-production-logic litmus**:
  - `OnFileNew` の `GuardThenProceed` を外し `DoFileNew` 直呼び → FILEGUARD-01/02/03 RED。
  - `OnFileOpen` の `GuardThenProceed` を外し `DoFileOpen` 直呼び → FILEGUARD-05/07 RED（dirty が黙って切替）。
  - `DoFileOpen` の `discardDirty:true` を既定 false に戻す → FILEGUARD-06 RED（F1 refuse で buffer 据え置き）。
  - `OnGuardSave` のデータ保護（`ResolveSaveAs(false)`→Abort）を常に proceed に壊す → FILEGUARD-09 RED。

### 再走手順（AFK）

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod FileNavGuardE2ERunner.Run -logFile <log>
# expect: [E2E FILE-NAV-GUARD PASS] / exit=0  （Bash `grep -a "FILE-NAV-GUARD"`）
# AuthorToRun 回帰: -executeMethod AuthorToRunJourneyE2ERunner.Run → [E2E AUTHOR→RUN PASS]（`grep -a` ・→ 取りこぼし注意）
# 終了確認の純判定: -executeMethod QuitConfirmE2ERunner.Run（QUIT-01..10 不変・rename 後も GREEN）
# compile-only ゲート: -executeMethod を外した同コマンドで `error CS\d+` が 0 件。
# Unity = C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe
```
