# Replay panel real-data Findings: Replay 実行中の base パネル(BuyingPower/Positions/Orders/RunResult)へ実数値を配線

- 受け皿 issue: **#65**（chart/panels: Replay 時の base パネルに実数値を配線）。親 #1 (Epic) / #5 (Step3 cutover)。**#61（findings 0028・mode-conditional base tiles）の follow-up**（owner 指示「silent truncation 禁止＝Replay パネルの空表示を追跡可能に起票」2026-06-16）。
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — 1:1 surface parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted・自己保護節）, [ADR-0006 — DuckDB 直読み＋nautilus 完全退役](../adr/0006-*.md)（本線 Replay は kernel-native）。下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0005/0006」として参照。
- 先行: **#61（findings 0028・base tile 化＋honest "(no data)"）**, #49（findings 0019・DuckDB→kernel cutover・`ReplayKernelObserver` 導入）, #29（bar-by-bar streaming poll seam）, #10（`ReplayPanelDecoder`）, #23（`LivePanelTileView`）。
- 設計確定: `grill-with-docs`（2026-06-17・owner インタビュー）。AC を TTWR 実ソース（`src/backend/transport.rs` の push_portfolio/push_run_complete・`src/backend/sync.rs` の PortfolioState reducer・`src/ui/buying_power.rs`/`run_result_panel.rs`）と照合。

> **状態: 設計確定（grill 2026-06-17・全分岐 lock）。** 実装着手時に §11 実装証跡へ追記する。

---

## 0. 確定した現状（コード裏取り 2026-06-17）

**サーバ側にデータは既に存在する**（engine 大改造は不要）:
- 本線 Replay = `_backend_impl._start_engine_duckdb` → `KernelRunner.run()`（`engine/kernel/runner.py`）。約定ごとに `push_order`→`push_portfolio`、毎バー `on_equity`、終了時 `push_run_complete`。
- 本線の sink は `ReplayKernelObserver`（`engine/strategy_runtime/replay_kernel_observer.py`）。`push_bar`→`apply_replay_event`（reducer→poll）、`push_order`/`on_equity`→`RunBuffer`。**`push_portfolio`/`push_run_complete` は本番 no-op**（last_portfolio は run 完了時に `compute_portfolio` で1回だけ作る設計）。
- run 完了時 `_finalize_run` が `compute_portfolio`（`{buying_power,cash,equity,positions,orders}`）と `compute_summary`（`{sharpe,sortino,max_drawdown,fills_count,...}`）を作り、`engine.last_portfolio` に格納。`get_portfolio()` で取れる（`backend_service`/`inproc_server` 経由で C# に露出済み）。

**2つの断線（#65 が繋ぐ対象）**:
1. **本線 observer が走行中の portfolio を保持しない** → `get_portfolio` は run 完了後しかライブ値を返さない。
2. **C# は Replay では `get_state_json` poll しか読まない** → `get_portfolio` を叩く caller が C# に無い。`ReplayPanelDecoder.DecodePortfolio/DecodeRunResult` は実装済みだが editor probe（`KernelSinkDecodeProbe`）でしか使われない死蔵状態。base パネルは #61 で `ShowReplayEmpty()`（`_baseLive==false`）。

**TTWR parity（移植元 `The-Trader-Was-Replaced`）**:
- transport は**2チャネル分離**（`src/backend/transport.rs`）: チャート(`push_bar`)→`InProcResp::StateJson`（state_tx）／portfolio(`push_portfolio`)＋成績(`push_run_complete`)→`InProcResp::Status`（status_tx）。**StateJson に portfolio を混ぜていない**。
- `push_portfolio(json)` は**約定ごとに stream** → `PortfolioUpdate::PortfolioLoaded{buying_power,cash,equity,positions,orders}` → 共有 `PortfolioState` resource → `buying_power_panel_system` ほかが描画（`src/backend/sync.rs:587`, `src/ui/buying_power.rs`）。
- `push_run_complete(run_id, summary_json)` は **run 完了時に1回** → `RunUpdate::RunComplete` → `CurrentRun`/`RunState` → `run_result_panel`。
- ⇒ **parity 挙動: 余力/建玉/注文＝再生中ライブ更新、成績(run_result)＝完了時に確定表示（途中値は出さない）**。

