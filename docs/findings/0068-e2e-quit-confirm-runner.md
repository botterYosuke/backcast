# findings 0068 — issue #89: 終了時の確認ダイアログ（QuitConfirmController + QuitConfirmE2ERunner）

**日付**: 2026-06-20
**issue**: #89（アプリ終了時に未保存ドキュメントの確認ダイアログを出す）
**対象サーフェス**: アプリ終了確認（純ロジック `QuitConfirmController` ＋ chrome `QuitConfirmOverlay` ＋ root 連携 `BackcastWorkspaceRoot` の `Application.wantsToQuit` フック）
**台本**: `Assets/Tests/E2E/Editor/QuitConfirmE2ERunner.md`

## 背景 — 何を変えるか

従来の終了は **autosave**（`BackcastWorkspaceRoot.OnApplicationQuit` → `StopAndDispose` → `AutosaveCurrentDocument`、
ヘッダ comment「quit autosaves into the open document」）で、開いているドキュメントを**無言で .py に上書き保存**していた。
issue #89 はこれを **確認ダイアログ方式**に置き換える（owner 判断①）——終了時に未保存（dirty）なら、保存する / 保存しない /
キャンセル をユーザーに問う。autosave はしない。

## owner 判断（確定）

1. **終了時は確認ダイアログ方式**（自動保存ではない）。dirty なら保存可否をユーザーに問う。
2. **untitled（未バインド）ドキュメントの dirty で「保存」を選んだとき**は **案A: Save As ダイアログ**を開く。
   picker でパスが返れば保存して終了、picker を cancel したら**終了を取りやめ**（中断）。

## canonical 契約 — `QuitConfirmController`（純ロジック・UnityEngine-free・`SecretModalController` 踏襲）

時間や notebook 参照を持たない pure logic（AFK が真理値で決定的に駆動できる）。実 `Save()`/`SaveAs()`・native picker・
`Application.Quit()` の配線は MonoBehaviour 側。Controller は**判定だけ**。

```csharp
enum QuitDecision { QuitNow, Confirm }
enum QuitOutcome  { SaveThenQuit, SaveAsThenQuit, QuitWithoutSave, AbortQuit }

QuitDecision RequestQuit(bool isDirty)
  // !isDirty → QuitNow  （IsOpen は false のまま・ダイアログ出さない）
  // isDirty  → Confirm  （IsOpen=true・ダイアログ表示）

// ダイアログ表示中（IsOpen==true）の選択。isBound は引数注入（Controller は notebook 参照を持たない）。
QuitOutcome ChooseSave(bool isBound)
  // isBound  → SaveThenQuit    （IsOpen=false・LastOutcome=SaveThenQuit・terminal commit）
  // !isBound → SaveAsThenQuit   （IsOpen=false・LastOutcome=SaveAsThenQuit・後続 ResolveSaveAs を要する）
QuitOutcome ChooseDiscard()  // → QuitWithoutSave （IsOpen=false・LastOutcome=QuitWithoutSave）
QuitOutcome ChooseCancel()   // → AbortQuit       （IsOpen=false・LastOutcome=AbortQuit）

// Save As 後処理（案A）。ChooseSave(false) でダイアログを閉じた後の picker 結果を解決する standalone resolver
// （IsOpen ガードはしない＝ダイアログは既に閉じている）。
QuitOutcome ResolveSaveAs(bool pickerReturnedPath)
  // true  → SaveAsThenQuit （commit＝終了続行・LastOutcome=SaveAsThenQuit）
  // false → AbortQuit       （picker cancel → 終了取りやめ・LastOutcome=AbortQuit）
```

- **観測フィールド**: `bool IsOpen` / `QuitOutcome LastOutcome`（AFK が真理値で叩ける）。`ChooseSave/Discard/Cancel`
  は `IsOpen==false` のとき no-op（`SecretModalController` の IsOpen ガード踏襲）。`QuitNow` は `QuitDecision`
  であって `QuitOutcome` ではない（clean 終了は outcome を持たない）。
- **latch は Controller に持たせない**: 「2 回目の `wantsToQuit` が true を返す」は **配線側の責務**。
  `BackcastWorkspaceRoot` 側に `_quitConfirmed` latch を持ち、Save/Discard/SaveAs-commit 解決後に latch を立てて
  `Application.Quit()` を再呼び → 2 回目の `wantsToQuit` ハンドラは latch を見て true を返す。Controller は pure のまま
  （latch は Unity quit ライフサイクルの都合だから）。

