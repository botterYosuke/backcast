# findings 0068 — issue #89: 終了時の確認ダイアログ（SaveGuardController + QuitConfirmE2ERunner）

**日付**: 2026-06-20
**issue**: #89（アプリ終了時に未保存ドキュメントの確認ダイアログを出す）
**対象サーフェス**: アプリ終了確認（純ロジック `SaveGuardController` ＋ chrome `SaveGuardOverlay` ＋ root 連携 `BackcastWorkspaceRoot` の `Application.wantsToQuit` フック）
**台本**: `Assets/Tests/E2E/Editor/QuitConfirmE2ERunner.md`

## 背景 — 何を変えるか

従来の終了は dirty な編集ドキュメント（`.py`）を**無言で破棄**していた（明示 `Save` しない限り in-memory 編集は失われる）。
`BackcastWorkspaceRoot.OnApplicationQuit`/`OnDestroy` → `StopAndDispose` → `AutosaveCurrentDocument` は走るが、これは
**layout sidecar `.json`（`TryWriteLayout` → `LayoutSidecarStore.WriteLayout`）だけを書く**――`.py` は一切書かない
（ヘッダ comment「quit autosaves into the open document」は誤解を招く表現で、実際に autosave されるのは layout のみ）。
issue #89 はこの**「dirty `.py` の無言破棄」を確認ダイアログ方式に置き換える**（owner 判断①）――終了時に dirty なら、
保存する / 保存しない / キャンセル をユーザーに問う。layout sidecar の autosave（`AutosaveCurrentDocument`）はドキュメント
保存とは別物なので確認フローに巻き込まない。

## owner 判断（確定）

1. **終了時は確認ダイアログ方式**（自動保存ではない）。dirty なら保存可否をユーザーに問う。
2. **untitled（未バインド）ドキュメントの dirty で「保存」を選んだとき**は **案A: Save As ダイアログ**を開く。
   picker でパスが返れば保存して終了、picker を cancel したら**終了を取りやめ**（中断）。

## canonical 契約 — `SaveGuardController`（純ロジック・UnityEngine-free・`SecretModalController` 踏襲）

時間や notebook 参照を持たない pure logic（AFK が真理値で決定的に駆動できる）。実 `Save()`/`SaveAs()`・native picker・
`Application.Quit()` の配線は MonoBehaviour 側。Controller は**判定だけ**。

```csharp
enum SaveGuardDecision { Proceed, Confirm }
enum SaveGuardOutcome  { SaveThenProceed, SaveAsThenProceed, ProceedWithoutSave, Abort }

SaveGuardDecision RequestProceed(bool isDirty)
  // !isDirty → Proceed  （IsOpen は false のまま・ダイアログ出さない）
  // isDirty  → Confirm  （IsOpen=true・ダイアログ表示）

// ダイアログ表示中（IsOpen==true）の選択。isBound は引数注入（Controller は notebook 参照を持たない）。
SaveGuardOutcome ChooseSave(bool isBound)
  // isBound  → SaveThenProceed    （IsOpen=false・LastOutcome=SaveThenProceed・terminal commit）
  // !isBound → SaveAsThenProceed   （IsOpen=false・LastOutcome=SaveAsThenProceed・後続 ResolveSaveAs を要する）
SaveGuardOutcome ChooseDiscard()  // → ProceedWithoutSave （IsOpen=false・LastOutcome=ProceedWithoutSave）
SaveGuardOutcome ChooseCancel()   // → Abort       （IsOpen=false・LastOutcome=Abort）

// Save 後処理（データ保護ガード・#89 の核）。ChooseSave(true) でダイアログを閉じた後の実 Save() 成否を解決する
// standalone resolver（IsOpen ガードはしない）。ResolveSaveAs と対称。
SaveGuardOutcome ResolveSave(bool saved)
  // true  → SaveThenProceed （保存成功＝終了続行・LastOutcome=SaveThenProceed）
  // false → Abort    （書込失敗で still dirty → 終了取りやめ・編集保全・LastOutcome=Abort）

// Save As 後処理（案A）。ChooseSave(false) でダイアログを閉じた後の picker 結果を解決する standalone resolver
// （IsOpen ガードはしない＝ダイアログは既に閉じている）。
SaveGuardOutcome ResolveSaveAs(bool pickerReturnedPath)
  // true  → SaveAsThenProceed （commit＝終了続行・LastOutcome=SaveAsThenProceed）
  // false → Abort       （picker cancel → 終了取りやめ・LastOutcome=Abort）
```