## 1. 表示タイミング（owner 確定 2026-06-17）= **A案 = 再生中ライブ更新**

- 余力/建玉/注文は、チャートが1バーずつ伸びるのと同テンポで刻々更新。run_result は完了時に確定（上記 parity ニュアンス）。
- 不採用 B案（run 完了後に一括表示のみ）: #61 の「再生中は空」を半分しか脱せず、owner「沈黙の欠落を残さない」方針・TTWR の per-fill stream parity に逆行。

## 2. トランスポート（owner 確定 2026-06-17）= **別 poll（`get_portfolio` 再利用）**

- C# poll ループ（`LiveRpcLanes`・50ms・現状 `get_state_json` 一本）に **`get_portfolio` を並べる**。`get_state_json`（チャート）に portfolio を混ぜない。
- **決め手 = TTWR の2チャネル分離の忠実写し**（StateJson と Status が最初から別系統）。B案（`TradingState` 相乗り）は TTWR が分けているものを統合する parity 逆行で、かつ `TradingState` の `frozen=True` boundary model ＋ §9.14 ADR「`live_last_error` を必ず末尾 field」（`models.py:84-86`）という責務境界を汚す。
- 既存資産の再利用: `ReplayPanelDecoder.DecodePortfolio`（死蔵）をそのまま本線 poll に配線。`get_portfolio` の dict 形（`backend_service.get_portfolio`）は decoder の期待形と一致。
- トレードオフ（許容）: チャートと建玉が別 poll なので最大 ~50ms ズレ得るが、再生中建玉表示で実害になりにくく、parity＋責務分離の利益が上回る（owner 判断）。

## 3. 走行中スナップショットの更新ルール（owner 確定 2026-06-17）

`ReplayKernelObserver` が `self._snapshot` を持ち、2フックで更新 → **完成 dict を `engine.last_portfolio` に atomic ref swap**（in-place 変更禁止。読み口は `get_portfolio`＝`last_portfolio` の1本のまま）:

- **`push_portfolio(portfolio)`（約定ごと）** → `positions` と `cash`(=余力) を更新。
  - `positions` は **`portfolio.open_positions()`（kernel `Portfolio._positions`: qty/avg_px のみ）から構築**。`unrealized_pnl=0.0` 固定（確定 snapshot の `strategy_runtime/portfolio.py:43` も 0.0 ハードコード＝完全一致、現値マーク不要）。
  - **`qty` は `int(round(signed_qty))` で丸める**（`_net_positions` が int 丸め・`Position.quantity` は float。丸めないと finalize で 100.0→100 の見かけジャンプ）。
  - cash/buying_power = `portfolio.cash`。**equity は push 側で触らない**（`Portfolio.equity==cash` で MTM でない・`kernel/portfolio.py:55-62`。equity は on_equity の MTM のみ権威）。
- **`on_equity(ts, equity, cash)`（毎バー無条件）** → `equity`(=MTM) と `cash`/`buying_power` を更新。`positions` は据え置き。
- 同一バー内順序は push_portfolio(@runner.py:291) → on_equity(@308) なので equity は最後の on_equity の MTM 値で確定。確定 snapshot（`compute_portfolio`: `equity=equity_points[-1].equity`/`cash=last.cash`）と収束先が一致 → **finalize で値が飛ばない**。cash は両フックとも `self._portfolio.cash`(@309) で競合なし。

