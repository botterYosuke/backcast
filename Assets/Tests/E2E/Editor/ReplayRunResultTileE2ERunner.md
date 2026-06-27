# ReplayRunResultTileE2ERunner — Run Result ポップアップ (Surface E2E / Panel category)

run_result の **screen-anchored ポップアップ**（#172/#173・ADR-0037 / findings 0125）のユーザー観測挙動を AFK
回帰ゲート化する台本。`.cs` が自動判定の正本、本 `.md` が仕様・観測点・合否の正本。

実行:

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod ReplayRunResultTileE2ERunner.Run -logFile <log>
# expect: [REPLAY RUNRESULT TILE PASS] ... / exit=0
```

## なぜ存在するか（cutover の経緯）

- run_result は元々 back-plane の dock base singleton（DockLayer 1.0×）で、`PushReplayTiles` の running↔full-stats
  分岐＋ #100① の再実行 stale ガード＋ #138 の LiveManual-hide（`DriveRunResult`）を持っていた。
- **ADR-0037（#172）** で run_result を **screen-anchored な右上ポップアップ**（`RunResultPopup`）へ cutover。
  表示は content-derived、pan で動かない（`ScreenSpaceOverlay` 直下・`Content` の子でない）、永続化しない。
- **#173** で × close ＋ dismiss latch（次 run まで再出現せず）を追加。再 arm は **Replay/LiveAuto 対称**。
- 本 runner は (a) running↔full-stats のテキスト描画（format 関数は無改変で再利用）＋ #100① の再実行 stale ガード、
  (b) content-derived 可視、(c) **LiveManual で出さない＝live hasContent を LiveAuto に scoping する D3 の
  sticky-flag ガード**、(d) **永続化しない**（CaptureLayout 非対象）、(e) × close + 同一 run latch、(f) **対称再 arm**
  を AFK で固定する。

## 2 ゲート分割（C#↔Python 跨ぎ DATA 経路）

| 半分 | 何を証明するか | 正本 |
|---|---|---|
| DATA（#100① 根本原因） | `engine.last_run_summary` を **run-begin で clear** する source 挙動 | `python/tests/test_notebook_replay_afk.py::test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale`（findings 0077 §2） |
| RENDER + 可視 + latch（本 runner） | **summary 空なら running・非空なら full-stats** を描く／content-derived 可視／LiveAuto-scope／× latch／対称再 arm | 本 runner RRT-01..09 |

## Python-FREE seam

内容は `WorkspaceEngineHost.TestPortfolioJsonOverride` / `TestRunSummaryJsonOverride` に poll snapshot を直接注入、
live の telemetry は `host.Panel.Apply(<wire>)`、mode は `FooterModeViewModel.ApplyPoll` で駆動。`_lanes` / 実 backend
を一切起こさない。可視は `RunResultPopup.IsVisible`、テキストは `_runResultView._content.text` を読む。× は popup の
`OnClose` Action を直接 invoke（close Button の onClick と同一経路）。

## 操作一覧表

| Action ID | 行動 | 入口 (file:line) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| RRT-01 | run 未開始（portfolio 無） | `BackcastWorkspaceRoot.DriveRunResultPopup` (Replay honest-empty 枝) | popup `IsVisible` | content-derived で **非表示** | 自動(E2E済) |
| RRT-02 | run1 走行中（portfolio 有・summary 空） | `PushReplayTiles` running 分岐 ＋ `DriveRunResultPopup` | popup 可視 ＋ body text | **可視** ＋ `Contains("running")` かつ `!Contains("fills:")` | 自動(E2E済) |
| RRT-03 | run1 完了（summary publish） | `FormatReplayRunResultComplete` | popup 可視 ＋ body text | 可視 ＋ `!running` ・`fills:2` ・`total_pnl -410010` | 自動(E2E済) |
| RRT-04 | **#100①** run2 開始＝summary が空へ戻る（portfolio 走行継続） | findings 0077 D1 render 半分 | body text | `Contains("running")` かつ run1 の `fills:2`/`-410010` が**残らない** | 自動(E2E済) |
| RRT-05 | run2 完了（新 summary） | complete 分岐の反復 | body text | `!running` ・run2 の `fills:3`/`12345` | 自動(E2E済) |
| RRT-06 | **D3** LiveAuto run で可視（body text 込み）→ LiveManual で非表示（sticky-flag anti-stale）＋ mode round-trip で dismissed が spurious 再 arm しない | `DriveRunResultPopup` live 枝 `(HasLifecycle||HasTelemetry) && DisplayMode==LiveAuto` ＋ `if(!IsNullOrEmpty(runId))` guard | popup `IsVisible` ＋ body `ViewText` | LiveAuto で可視＋body に `run-A`・LiveManual で **非表示（sticky でも漏れない）**・復帰で再可視・**dismiss 後の LiveManual 往復で再出現しない**（run_id tracker を null に潰さない guard） | 自動(E2E済) |
| RRT-06T | **D3/D8** telemetry-only LiveAuto run（`HasTelemetry` のみ・lifecycle 無）でも可視＋再 arm | `DriveRunResultPopup` の `|| HasTelemetry` 半分 ＋ run_id の `: HasTelemetry ? LatestTelemetry.RunId` fallback（F8） | popup `IsVisible` ＋ body `ViewText` | telemetry だけで **可視**＋body に `fills=2`（`run=` 行なし）・新 telemetry run_id で dismissed 再出現（lifecycle が一度も来ない run でも再 arm） | 自動(E2E済) |
| RRT-07 | **D6** 永続化しない | `CaptureLayout` | `CaptureLayout().floatingWindows` | run_result が capture list に**乗らない**（popup は controller window でない） | 自動(E2E済) |
| RRT-10 | **D1** screen-anchored / pan-invariant（構造 pin） | `RunResultPopup.Build` overlay Canvas | popup `_root` の親 Canvas | overlay は **ScreenSpaceOverlay** かつ親が workspace root（**Content 配下でない**）＝pan で動かない | 自動(E2E済) |
| RRT-08 | **D7** × close latch ＋ 同一 run dismiss | `RunResultPopup.OnClose` → `_runResultDismissed` | popup `IsVisible` | × で非表示・同一 run の running→complete でも再出現しない | 自動(E2E済) |
| RRT-09A | **D8** Replay 再 arm（honest-empty→content rising） | `DriveRunResultPopup` `_runResultPrevReplayHasContent` rising edge | popup `IsVisible` | 次 Replay run（portfolio 再投入）で再出現 | 自動(E2E済) |
| RRT-09B | **D8** LiveAuto 再 arm（run_id 変化・#164 対称化） | `DriveRunResultPopup` `_runResultLastRunId` 変化 | popup `IsVisible` | 次 LiveAuto run（新 run_id）で再出現（sticky フラグでも boolean falling edge に頼らない） | 自動(E2E済) |
| — | 実 backtest 2 連続実行で source が実際に clear されること | `python/tests/test_notebook_replay_afk.py` | engine.last_run_summary lifecycle | （別ゲート＝Python e2e） | 自動(E2E済・pytest) |
| — | 実 pan で popup が canvas と一緒に動かない（パララックス層から除外） | — | 実画面 | （HITL専用・**構造的不変条件は RRT-10 が AFK pin**） | HITL専用（実 pan の奥行き目視・GPU 依存） |
| — | LiveAuto 2 連 run の実画面 latch 再 arm／実 × クリック | — | 実画面 | （HITL専用） | HITL専用（GPU 依存・AFK が可視/latch/再 arm を担保済） |

## RED litmus（delete-the-production-logic）

- `PushReplayTiles` の `IsNullOrWhiteSpace(summaryJson) ? running : complete` を **complete 固定**に潰す → **RRT-02/04 RED**。summary を sticky にする旧 #100 バグ形でも **RRT-04 RED**。
- `DriveRunResultPopup` の live hasContent から **`&& DisplayMode==LiveAuto`（D3）を外す** → LiveAuto run 後に LiveManual へ flip すると sticky telemetry が漏れ **RRT-06 RED**（実機 AFK 確認 2026-06-27）。
- hasContent の **`|| HasTelemetry` 半分**、または run_id の **telemetry fallback** を外す → telemetry-only run が出ない／再 arm せず **RRT-06T RED**。
- run_result を再び controller window 化（CaptureLayout に乗る）→ **RRT-07 RED**。
- popup overlay を **Content（infinite canvas）配下に再 parent** する（または非 ScreenSpaceOverlay 化）→ pan で動く構造退行で **RRT-10 RED**。
- × の latch を no-op に → **RRT-08 RED**。
- LiveAuto 再 arm を run_id 変化でなく **boolean falling edge** にする → sticky フラグでは 2nd run で edge が出ず **RRT-09B RED**（#164 の片側欠落死角を pin）。

親 findings: `docs/findings/0125-run-result-popup-screen-anchored-content-derived.md`（RRT-01..09・ADR-0037）
／ `docs/findings/0077-issue100-audit-bugfixes-run-summary-and-shared-stop-event.md`（RRT-01..05 の #100① 由来）
／ `docs/findings/0110-livemanual-hides-strategy-editor.md` §7（退役した #138 `DriveRunResult` の履歴・SUPERSEDED）
