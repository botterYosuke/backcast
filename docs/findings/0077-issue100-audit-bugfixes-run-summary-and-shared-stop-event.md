# findings 0077 — #100: #95 監査で発見した 2 実バグの修正（run_summary 再実行 stale ／ 共有 stop_event 汚染）

Parent: #95（Strategy Editor の marimo frontend 化・CLOSED）。本 issue は #95 を敵対的に監査して見つけた
2 件の実バグの RED-first 修正。いずれも #95 の characterization 寄り E2E が**構造的に踏まないシーケンス**に
潜んでいた。設計は `/behavior-to-e2e` → `/grill-with-docs` の順で固め、両スライスとも **RED→GREEN** をこの
findings に記録する。

関連: [[0073-issue95-phase4-b2-replay]]（#65 running-snapshot seam・`last_portfolio` poll）、
[[0074-issue95-phase5-bt-step-reset-idempotency]]（step bt 永続・router table）、
[[0075-issue95-phase6-run-state-ui-rich-output-sunset]]（P6-6 `notebook_uses_step` ゲート・title-bar Run sunset）。

---

## コード裏取り（grill）— 2 バグとも実コードで確認

### スライス① run_result が再実行中ずっと前回 full-stats を表示（AFK）
- `WorkspaceEngineHost._runSummaryJson` を **null へクリアする経路は `TryStartRun`（`:324`）にしか無く**、
  `WorkspaceEngineHost.TryStartRun` には **production 呼び出し元が 0**（OnRun/title-bar Run の sunset。grep で
  `TryStartRun` の hit は全て無関係な `ScenarioStartupController.TryStartRun`）。
- per-cell 経路は `HostNotebookCellExecutor.Run`（`:32`）が **同期 `InvokeRunCell` が戻った後に** result の
  `run_summary` key 有無で `SetReplayRunSummary` を set/clear するだけ。run2 のストリーミング中（worker が
  `InvokeRunCell` でブロック中）は poll lane が **run1 の `_runSummaryJson` を読み**、`PushReplayTiles`
  （`BackcastWorkspaceRoot:1039`）の `summaryJson != null ? full-stats : running` 分岐が前回値で誤判定する。
- 他 3 タイル（positions/orders/buying_power）は `engine.last_portfolio` を **`on_run_begin` で None クリア**
  しているため正しく running 表示になる。**summary だけが `last_portfolio` の持つ「run 開始時クリア」を欠いていた**。

### スライス② replay+step 混在で replay 押下が step を沈黙終了（HITL）
- 全 bt が engine の単一 `replay_stop_event` を共有（`_build_notebook_bt:1191`）。step セッションは engine RUNNING。
- replay 押下 → `_build_notebook_bt` → `load_replay_data`（IDLE 必須 / `core.py:161`）失敗 → `except` →
  `force_stop_replay()`（`core.py:268`）が**共有イベントを set**。Phase 6 の `notebook_uses_step` ゲート
  （`:911`）で step キャッシュは温存 → 次の step 押下は cache HIT（`_acquire_step_bt:1103`）で `on_run_begin`
  再発火せず＝イベント set のまま → `KernelStepper.open_next_bar:288` が `is_set()` → STOPPED → `bt.step()` が
  None → `is_step_bt and bt.result is not None`（`:993`）で**部分実行を「完了サマリ」として finalize**。

---

## owner 決定（HITL / plain-language で確認）

| 問い | 決定 |
|---|---|
| ① run_result の stale を直す方向 | **Python 側で統一**（poll-symmetric）。`engine.last_run_summary` を `last_portfolio` と同型に：run-begin でクリア・finalize で確定、C# は poll で覗くだけ。 |
| ① File→New/Open 跨ぎ | **空に戻す**。New/Open で run_result（および建玉 poll）を honest-empty へ。 |
| ② 混在ノートで step 中に replay 押下 | **(c) step 終了して replay へ切替**（mode switch）。replay 押下で step セッションを明示終了し、replay は IDLE から clean に走る。 |

② の (c) は findings 0074 §router table の旧ルール「混在では step キャッシュ teardown」に整合する。Phase 6
（[[0075-issue95-phase6-run-state-ui-rich-output-sunset]] P6-6）が `notebook_uses_step` ゲートへ変えた際、
**replay 経路の `force_stop_replay` が共有イベントを set する点が未考慮**だった——その穴を (c) で塞ぐ。なお P6-6 の
「**pure-compute** sibling press は pointer を殺さない」意図は維持（teardown は **replay 駆動 press 限定**）。

---

## 設計（poll-symmetric ＝ 既に「正しい」と監査確認済みの `last_portfolio` を鏡映）

監査の「正しいと確認済み」リストに `last_portfolio` の **atomic swap・run 開始時クリア**が入っている。①の理想形は
run_summary を**その実証済みパターンに合流させる**こと（新規機構を足さない）。

