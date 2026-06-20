# findings 0073 — #95 Phase 4 設計の木（B2 `bt.replay()` ＝実 backtest を worker thread 駆動・走行中 Hakoniwa・pacing・stop）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）。
Phase 1 設計の木: [findings 0070](./0070-notebook-equals-backtest-grill.md)。
Phase 2 土台: [findings 0071](./0071-notebook-foundation-per-cell-run.md)。
Phase 3 `bt` ハンドル: [findings 0072](./0072-issue95-phase3-bt-handle-kernelstepper.md)。

本 findings は **#95 Phase 4**（B2 `bt.replay()`・#97 を吸収）の `/grill-with-docs` セッション 2026-06-20 で確定した下位決定を、会話で消えないように固定する。ADR-0016 / ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は immutable（書き戻さない＝自己保護条項）。実装事実は本 findings に固定し ADR を「方針」として参照する。

base ブランチ: `feat/#95-phase4`（`feat/#95-phase3` 起点。Phase 2 ⊕ Phase 3 を merge `59ed690` で内包＝per-cell RUN ボタン土台 + `bt` ハンドルの両方が揃った唯一の base）。

---

## 0. Phase 4 が出荷するもの（ADR-0016 D7–D11 ＋ findings 0072 Q5 Phase-4 列）

Phase 3 は `bt` を「`bars_per_second` を受理だけして sleep しない / `stop_event` を seam として持つだけ / running guard・worker driver 未配線」の状態で意図的に止めた（findings 0072 Q5）。Phase 4 はその seam を実機能へ昇格する:

1. **`bt.replay(bars_per_second=N)` cell の per-cell RUN が実 backtest を worker thread 駆動**（既存 `BackcastWorkspaceRoot`/`NotebookRunLane` の worker thread 上で `bt.replay()` 本体が回る）。
2. **走行中 Hakoniwa 逐次更新（D8）**: per-bar push を **既存 #65 経路**（`ReplayKernelObserver` → `engine.last_portfolio` → poll lane `LiveRpcLanes.get_portfolio_json`@50ms → `PushReplayTiles`）へ流す。**新 sink を作らない**（D7）。
3. **pacing（D8/D9）**: `bars_per_second=N` を **`replay()` 開始時にキャプチャ**し per-bar `sleep(1/N)`。未指定→全力（sleep なし）。`_REPLAY_BAR_INTERVAL_SEC` 撤去。
4. **stop（D9）**: `force_stop_replay()` → `_replay_stop_event` → `KernelStepper.open_next_bar` が `STOPPED` を返し replay generator を抜ける。**UI は走行中 cell の ▶→⏹ トグル**（owner HITL・下記 §2）。
5. **running guard（D3）**: in-flight 中の第二 RUN を **即時 reject**（queue/no-op ではない）。
6. **GIL ハンドオフの実機 reconfirm（D9・binding）**: 明示 sleep 撤去後、poll lane（C# foreign thread `Py.GIL()`）が走行中 snapshot を逐次読めるかを **実機 Unity AFK で reconfirm してから sleep 撤去を最終 lock**。RED なら `sys.setswitchinterval` floor へ pivot。
7. **production parity gate**: `bt.replay()` 駆動 run が `KernelRunner.run()` と byte-identical（ADR-0016 Consequences）。

---

## 1. ⚠️ issue 本文 D9 を後発 grill F6 が supersede 済み（最重要・額面で実装しない）

issue #95 本文 Phase 4 AC は「**固定 `_REPLAY_BAR_INTERVAL_SEC` を host 所有・スレッドセーフ・live-mutable な速度レジスタに置き換え loop が毎 bar 読む**」「AFK で **速度変更が効く**」と書く。これは **B1 前提の旧設計**で、後発 grill **F6（findings 0070）＝ADR-0016 D8/D9** が逆に確定している:

