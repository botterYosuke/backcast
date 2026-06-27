# findings 0076 — issue #95 E2E 仕上げ: notebook→Hakoniwa 縦串 Journey ＋ placeholder hint 昇格

**方針**: ADR-0016（notebook=backtest・per-cell RUN）/ ADR-0015（E2E runner 配置）/ ADR-0017（Hakoniwa=dockable
floating window）— いずれも不編集（自己保護条項）。本 finding は #95 の release-gate E2E を「理想的な完成形」に
仕上げた実装着地の記録。

## 0. 何を埋めたか（発見した穴）

issue #95（全 Phase 実装済み・close 済み）の E2E を棚卸ししたところ、**中心命題 D6/D7/D8 の縦串に C# の
Journey ゲートが無い**ことが判明した:

- **C# Surface**（`StrategyEditorNotebookE2ERunner` STRATEGY-19..33）は **fake executor の制御ロジック半分**だけ
  （index→窓 routing・running guard・glyph・stale badge・block popup）を観測。
- **Python e2e**（`test_notebook_replay_afk.py` / `test_notebook_step_afk.py`）は **エンジン DATA 半分**だけを観測。
- 両者を**実 `BackcastWorkspaceRoot` 上で縫い合わせる縦串**（commit scenario → bt cell press → 実 wiring →
  走行スナップショット → Hakoniwa base tile 逐次更新 → stop）は、並行で走った Hakoniwa リファクタ（#99 / ADR-0017）に
  巻き込まれて退役した旧 `ReplayToHakoniwaE2ERunner`（chart-OHLC 串・`77e39c7` で削除）以降、**空席**だった。
  `StrategyEditorNotebookE2ERunner.md` は退役済み runner への dangling 参照を残していた。

加えて、`StrategyEditorNotebookE2ERunner.md` の唯一の `要新規自動化` 行 **STRATEGY-11（single-cell host-API
placeholder hint）** が未自動化のまま残っていた。

owner 決定（plain-language question・2026-06-21）: **「縦串 Journey ＋ 残り穴も全部」**（最大スコープ）。

## 1. 新 Journey runner — `NotebookToHakoniwaJourneyE2ERunner`

退役 `ReplayToHakoniwa` の縦串の席を、**新 Hakoniwa＝dockable floating window の base tile モデル**で継ぐ。
旧 runner は chart-OHLC 串（`InstrumentOhlcDecoder → ChartView.Render`・**実 Python ＋ 合成 DuckDB**）だったが、
#95 の D6 は「結果は Hakoniwa の `buying_power` / `orders` / `positions` / `run_result` **base tile**」で、これは
**`get_portfolio_json` poll → `PushReplayTiles`** の経路。本 Journey はこの D6/D8 経路を縫う。

### Python-FREE の 2 ゲート分割（findings 0073 / behavior-to-e2e）

`host.InitializePython` を**呼ばない**。Unity でしか証明できない C# の縫い目だけを駆動する:

| 半分 | どう駆動するか | 何を満たすか |
|---|---|---|
| press→制御 | 実 root を反射合成し、root の本物の private callback（`BuildNotebookScenarioJson` / `SetCellRunButtonState` / `host.ForceStop` / `ViewFor` / `SetCellStaleRegions`）に bind した `NotebookRunController` を、**executor だけ Python-FREE な fake** に差し替えた同期レーン（`startWorker:false`）で駆動 | scenario が**実 `_scenario` から実 `BuildNotebookScenarioJson`** で組まれて press に乗る／glyph が**実 `WireCellRunButton` が作った `_cellRunButtons` の本物のボタン**でトグルする＝wiring identity は production と同一 |
| 走行スナップショット→Hakoniwa | #65 の test seam `WorkspaceEngineHost.TestPortfolioJsonOverride` / `TestRunSummaryJsonOverride` に合成スナップショットを注入し、`_lastLiveShape=false`（Replay shape）で実 `RefreshLiveTiles()`→`PushReplayTiles()` を pump | override が満たすのは**実 poll lane と同一の `LatestPortfolioJson` / `RunSummaryJson` プロパティ**なので、`ReplayPanelDecoder.DecodePortfolio` → `FormatReplay*` → `LivePanelTileView.ShowText` の鎖は **100% production** |

> **なぜ実 Python で end-to-end しないか**: 旧 ReplayToHakoniwa の実 Python ＋ 合成 DuckDB 方式は 1 走 2–3 分＋
> DuckDB マウント flap で flaky だった。#95 の engine DATA（byte-identical fills・pacing wallclock・cross-thread
> stop の実挙動）は既に `test_notebook_replay_afk.py` / `test_notebook_step_afk.py` が決定論的に・秒で固定済み。
> 本 Journey が**唯一の新規価値**として埋めるのは「実 root の C# wiring が両半分を縫う」縦串なので、Python-FREE で
> 速く・決定論的に・隔離して gate する（#65 override は実 poll lane と同一プロパティを満たす＝production 鎖を gate する）。

### Action ID（`JOURNEY-NBHAKO-01..14`・台本が正本）