```
engine.last_run_summary : Optional[dict]            # core.py（last_portfolio の隣）
  on_run_begin:  last_portfolio=None, last_run_summary=None     # _build_notebook_bt（両方同時クリア）
  _finalize_run: last_portfolio=compute(...), last_run_summary=summary   # 両方同時セット
BackendService.get_run_summary_json() -> ""|json    # honest-empty（get_portfolio_json と同型）
InprocLiveServer.get_run_summary_json / clear_run_view  # 転送
LiveRpcLanes.PollLane: Replay gate 下で get_run_summary_json → _latestRunSummary   # get_portfolio_json と同 GIL hold
WorkspaceEngineHost.RunSummaryJson => override ?? _lanes.LatestRunSummary   # LatestPortfolioJson と同型
  （旧 _runSummaryJson フィールド・SetReplayRunSummary・Launcher/TryStartRun の write は撤去＝単一 source）
HostNotebookCellExecutor.Run: SetReplayRunSummary 呼びを撤去（summary は poll 経由）
BackcastWorkspaceRoot.DoFileNew / DoFileOpen: _host.ClearReplayRunView()   # ① New/Open 空クリア
```

「pure-compute press はクリアしない（key 省略の現契約）」は**自動的に保たれる**：`on_run_begin` は bt 駆動 press
でしか発火しないので、pure-compute press は `last_run_summary` を触らない。

②は `run_cell` の replay 分岐（`_backend_impl.py` else / `:942` 付近）で **`_build_notebook_bt` の前に**
`if self._step_bt is not None: self._teardown_step_bt()` を入れるだけ。`_teardown_step_bt` は
`force_stop_replay()` で engine を IDLE 化＋providers リセットするので、後続 `load_replay_data` が成功し、fresh
replay bt の `on_run_begin`→`start_engine` が共有イベントを clear してから走る。

---

## RED→GREEN（回帰ゲート）

### スライス② — `python/tests/test_notebook_step_afk.py::test_mixed_notebook_replay_press_ends_step_session_cleanly`
- **RED**（修正前）: 混在ノートで step×3 → replay 押下が
  `RuntimeError: load_replay_data failed: LoadReplayData is only allowed from IDLE` で `ok=False`（バグの(a)症状。
  この後 step 再押下で None→部分 finalize の(b)症状に至る）。
- **GREEN**（teardown-before-replay 追加後）: replay が IDLE から走り `run_summary` を返す → step 再押下は **fresh
  rebuild**（pointer reset・実 bar・premature `run_summary` 無し）。`test_notebook_step_afk.py` 全 11 件緑。

### スライス① — `python/tests/test_notebook_replay_afk.py`
- `test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale`:
  **RED**（`on_run_begin` クリア未追加時）: run1 完了後 `last_run_summary = {total_pnl:3700,...}`、run2 の
  `on_run_begin` 後も **残存**（`assert ... is None` 失敗）。**GREEN**（クリア追加後）: run-begin で None。
- `test_pure_compute_press_does_not_clear_run_summary`: pure-compute press 後も `last_run_summary` 不変（contract）。
- `python/tests/test_get_portfolio_json.py`: `get_run_summary_json` honest-empty／populated、`clear_run_view`
  が両 snapshot を落とす。
- 回帰: `test_notebook_replay_afk.py`(7) / `test_notebook_step_afk.py`(11) / `test_get_portfolio_json.py`(7) 緑。

### C# 側のゲート分担（#65 と同じ）
poll glue（`get_run_summary_json` → `_latestRunSummary` → `RunSummaryJson`）は実サーバ無しに AFK 化できない
（`get_portfolio_json` と同様）。よって **engine lifecycle は Python e2e（上記）が正本**、**tile rendering は
NBHAKO-07 が `TestRunSummaryJsonOverride` 経由で緑維持**（override は撤去せず・RunSummaryJson が最優先で読む）、
**C# 配線は compile-gate（`error CS` 0 件）**で担保。New/Open クリアは `clear_run_view` の pytest＋C# compile-gate。

---

## 既知の性質（code-review で確認・honest disclosure）
poll モデル固有の **≤1 poll tick（50ms）の遅延**は run_summary にも残る：再実行の `on_run_begin` が
`last_run_summary` を None にした瞬間〜次 poll までの間、C# の `_latestRunSummary` キャッシュは run1 値を保持し得る
（最大 50ms だけ full-stats がちらつく）。これは **`last_portfolio` poll と完全に同一**の挙動で、#100 の本バグ
（run2 の**全区間**＝秒〜分 stale）とは別物（秒→1 tick へ縮小）。これを 0 にするには run-begin で C# キャッシュを
即リセットする Python→C# コールバックが要り、portfolio との対称性を壊す非対称な特別扱いになる（altitude 上不可）。
よって **portfolio と同じ poll 遅延を許容**＝設計どおり。File→New/Open は UI スレッドから到達できるので
`ClearReplayRunView` で即時キャッシュリセットし、この経路だけは tick 遅延も消している。

## 完了基準（全達成 2026-06-21）
- [x] slice②: step AFK 全緑（新 RED→GREEN 含む）— `test_notebook_step_afk.py` 11/11
- [x] slice①: replay AFK／portfolio-json 全緑（新 RED→GREEN 含む）— replay 7/7・portfolio-json 7/7
- [x] C# compile-gate `error CS\d+` 0 件（batchmode exit success）
- [x] #24 golden byte-identical（`test_kernel_golden_cpython.py` 緑＝engine 出力不変・snapshot state 追加のみ）
- [x] 既存 NBHAKO 緑（`[E2E NB→HAKONIWA PASS]`・FAIL 0・CS 0・clean shutdown）。full pytest **474 passed**。