| 項 | issue 本文 D9（旧・B1 前提） | **binding（F6 / ADR-0016 D8-D9）** |
|---|---|---|
| 速度の持ち方 | live-mutable な速度レジスタ・毎 bar 読む | **`bt.replay(bars_per_second=N)` の開始時キャプチャ値**（走行中変更不可） |
| sleep | 毎 bar 強制 sleep（GIL 床） | **未指定→明示 sleep 撤去・全力**（GIL は CPython auto-switch ~5ms）。指定時のみ pacing sleep `≈1/N` |
| cross-thread register | 速度もレジスタ | **stop（`_replay_stop_event`）だけ**・速度はレジスタ化しない |
| AFK | 「速度変更が効く」 | **「`bars_per_second` 違い → 実測 wallclock pacing が変わる」へ読み替え**（mid-run 変更は実現不能＝設計が却下） |

理由（findings 0070 F6・spike PASS）: owner 優先順位は「**最悪なのは Hakoniwa 更新の都合でエンジン速度を縛ること／見た目の遅れは許容**」。毎 bar 強制 sleep の GIL 床はこの優先順位に反する。GIL spike（`python/spike/gil_handoff_spike.py`）は sleep 撤去で engine ~2.37M bars/s・reader 遅れ ~18ms を実測（PASS）。残存リスクは「spike の reader は Python thread・実物は C# poll lane の foreign thread」で、**それを潰すのが本 Phase の §P4-5 実機 reconfirm**。

→ **本 Phase は issue 本文 D9 ではなく F6/ADR-0016 D8-D9 を実装する**。issue 本文 AC「速度変更が効く」は **§P4-7 で「pacing 差」に読み替えて pin**。

---

## 2. owner HITL（2026-06-20 grill）

**stop の見た目を Phase 4 で出す**。走行中 cell の ▶ ボタンが **⏹（停止）にトグル**し、押すと `force_stop_replay()` で即停止する見た目まで本 Phase で作る。これは Phase 6 の「per-cell の実行状態 UI（idle/running/stale）」を**一部前倒し**する判断（owner が「理想的な完成形を目指せ・手を抜くな」と明示）。Phase 6 とは「⏹ トグルと running state は Phase 4 で先行・block popup と rich output と stale 表示は Phase 6」で整合させる。

---

## 3. 確定した下位決定（P4-1 〜 P4-7 lock）

### P4-1 — `bt` の注入・lifecycle wrap（host 所有・`Backtester` は thin のまま）

**問題**: Phase 2 `NotebookSession` は「engine 非接続の純粋計算」で、source 変化で **kernel を rebuild**（globals 破棄）する（findings 0071 申し送りの landmine: 「Phase 3+ で bt/engine state が kernel に載ると一打鍵で live state を破棄」）。一方 `bt` は **commit された config 単位で 1 個・source 変化では破棄しない**（ADR-0016 D3）。さらに「engine RUNNING 状態」「RunBuffer / summary finalize」は既存 `start_engine(duckdb)`（`_backend_impl.py:849-976`）が持つ重い lifecycle で、これを cell 本体の `for bar in bt.replay()` に被せる必要がある。

**採用（host-owned wrap・thin bt）**:

- **`bt` は host 所有**。`Backtester.from_scenario(scenario, data_root=engine.replay_duckdb_root, sink=ReplayKernelObserver(engine, run_buffer), stop_event=engine.replay_stop_event, rails=...)` で構築し、**marimo kernel globals に free ref `bt` として注入**（既存 `get_bar` 注入と同型・`thin_drain.py:330` パターン）。
- **source-rebuild を跨いで同じ `bt` を再注入**。`NotebookSession` が source 変化で rebuild しても host が保持する同一 `bt`（＝engine/pointer state を内包）を再注入するので、cell 編集の一打鍵で live run state を失わない。**config 再 commit でだけ** host が新 `bt` を作る（D3）。
- **`Backtester` は Phase 3 の thin marimo-free façade のまま**（engine/RunBuffer/finalize を直接 import しない）。engine lifecycle は **host が渡す薄い hook** で駆動する:
  - `on_run_begin`（**初回 drive で 1 回**・`engine.start_engine()` で LOADED→RUNNING ＋ `engine.last_portfolio=None`）— これで poll lane（Replay-mode-gated な `get_portfolio_json`）が走行中 snapshot を拾い始める。
  - 終端（`bt.replay()` の StopIteration / `STOPPED`）後に host が `RunResult` を読んで **RunBuffer finalize → summary → `force_stop_replay()`**（RUNNING→IDLE）。