01 config→run 手渡し（commit 前 null→commit 後 instruments JSON の反転）/ 02 bt.replay cell 著作 / 03 scenario が
executor に届く / 04 running guard＋▶→■（実 button）/ 05 走行スナップショット bar1→4 tile / 06 bar2→逐次更新 /
07 終端 summary→run_result が full-stats へ / 08 ■→force-stop→drain→▶ / 09 走行中 2nd RUN 即時 reject /
10 bt.step は guard 非活性（glyph ▶ のまま）＋scenario 手渡し / 11 step スナップショット→tile / 12 空 universe→
null scenario→running 立たず（D5）。**13 対象外**（engine DATA = Python e2e）/ **14 HITL専用**（実 GPU tile 描画）。

## 2. STRATEGY-11 placeholder hint 昇格（Section20）

> **supersede: ADR-0036（#169・2026-06-26）** — placeholder hint 機構（`HostApiHint`/`UpdatePlaceholders`/
> `SetPlaceholderHint`/`_placeholder`/Placeholder GameObject）は **RETIRED**。Section20 は撤去 pin に反転した
> （fresh New が観察セルを種付けするためヒントは無用・findings 0124）。以下は履歴記録。

`StrategyEditorNotebookE2ERunner` に Section20 を追加。bare-RT `FloatingWindowController` ＋ 実 `StrategyEditorView`
（`StrategyEditorContentBuilder.Build` が Placeholder Text を作る）＋ 実 `NotebookCellCoordinator` で、
`UpdatePlaceholders`（`single = CellCount==1` のときだけ `HostApiHint`）を Sync/Add/Delete で駆動し、実 view の
private `_placeholder` を反射読みして assert。これで本台本の `要新規自動化` 残はゼロ。

## 3. RED→GREEN litmus（delete-the-production-logic）

- `BuildNotebookScenarioJson` の空-universe ガード（`return null`）を消す → NBHAKO-01/12 RED（反転消失／空でも ■）。
- `PushReplayTiles` の `ShowText`/`DecodePortfolio` を消す → NBHAKO-05/06/07 RED（tile が `(no data — Replay)` のまま）。
- `PushReplayTiles` の running/full-stats 分岐を running 固定 → NBHAKO-07 RED（summary 後も `fills:` が出ない）。
- `RunCell` の `drivesReplay && scenarioJson` guard 条件を緩める → NBHAKO-10（step が ■）／`scenarioJson` 必須を外す
  → NBHAKO-12（null でも ■）RED。
- `RunCell` の `_btRunActive` 早期 return を消す → NBHAKO-09 RED（2nd RUN が executor に到達）。
- `ApplyResult` の `_btRunActive=false` 解除を消す → NBHAKO-08 RED（drain 後も ■）。
- ~~`UpdatePlaceholders` の `single` ゲートを外す → STRATEGY-11 の「2 セルで非 active」RED／hint を `Cell.Body` へ seed
  → 「body 空」RED。~~ **（ADR-0036/#169 で機構撤去・新 litmus は「Placeholder GO + input.placeholder を復活させると Section20 RED」）**

## 4. AFK 実走結果（2026-06-21・全 GREEN）

- **compile-only ゲート**: `error CS` 0 件・return code 0。
- **`NotebookToHakoniwaJourneyE2ERunner.Run`**: `[E2E NB→HAKONIWA PASS]`・exit 0・`error CS` 0 件
  （NBHAKO-01..12 全観測点 GREEN）。
- **`StrategyEditorNotebookE2ERunner.Run`**: `[E2E STRATEGY NOTEBOOK PASS]`（S1–S20＝STRATEGY-11 含む全 33 行）・
  exit 0・`error CS` 0 件（回帰なし）。
- **RED→GREEN litmus（実証）**: `PushReplayTiles` の `_positionsView?.ShowText(FormatReplayPositions(snap))` を
  コメントアウト → AFK 再走で `[E2E NB→HAKONIWA FAIL] NBHAKO-05: positions tile did not reflect the running
  snapshot (got [])`（tile が空のまま＝production ShowText を gate していることを実証）→ revert で GREEN 復帰。

実行コマンド（memory `unity-afk-probe-run`・bash `grep -a` で PASS 行を読む）:

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod NotebookToHakoniwaJourneyE2ERunner.Run -logFile <log>
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod StrategyEditorNotebookE2ERunner.Run -logFile <log>
```

## 5. 番号衝突メモ（pre-existing・本 finding の対象外）

`docs/findings/` に **0075 が 2 ファイル**存在する（`0075-hakoniwa-docking-floating-windows.md`＝#99／
`0075-issue95-phase6-run-state-ui-rich-output-sunset.md`＝#95 Phase 6）。これは #95↔#99 の並行ブランチ merge で
生じた findings 0070 が警告する「同番号別ファイル」事故だが、両者は別設計の木で既に多数の参照を持つため、本 #95
E2E 仕上げのスコープでは**触れない**（renumber は別途）。本 finding は次の空き番号 **0076** を採る。

## 関連

- 正本台本: `Assets/Tests/E2E/Editor/NotebookToHakoniwaJourneyE2ERunner.md`（+ `.cs`）/
  `StrategyEditorNotebookE2ERunner.md`（Section20）。
- rollup: `Assets/Tests/E2E/Editor/E2E-INDEX.md`（Journey 4 本＝57 行・JOURNEY-NBHAKO-01..14 登録・STRATEGY-11 昇格）。
- 方針: ADR-0016 / ADR-0015 / ADR-0017。engine DATA 正本: `python/tests/test_notebook_replay_afk.py` /
  `test_notebook_step_afk.py`。退役: `ReplayToHakoniwaE2ERunner`（#99 / `77e39c7`）。