**run 境界 / honest empty:**
- `_start_engine_duckdb` の run 開始時に **`engine.last_portfolio` をクリア**（現状クリアしておらず前 run の値が残る＝要追加）。「ロード済み・未走行」は honest empty "(no data)"。
- 走行開始後は **bar 1 の on_equity から `mark_to_market_equity({})==cash==initial_cash`・positions=[] が即得られ、初回約定前でも初期資金・建玉なしを即表示**。クリア→bar1 on_equity 直前のごく一瞬だけ "(no data)" がちらつくが Replay では無視可。
- **前進的乖離（許容・owner 確定）**: TTWR は初回 position イベントまで "—"。backcast は #49/**ADR-0007** で per-bar `on_equity`(MTM equity) フックを ADR 承認済み＝richer 設計。初期資金＋フラット即表示はこの承認済みフックの自然帰結であり新規の恣意的乖離でない。TTWR の "—" は equity フック不在の副産物。逆に「TTWR に合わせて初回約定までゲート」は ADR-0007 の MTM 設計に逆行するので採らない。

**実装フェーズで裏取りする前提（更新ルールとは別軸・owner 指摘）:**
1. **observer→engine 配線**: 現 `ReplayKernelObserver` は `self._buf` しか持たず push_portfolio は no-op。走行中 snapshot 化には observer に engine（or holder）参照を渡し完成 dict を atomic swap する配線が要る。
2. **毎バー GIL 解放**: C# poll は毎回 `Py.GIL()` を取る。run ループがバー間で GIL を譲らないと poll が走行中 snapshot を読めず「完了時に一気出し」になる（A案 live の前提）。runner 末尾 `_time.sleep(bar_interval_sec/…)`(@330-334) が GIL を手放す既存 #29 seam なので `_REPLAY_BAR_INTERVAL_SEC>0` を確認。

## 4. RunResult パネル（owner 確定 2026-06-17）

**権威は issue #65 AC**（「Replay 実行中、BuyingPower/Positions/Orders/RunResult が実数値を表示する」）。AC が TTWR replay 挙動を上書きする。

**4-a 完了時の確定統計（= launcher が summary_json を回収）:**
- 今 launcher は `start_engine` 戻りの `success`/`error` のみ読み **`summary_json` を捨てている**（`WorkspaceEngineHost.cs:225-232`）。Python `start_engine` は summary_json を返しているので**拾うだけ**（新規 RPC 不要・TTWR `push_run_complete→RunComplete{summary_json}` の忠実写し）。
- 本番 `_finalize_run` は `compute_summary` のみ（sharpe/sortino 無し・`summary.py:41-75`）。`equity_curve_stats`（sharpe/sortino・同 78-109）は KernelRunner の no-op push_run_complete 経路でしか呼ばれず本番では捨てられている。→ **`_finalize_run` で `equity_curve_stats(equity_values)` を合流**し TTWR `RunSummary` と同じ union **{fills_count, equity_points, total_pnl, max_drawdown, sharpe, sortino}** にする。
- ⚠ **`max_drawdown` 二重計算**: `compute_summary` と `equity_curve_stats` が別々に算出。**1ソースに寄せて**値が食い違わないようにする。
- C# `RunResult`/`RunResultDto` に **`total_pnl` 追加**（現状 sharpe/sortino はあるが total_pnl 無し・`ReplayPanelDecoder.cs:81-120`）。

