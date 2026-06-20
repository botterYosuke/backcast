# QuitConfirmE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`QuitConfirmE2ERunner.cs` が自動検証する **アプリ終了確認サーフェス**の台本（issue #89）。実装者は `.cs` と本 `.md` を
セットで読む。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。
canonical 契約と RED→GREEN は [findings 0068](../../../../docs/findings/0068-e2e-quit-confirm-runner.md)。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。終了判定（dirty→確認 / clean→即終了 / Save・Discard・Cancel・Save As
> 後処理）は純ロジック（`SaveGuardController` は `isDirty`/`isBound`/`pickerReturnedPath` を真理値注入する pure logic）
> なので**自動可**。batchmode 抑制（headless で確認しない）は配線不変条件、実 OS ウィンドウ close ＋実 native picker は
> *HITL*。

## 対象サーフェス

アプリ終了確認（頭脳 `SaveGuardController` ＋ chrome `SaveGuardOverlay` ＋ root 連携 `BackcastWorkspaceRoot` の
`Application.wantsToQuit` フック）。従来の **autosave-on-quit**（無言で開いているドキュメントを .py 上書き）を
**確認ダイアログ方式**に置換する（owner 判断①）。controller は時間も notebook 参照も持たない pure logic で、実
`Save()`/`SaveAs()`・native picker・`Application.Quit()` の配線は MonoBehaviour 側（findings 0068）。

## 対象ユーザー行動

OS ウィンドウを閉じる／終了する（`wantsToQuit`）→ dirty なら確認ダイアログ。ダイアログで「保存」「保存しない」
「キャンセル」を選ぶ。untitled（未バインド）で「保存」を選ぶと Save As ダイアログ（案A）——picker でパスが返れば
終了続行、cancel なら終了取りやめ。clean（非 dirty）終了はダイアログを出さず即終了。batchmode（headless）は確認抑制。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 / 状態遷移 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| QUIT-01 | clean（非 dirty）で終了 → ダイアログ出さず即終了 | `SaveGuardController.RequestProceed`／`BackcastWorkspaceRoot`(wantsToQuit) | `RequestProceed(false)==Proceed`・`!IsOpen` | clean→`RequestProceed`→`Proceed` を assert | 自動(E2E済) | `QuitConfirmE2ERunner`(RequestQuitGate) |
| QUIT-02 | dirty（bound）で終了 → 確認ダイアログ表示 | `SaveGuardController.RequestProceed` | `RequestProceed(true)==Confirm`・`IsOpen==true` | dirty→`RequestProceed`→`Confirm`＋`IsOpen` を assert | 自動(E2E済) | `QuitConfirmE2ERunner`(RequestQuitGate) |
| QUIT-03 | ダイアログで「保存」（bound） → 保存して終了 | `SaveGuardController.ChooseSave` | `ChooseSave(true)==SaveThenProceed`・`!IsOpen`（配線で .py 書込＋dirty 解除） | open→`ChooseSave(true)`→`SaveThenProceed`＋`!IsOpen` を assert | 自動(E2E済) | `QuitConfirmE2ERunner`(ChooseOutcomes) |
| QUIT-04 | ダイアログで「保存しない」 → 保存せず終了 | `SaveGuardController.ChooseDiscard` | `ChooseDiscard()==ProceedWithoutSave`・`!IsOpen`（on-disk 不変・dirty 据え置き・終了続行） | open→`ChooseDiscard`→`ProceedWithoutSave` を assert（選択前 `IsOpen` を liveness 確認） | 自動(E2E済) | `QuitConfirmE2ERunner`(ChooseOutcomes) |
| QUIT-05 | ダイアログで「キャンセル」 → 終了中断 | `SaveGuardController.ChooseCancel` | `ChooseCancel()==Abort`・`!IsOpen`（終了中断・残留） | open→`ChooseCancel`→`Abort` を assert（選択前 `IsOpen` を liveness 確認） | 自動(E2E済) | `QuitConfirmE2ERunner`(ChooseOutcomes) |
| QUIT-06 | dirty（untitled）で「保存」 → Save As；path→終了 / cancel→中断（案A） | `SaveGuardController.ChooseSave`／`ResolveSaveAs` | `ChooseSave(false)==SaveAsThenProceed`；`ResolveSaveAs(true)==SaveAsThenProceed`(commit) / `ResolveSaveAs(false)==Abort` | untitled で `ChooseSave(false)`→`SaveAsThenProceed`、続けて `ResolveSaveAs(true/false)` を両方 assert | 自動(E2E済) | `QuitConfirmE2ERunner`(SaveAsResolve) |
| QUIT-07 | dirty（untitled）で「保存しない」 → 保存せず終了 | `SaveGuardController.ChooseDiscard` | `ChooseDiscard()==ProceedWithoutSave`（isBound 非依存） | untitled でも `ChooseDiscard`→`ProceedWithoutSave` を assert | 自動(E2E済) | `QuitConfirmE2ERunner`(ChooseOutcomes) |
| QUIT-08 | batchmode（headless）では確認抑制 | `BackcastWorkspaceRoot`（`Application.isBatchMode` ガード） | batchmode では `wantsToQuit` を購読しない＝AFK `-quit` をモーダルで止めない | — | 要新規自動化 | — |
| QUIT-09 | 実 OS ウィンドウ×close ＋ 実 native picker | `BackcastWorkspaceRoot`(wantsToQuit)／native `SaveStrategyAs` | 実ウィンドウ close で確認ダイアログ、実 picker で path/cancel | — | HITL専用（実 OS ウィンドウ・OS ネイティブダイアログ依存） | — |
| QUIT-10 | bound「保存」後に書込失敗（still dirty）→ 終了中断・編集保全（#89 の核データ保護ガード） | `SaveGuardController.ResolveSave`／`BackcastWorkspaceRoot.OnQuitSave` | `ResolveSave(true)==SaveThenProceed`(commit) / `ResolveSave(false)==Abort`（saved=`!IsDirty`） | `ChooseSave(true)`→実 Save()→`ResolveSave(saved)` の **decision** を両値 assert。実 `.py` 書込と IsDirty 検出は配線/HITL | 自動(E2E済・decision のみ) | `QuitConfirmE2ERunner`(SaveResolve) |

