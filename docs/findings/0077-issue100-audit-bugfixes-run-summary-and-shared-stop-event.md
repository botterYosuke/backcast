# findings 0077 — #100 監査由来 2 バグの設計の木（run_summary lifecycle ＋ 共有 stop_event 汚染）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）の品質仕上げ。
親: [findings 0075 §Phase 6](./0075-issue95-phase6-run-state-ui-rich-output-sunset.md)（P6-1/P6-6 と sunset 後の context）。
直接の前提: [findings 0073](./0073-issue95-phase4-b2-replay.md)（B2 replay の `on_run_begin` ＋ ReplayKernelObserver）／[findings 0074](./0074-issue95-phase5-bt-step-reset-idempotency.md)（B3 step の永続 bt 〜 cache lifecycle ＋ shared engine.replay_stop_event）。

本 findings は `/behavior-to-e2e` → `/grill-with-docs` セッション 2026-06-21 を通じて #100 の 2 バグについて確定した下位決定と RED→GREEN 証跡を、会話で消えないように固定する。ADR-0016 / ADR-0012 は immutable（書き戻さない＝自己保護条項）。Phase 6 で名指された P6-4 「title-bar Run sunset」が落とした **per-cell 経路の `_runSummaryJson` クリア欠落** がスライス①、Phase 6 P6-6 「`bt.step` 永続 + replay sibling 温存」が見落とした **混在ノートでの `force_stop_replay` による共有 `_replay_stop_event` 汚染** がスライス②。

---

## 0. 監査で「実バグ」と裏取りした 2 件（既存 E2E が構造的に踏まない sequence）

### ① bt.replay 再実行中に RunResult タイルが「前回の」full-stats を表示し続ける

**観測**: bt.replay セルを 1 回押す → 走行完了で full-stats（fills/sharpe/dd）。**もう 1 回押すと、run2 ストリーミング中ずっと run1 の最終統計が出続ける**。他 3 タイル（positions/orders/buying_power）は走行スナップショットを正しく逐次表示。run2 完了で自己修復。

**原因**:
- per-cell 経路の summary 配線は `HostNotebookCellExecutor.Run` が backend JSON の `run_summary` キーを拾って `WorkspaceEngineHost.SetReplayRunSummary` → `_runSummaryJson` を**set-at-return**。
- **クリアは `WorkspaceEngineHost.TryStartRun(RunRequest)` の `_runSummaryJson = null` だけ** にあったが、`TryStartRun(RunRequest)` は **#95 Phase 6 / findings 0075 P6-4** で OnRun / 全 title-bar Run sunset 済み = production 呼び出し元なし。per-cell 経路では開始時クリアが一切走らず、`PushReplayTiles` の `summaryJson != null ? full-stats : running` 分岐が**前回値で誤判定**。

**既存 E2E が見逃した理由**: `NotebookToHakoniwaJourneyE2ERunner`（#95 Phase 6 仕上げ NBHAKO-07）は `TestRunSummaryJsonOverride` を直接注入して running→full-stats の **1 回遷移**だけを見ていた。「実 run × 2 回連続」を通すケースを持たない（characterization 寄り E2E のホワイトスポット）。

### ② replay + step 混在ノートで replay 押下が step セッションを silently 殺す

**観測**: 1 ノートに `for bar in bt.replay():` セルと `bar = bt.step()` セルが両方ある状態で step を何度か押して bar-by-bar デバッグ中に **replay セルを押すと**:
- (a) replay は必ず `LoadReplayData is only allowed from IDLE` で失敗、
- (b) その副作用で**次の `bt.step()` が `None` を返し**、部分実行を「完了サマリ」として finalize → ユーザーは step セッションが走り切ったと誤認。