**4-b 走行中の running view（採用・ただし AC 根拠の前進的乖離）:**
- **これは parity ではない**。TTWR の running view（`o:/f:/realized/unrlz`）を駆動する `LiveStrategyTelemetry` は **live 専用**（`live_orchestrator._on_auto_telemetry`・`NautilusLiveEngineController` のみ登録）で Replay/backtest runner は emit しない。TTWR Replay では running view は出ず counts/pnl=0 のまま完了時に full stats へジャンプ（`n2_live_strategy_telemetry.rs` も live 専用）。
- backcast は **issue #65 AC が「RunResult を Replay 実行中に実数値」と要求**するので Q5 と同じ「AC 根拠の前進的乖離」として走行中 running view（`o:注文数 f:約定数` ＋ `pnl:realized / unrlz:unrealized`）を採用。
- **UI 構造は parity**（`run_result_panel.rs:124-170` の「実行中=running view／完了後=full stats」2段描画を忠実に写す）。**データ供給が前進的乖離、UI 構造は parity** と整理。
- 値の出どころ（既存 snapshot から・追加コスト小）:
  - counts: 約定数 = running snapshot.orders 長。
  - **realized = `Portfolio.realized_pnl`**（push_portfolio が渡す Portfolio オブジェクトに直接ある・`kernel/portfolio.py:79`。導出不要）。
  - **unrealized = (MTM_equity − cash) − Σ(qty×avg_px)**（時価−取得原価）。⚠ `MTM_equity − cash` 単体は建玉時価総額 Σ(qty×price) であって含み損益ではない。

## 5. Orders パネル（owner 確定 2026-06-17）= **fills を FILLED 注文行で表示**

- 権威は issue #65 AC（Orders に実数値）。TTWR は Live→`LiveOrders`/Replay→`PortfolioState.orders` 分岐（`orders.rs:101-147`）だが私が読んだ transport slice は push_portfolio が `orders:[]`＝TTWR replay の Orders は空。**backcast は `compute_portfolio.orders`（fills を FILLED 行化）を既に持つ前進的乖離**（AC＋データ可用性が根拠・Replay は MARKET 即約定で resting order 無し＝「注文＝約定履歴」が自然）。
- C# `PortfolioDto` は **orders 未宣言**（`ReplayPanelDecoder.cs:106-111`・コメント「orders は常に [] なので宣言しない」）→ **orders 追加＋コメント撤去**。
- observer の `push_order` は現状 `fills.jsonl` にしか書かない（`replay_kernel_observer.py:59-70`）→ **走行中ライブで Orders を増やすには push_order ごとに running snapshot.orders へ追記**する配線が要る（4-b の counts もこの orders 長から導出）。

## 6. golden #24 byte-identical 不変の担保（設計制約）

変更は **`ReplayKernelObserver`（本番 observer）＋ `_finalize_run`（本番 DuckDB 経路のみ・`_start_engine_duckdb` から呼ぶ）＋ C# decode/render** に限定。**`KernelRunner`/`EventSink`/`Portfolio` には触れない**（golden #24 は KernelRunner+push_target→EventSink 直結経路で `_finalize_run` を通らない）。observer は既存の `push_portfolio`(Portfolio 渡し)/`push_order`/`on_equity` フックを使うだけで runner 改修不要（realized_pnl も既存）。→ 凍結 golden は byte-identical 維持。

## 7. C# 描画 seam（owner 確定 2026-06-17）

**7-a get_portfolio poll — ⚠ `get_portfolio_json()` 新設が必須（計画の穴・owner 検出）:**
- `get_portfolio` は **JSON を返さない**: `_backend_impl` は `PortfolioResult` オブジェクト、`backend_service`/`inproc_server` は dict。一方 C# `DecodePortfolio(string json)` は JSON 文字列を食う。現状 DecodePortfolio に JSON を供給しているのは sink キュー（`ReplayEventSink.push_portfolio(json)`）だけで、**get_portfolio→JSON→decoder の経路は存在しない**。
- → **`get_state_json` と対称に `get_portfolio_json()` を新設**（backend_service が既に作る dict を `json.dumps(pf, ensure_ascii=False)`）。`LiveRpcLanes.PollLane` がこれを叩き `LatestStateJson` 同型の latest-wins string スロット **`LatestPortfolioJson`** に入れる → 既存 DecodePortfolio と AFK round-trip がそのまま使える。「get_portfolio を並べるだけ」では繋がらない。
- poll は **Replay 限定**（同じ poll で取った state の `execution_mode` を見て Replay のときだけ get_portfolio_json を叩く・Live は `_host.Panel` 経路）。

