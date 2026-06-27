# ReplayRunResultTileE2ERunner — Replay RunResult タイル (Surface E2E / Panel category)

Replay の **RunResult タイル**（`BackcastWorkspaceRoot.PushReplayTiles` の running↔full-stats 分岐）の
ユーザー観測挙動を AFK 回帰ゲート化する台本。`.cs` が自動判定の正本、本 `.md` が仕様・観測点・合否の正本。

実行:

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod ReplayRunResultTileE2ERunner.Run -logFile <log>
# expect: [REPLAY RUNRESULT TILE PASS] ... / exit=0
```

## なぜ存在するか（カバレッジ穴の経緯）

- #65 で RunResult タイルに running→full-stats の 2 段表示が入り、`HakoniwaBaseModeProbe` S5/B2 が AFK 化した。
- それらは findings 0060 で `HakoniwaE2ERunner` に集約されたが、**#99 (commit `77e39c7`)** が Hakoniwa を
  floating-window 化した際 `HakoniwaE2ERunner`（836 行）を **run_result カバレッジごと wholesale 削除**。
  コメントは「将来 Panel カテゴリ runner へ移送予定」としたが re-home されず、RunResult タイルの C# AFK
  カバレッジはゼロになった。
- その死角に **#100 ①**（`bt.replay` を 2 連続実行すると run2 走行中ずっと run1 の full-stats が残る）が
  landed した。本 runner は削除された B2 を Panel surface として復活させ、さらに #100 ① の再実行不変条件を
  RRT-04 として追加する。

## 2 ゲート分割（C#↔Python 跨ぎ DATA 経路）

| 半分 | 何を証明するか | 正本 |
|---|---|---|
| DATA（#100 ① 根本原因） | `engine.last_run_summary` を **run-begin で clear** する source 挙動（再実行で前回 summary が残らない） | `python/tests/test_notebook_replay_afk.py::test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale`（findings 0077 §2） |
| RENDER（本 runner） | **summary source が空なら running、非空なら full-stats** を描く（Unity でしか証明できない描画分岐） | 本 runner RRT-02 / RRT-04 |

両ゲートで #100 ① を Python（source clear）と C#（render branch）の両層から固定する。

## Python-FREE seam

`WorkspaceEngineHost.TestPortfolioJsonOverride` / `TestRunSummaryJsonOverride` に poll snapshot を直接注入し、
`_lanes` / 実 backend を一切起こさない（削除された `HakoniwaBaseModeProbe` と同型）。`_lastLiveShape=false`
（Replay）で `RefreshLiveTiles` → `PushReplayTiles` を駆動し、`_runResultView._content.text` を読む。

## 操作一覧表

| Action ID | 行動 | 入口 (file:line) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| RRT-01 | run 未開始（portfolio 無） | `BackcastWorkspaceRoot.cs` `PushReplayTiles` (portfolio empty 分岐) | RunResult タイル text | `_content.text == ReplayEmpty "(no data — Replay)"` | 自動(E2E済) |
| RRT-02 | run1 走行中（portfolio 有・summary 空） | 同上 `IsNullOrWhiteSpace(summaryJson) ? running` | RunResult タイル text | `Contains("running")` かつ `!Contains("fills:")` | 自動(E2E済) |
| RRT-03 | run1 完了（summary publish） | 同上 `: FormatReplayRunResultComplete(...)` | RunResult タイル text | `!Contains("running")` ・`fills:2` ・`total_pnl -410010` 束縛 | 自動(E2E済) |
| RRT-04 | **#100 ①** run2 開始＝summary が空へ戻る（portfolio は走行継続） | findings 0077 D1 render 半分 | RunResult タイル text | `Contains("running")` かつ run1 の `fills:2`/`-410010` が**残らない** | 自動(E2E済) |
| RRT-05 | run2 完了（新 summary） | 同上 complete 分岐の反復 | RunResult タイル text | `!Contains("running")` ・run2 の `fills:3`/`12345` | 自動(E2E済) |
| RRT-06 | **#138 第2スライス** mode→run_result 可視性（Replay/LiveAuto=表示・LiveManual=非表示）＋巻き込み非表示なし | `BackcastWorkspaceRoot.cs` `DriveRunResult` (`_dockWindows.RectOf(WINDOW_ID_RUN_RESULT)` を直接 `SetActive`＝絶対トグル・単一窓) | run_result 窓 `activeSelf` ＋ chart sibling の `activeSelf`（ADR-0038 で orders/positions/buying_power tile 退役→bar・collateral witness は chart 窓に移行）＋ `CaptureLayout` premise (RRT-08) | Replay/LiveAuto で `run_result.activeSelf==true`・LiveManual で `false`・chart sibling は LiveManual でも `true` | 自動(E2E済) |
| RRT-07 | LiveManual 遷移が非破壊（純可視性・AC3） | 同上（pure SetActive・groupId 不変） | run_result の `GetInstanceID`/`anchoredPosition`/`sizeDelta`/`GroupIdOf` | Replay→LiveManual→Replay で同一 instance・geometry 保持・groupId 不変・復帰で `activeSelf==true` | 自動(E2E済) |
| RRT-08 | **self-heal（review Finding 1 ガード）** persisted-hidden（LiveManual 中 save→`ApplyGeometry` 復元）が Replay boot で可視へ自己回復 | `DriveRunResult` 絶対トグル（DriveOrderTicket 鏡像） | **premise**: LiveManual hide 中の `CaptureLayout()` が run_result を `visible==false` で乗せること（capture に乗る＝絶対トグルが要る根拠）→ 直接 `SetActive(false)` した run_result を Replay drive | premise が GREEN（run_result が capture に hidden で乗る）かつ drive 後 `activeSelf==true`（remembered-set へ戻すと永久非表示で RED・capture から外すと premise が RED） | 自動(E2E済) |
| — | 実 backtest 2 連続実行で source が実際に clear されること | `python/tests/test_notebook_replay_afk.py` | engine.last_run_summary lifecycle | （別ゲート＝Python e2e） | 自動(E2E済・pytest) |
| — | footer/Settings で mode 切替時の run_result 消失/復帰の実画面目視 | — | 実画面 | （HITL専用） | HITL専用（GPU 依存・AFK が可視性/排他/非破壊を担保済） |
| — | File→New/Open で run_summary も honest-empty 化（D4） | `clear_run_view` RPC ＋ `ResetReplaySnapshot` | poll snapshot | Python `test_clear_run_view_clears_both...` ＋ HITL 目視 | 自動(E2E済・pytest) / HITL |
| — | 実 venue データでの体感・実ピクセル | — | 実画面 | （HITL専用） | HITL専用（GPU/venue 依存） |

## RED litmus（delete-the-production-logic）

`BackcastWorkspaceRoot.PushReplayTiles` の
`string.IsNullOrWhiteSpace(summaryJson) ? FormatReplayRunResultRunning(...) : FormatReplayRunResultComplete(...)`
を **complete 固定**に潰す（running 分岐を消す）と **RRT-02 と RRT-04 が RED**。
さらに summary を sticky（最後の非空値を記憶して empty を無視）にする **旧 #100 バグ形**でも **RRT-04 が RED**。
RRT-04 は「summary が空へ戻ったら full-stats を捨てて running へ戻る」を要求するため、production の分岐を
消すと必ず落ちる（vacuous でない）。

**#138 第2スライス（mode→可視性）の RED litmus**:
`BackcastWorkspaceRoot.DriveRunResult` を no-op に潰す（run_result を一切隠さない）と **RRT-06 が RED**
（実機 AFK 確認 2026-06-25: `[REPLAY RUNRESULT TILE FAIL] RRT-06: run_result VISIBLE under LiveManual`・exit 1）。
`DriveRunResult` を絶対トグルから **in-memory remembered-set**（`HideKind`/`ShowHidden`・自分が隠した id だけ
再表示）へ戻すと **RRT-08 が RED**（run_result は CaptureLayout に乗るため LiveManual 中 save→boot で persisted-hidden
になり、空の remembered-set では self-heal せず永久非表示＝review Finding 1 の brick）。いずれも production を変えると
必ず落ちる。

親 findings: `docs/findings/0077-issue100-audit-bugfixes-run-summary-and-shared-stop-event.md`（RRT-01..05）
／ `docs/findings/0110-livemanual-hides-strategy-editor.md` §7（RRT-06/07/08・#138 第2スライス）
