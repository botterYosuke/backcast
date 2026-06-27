# NewSeedsObserveNotebook E2E 台本（issue #169 / ADR-0036 / findings 0124）

**挙動**: New/初期化（File→New・no-resume boot・最初の spawn）が「**トヨタを眺めて replay 観察できる、すぐ Run 可能な
雛形**」に着地する。①唯一のセルに `bt.replay` 観察コード（売買ゼロ）が種付けされ、②universe にデフォルトでトヨタ
（`7203.TSE`）が入り、③universe↔chart 同期でトヨタ chart が自動 spawn し、④種付き untitled は起動直後から Run 可
（遅延 scratch `.py`）。findings 0050（空セル＋placeholder）と #76（空白 New＋Save まで Run ブロック）を ADR-0036 が
supersede する。

**正本**: ADR-0036 / [findings 0124](../../../../docs/findings/0124-new-seeds-observe-notebook-and-default-toyota-universe.md)。
**runner**: `NewSeedsObserveNotebookE2ERunner.cs`（実 `BackcastWorkspaceRoot` を反射駆動・Python-FREE・OpenScene 1 回）。

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod NewSeedsObserveNotebookE2ERunner.Run -logFile <abs log>
# expect: [E2E NEWSEED SLICE PASS] / exit=0、各到達点で [E2E NEWSEED-NN PASS]（Bash `grep -a`）
```

## 操作一覧表

| Action ID | 行動 | 入口(file) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| `NEWSEED-01` | no-resume boot が観察セル＋トヨタ universe を種付け | `BackcastWorkspaceRoot.OpenFileNewDefault` | cell0.Body==ObserveSeedBody・clean・unbound／universe==[7203.TSE] | S1 `OpenFileNewDefault` 反射→assert | 自動(E2E済) |
| `NEWSEED-02` | universe→chart 同期でトヨタ chart 自動 spawn | `SyncChartWindowsToUniverse`（`InstrumentRegistry.Changed`） | `_chartViews` に `7203.TSE` | S1 → `_chartViews.Contains` | 自動(E2E済) |
| `NEWSEED-03` | File→New が観察セル本文を種付け | `BackcastWorkspaceRoot.DoFileNew` | cell0.Body==ObserveSeedBody・clean untitled・universe==[7203.TSE] | S2 `DoFileNew` 反射→assert | 自動(E2E済) |
| `NEWSEED-04` | restore/Open は自前 universe を honor（種付けは fresh だけ） | `ScenarioStartupController.PopulateFrom` | snapshot[8035]→universe==[8035]／`PopulateFrom(null)`→空（トヨタ非注入） | S3 PopulateFrom 駆動→assert | 自動(E2E済) |
| `NEWSEED-05` | 観察セル本文の synth 出力が owner 指定形と一致（golden） | `cell_synthesis.synthesize_json` | byte-golden（version masked）＋round-trip 冪等 | **pytest** `test_marimo_cell_synthesis_golden.py`（実 marimo） | 自動(E2E済・pytest) |
| `NEWSEED-06` | 種付き untitled＋valid scenario が起動直後 Run 可・遅延 scratch | `BackcastWorkspaceRoot.BuildNotebookStrategyPath` / `ScenarioStartupController.TryStartRun` | BuildPath→scratch `.py`（File.Exists・notebook 非 bind）／TryStartRun==Ready・StrategyPath==scratch | S4 → assert | 自動(E2E済) |
| `NEWSEED-07` | 空 untitled buffer は Run 不可（述語＝非空セル） | 同上 | BuildPath==null／TryStartRun==BlockedNoStrategy | S5 cell空化→assert | 自動(E2E済) |
| `NEWSEED-08` | named+dirty は BlockedNoStrategy（WYSIWYR 不変） | 同上 | BuildPath==null／TryStartRun==BlockedNoStrategy | S6 SaveAs→edit→assert | 自動(E2E済) |
| `NEWSEED-09` | New は scratch 非生成（Untitled）／Save As で named WYSIWYR 復帰 | 同上 / `OnFileSaveAs` | New 後 scratch 非存在・unbound／SaveAs 後 BuildPath==bound .py・Ready／再 edit→null（dirty ゲート復活） | S7 → assert | 自動(E2E済) |
| — | 実 replay の bar 観察（DuckDB に 7203 データ） | 実 venue/データ | 雛形を実走し bar が流れる | 実データ依存（種コードは cwd/__file__ 非依存） | HITL専用 |

## 非空虚性（delete-the-production-logic litmus）

- NEWSEED-01/03: `DoFileNew`/`OpenFileNewDefault` の `_coordinator.New(ObserveSeedBody)` を `New()` に戻す → 種が消えて RED。
- NEWSEED-02: `SeedFreshDocumentDefaults`（→`ScenarioStartupController.SeedFreshDefaults` の `Universe.ReplaceAll`）を消す（または `_scenario.Universe.Changed += SyncChartWindowsToUniverse` 配線を切る）→ chart 不在 RED。
- NEWSEED-04: `SeedFreshDocumentDefaults` のトヨタ seed を `PopulateFrom` 内に動かす（findings F8 が却下した誤 seam）→ restore に 7203 が混ざり RED。
- NEWSEED-06: `BuildNotebookStrategyPath` の untitled scratch 分岐を消す → null→BlockedNoStrategy で RED。
- NEWSEED-07: `MarimoNotebookDocument.HasNonEmptyCell` を常に true にする → 空 buffer が Ready になり RED。
- NEWSEED-08: `BuildNotebookStrategyPath` の `nb.IsBound` ガードを外す → named+dirty が scratch を書いて Run 可になり RED。
- NEWSEED-09: New 経路が `BuildNotebookStrategyPath`（scratch 書き）を呼ぶ → New 時点で scratch が出来て RED。

## 関連

- supersede: STRATEGY-11（単一セル placeholder hint）は #169（ADR-0036 D3）で RETIRED。
  [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) Section20 が撤去を pin。
- JOURNEY-AUTHOR-02（[AuthorToRunJourney](./AuthorToRunJourneyE2ERunner.md)）も種付き New へ更新済み。