**7-b 描画分岐:**
- `PushLiveTiles` の Replay 分岐（`BackcastWorkspaceRoot.cs:762-769`・今は4パネル全部 `ShowReplayEmpty`）を **「`LatestPortfolioJson` を decode → あれば描画／無ければ `ShowReplayEmpty`」** に変更。`ShowReplayEmpty` は honest-empty フォールバックとして残す。
- RunResult 2段: running view = portfolio poll（counts＋realized/unrealized）／full stats = launcher 回収の summary_json、切替は `replay_state`（get_state_json 由来）。

**7-c realized/unrealized は Python 権威・3層スレッド:**
- realized は cost 履歴が要り **C# 側導出不能** → Python 権威（`Portfolio.realized_pnl`）。unrealized も同ソースで一貫（式 §4-b）。C# は表示専用ダム。
- **同フィールドを3層＋C# DTO 全てに通す**: `PortfolioResult`（additive 安全・全 consumer 名前アクセス）/ `backend_service` dict / `get_portfolio_json`、および C# `PortfolioDto`・`PortfolioSnapshot`。
- ⚠ **`PositionRow` に `unrealized_pnl` が無く JsonUtility が黙って捨てている**現状も併せて直す（snake_case 維持・§ReplayPanelDecoder の zero-fill 規律）。

## 8. `ReplayPanelDecoder` 命名（owner 確定 2026-06-17）= **リネームしない・コメント更新のみ**

#65 で `get_portfolio_json` poll に配線されて初めて名前どおり Replay パネル供給になる。ヘッダの "UNUSED / INTERMEDIATE STATE" 注記を「#65 で本線 poll に配線済み」に更新。深い責務再編は low-stakes で見送り。

## 9. 検証サーフェス（owner 確定 2026-06-17）

- **Python-AFK（pytest）**: observer snapshot 累積（push_portfolio で positions/cash、push_order で orders 追記、on_equity で MTM equity＋realized/unrealized）／run 開始クリア／`_finalize_run` union 化＋**max_drawdown 単一ソース**／get_portfolio(_json) が走行中 live・完了時確定。
- **golden #24 の切り分け（重要）**: "byte-identical" は **fill/equity イベント列**の話で不変。`_finalize_run` の union 化（sharpe/sortino 追加・max_drawdown 単一ソース化）は **summary_json を意図的に変える**変更 → golden が summary_json をピンしているなら**そこは再 bless**（イベント列=不変／summary=意図的更新、と切り分けて記録）。
- **AFK probe（Unity）**: 既存 sink キュー由来 round-trip に加え、**新経路 `get_portfolio_json()` 出力が orders 入り DecodePortfolio／total_pnl 入り DecodeRunResult で正しく decode** されることも通す。`KernelSinkDecodeProbe` 拡張 or 新規。
- **#61 回帰は「弱める」でなく「分岐させる」（重要・owner 強調）**: `HakoniwaBaseModeProbe.Section5`（:246-287）は commit cdc09d4 の stale-live-leak 退行ロック（live AccountEvent 注入→Replay flip→4パネルが stale live でなく ReplayEmpty を assert）。#65 で実データ表示に変えるとき anti-stale-live 意図を生かす：
  - portfolio 無し（run前/クリア直後）→ `ReplayEmpty`（従来 assert 維持）
  - `LatestPortfolioJson` 注入 → replay 実数値を表示、かつ**先に注入した live 値とは別物**であることを assert
  - 単に assert を消すと cdc09d4 退行検知が死ぬ → **サブケース2分割**。
- **owner HITL**: Replay 実行で4パネルがライブで埋まる（開始時=初期資金/建玉なし、約定で建玉・Orders 増加、RunResult が running→完了で full stats）。

