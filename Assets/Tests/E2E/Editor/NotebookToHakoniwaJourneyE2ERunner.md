# NotebookToHakoniwaJourneyE2ERunner — 台本（Journey E2E / issue #95 release-gate 縦串）

`NotebookToHakoniwaJourneyE2ERunner.cs` が自動検証する **issue #95 の中心命題（D6/D7/D8）の縦串**の台本。
実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、**この縦串でユーザーが体験する一連の挙動と
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **なぜこの Journey が要るか（retire された前任）**: issue #95 の「per-cell RUN で notebook が **実エンジン**を駆動し、
> **結果が Hakoniwa に逐次出る**」（D6「結果は Hakoniwa」/ D7「dry-run 廃止＝notebook=backtest 一本化」/ D8「走行中の逐次更新」）
> の縦串は、長らく **C# の 1 本の Journey ゲートが無い**状態だった。`StrategyEditorNotebookE2ERunner`（Surface・
> STRATEGY-21..26）は **fake executor の制御ロジック半分**（routing・running guard・glyph）だけを、`test_notebook_replay_afk.py`
> / `test_notebook_step_afk.py`（Python e2e）は **エンジン DATA 半分**だけを担い、**両者を実 root 上で縫い合わせる縦串**は
> 並行で走った Hakoniwa リファクタ（#99 / ADR-0017）に巻き込まれて消えた `ReplayToHakoniwaE2ERunner`（旧 chart-OHLC 串）の
> 退役以降、空席だった。本 Journey はその空席を **新 Hakoniwa モデル（dockable floating window の base tile）**で埋める。

> **二層 E2E の位置づけ**: 本台本は *Journey E2E*（複数サーフェスをまたぐ実ユーザーストーリーの横断データ伝播）であり、
> 同時に **issue #95 の release-gate 縦串**。各サーフェス単体の正本は既存 runner（[ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md) /
> [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md)）で、本 Journey は**移送せず**、実 `BackcastWorkspaceRoot`
> を通した「scenario commit → bt cell press → 実 root wiring → 走行スナップショット → Hakoniwa base tile 逐次更新」の
> 縫い目だけを観測する＝重複ではなく縦串。

## 対象ストーリー（#95 の確定形コード `for bar in bt.replay(): …` / `bar = bt.step()` をユーザーが書いて走らせる）

1. startup パネル（`ScenarioStartupTile` / D5）で universe・期間・cash を **commit** する。
2. adopted cell 窓に `for bar in bt.replay():` を著作する（D11「run 入口は per-cell RUN」）。
3. その窓の ▶ RUN を押す → **commit 済み scenario が実 backend に渡り**（D4 free-ref 注入 / D7 一本化）、cell が **RUNNING**（▶→■・D8）。
4. 実エンジンが bar を流す → **走行中スナップショット**が Hakoniwa の `buying_power` / `orders` / `positions` / `run_result`
   base tile に**逐次**反映される（D6 / D8「走行中の逐次更新」）。
5. run が終端まで行く → `summary_json` で `run_result` tile が **running ビュー → full-stats** に切替（D10 golden 同値の終端）。
6. ■ を押すと即停止し ▶ に戻る（D8 stop・cross-thread）。
7. `bt.step()` cell では押すたびに 1 bar 進み（D11・bar-by-bar デバッグ）、running guard は立たない（findings 0074）。

## アーキテクチャ前提・Python 層分け（**Python-FREE**）

- **本 Journey は実埋め込みインタプリタを起こさない**（`host.InitializePython` を**呼ばない**）。#95 Phase 4 の
  「2 ゲート分割」（findings 0073 / behavior-to-e2e）に従い、**Unity でしか証明できない C# の縫い目だけ**を駆動する。
- **press→制御半分**: 実 `BackcastWorkspaceRoot` を反射合成（`ScenarioStartupController` / `NotebookCellCoordinator` /
  実 `_cellRunButtons`）し、root の **本物の private callback**（`BuildNotebookScenarioJson` / `SetCellRunButtonState` /
  `host.ForceStop` / `ViewFor` / `SetCellStaleRegions`）に bind した `NotebookRunController` を、**executor だけ Python-FREE な
  fake** に差し替えた同期レーン（`startWorker:false`）で駆動する。scenario は **実 `_scenario` から `BuildNotebookScenarioJson`** が
  生成し、glyph は **実 `WireCellRunButton` が作った `_cellRunButtons` の本物のボタン**がトグルする＝wiring identity は production と同一。