- **観測フィールド**: `bool IsOpen` / `SaveGuardOutcome LastOutcome`（AFK が真理値で叩ける）。`ChooseSave/Discard/Cancel`
  は `IsOpen==false` のとき no-op（`SecretModalController` の IsOpen ガード踏襲）。`Proceed` は `SaveGuardDecision`
  であって `SaveGuardOutcome` ではない（clean 終了は outcome を持たない）。
- **latch は Controller に持たせない**: 「2 回目の `wantsToQuit` が true を返す」は **配線側の責務**。
  `BackcastWorkspaceRoot` 側に `_quitConfirmed` latch を持ち、Save/Discard/SaveAs-commit 解決後に latch を立てて
  `Application.Quit()` を再呼び → 2 回目の `wantsToQuit` ハンドラは latch を見て true を返す。Controller は pure のまま
  （latch は Unity quit ライフサイクルの都合だから）。

## 配線（`BackcastWorkspaceRoot`）— step 5 で実装

- `Application.wantsToQuit` を購読（`BuildWorkspace`）。**batchmode（`Application.isBatchMode`）では購読しない**＝
  AFK `-quit` をモーダルで止めない（QUIT-08）。
- ハンドラ: `_quitConfirmed` latch が立っていれば `true`（終了許可）。そうでなければ
  `controller.RequestProceed(notebook.IsDirty)` → `Proceed` なら `true`（即終了）、`Confirm` なら overlay 表示して `false`（終了ブロック）。
- overlay ボタン → `ChooseSave(isBound)`/`ChooseDiscard()`/`ChooseCancel()`:
  - `SaveThenProceed` → 実 `Save()`（`OnFileSave` 相当） → latch → `Application.Quit()`
  - `SaveAsThenProceed` → native picker → `ResolveSaveAs(path!=null)`：commit なら 実 `SaveAs()`＋latch＋`Quit()` / abort なら overlay 閉じて据え置き（終了しない）
  - `ProceedWithoutSave` → latch → `Quit()`（保存しない・dirty 据え置きだが終了する）
  - `Abort` → overlay 閉じて据え置き（終了しない）
- **「autosave 撤去」は no-op**（step 5 着手時に確認済み 2026-06-20）: 現行 quit 経路に**撤去すべきドキュメント `.py` autosave は存在しない**。
  `AutosaveCurrentDocument` は `TryWriteLayout`（layout sidecar `.json`）だけを書き、`.py` は `OnFileSave`/`OnFileSaveAs` の
  `_coordinator.Save()`/`SaveAs()` でしか書かれない。よって step 5 は「.py autosave の撤去」ではなく**確認ダイアログ経路の追加**であり、
  `AutosaveCurrentDocument`（layout sidecar）は KEEP し確認フローに巻き込まない（layout 永続はドキュメント保存とは別物）。

## E2E マトリクス（owner 承認済みの挙動 → QUIT-01..09）

| QUIT | 条件 → 挙動 | 観測 |
|---|---|---|
| 01 | clean → Proceed（ダイアログ出ない） | `RequestProceed(false)==Proceed`・`!IsOpen` |
| 02 | dirty+bound → Confirm | `RequestProceed(true)==Confirm`・`IsOpen` |
| 03 | dirty+bound+Save → SaveThenProceed | `ChooseSave(true)==SaveThenProceed`・`!IsOpen`（配線で .py 書込＋dirty 解除） |
| 04 | dirty+Discard → ProceedWithoutSave | `ChooseDiscard()==ProceedWithoutSave`（on-disk 不変・dirty 据え置き・終了続行） |
| 05 | dirty+Cancel → Abort | `ChooseCancel()==Abort`・`!IsOpen`（終了中断・残留） |
| 06 | dirty+untitled+Save → SaveAsThenProceed | `ChooseSave(false)==SaveAsThenProceed`；`ResolveSaveAs(true)==SaveAsThenProceed`(commit) / `ResolveSaveAs(false)==Abort`(案A) |
| 07 | dirty+untitled+Discard → ProceedWithoutSave | `ChooseDiscard()==ProceedWithoutSave`（isBound 非依存） |
| 08 | batchmode → 確認抑制 | headless は `wantsToQuit` を購読しない＝決してブロックしない（配線不変条件） |
| 09 | 実 OS ウィンドウ×close ＋ 実 native picker | HITL（AFK 不可観測） |
| 10 | bound Save 後 書込失敗(still dirty) → 終了中断・編集保全 | `ResolveSave(true)==SaveThenProceed`(commit) / `ResolveSave(false)==Abort`（decision のみ自動。実 `.py` 書込と `!IsDirty` 検出は配線/HITL） |