## 配線（`BackcastWorkspaceRoot`）— step 5 で実装

- `Application.wantsToQuit` を購読（`BuildWorkspace`）。**batchmode（`Application.isBatchMode`）では購読しない**＝
  AFK `-quit` をモーダルで止めない（QUIT-08）。
- ハンドラ: `_quitConfirmed` latch が立っていれば `true`（終了許可）。そうでなければ
  `controller.RequestQuit(notebook.IsDirty)` → `QuitNow` なら `true`（即終了）、`Confirm` なら overlay 表示して `false`（終了ブロック）。
- overlay ボタン → `ChooseSave(isBound)`/`ChooseDiscard()`/`ChooseCancel()`:
  - `SaveThenQuit` → 実 `Save()`（`OnFileSave` 相当） → latch → `Application.Quit()`
  - `SaveAsThenQuit` → native picker → `ResolveSaveAs(path!=null)`：commit なら 実 `SaveAs()`＋latch＋`Quit()` / abort なら overlay 閉じて据え置き（終了しない）
  - `QuitWithoutSave` → latch → `Quit()`（保存しない・dirty 据え置きだが終了する）
  - `AbortQuit` → overlay 閉じて据え置き（終了しない）
- **autosave 撤去**: 現行の quit 経路の**ドキュメント .py autosave を撤去**し確認フローに置換する。`OnDestroy`/`OnApplicationQuit`
  の layout 永続（PlayerPrefs の resume ポインタ等・ドキュメント保存とは別）を巻き込まないこと——step 5 着手時にこの境界を
  Navigator が再確認する（要確認: `AutosaveCurrentDocument` がドキュメント .py を書くのか layout.json だけか）。

## E2E マトリクス（owner 承認済みの挙動 → QUIT-01..09）

| QUIT | 条件 → 挙動 | 観測 |
|---|---|---|
| 01 | clean → QuitNow（ダイアログ出ない） | `RequestQuit(false)==QuitNow`・`!IsOpen` |
| 02 | dirty+bound → Confirm | `RequestQuit(true)==Confirm`・`IsOpen` |
| 03 | dirty+bound+Save → SaveThenQuit | `ChooseSave(true)==SaveThenQuit`・`!IsOpen`（配線で .py 書込＋dirty 解除） |
| 04 | dirty+Discard → QuitWithoutSave | `ChooseDiscard()==QuitWithoutSave`（on-disk 不変・dirty 据え置き・終了続行） |
| 05 | dirty+Cancel → AbortQuit | `ChooseCancel()==AbortQuit`・`!IsOpen`（終了中断・残留） |
| 06 | dirty+untitled+Save → SaveAsThenQuit | `ChooseSave(false)==SaveAsThenQuit`；`ResolveSaveAs(true)==SaveAsThenQuit`(commit) / `ResolveSaveAs(false)==AbortQuit`(案A) |
| 07 | dirty+untitled+Discard → QuitWithoutSave | `ChooseDiscard()==QuitWithoutSave`（isBound 非依存） |
| 08 | batchmode → 確認抑制 | headless は `wantsToQuit` を購読しない＝決してブロックしない（配線不変条件） |
| 09 | 実 OS ウィンドウ×close ＋ 実 native picker | HITL（AFK 不可観測） |

## RED→GREEN 計画

- **gate 形**: `SecretModalE2ERunner` と同型の pure-logic AFK gate（`QuitConfirmController` を直接 `new` し、`isDirty`/
  `isBound`/`pickerReturnedPath` を真理値注入）。Check-counter（`_fail` 累積→`EditorApplication.Exit`）形。
  PASS タグ `[E2E QUIT CONFIRM PASS]` / FAIL `[E2E QUIT CONFIRM FAIL]`。
- **RED litmus（delete-the-production-logic）**:
  - `RequestQuit` の dirty 分岐を消し常に `QuitNow` → QUIT-02..07 が RED（dirty でも Confirm にならない）。
  - `ChooseSave` の `isBound` 分岐を消す → QUIT-03 か QUIT-06 が RED（bound/untitled の振り分け破綻）。
  - `ResolveSaveAs` の cancel→`AbortQuit` を消す → QUIT-06 の picker-cancel leg が RED（案A 破綻）。
- **vacuity 回避**: QUIT-04/05（`QuitWithoutSave`/`AbortQuit` の負側）は、選択前に `IsOpen==true`（liveness/presence）を
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