- **走行スナップショット→Hakoniwa 半分**: #65 の test seam `WorkspaceEngineHost.TestPortfolioJsonOverride` /
  `TestRunSummaryJsonOverride` に合成スナップショットを注入し、`_lastLiveShape=false`（Replay shape）で実 `RefreshLiveTiles()`→
  `PushReplayTiles()` を pump する。**override が満たすのは実 poll lane が満たすのと同一の `LatestPortfolioJson` / `RunSummaryJson`
  プロパティ**なので、`ReplayPanelDecoder.DecodePortfolio` → `FormatReplay*` → `LivePanelTileView.ShowText` の**鎖は 100% production**。
  poll lane→`last_portfolio` 側（#65）は Python e2e が正本。

## 自動検証する範囲（この Runner がゲートする）

- **D5 config→run の手渡し**: `BuildNotebookScenarioJson` が commit 前は `null`、universe/期間/cash 設定後は instruments を
  含む JSON へ反転し、press でその JSON が executor に届く。
- **D4/D7/D8 RUNNING 状態**: 実 `_cellRunButtons` の glyph が press で ▶→■、stop/drain で ■→▶。2nd RUN の即時 reject。
- **D6/D8 結果は Hakoniwa・逐次更新**: 走行スナップショットを 2 つ続けて流すと 4 base tile が**異なる実数へ追従**し、
  終端 summary で `run_result` が full-stats へ切替。
- **Phase 5 step**: `bt.step()` press は running guard を立てず（glyph ▶ のまま）、scenario は同様に手渡され、step の
  スナップショットも tile に届く。
- **D5 fail-closed**: universe 未 commit（空）の `bt.replay()` press は `null` scenario となり running guard を立てない
  （実 scenario の時だけ bt が走る）。

## 自動検証しない範囲（carve-out・理由併記）

- **実埋め込みエンジンの DATA**（byte-identical fills・`bars_per_second` の wallclock pacing・cross-thread stop の実挙動）。
  → **対象外**: `python/tests/test_notebook_replay_afk.py` / `test_notebook_step_afk.py`（engine 半分の正本）。本 Journey は
  C# の縫い目に限定（#95 Phase 4 の 2 ゲート分割）。
- **実 GPU での base tile 実描画（ピクセル・色・レイアウトの視覚的正しさ）**。→ **HITL専用**: `-nographics` は GPU を持たず、
  本 Journey は tile を**テキスト DATA 層**で観測する。実ピクセルは owner 実 Play 目視。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| JOURNEY-NBHAKO-01 | startup パネルで universe/期間/cash を commit | `ScenarioStartupController.cs:95-99`→`BackcastWorkspaceRoot.cs:733`→`BuildNotebookScenarioJson` | commit 前は universe 空で `BuildNotebookScenarioJson`==null → 設定後は instruments/start/end/granularity/initial_cash を含む JSON | Section1: 空 universe で provider null → AddInstrument/SetStart/… 後に instruments を含む JSON（false→true 反転） | 自動(E2E済) | — |