## RED→GREEN 計画

- **gate 形**: `SecretModalE2ERunner` と同型の pure-logic AFK gate（`SaveGuardController` を直接 `new` し、`isDirty`/
  `isBound`/`pickerReturnedPath` を真理値注入）。Check-counter（`_fail` 累積→`EditorApplication.Exit`）形。
  PASS タグ `[E2E QUIT CONFIRM PASS]` / FAIL `[E2E QUIT CONFIRM FAIL]`。
- **RED litmus（delete-the-production-logic）**:
  - `RequestProceed` の dirty 分岐を消し常に `Proceed` → QUIT-02..07 が RED（dirty でも Confirm にならない）。
  - `ChooseSave` の `isBound` 分岐を消す → QUIT-03 か QUIT-06 が RED（bound/untitled の振り分け破綻）。
  - `ResolveSaveAs` の cancel→`Abort` を消す → QUIT-06 の picker-cancel leg が RED（案A 破綻）。
- **vacuity 回避**: QUIT-04/05（`ProceedWithoutSave`/`Abort` の負側）は、選択前に `IsOpen==true`（liveness/presence）を
  先に assert してから outcome＋`!IsOpen` を assert する。
- QUIT-08（batchmode 抑制）は配線不変条件で pure controller では検証できず **要新規自動化** 据え置き（将来 root 反射 harness。
  `SecretModalE2ERunner` の SECRET-07/08/09 と同方針）。QUIT-09 は **HITL専用**。

## 再走手順（AFK）

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod QuitConfirmE2ERunner.Run -logFile <log>
# expect: [E2E QUIT CONFIRM PASS] ... / exit=0
# compile-only ゲート: -executeMethod を外した同コマンドで `error CS\d+` が 0 件。
# Unity ログは UTF-8 → bash `grep -a "[E2E QUIT CONFIRM PASS]"`（recompile-skip: .cs 編集直後の初回は
# コンパイルで終わり実行されない＝2 回目で走る。flush race: shutdown sentinel `Found no leaked weakptrs` を待って再 grep）。
```

## 据え置き / 仕分け

- QUIT-08（batchmode 抑制・latch）= 要新規自動化（実 `BackcastWorkspaceRoot` 反射 harness を要する）。
- QUIT-09（実 OS ウィンドウ close・実 native picker）= HITL専用（実 OS ウィンドウ・OS ネイティブダイアログ依存）。
- QUIT-10（save-failure → quit abort）= **decision は自動(E2E済)・実 I/O は HITL**（2026-06-20 更新）。当初は「pure controller では検証不能」として全体 deferred だったが、`ResolveSaveAs`（案A）と対称な **`ResolveSave(bool saved)` resolver** を controller に足し、配線 `OnQuitSave` の bound-Save 枝を `ChooseSave(true)`→実 `Save()`→`ResolveSave(_notebook!=null && !_notebook.IsDirty)` と流すようにした。これで「保存成功→終了続行 / 書込失敗→Abort（終了中断・編集保全）」という **#89 の核データ保護判定**が `QuitConfirmE2ERunner.SaveResolve` で両値 assert され、delete-the-production-logic litmus（`ResolveSave` の `false→Abort` を消す→QUIT-10 RED）が効く。**残る HITL** は実 `Save()` の書込成否そのものと `!IsDirty` 検出（pure gate は実 `Save()` を持たない）＝書込失敗を擬似注入、または read-only パスへ保存して終了が中断されドキュメントが保全されることを目視確認する。なお同じ data-protection 判定の untitled 側は QUIT-06 の `ResolveSaveAs(false)→Abort` で既に自動カバー済み。