## 10. 実装タスク（設計 lock 後の作業リスト・着手時 §11 に証跡）

Python:
1. `ReplayKernelObserver`: engine 参照を受け取り、`self._snapshot` を push_portfolio（positions[qty=int丸め, avg_px, unrealized_pnl=0]/cash）・push_order（orders 追記）・on_equity（MTM equity/cash/realized/unrealized）で更新 → 完成 dict を `engine.last_portfolio` に atomic ref swap。
2. `_start_engine_duckdb`: run 開始時 `engine.last_portfolio` クリア。
3. `_finalize_run`: `equity_curve_stats` 合流で summary union 化・max_drawdown 単一ソース。
4. `PortfolioResult`/`backend_service` dict に `realized_pnl`/`unrealized_pnl` 追加。
5. **`get_portfolio_json()` 新設**（dict→json.dumps）。

C#:
6. `LiveRpcLanes.PollLane`: Replay 時のみ `get_portfolio_json` を叩き `LatestPortfolioJson` へ。
7. `ReplayPanelDecoder`: `PortfolioDto` に orders 追加・`PositionRow`/`PortfolioSnapshot` に unrealized_pnl・`RunResult`/`RunResultDto` に total_pnl。ヘッダ注記更新。
8. `BackcastWorkspaceRoot.PushLiveTiles` Replay 分岐: decode→描画／無ければ ShowReplayEmpty。RunResult 2段（running/full・replay_state 切替）。launcher（`WorkspaceEngineHost`）が summary_json 回収。

検証:
9. pytest（observer/finalize/get_portfolio_json）・golden 再 bless（summary 部）・AFK probe 拡張・`HakoniwaBaseModeProbe.Section5` 2分割・owner HITL。

## 11. 実装証跡（#65・2026-06-17）

**owner 実装（タスク1-8）**: Python（observer running snapshot / `last_portfolio` 開始時クリア / `_finalize_run` union（max_drawdown 単一ソース）/ `PortfolioResult`+dict に realized/unrealized / `get_portfolio_json` 新設）＋ C#（`ReplayPanelDecoder` に orders/realized/unrealized/total_pnl・`PositionRow.unrealized_pnl` bind・`LiveRpcLanes` Replay 限定 portfolio poll・`WorkspaceEngineHost` が summary_json 回収・`PushReplayTiles` 2段描画）。#65 pytest 17/17 GREEN・Unity batchmode コンパイル exit 0・golden #24 event列 byte-identical（summary_json のみ意図的に sharpe/sortino 追加）。

**grill 実装レビューで検出＋修正した3バグ（2026-06-17・diff を設計の木に突き合わせ）**:
- **🔴 HIGH — Replay パネルがライブ更新しない**: `Update→RefreshLiveTiles` は `_host.Panel.AppliedCount`（live イベント数）変化時のみ `PushReplayTiles` を呼ぶが、Replay では AppliedCount が凍結 → flip 時1回しか描画されず portfolio poll の更新が画面に出ない（A案/AC の根幹が無効）。**修正**: `RefreshLiveTiles` をモード別に（Replay は AppliedCount ゲートを通さず毎フレーム `PushReplayTiles`）。`PushReplayTiles` に payload 変化ゲート（`_lastReplayPortfolioPayload`+`_lastReplaySummaryPayload`・JSON parse を変化時のみ・summary 両追跡で running→full 切替も拾う）。`ForceRefreshLiveTiles`（flip）が payload ゲートをリセットして強制再描画。
- **🟡 MEDIUM — 未走行が "(no data)" でなくゼロ表示**: `last_portfolio is None` でも `get_portfolio`(881-882) が success=True の全0を返すため、run 開始クリア後〜初回 publish に「bp=0/flat/o:0」を描画（§3 honest-empty 崩れ・stale-live 自体は無事）。**修正**: `backend_service.get_portfolio_json` が `last_portfolio is None` のとき **空文字 `""`** を返す → C# 既存 `IsNullOrWhiteSpace`→`ShowReplayEmpty`。
- **🟢 LOW — Replay ゲート脆弱**: `state.Contains("\"Replay\"")` をキー込み **`"execution_mode":"Replay"`** に（pydantic は空白なし dump で確実）。