> QUIT-08 は入力起点でない**配線不変条件**だが、終了ライフサイクルの不変条件として台帳に載せる。`_quitConfirmed`
> latch（2 回目の `wantsToQuit` が true）は配線側責務で別 Action 行にはしない（findings 0068）。
> QUIT-10 は `ResolveSaveAs`（案A）と対称な `ResolveSave` resolver で、bound Save のデータ保護判定（保存失敗→
> 終了中断）を pure gate で assert する。実 `Save()` 書込成否の **検出**（`!IsDirty`）と実ファイル I/O は配線/HITL。

## 観測点（詳細）

- **QUIT-01/02（終了判定ゲート）**: `RequestProceed(isDirty)` は dirty 判定だけで分岐する——非 dirty は `Proceed`（ダイアログ
  なし・`IsOpen` 据え置き false）、dirty は `Confirm`（`IsOpen=true`）。notebook 参照は持たず、dirty は引数注入。
- **QUIT-03/04/05/07（ダイアログ選択 outcome）**: `ChooseSave(isBound)` は bound→`SaveThenProceed`・untitled→`SaveAsThenProceed`、
  `ChooseDiscard()`→`ProceedWithoutSave`（isBound 非依存）、`ChooseCancel()`→`Abort`。いずれも `IsOpen=false` にし
  `LastOutcome` を更新。QUIT-04/05 の負側 outcome は選択前に `IsOpen==true` を先に確認して vacuous を回避。
- **QUIT-06（Save As 後処理・案A）**: untitled で `ChooseSave(false)` は `SaveAsThenProceed` を返してダイアログを閉じ、配線が
  native picker を開く。`ResolveSaveAs(pickerReturnedPath)` は path あり→`SaveAsThenProceed`（終了続行）、cancel→`Abort`
  （終了取りやめ＝ダイアログも再表示しない）。
- **QUIT-08（batchmode 抑制）**: headless では `Application.wantsToQuit` を購読しないので AFK `-quit` がモーダルでブロック
  されない。pure controller では検証できず実 `BackcastWorkspaceRoot` 反射 harness を要する（要新規自動化）。
- **QUIT-09（HITL）**: 実 OS ウィンドウの close ボタン／OS 終了による確認ダイアログ表示、および OS ネイティブ Save As picker は
  HITL（AFK 不可観測）。

## 自動判定（合格条件）

- ログに `[E2E QUIT CONFIRM PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(E2E済)` 行の観測点を 1 つでも落としたら `[E2E QUIT CONFIRM FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `RequestProceed` の dirty 分岐を消す → QUIT-02..07 RED。`ChooseSave` の `isBound`
  分岐を消す → QUIT-03/06 RED。`ResolveSaveAs` の cancel→`Abort` を消す → QUIT-06 の picker-cancel leg RED。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用` と `要新規自動化` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `QuitConfirmE2ERunner` | EditMode・pure C#（真理値注入） | **QUIT-01..07 の正本**。`SaveGuardController` を直接 new し `isDirty`/`isBound`/`pickerReturnedPath` を注入する pure-logic gate（findings 0068） |

> 従来の autosave-on-quit を確認ダイアログに置換するため、本サーフェスに先行 Probe は無い（全行新規オーサリング）。
> root 連携（QUIT-08 batchmode 抑制・latch）は実 `BackcastWorkspaceRoot` 反射 harness を要するため据え置き。

## 将来の `QuitConfirmE2ERunner.cs` 実装方針

- controller 単体の不変条件（QUIT-01..07）は `SaveGuardController` を直接 new する pure-logic セクションで assert
  （root 合成も Python も不要）。`SecretModalE2ERunner` と同型の Check-counter gate（`_fail` 累積→`EditorApplication.Exit`）。
  セクション: RequestQuitGate（QUIT-01/02）/ ChooseOutcomes（QUIT-03/04/05/07）/ SaveAsResolve（QUIT-06）。各 section に
  `Covers:` で Action ID を付与。
- QUIT-08（batchmode 抑制・latch）は **実 `BackcastWorkspaceRoot` 反射合成**（`OpenScene`→`ResolvePaths`→`BuildWorkspace`）
  harness を要するため本昇格では追加せず **要新規自動化** のまま（`SecretModalE2ERunner` の SECRET-07/08/09 と同方針）。
- QUIT-09 は HITL のため runner には載せない。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod QuitConfirmE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので **ripgrep / bash `grep -a`** で grep。