**原因**:
- 全 bt が engine 単一 `replay_stop_event` を**共有**（`_build_notebook_bt` で `stop_event=self.engine.replay_stop_event`）。
- 永続 step bt は `on_run_begin`→`start_engine` で event を**一度だけ** clear（`_run_begun` ラッチ）。step セッション中は engine RUNNING のまま。
- replay 押下 → `load_replay_data`（IDLE 必須）失敗 → raise → `except` で `force_stop_replay()` が**共有 event を set** ＋ engine IDLE 化。Phase 6 `notebook_uses_step=True` 分岐により step キャッシュは**温存**される。
- 次の step 押下は**キャッシュヒット**（`on_run_begin` 再発火せず＝event set のまま）→ `KernelStepper.open_next_bar` の `stop_event.is_set()` が True → `STOPPED`→`None`→`is_step_bt and bt.result is not None` 分岐で部分実行を finalize。

**Phase 6 P6-6 残課題**: 「pure sibling press は pointer を殺さない」を全 sibling 共通で適用したが、replay は `force_stop_replay` を呼ぶので **pure sibling とは別扱い**になるべきだった。findings 0074 §router table の旧ルール（混在では step キャッシュ teardown）に近いが「混在 detect」ではなく「pressed cell の drive」で判定する。

---

## 1. owner-locked 下位決定（HITL・2026-06-21）

### D1 — ① 単一 source は Python の `engine.last_run_summary`（poll-symmetric）

owner Q1: 「run_summary は portfolio と同じく Python 側 source に統一、C# 側 `_runSummaryJson` set-at-return を撤去」→ **採用**。

- `engine.last_run_summary: Optional[dict] = None` を `engine.last_portfolio` と**同型**で導入（core.py）。
- run-begin で None クリア：per-cell は `_build_notebook_bt.on_run_begin`、launcher は `_start_engine_duckdb`。**共に `start_engine()` の前**にクリア（C# poll @ 50ms と TOCTOU しないよう、新規 run gesture と LOADED→RUNNING transition を同一時点へ）。
- `_finalize_run` が summary 完成と同時に `engine.last_run_summary = summary` を set（last_portfolio と同じ tail で対称）。
- C# は `LiveRpcLanes` poll lane で `get_run_summary_json` を `get_portfolio_json` と**同じ GIL hold 内**で読む（bar 境界で 2 ch が乖離しない、findings 0044 §2/§7-a の StateJson/Status 2 channel split を踏襲）。
- C# 側 `_runSummaryJson` field, `SetReplayRunSummary`, launcher の `summary_json` set-at-return, `HostNotebookCellExecutor.TryGetRunSummary` を**全部撤去**。`RunSummaryJson` プロパティは `_lanes.LatestRunSummary` を読む（`LatestPortfolioJson` と対称）。`TestRunSummaryJsonOverride` は **NBHAKO probe seam として温存**（probe が直接注入する経路）。
- File→New / File→Open は `_host.ClearReplayRunView()` を呼び `BackendService.clear_run_view` で **both** `last_portfolio` ＋ `last_run_summary` をクリア、加えて `_lanes.ResetReplaySnapshot()` で**ローカル poll cache** もクリア（次 poll までの 50ms ギャップを honest-empty にする）。

却下: ① C# 側にクリアパスを per-cell 経路にも追加（whack-a-mole、Phase 6 の Run 経路集約と逆向き）、② `HostNotebookCellExecutor` がクリア責務を持つ（責務は finalize/clear 共に Python が持つべき＝C# は表示のみ）。

### D2 — ② 混在中の replay 押下 = **(c) mode switch**（step を teardown → replay 走行）

owner Q2: 「(a) 専用 event」「(b) クリーン拒否」「(c) step 終了して replay へ」→ **(c) を採用**。

owner 理由（HITL 2026-06-21）: 「step デバッグ中に replay を押すのは『じゃあ全部回そう』というユーザー gesture。拒否や silent skip ではなく、step を**明示終了**して replay を**素直に走らせる**のが期待挙動」。Phase 6 P6-6「pure sibling press は pointer を温存」とは別扱い：pure sibling press は engine を触らないが、replay press は engine RUNNING→IDLE 経由で `force_stop_replay()` を必ず呼ぶ＝**共有 event 汚染が構造的に起きる**ので、設計上「先に step を終わらせる」しか選択肢がない。