| JOURNEY-NBHAKO-02 | adopted cell に `for bar in bt.replay():` を著作 | `MarimoNotebookDocument.cs:97`→`SynthesizeLiveSource` | `Cells[0].SetBody` 後の synth source が `"bt.replay"` を含む | Section1: source.Contains("bt.replay") | 自動(E2E済) | — |
| JOURNEY-NBHAKO-03 | ▶ RUN 押下 → commit 済み scenario が実 executor に届く（D4 free-ref / D7） | `NotebookRunController.cs:86`（`scenarioJsonProvider?.Invoke()`＝root `BuildNotebookScenarioJson`） | fake executor が受領した scenarioJson == `BuildNotebookScenarioJson()` の現在値（実 `_scenario` 由来） | Section1: `exec.LastScenario` == 提供 JSON（≠null・instruments 含む） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S14・hand-set provider) |
| JOURNEY-NBHAKO-04 | replay press → running guard 立ち ▶→■（実 `_cellRunButtons`） | `NotebookRunController.cs:100-106`→`SetCellRunButtonState`（root `:700`）→`StrategyEditorWindowFrame.SetRunButtonGlyph` | press 前は実ボタン glyph "▶"、press 後 `IsBacktestRunning==true` かつ glyph "■" | Section1: press 前 ▶ / press 後 ■ ＆ IsBacktestRunning | 自動(E2E済)（実 button。制御ロジックは STRATEGY-21） | `StrategyEditorNotebookE2ERunner`(S14) |
| JOURNEY-NBHAKO-05 | 走行中スナップショット bar1 → アカウントサマリーバーのホバー card（②③④）＋ run_result tile が実数表示（D6/D8） | `WorkspaceEngineHost`（override）→`BackcastWorkspaceRoot.PushReplayTiles`→`PushReplayAccountBar`→`ReplayPanelDecoder.DecodePortfolio`→`FormatReplay*`→`AccountSummaryBarView.SetDetail` | `TestPortfolioJsonOverride`=snap1 → `RefreshLiveTiles` → ③positions card が建玉行・④orders card が FILLED 行＋count1・②bp card が bp/equity・②bp 主数値が `887,600`・run_result tile が `running o:1 f:1` | Section1: bar `CardText(1/2/3)` が snap1 の実数（**退役 dock tile と byte 一致の `FormatReplay*`**）・`PrimaryText(1)` が bp・run_result tile text を含む | 自動(E2E済) | — |
| JOURNEY-NBHAKO-06 | bar2 スナップショット → 同じ card が**逐次更新**（one-shot でなく追従） | 同上（payload-change gate が新 string を通す） | override=snap2（position 増・equity 変）→ `RefreshLiveTiles` → card text が snap2 の実数へ**変化**（snap1 と異なる） | Section1: positions/buying_power hover card text が snap1≠snap2 で更新 | 自動(E2E済) | — |
| JOURNEY-NBHAKO-07 | run 終端 → `summary_json` で `run_result` が running→full-stats 切替（D10） | `WorkspaceEngineHost.cs:139`（summary override）→`PushReplayTiles cs:1039` | `TestRunSummaryJsonOverride`=summary → `RefreshLiveTiles` → run_result tile が `fills:.. sh:.. dd:..`（running ビューから切替） | Section1: run_result text が `fills:` を含む（`running` から変化） | 自動(E2E済) | — |
| JOURNEY-NBHAKO-08 | ■ press → force-stop 配線呼ばれ・result drain で guard 解除・■→▶（D8） | `NotebookRunController.cs:135`→`onStop`（root `:385` `host.ForceStop`）／`:159-166` drain で `_btRunActive=false` | `StopRunning` で onStop 発火、`DrainAndRoute` 後 `IsBacktestRunning==false`・glyph "▶"・新 press 受理 | Section1: stopCalls==1 / drain 後 ▶ ＆ 再 press 受理 | 自動(E2E済)（実 wiring。force-stop の engine 実挙動は Python e2e） | `StrategyEditorNotebookE2ERunner`(S14) |
| JOURNEY-NBHAKO-09 | 走行中の 2nd RUN は即時 reject（queue しない・ADR-0016 D3） | `NotebookRunController.cs:71-75`（`_btRunActive` 早期 return → onError） | running 中の RunCell は executor 未呼出（Calls 不変）・onError に "already running" | Section1: 2nd press 後 `exec.Calls` 不変 ＆ errors に message | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S14) |
| JOURNEY-NBHAKO-10 | `bt.step()` cell press → running guard 非活性（glyph ▶ のまま・findings 0074） | `NotebookRunController.cs:83-85,100`（`drivesReplay` のみ guard を立てる） | step-only source の press 後 `IsBacktestRunning==false`・glyph "▶"・executor が scenario JSON 受領 | Section2: glyph ▶ のまま ＆ scenario 手渡し | 自動(E2E済)（実 wiring。step persistence は STRATEGY-24/Python） | `StrategyEditorNotebookE2ERunner`(S15) |
| JOURNEY-NBHAKO-11 | step のスナップショットもバーに届く（bar-by-bar→③positions hover card） | 同 NBHAKO-05 の経路（step も同じ poll/override→bar 鎖） | step press 後 override=stepSnap → `RefreshLiveTiles` → ③positions card が stepSnap の建玉を反映 | Section2: positions hover card text が stepSnap の実数 | 自動(E2E済) | — |
| JOURNEY-NBHAKO-12 | universe 未 commit の `bt.replay()` press → null scenario → running guard 立たない（D5「実 scenario の時だけ」） | `NotebookRunController.cs:86,100`（`scenarioJson` null → guard 条件 false）／root `BuildNotebookScenarioJson cs:736` が空 universe で null | empty universe → provider null → replay press で `IsBacktestRunning==false`・glyph "▶" | Section3: null scenario → no running state | 自動(E2E済) | — |
| JOURNEY-NBHAKO-13 | 実埋め込みエンジンの DATA（byte-identical fills・pacing wallclock・cross-thread stop の実挙動） | — | `python/tests/test_notebook_replay_afk.py` / `test_notebook_step_afk.py` が正本（engine 半分） | — | 対象外（engine DATA は Python e2e が正本・本 Journey は C# 縫い目に限定＝#95 Phase 4 の 2 ゲート分割） | `test_notebook_replay_afk.py` |
| JOURNEY-NBHAKO-14 | 実 GPU での base tile 実描画（ピクセル・色・レイアウト） | — | `-nographics` は GPU 無し・tile は DATA 層で観測 | — | HITL専用（実 Play 目視・S3/S8/STRATEGY-33 と同型の GPU 降格） | — |

## 観測点（詳細）

