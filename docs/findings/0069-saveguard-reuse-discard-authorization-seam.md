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