実装契約（`_backend_impl.run_cell` の replay 分岐）:
1. `uses_replay` が True で `self._step_bt is not None` → **`load_replay_data` の前に** `self._teardown_step_bt()` を呼ぶ。
2. `_teardown_step_bt()` が `force_stop_replay()` 経由で event を set し engine を IDLE に戻す（step session の clean end）＋ step cache を None 化。
3. 続けて `_build_notebook_bt` が `load_replay_data` を IDLE から呼べる → start_engine の `_replay_stop_event.clear()` で event をフレッシュ化（core.py L254）。
4. 結果: replay は fresh run、step session は teardown＝**partial finalize None None None の沈黙終了は構造的に起きない**。直後の step press は cache miss でフレッシュ rebuild（pointer は 0 から）。

却下:
- **(a) 専用 event**: 「step デバッグ中の replay は別事象＝触らない」モデルは owner の gesture 期待と逆。
- **(b) クリーン拒否**: ガイダンス出すだけで何も走らない＝「全部回そう」が叶わない。

### D3 — Pure-compute sibling は依然 step cache 温存（findings 0075 P6-6 不変条件保持）

D2 の teardown gate は「**pressed cell が `bt.replay` を drive する**」ことだけ（`uses_replay`）。`bt.replay` を含む sibling cell があっても、pressed cell が pure-compute なら step cache は触らない＝Phase 6 P6-6 の「pure sibling press は pointer を温存」は無傷。これは `test_step_bt_torn_down_when_source_no_longer_uses_bt_step` と `test_mixed_replay_and_step_notebook_pressing_step_persists` の既存 GREEN が証明し続ける（リグレッション網）。

### D4 — File→New/Open での clear は run_summary も同時に対象

owner 選択（issue ① AC L11「File→New/Open 跨ぎでも前回 run の stale full-stats を表示しないこと（owner 判断: portfolio poll も同様に persistent なので許容なら明記）」）: 「両方クリアして honest-empty で揃える」。`clear_run_view` が portfolio + run_summary を一括クリア＝**document boundary の意味は『前 doc の run output を全捨て』**。idempotent（既に None なら no-op）。

---

## 2. RED → GREEN（characterization 再来を避ける regression net）

すべて **`/behavior-to-e2e` 起源**＝この 2 バグの sequence を構造的に走るゲートを追加。

### ② `test_notebook_step_afk.py::test_mixed_notebook_replay_press_ends_step_session_cleanly`
- RED（fix 前）: replay 押下が `LoadReplayData is only allowed from IDLE` で失敗 → `_step_bt` 温存 ＋ 共有 event set → 次 step press が partial finalize で None 返却。
- GREEN（D2 適用後）: replay 押下 = mode switch（step teardown → fresh replay）→ `out_replay["run_summary"]` 真値・BUY+SELL fills・`ohlc_points == _N_BARS`・`_step_bt is None`・engine IDLE。続く step press は cache miss で fresh rebuild、partial finalize なし。

### ① `test_notebook_replay_afk.py::test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale`
- RED: 2 連続 bt.replay press の **run2 on_run_begin で `engine.last_run_summary` を観測**すると `{total_pnl, fills_count, sharpe, ...}` が残存（run1 の summary）。
- GREEN: D1 の「start_engine の前にクリア」適用後、observed `captured[1] is None`。test は `engine.start_engine` を wrap して observed; clear 順を逆にした（start_engine → clear）と RED に逆戻りする regression net。

### ① contract: `test_notebook_replay_afk.py::test_pure_compute_press_does_not_clear_run_summary`
- pure-compute press は `engine.last_run_summary` を**触らない**ことを assert（only bt drive may clear/set）。owner が「pure-compute は pointer も summary も temper しない」P6-6 と整合。