**検証 GREEN（2026-06-17）**:
- **Python**: `test_get_portfolio_json`（fix #2 の None→"" 新テスト含む）/`test_replay_kernel_observer`/`test_replay_review_fixes` = **18/18 passed**。golden #24（`test_kernel_golden_cpython`/`test_scenario_inline_golden`/`test_kernel_runner_production_seam`/`test_kernel_buying_power_seam`）= **13 passed, 1 skipped**。**union は `_finalize_run`(DuckDB 経路) のみで EventSink golden を通らない → golden 再 bless 不要**（event列も summary も golden 不変）。
- **AFK probe（Unity 6000.4.11f1 `-batchmode -nographics`）**:
  - `HakoniwaBaseModeProbe` = **EXIT 0 `[HAKONIWA BASE MODE PASS]`**。Section5 2分割（A: anti-stale-live cdc09d4 維持／B: 実データ描画＋stale live 12345 非表示／B2: running→full 切替＋`total_pnl` バインド＋dual payload gate／C: honest-empty ""→ReplayEmpty）が `RefreshLiveTiles` 経由で GREEN ＝ **HIGH 駆動ループ修正・MEDIUM honest-empty を機械ロック、HITL 依存を外した**。
  - `KernelSinkDecodeProbe` = **`[REPLAY POLL SHAPE PASS]`**（新 `ValidateReplayPollShape`・データ非依存の純 decode round-trip で orders/realized/unrealized/total_pnl バインドを検証）。kernel-sink 本体は本環境に DuckDB データ `S:\jp\stocks_daily\8918.duckdb` が無く未走（owner 機で full pass）。CS エラー無し＝全 C# 編集はコンパイル通過。
- **stray sidecar（#65 無関係・要 owner 対応）**: `python/spike/fixtures/strategies/kernel_spike_buy_sell.json`（会話開始時から**未追跡**・一度も track されたことが無い Minute-universe schema_v3 の HITL 残骸）が `test_fixtures_have_no_sidecar`/golden 2-leg/`KernelSinkDecodeProbe` の inline SCENARIO を shadow して落とす。退避すると golden 全 GREEN を確認済み。**#65 の変更とは無関係**。owner に削除推奨（テストが明示的に禁止する flotsam）。
- **残: owner HITL** — Replay 実行で4パネルがライブで埋まる（開始時=初期資金/フラット、約定で Orders/Positions 増加、RunResult が running→完了で full stats）。

**code-review(simplify) high（2026-06-17・CLAUDE.md 規約）**: 8アングル finder→verify。**Medium 以上ゼロ**。REFUTED: `res["summary_json"]` KeyError（`backend_service.start_engine` が全分岐で key 必須）／observer スレッド競合（runner 単一スレッド逐次＋atomic swap）／dual-gate stuck（両 payload 追跡）／equity_curve_stats NaN（n<2・std=0 guard）。Low（適用済み）: `HakoniwaBaseModeProbe` の実データフィクスチャを自己整合に修正（equity を cash＋建玉時価＝unrealized 整合へ）。Low（注記のみ）: replay→live→replay で直前 run の最終 portfolio が新 run 開始まで残る（保護対象の live→replay 契約ではない・設計内）／`_publish_snapshot` の orders list コピー（Replay throttle 下で許容）。pair-relay 反復は不要（Medium+ 無し）。

> **状態（更新）: 設計確定＋実装＋レビュー3修正＋code-review GREEN（Medium+ 0）＋AFK GREEN。残りは owner HITL ＋ stray sidecar 削除のみ。**