- **境界は cell の駆動 operation 呼び出しの有無で構造的に決まる**（ADR-0016 D1）: `bt.replay()`/`bt.step()` を**呼ばない**純粋計算 cell は `on_run_begin` を一度も叩かない＝Phase 2 経路そのまま（engine 非接続）。**flag や mode を持たない**。

> 却下: (a) `Backtester` に engine/RunBuffer/finalize を全部持たせる（Phase 3 の thin charter＝marimo-free façade を壊す・層逆転）。(b) bt-driving cell を別 RPC へ routing（「同じ 1 個の marimo engine・同じ RUN ボタン」D2/D9 を壊す）。

### P4-2 — pacing（開始時キャプチャ・機械は stepper に既存）

- `bt.replay(bars_per_second=N)` は **`replay()` 冒頭で `stepper._bar_interval_sec = 1.0/N` をキャプチャ**（`N is None` → `0.0`＝全力）。pacing 機械は **stepper に既存**（`stepper.py:346` `if self._bar_interval_sec > 0: time.sleep(...)`）＝Phase 3 が 0 で no-op にしていただけ。
- **`_REPLAY_BAR_INTERVAL_SEC = 0.01`（`_backend_impl.py:189,952`）を撤去**。imperative `start_engine(duckdb)` 経路も `bar_interval_sec` を **0（全力）** にする＝F6 の「明示 sleep 撤去」を imperative 側にも適用。`bar_interval_sec` は wallclock のみで equity_curve に入らない＝**golden byte-identical 不変**（#24・ADR-0006）。
- 撤去の安全根拠は **§P4-5 GIL reconfirm が GREEN**であること（imperative path の既存 ReplayToHakoniwa AFK が sleep 無しでも逐次更新を保つ）。reconfirm 前に撤去しない。

### P4-3 — stop

- `force_stop_replay()`（`core.py:257`）→ `_replay_stop_event.set()` → `KernelStepper.open_next_bar`（`stepper.py:271`）が `STOPPED` を返す → `bt.replay()` generator が return → cell 本体終了 → host が partial run を finalize。
- **UI（owner HITL §2）**: 走行中 cell の ▶ を ⏹ にトグル。押下 → `WorkspaceEngineHost.ForceStop()` → `force_stop_replay`。C# は「どの cell が走行中か」を持ち、その窓のボタンだけ swap。
- stop は **唯一の cross-thread register**（速度はレジスタ化しない・D9）。

### P4-4 — running guard（in-flight reject）

- bt-driving run が in-flight の間、第二 RUN（純粋計算 cell・別 bt cell・同 cell 重複いずれも）を **即時 reject**（ADR-0016 D3「exception を上げて即時拒否・queue や no-op ではない」）。
- 実装: **host 側 in-flight フラグ**。`NotebookRunController.RunCell` が「run submitted & not yet drained」を見て while-busy は reject（Phase 4 は最小通知＝`_menuBarView.ShowMessage` ライン。polished block popup は Phase 6・D11）。`NotebookRunLane` の BlockingCollection は queue するので、reject は **Submit 前の main-thread guard**で行う。

### P4-5 — GIL ハンドオフ実機 reconfirm（binding・Phase 4 頭で実施）

- ADR-0016 D9 / findings 0070 F6 の binding 義務。**`_REPLAY_BAR_INTERVAL_SEC` 撤去（sleep なし全力）で、C# poll lane（foreign thread `Py.GIL()`）が走行中 `last_portfolio` を逐次読めるか**を実機 Unity AFK で reconfirm。
- 方法: 既存 `ReplayToHakoniwaE2ERunner`（実 run + poll + 逐次 render を assert）を **sleep 撤去後に再走**。逐次更新（all-at-once でない）が保たれれば GREEN。
- **GREEN → 「明示 sleep 撤去」を最終 lock**。**RED → `sys.setswitchinterval` を下げる floor へ pivot**（engine 速度は縛らず switch 頻度だけ上げる）。結果は本 findings の §実機 reconfirm 結果に記録。