### portfolio-json 層: `test_get_portfolio_json.py` +3
- `test_get_run_summary_json_emits_dict_when_set`（last_run_summary set → JSON で keys 露出）
- `test_get_run_summary_json_empty_when_no_summary`（honest-empty `""`）
- `test_clear_run_view_clears_both_last_portfolio_and_last_run_summary`（document-boundary reset）

### ① RENDER 半分（C# AFK）: `ReplayRunResultTileE2ERunner`（RRT-01..05）

D1 は run_summary の single source を Python（`engine.last_run_summary`）へ寄せた＝**「source が空なら running／非空なら full-stats を描く」分岐は C# (`BackcastWorkspaceRoot.PushReplayTiles`) にしか無い**。この RENDER 半分は Python e2e では証明できないので、Unity AFK で固定する（behavior-to-e2e の「DATA 経路 C#↔Python 跨ぎ＝2 ゲート分割」）。

- **カバレッジ穴の経緯**: #65 の RunResult running→full-stats は `HakoniwaBaseModeProbe` S5/B2 が AFK 化していたが、findings 0060 で `HakoniwaE2ERunner` に集約 → **#99 (commit `77e39c7`)** が Hakoniwa floating-window 化で `HakoniwaE2ERunner`（836 行）を **run_result カバレッジごと wholesale 削除**し「将来 Panel runner へ移送」のまま re-home せず。RunResult タイルの C# AFK カバレッジがゼロになった死角に #100 ① が landed。
- **新 runner**（`Assets/Tests/E2E/Editor/ReplayRunResultTileE2ERunner.{cs,md}`、Python-FREE = `TestPortfolioJsonOverride`/`TestRunSummaryJsonOverride` 注入・削除された probe と同型）:
  - RRT-01 portfolio 無→honest-empty、RRT-02 portfolio 有+summary 空→running、RRT-03 summary publish→full-stats（`fills:2`/`total_pnl -410010` 束縛）。
  - **RRT-04（#100 ① GATE）**: full-stats の後に summary を空へ戻す（run2 開始・portfolio は走行継続）→ RunResult は **running へ戻る**（run1 の `fills:2`/`-410010` が残らない）。pre-#100 は run2 走行中ずっと run1 の full-stats が残った＝そのバグの render 層を踏む。
  - RRT-05 run2 summary→full-stats（running↔full の反復＝one-shot でない）。
- **RED litmus（delete-the-production-logic）**: `PushReplayTiles` の `IsNullOrWhiteSpace(summaryJson) ? running : complete` を complete 固定に潰すと RRT-02/RRT-04 が RED。summary を sticky 化（旧 #100 バグ形）でも RRT-04 が RED。
- 件数を `E2E-INDEX.md`（Surface 13 本）へ登録。

### code-review (Agent 3) 追補: `test_notebook_replay_afk.py::test_build_failure_after_successful_run_clears_run_summary` +1
- 監査で発見: `_build_notebook_bt` / `_acquire_step_bt` の **exception path** は prior 成功 run の `last_portfolio` / `last_run_summary` をクリアしない（on_run_begin の clear は build 成功時にしか fire しない）→ build failure 後の poll が **stale full-stats** を露出。
- fix: `run_cell` の **step / replay 両 exception branch** で `force_stop_replay` 直後に `last_portfolio = None; last_run_summary = None` を明示クリア（on_run_begin の clear を mirror）。
- 同時に redundant な `if self._step_bt is not None:` guard を除去（`_teardown_step_bt` 自身が冪等＝D2 の comment で文書化）。

---

## 3. ゲート（2026-06-21）

