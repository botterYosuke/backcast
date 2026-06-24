# FileNavGuardE2ERunner — 台本（横断 E2E 仕様 / 観測点 / 合格条件）

`FileNavGuardE2ERunner.cs`（#87 slice 3）が自動検証する **横断 E2E** の台本。実装者は `.cs` と本 `.md` を
セットで読む。Action ID 採番・カバー語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)
（命名・配置は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **位置づけ**: #89 の `SaveGuardController`（Save/Discard/Cancel 純判定・findings 0068）を **File→New / File→Open**
> の未保存ガードへ再利用した配線（#87 slice 3・findings 0069）を、実 `BackcastWorkspaceRoot` を反射駆動して観測する。
> SaveGuard 単体の純判定正本は [QuitConfirmE2ERunner](./QuitConfirmE2ERunner.md)（QUIT-01..10）。本台本は移送せず、
> **dirty な File 操作が SaveGuard を挟む横断配線**（defer / Cancel 据え置き / Discard 続行 / Save 続行 / データ保護
> Abort / clean 素通し / valid-marimo も確認必須 / **非 marimo は marimo-or-error で拒否**）を観測する。集約 `Open` の
> marimo-or-error 拒否（#113）は [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) `S10` /
> [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) が正本。

## owner-veto supersession（findings 0069 slice 3）

旧挙動「valid marimo `.py` への File→Open は dirty でも黙って切替（#86 §F1 が valid-marimo を refuse 対象外にしていた）」
を #87 で **意図的に廃する**。File→Open は valid-marimo への切替も含め、dirty なら必ず Save/Discard/Cancel を出す
（FILEGUARD-07）。ユーザーが操作ミスでコードを失わないことを、marimo/非marimo の別なく一様に保証する。

## 対象ストーリー

1. dirty な notebook で File→New すると確認（Save/Discard/Cancel）が出る（即クリアしない）。
2. Cancel すると現在の editor / universe / path が一切変化しない。
3. Discard すると New が続行し 1 空セル・universe 空・untitled へ落ちる。
4. Save（既存 path あり）すると保存してから New が続行する（編集は disk に残る）。
5. dirty な notebook で File→Open → Cancel すると現在 document が維持される（選択 `.py` は読まれない）。
6. dirty で **非 marimo** `.py` へ File→Open → 確認は出るが Discard しても **拒否**（#113 marimo-or-error「marimo notebook ではありません」）＝バッファは保全される（target が notebook でないので作業は失われない）。
7. dirty で **valid-marimo** `.py` へ File→Open しても確認が出る（owner-veto）。Discard で切替。
8. clean な document の File→New / File→Open は確認なしで即実行（ガードは過剰発火しない）。
9. dirty untitled で File→Open → Save（Save As）→ picker キャンセルなら **Abort**（編集を失わず Open も中止）。

## アーキテクチャ前提

- **判定は純 `SaveGuardController`**（findings 0068）。`RequestProceed(isDirty)`＝clean→Proceed / dirty→Confirm。
  `ChooseSave/ChooseDiscard/ChooseCancel` と `ResolveSave/ResolveSaveAs`（データ保護 resolver）。本 controller は
  UnityEngine も notebook も時間も持たない＝真理値で決定的に駆動できる。
- **配線は `BackcastWorkspaceRoot`**: `GuardThenProceed(Action)`（clean→即実行 / dirty→modal へ defer）が File→New
  （`OnFileNew`→`DoFileNew`）と File→Open（`OnFileOpen`→`DoFileOpen`）の入口。modal のボタンは `OnGuardSave/
  OnGuardDiscard/OnGuardCancel` に配線され、認可された `_guardProceed`（deferred Action）を走らせる。終了確認
  （`OnWantsToQuit`→`ConfirmAndQuit`）も同じ seam を共有する（#89 と #87 の統合）。
- **marimo-or-error Open**（#113・findings 0098）: `NotebookCellCoordinator.Open(path, positions)`→
  `MarimoNotebookDocument.Open(path)`。非 marimo `.py` は `_synth.Decompose` が null を返し Open が
  `Fail("not a marimo notebook")`＝**1-cell wrap せず拒否**。失敗 leg は `_cells` を一切触らないので dirty バッファは
  構造的に保全される（#86 F1 dirty-refuse / `discardDirty` 認可シームは #113 で退役＝もう不要）。
- 本 Runner は **全行 Python-FREE**（File 操作も SaveGuard も host 非依存・modeReq は disconnected で null）。
  `FakeMarimoSynthesizer` を注入（`FailDecompose=true` で非 marimo 拒否 leg を再現）。

## 操作一覧表（網羅台帳）