- **NBHAKO-01（config→run 手渡し）**: `BuildNotebookScenarioJson`（`cs:733`）は `_scenario.Universe.Ids` が空なら null を返す
  （`cs:736`）。Section1 は **commit 前に provider==null** を確認してから `AddInstrument`/`SetStart`/`SetEnd`/`SetGranularity`/
  `SetInitialCash` を回し、**null→JSON の反転**を観測する（false→true 非空虚化）。これは Surface の STRATEGY-21（hand-set provider）
  と違い、**実 `_scenario` から実 `BuildNotebookScenarioJson` が組む JSON が press に乗る**という縦串の固有点。
- **NBHAKO-04/08（glyph トグル）**: glyph は実 `_cellRunButtons[region_001]`（`WireCellRunButton cs:681` が production で作るボタン）の
  "RunGlyph" 子 Text を読む。onRunningChanged を **root の本物 `SetCellRunButtonState`** に bind するので、トグルは production の
  描画コードを通る（fake で glyph を書かない）。
- **NBHAKO-05/06/07（Hakoniwa 逐次更新）**: `_lastLiveShape=false`（Replay shape）にして実 `RefreshLiveTiles()` を pump する。
  `RefreshLiveTiles` は Replay shape では `PushReplayTiles()` を毎フレーム呼ぶ（`cs:970`）。snap1≠snap2≠空 の **異なる string** を
  override に置くことで payload-change gate（`cs:1020`）を通し、4 tile が逐次再描画される。tile text は **`LivePanelTileView` の
  private `Text _content`**（`LivePanelTileView.cs:21`）を反射で読む。run_result は snap だけのとき running ビュー
  （`FormatReplayRunResultRunning cs:1408`）、summary override が乗ると full-stats（`FormatReplayRunResultComplete cs:1418`）。
- **NBHAKO-10/12（guard 非活性）**: `drivesReplay = source.Contains("bt.replay")`（`cs:83`）かつ scenario 非 null のときだけ guard が
  立つ（`cs:100`）。step-only source（NBHAKO-10）も null scenario（NBHAKO-12）も guard を立てない。

## 自動判定（合格条件）

- ログに `[E2E NB→HAKONIWA PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E NB→HAKONIWA FAIL] <msg>` で exit 1。
- **Unity ログは UTF-8（`→` を含む）なので Bash `grep -a "NB→HAKONIWA"` で読む**（PowerShell `Select-String` も ripgrep の
  Grep ツールも `→` 入り PASS 行を取りこぼす）。flush race: `Application will terminate with return code` を flush 完了 sentinel と
  して待ってから読む。
- **delete-the-production-logic litmus**:
  - `BackcastWorkspaceRoot.BuildNotebookScenarioJson` の空-universe ガード（`cs:736 return null`）を消すと NBHAKO-01/12 が落ちる
    （commit 前から非 null になり反転が消える／空でも guard が立つ）。
  - `PushReplayTiles` の `ShowText`（`cs:1035-1042`）または `DecodePortfolio`（`cs:1034`）を消すと NBHAKO-05/06/07 が落ちる
    （tile が `(no data — Replay)` のまま追従しない）。
  - `PushReplayTiles` の running/full-stats 分岐（`cs:1039-1042`）を running 固定にすると NBHAKO-07 が落ちる（summary 後も `fills:` が出ない）。
  - `NotebookRunController.RunCell` の `drivesReplay && scenarioJson` guard 条件（`cs:100`）を `drivesBacktest` に緩めると
    NBHAKO-10（step が ■ になる）／`scenarioJson` 必須を外すと NBHAKO-12（null でも ■）が落ちる。
  - `RunCell` の `_btRunActive` 早期 return（`cs:71`）を消すと NBHAKO-09（2nd RUN が executor に到達）が落ちる。
  - `ApplyResult` の `_btRunActive=false` 解除（`cs:161`）を消すと NBHAKO-08（drain 後も ■）が落ちる。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用`（NBHAKO-14）と `対象外`（NBHAKO-13）は理由を併記済み。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod NotebookToHakoniwaJourneyE2ERunner.Run -logFile <log>
```

このマシンの Unity: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
compile だけ先に通すゲート: `-executeMethod` を外した同コマンド（`error CS\d+` 0 件＋return code 0 を確認）。
`.cs` 編集直後の初回 `-executeMethod` はコンパイルで終わり実行されない → **2 回目で走る**（recompile-skip）。
次の Unity 起動前に `Get-Process Unity` が空か確認（lock-abort）。

## 命名規約

E2E 回帰ゲートは `Assets/Tests/E2E/Editor/<ScenarioName>E2ERunner.cs` ＋ `<...>E2ERunner.md` に置く（ADR-0015）。
本 Journey は特定 issue（#95）の release-gate 縦串なので、`E2E-INDEX.md` の **Journey E2E** 表に登録する
（旧 `ReplayToHakoniwaE2ERunner` の縦串の席を新モデルで継ぐ）。