- step AFK 11/11 ・ replay AFK 8/8 ・ portfolio-json 7/7（追加 5 本 = ② 1 + ① 3 + portfolio-json 3、code-review +1）
- full pytest **474 passed + 1 skipped**（既存スキップ・regression 0）
- [#24 golden byte-identical](./0024-byte-identical-golden.md) `test_kernel_golden_cpython.py` 緑（engine 出力不変＝設計通り core/replay loop 改変なし）
- C# compile-gate `error CS` 0 件・`warning CS` 0 件・`Exiting batchmode successfully now!`・exit 0
- NBHAKO probe seam: `TestRunSummaryJsonOverride` 残置（直接注入経路は不変、tile rendering 契約 preserved）。

---

## 4. 変更ファイル要約

Python:
- `engine/core.py`: `DataEngine.last_run_summary: Optional[dict]` 追加（last_portfolio と同型）。
- `engine/_backend_impl.py`:
  - `_build_notebook_bt.on_run_begin`: `start_engine` の前に `last_portfolio = None; last_run_summary = None`。
  - `_start_engine_duckdb`: 同上を `start_engine` の前で実施。
  - `_finalize_run`: `engine.last_run_summary = summary`（last_portfolio set と同じ tail で対称）。
  - `run_cell` replay 分岐: `_build_notebook_bt` の前に `if self._step_bt is not None: self._teardown_step_bt()`（D2 mode switch）。
- `engine/backend_service.py`: `get_run_summary_json`（honest-empty）／`clear_run_view`（both clear）。
- `engine/inproc_server.py`: 上 2 メソッドを delegate 公開。

C#:
- `Assets/Scripts/Live/LiveRpcLanes.cs`: `_latestRunSummary` + `LatestRunSummary` プロパティ追加、PollLane で `get_run_summary_json` を `get_portfolio_json` と**同 GIL hold** 下で poll、`ResetReplaySnapshot()` 追加。
- `Assets/Scripts/Live/WorkspaceEngineHost.cs`: `_runSummaryJson` field と `SetReplayRunSummary` を撤去、`RunSummaryJson` を `_lanes.LatestRunSummary` ベースへ、`TryStartRun(RunRequest)` のクリアコメント差し替え、`Launcher` の summary_json set-at-return 撤去、`ClearReplayRunView()` 追加（backend `clear_run_view` + lane snapshot reset）。`TestRunSummaryJsonOverride` 温存（NBHAKO probe seam）。
- `Assets/Scripts/Live/HostNotebookCellExecutor.cs`: `TryGetRunSummary` ヘルパと `SetReplayRunSummary` 呼び出しを撤去（single source = Python finalize→poll）。
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`: `DoFileNew` と `DoFileOpen` の成功 commit 直後に `_host?.ClearReplayRunView()`。

Tests:
- `python/tests/test_notebook_step_afk.py`: `test_mixed_notebook_replay_press_ends_step_session_cleanly`（+1）。
- `python/tests/test_notebook_replay_afk.py`: `test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale`（+1）、`test_pure_compute_press_does_not_clear_run_summary`（+1 contract）。
- `python/tests/test_get_portfolio_json.py`: `test_get_run_summary_json_emits_dict_when_set` / `test_get_run_summary_json_empty_when_no_summary` / `test_clear_run_view_clears_both_last_portfolio_and_last_run_summary`（+3）。

---

## 5. なぜ ADR-0016 は無改変か

ADR-0016（per-cell RUN = backtest 実行エントリー）は decision レベルで「run-begin で prior run output を捨てる」を**既に**要求している（D1「per-cell が strategy 実行の唯一 entry」）。本 findings の D1/D2 は ADR の文言と矛盾せず、「**どこで** clear するか／**どの seam** を single source にするか」という下位決定の埋め込み（implementation lock-in）に当たる。ADR 0016 の自己保護条項「The decisions are fixed … reopening requires a new ADR superseding this one, not an edit to this file」と整合（findings 0075 が確立した運用と同じ）。Phase 6 P6-4 sunset が落とした _runSummaryJson クリアパスは ADR レベルではなく実装の漏れで、本 findings がそれを設計の木として固定する。