| Action ID | ステップ/行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 関連Surface台本 |
|---|---|---|---|---|---|---|
| FILEGUARD-01 | dirty File→New が defer | `BackcastWorkspaceRoot.cs:1603` `OnFileNew`→`1612 GuardThenProceed` | dirty→modal 開・clear 未実行（document 不変） | New invoke 後 `_saveGuardController.IsOpen`∧overlay 可視∧cell 数不変∧dirty を assert | 自動(E2E済) | [QuitConfirm](./QuitConfirmE2ERunner.md) QUIT-02 |
| FILEGUARD-02 | dirty File→New→Cancel | `BackcastWorkspaceRoot.cs:1820` `OnGuardCancel` | New 取りやめ・cells/dirty/universe/path 据え置き・modal 閉 | Cancel 後 cell 数==2∧dirty∧universe==1∧path=="" を assert | 自動(E2E済) | [QuitConfirm](./QuitConfirmE2ERunner.md) QUIT-05 |
| FILEGUARD-03 | dirty File→New→Discard | `BackcastWorkspaceRoot.cs:1813` `OnGuardDiscard`→`RunGuardProceed`→`DoFileNew` | New 続行・1 空セル・universe 空・untitled | Discard 後 cell 数==1∧空∧universe==0∧path=="" を assert | 自動(E2E済) | [AuthorToRun](./AuthorToRunJourneyE2ERunner.md) JOURNEY-AUTHOR-02 |
| FILEGUARD-04 | dirty(bound) File→New→Save | `BackcastWorkspaceRoot.cs:1791` `OnGuardSave`→`OnFileSave`→`ResolveSave`→`RunGuardProceed` | 編集を bound `.py` へ保存後 New 続行（保存→続行） | Save 後 disk が編集本文を持つ∧cell 数==1∧untitled を assert | 自動(E2E済) | [QuitConfirm](./QuitConfirmE2ERunner.md) QUIT-03 |
| FILEGUARD-05 | dirty File→Open→Cancel | `BackcastWorkspaceRoot.cs:1634` `OnFileOpen`→`1654 GuardThenProceed` / `OnGuardCancel` | 現在 document 維持・選択 `.py` 未読込 | Cancel 後 bound path 不変∧dirty∧本文不変 を assert | 自動(E2E済) | [QuitConfirm](./QuitConfirmE2ERunner.md) QUIT-05 |
| FILEGUARD-06 | dirty File→Open（非marimo）→Discard | `BackcastWorkspaceRoot.cs` `DoFileOpen`→`_coordinator.Open(py, positions)` | #113 marimo-or-error: 非 marimo は wrap せず拒否・buffer 保全 | prompt 出る∧Discard 後 **未 bind**（選択 `.py` に切替わらない）∧`LastError=="not a marimo notebook"`∧本文==`dirty_work`（保全）を assert（wrap leg を戻すと buffer が file body に置換され FAIL） | 自動(E2E済) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) S10, [FileOpenNonMarimo](./FileOpenNonMarimoE2ERunner.md) |
| FILEGUARD-07 | dirty File→Open（valid-marimo）も確認 | `BackcastWorkspaceRoot.cs:1654` `GuardThenProceed` | owner-veto: Decompose 成功（F1 不発）でも modal 開・Discard で切替 | Open 後 `IsOpen`（旧 silent-switch でないこと）∧Discard で bound==選択 を assert | 自動(E2E済) | — |
| FILEGUARD-08 | clean File→New/Open 素通し | `BackcastWorkspaceRoot.cs:1767` `GuardThenProceed`（Proceed 経路） | clean→modal 出さず即実行（ガード過剰発火なし） | clean File→Open で `IsOpen`==false∧選択 `.py` を即 load を assert | 自動(E2E済) | [BackcastWorkspaceProbe](../../../Editor/BackcastWorkspaceProbe.cs) S14 |
| FILEGUARD-09 | データ保護 Abort（Save キャンセル） | `BackcastWorkspaceRoot.cs:1805` `OnGuardSave`→`ResolveSaveAs(false)`→Abort | untitled Save→SaveAs picker cancel→still dirty→Abort（Open 中止・編集保持） | Save キャンセル後 unbound∧dirty∧本文不変∧選択 `.py` 未 load を assert | 自動(E2E済) | [QuitConfirm](./QuitConfirmE2ERunner.md) QUIT-10 |

## 自動検証する範囲（この Runner がゲートする）

- **defer/Cancel/Discard/Save の 4 verdict が File→New/Open で正しく分岐**する（FILEGUARD-01..05）。
- **非 marimo File→Open は marimo-or-error で拒否**され buffer が保全される（FILEGUARD-06・#113）。SaveGuard prompt は出るが Discard しても target が notebook でないので Open は失敗し作業は失われない。
- **owner-veto supersession**: valid-marimo 切替も dirty なら確認必須（FILEGUARD-07）。
- **clean は素通し**＝ガードは過剰発火しない（FILEGUARD-08・既存 reflective probe/runner が #87 で壊れない根拠）。
- **データ保護 Abort**: Save が編集を残したまま終わる（picker cancel）と Open も中止＝編集を失わない（FILEGUARD-09）。

## 自動検証しない範囲

- **実行中 run での File→New 拒否**（`FileNewDecision.RefusedRunning`）は live host を要するため [MenuBarHitlHarness](../../../Scripts/LiveSpike/MenuBarHitlHarness.cs) A4 が正本（本 Runner の guard は refuse 後の dirty 経路に限る）。
- **marimo cell 合成の実 Python 経路**（`FakeMarimoSynthesizer` で代替・round-trip 同値は layer 2/3 が再 assert）。
- **OS close の終了確認**（`OnWantsToQuit`→`ConfirmAndQuit`）は [QuitConfirmE2ERunner](./QuitConfirmE2ERunner.md) が正本（同 seam を共有）。

## 再走手順（AFK）

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod FileNavGuardE2ERunner.Run -logFile <log>
# expect: [E2E FILE-NAV-GUARD PASS] ... / exit=0  （確認は Bash `grep -a "FILE-NAV-GUARD"`）
# compile-only ゲート: -executeMethod を外した同コマンドで `error CS\d+` が 0 件。
# recompile-skip: .cs 編集直後の初回はコンパイルで終わり実行されない＝2 回目で走る。
# Unity = C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe
```
