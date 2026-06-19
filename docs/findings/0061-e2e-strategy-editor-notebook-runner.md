# findings 0061 — E2E 第二波8本目: StrategyEditorNotebookE2ERunner 昇格

**日付**: 2026-06-19
**issue**: #94（E2E 第二波 runner 昇格トラッカー）
**対象サーフェス**: Strategy Editor / marimo notebook（cell-as-floating-window・ADR-0013・findings 0050）
**台本**: `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.md`

## やったこと

throwaway AFK probe `Assets/Editor/StrategyEditorProbe.cs`（issue #16 の Strategy Editor 回帰 probe）を
`Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.cs` へ **git mv＋改名**で昇格（ADR-0015 命名規約。
先例 ScenarioStartup=0054 / FooterMode=0055 / InfiniteCanvas=0056 / FloatingWindow=0057 / UniverseSidebar=0058 /
DepthLadder=0059 / Hakoniwa=0060）。`.cs.meta` も git mv で GUID（371a9c08916c04384846016229b65fa6）保全。

- クラス `StrategyEditorProbe` → `StrategyEditorNotebookE2ERunner`、`-executeMethod` 名も同改名。
- PASS/FAIL タグ: `[STRATEGY EDITOR PASS/FAIL]` → `[E2E STRATEGY NOTEBOOK PASS/FAIL]`（台本 .md の自動判定に一致）。
  内部警告タグ `[STRATEGY EDITOR] Sn` → `[E2E STRATEGY NOTEBOOK] Sn`。
- 12 section（S1 highlighter / S2 history / S3 file model / S4 provider / S5 registry / S6 layout / S7 restore /
  S8 mesh / S9 run-wiring / S10 notebook aggregate / S11 spawn cascade / S12 coordinator）を **assert 1 行も
  削らず verbatim 移送**。各 section に台本 Action ID を `Covers:` で付与。
- `EditorApplication.Exit` は self-failing gate として無条件（PASS=Exit(0) / FAIL・例外=Exit(1)。元々無条件のため温存）。

## Covers マッピング（section ↔ Action ID）

| Section | Covers | 備考 |
|---|---|---|
| S1 | STRATEGY-04 | lexical token |
| S2 | STRATEGY-01,02,03 | edit/undo/redo history |
| S4 | STRATEGY-12 | supplyable 5条件 |
| S8 | STRATEGY-05 | 非scroll mesh 着色（scroll は HITL） |
| S10 | STRATEGY-01,10,12,15,16,17 | notebook 集約 |
| S11 | STRATEGY-06,07 | spawn cascade |
| S12 | STRATEGY-06,07,08,09,10,13,16 | coordinator window lifecycle |
| S3,S5,S6,S7,S9 | —（SUPPORTING PIN） | legacy StrategyDocument / registry / layout / restore / #78 run-wiring。STRATEGY Action ID に直接対応せず、別サーフェス（Run/Layout）が正本の pure core を温存 |

## 据え置き / 仕分け

- **STRATEGY-11（単一セル placeholder hint）= 要新規自動化のまま据え置き**。`NotebookCellCoordinator.UpdatePlaceholders`
  → `_viewFor(regionId)?.SetPlaceholderHint` は実 `StrategyEditorView`(MonoBehaviour)＋`InputField`＋placeholder `Text`
  harness を要し、S12 の bare-RT 経路（`viewFor = _ => null`）の verbatim 移送に収まらない。「安い昇格」方針に沿い
  本昇格では追加しない。将来 view harness を組む slice で昇格する。
- **STRATEGY-13（CapturePositions cell-order）= S12 が既に assert 済み**（`CapturePositions().Count == CellCount`）→
  自動(E2E済) へ再分類（新規コード不要）。move/drag 本体は FloatingWindow 共有ロジックで FloatingWindow 台本が正本。
- **STRATEGY-15（File→New reset）**: aggregate 側（`ResetUnboundEmpty`）は S10 で昇格。root/MenuBar 側は
  `MenuBarCutoverProbe`（MENU-02）が正本のまま据え置き（本 probe に MenuBar 共有コードは無し）。
- **STRATEGY-05(scroll 着色) / STRATEGY-18(IME・実キーボード)** = HITL専用、**STRATEGY-14(click-to-front)** = 対象外
  （FloatingWindow 共有ロジック）。

## カバー状態 rollup の変化

`STRATEGY-01..18`: `18 | 0 | 13 | 2 | 2 | 1` → `18 | 14 | 0 | 1 | 2 | 1`
（自動E2E済 0→14: 昇格13＋STRATEGY-13 再分類1。要新規 2→1: STRATEGY-11 のみ残）。

## 参照更新

- `Assets/Scripts/StrategyEditor/AtomicPyFile.cs` コメント `StrategyEditorProbe §3` → `StrategyEditorNotebookE2ERunner §3`。
- `StrategyEditorNotebookE2ERunner.md` / `FileOpenNonMarimoE2ERunner.md` の `StrategyEditorProbe` 参照を新 runner 名へ。
- `E2E-INDEX.md` rollup（✅・件数）＋prose（8本目追記）。
- 旧 findings（0010/0044/0050 等）は append-only 履歴のため改変せず。本 findings が改名を記録。

## 検証

- compile-only ゲート: `<Unity> -batchmode -nographics -quit -projectPath . -logFile <log>` で `error CS\d+` **0 件**・
  `Exiting batchmode successfully` / return code 0（2026-06-19 lead 実行・確定）。
- AFK GREEN: 上記＋`-executeMethod StrategyEditorNotebookE2ERunner.Run`。`[E2E STRATEGY NOTEBOOK PASS]` を
  bash `grep -a` で **1 件確認**、FAIL タグ 0 件、sentinel（`Found no leaked weakptrs` / Package Manager shutdown）
  あり＝executeMethod 実走、exit 0（2026-06-19 lead 直列 AFK 実行・GREEN 確定）。
- vacuity: verbatim 移送のため新規 vacuous section 無し。delete-the-logic litmus（台本 §自動判定）:
  `MarimoNotebookDocument._cells.Count<=1` ガード除去 → STRATEGY-10 RED / `Cell.SetBody` dirty hook 除去 →
  STRATEGY-12 RED / `NotebookCellCoordinator.DeleteCell` の region_001 分岐入替 → STRATEGY-08/09 RED。