### P4-6 — production parity gate

- `bt.replay()` を **`ReplayKernelObserver` 経由で駆動した run** が `KernelRunner.run()` と **byte-identical**（order/fill/equity/summary）であることを pin。Phase 3 の (β)/(γ) は sink buffer レベルの parity。Phase 4 は **production observer/host-path レベル**で追加（ADR-0016 Consequences「`test_dag_byte_identical_to_imperative_twin` は author-defined parity・production binding ではないので別建てが必要」）。

### P4-7 — AFK probe set

| probe | pin する挙動 | RED→GREEN |
|---|---|---|
| replay→Hakoniwa | replay cell RUN → Hakoniwa（orders/positions/buying_power/run_result）が **bar-by-bar 逐次更新** | sleep 撤去 reconfirm（P4-5）と同経路 |
| pacing | **`bars_per_second` 違い → 実測 wallclock が変わる**（issue「速度変更が効く」の F6 読み替え・§1） | 低 bps run が高 bps run より明確に長い |
| stop | replay 走行中に ⏹ → **全 bar 行く前に止まる**（`STOPPED`・bars < N） | stop seam を外すと最後まで走る |

---

## 4. Phase 4 done-gate

1. **GIL reconfirm 実機 AFK GREEN**（P4-5・sleep 撤去 lock の根拠）
2. **既存 #24 / golden 系 green**（`_REPLAY_BAR_INTERVAL_SEC` 撤去後も byte-identical＝主 gate・ADR-0006）
3. **production replay parity green**（P4-6）
4. **pacing pytest**（`bars_per_second=N` → `_bar_interval_sec==1/N` キャプチャ・実測 sleep 差）
5. **stop pytest**（走行中 set で `STOPPED`・bars < N・finalize 済み）
6. **running guard**（in-flight 中の第二 RUN reject）
7. **bt 注入・source-rebuild 跨ぎ persist**（config 不変で同一 bt 再注入・config 再 commit で新 bt）
8. **offline import-purity 不変**（`test_strategy_runtime_offline` が marimo を引かない＝bt 注入経路は lazy）
9. **AFK 3 probe GREEN**（P4-7・台本.md + E2E-INDEX 更新）
10. **CONTEXT 加筆**（pacing/stop/running-guard/bt-injection の実装着地）
11. **findings 0073 landed**（本 finding）
12. **`code-review(simplify)` Medium+ 0**（CLAUDE.md 必須・`/pair-relay` で潰す）＋ `post-impl-skill-update`

---

## 5. Phase 4 範囲外（Phase 5/6 に委譲）

- `bt.step()`（B3・#98）の reset/idempotency 方針・終端 `None`・config 変更 reset の pin → **Phase 5**
- block popup（`bt is already running` の polished 通知）・per-cell の stale 表示・`mo.md`/表/図 rich output → **Phase 6**（⏹ トグルと running state だけ Phase 4 前倒し・§2）
- 常時 block ラベル撤去・RUN クリック時の block-only popup → **Phase 6**
- cross-instrument 発注（`bt.submit_market(qty, instrument="...")`）→ 将来 additive ADR
- title-bar Run / global ▶ Run の formal 撤去 → **Phase 6**（ADR-0016 D2/D4 sunset・Phase 4 では残置）

これらは ADR-0016 の方針下で各 Phase の findings に固定する（ADR は書き戻さない＝自己保護条項）。

---

## 実機 reconfirm 結果（P4-5）

> 実装着地時に追記（GREEN なら sleep 撤去 lock / RED なら setswitchinterval floor の実測値を記録）。

## 実装着地（`feat/#95-phase4`）

> 実装完了時に landed したコード・done-gate 結果・実装中に確定した下位の下位決定を追記。
