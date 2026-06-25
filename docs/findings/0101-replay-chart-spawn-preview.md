# 0101 — Replay chart spawn-time preview（空 spawn の usability 解消）

owner 依頼（2026-06-24）「Replay モードで Chart を spawn したとき、デフォルトで DuckDB のデータを
表示した状態で spawn する。空状態で spawn するとユーザー視点でこれが何なのか分かりづらい
（usability）」を `grill-with-docs`（owner HITL Q1–Q3 ＋ scope 明確化）で設計ロックした記録。

実装はまだ。本ファイルがこの slice の設計正本。方針は [[ADR-0025]]（cell drives Replay/Live）と
ADR-0016（notebook = backtest・visual playback 直感）に従属し、execution 契約（exactly-once /
no-look-ahead）は**変えない**。

## 問題（現状の裏取り）

- chart は universe メンバーごとに spawn（`BackcastWorkspaceRoot.SyncChartWindowsToUniverse` /
  `SpawnChartWindowAt`）。spawn は `ChartView.Build()` のみで **Render しない＝空**。
- bar は RUN 時のみ供給される: `WorkspaceEngineHost.TryStartRun` が `load_replay_data`（IDLE→LOADED）
  と `start_engine`（LOADED→RUNNING）を**連続実行**し、kernel が 1 bar ずつ `apply_replay_event`
  → reducer の `per_id_ohlc_points[iid]` に exactly-once 累積（`core.py` `_load_replay_duckdb_locked`
  はあえて bar0 を prime せず `ohlc_points` を空のままにする＝prime→primary-skip 回避）。
- production には「LOADED で止まって待つ」段階が無い（LOADED は RUN 内の一瞬）。chart spawn 時点
  （universe 設定時・RUN 前）は engine が **IDLE**、`_rs` は static placeholder（`per_id_ohlc_points`
  空）。よって spawn 直後の chart は構造的に空 → 「これは何？」。

## 設計の木（locked）

- **D0 scope（owner）**: **Replay モードのみ**対象。Manual/Auto は Venue API 由来の株価を表示するので
  今回の改修対象外。preview の populate / clear は Replay 経路に限定する。
- **D1 spawn 表示（Q1=A）**: 空 placeholder ではなく **全期間の実ローソクを描く**（"全部を表示" を
  文字通り）。spawn 直後でも価格チャートが見えて一目で何か分かる。human が見る画面に「まだ再生して
  いない先のバー」が出るのは許容（戦略への供給は exactly-once のまま）。
- **D2 RUN 時の振る舞い（Q2=B）**: preview は **RUN 前の cold 表示だけ**。RUN を押したら現行どおり
  クリアして 1 bar ずつ累積描画する。`TryStartRun→load_replay_data` が `_rs` を fresh reset するので
  preview は自然にクリアされ、RUN の exactly-once / 「chart 本数 == streamed 本数」不変条件は保たれる
  （full→空→再充填のジャンプは owner 了承済み）。
- **D3 preview 期間（Q3 + owner 明確化）**: **scenario の start〜end**（`ScenarioStartupTile` 所有・
  RUN で実際に再生される範囲＝preview と再生が一致・perf 安全）。**start/end 未入力など問題があれば
  DuckDB の全期間（min〜max）へフォールバック**。granularity も scenario から取る（Daily/Minute で
  table 選択）。
- **D4 データ経路（実装・feasibility 裏取り済み）**: 新経路は RUN サイクルと独立。
  1. Python: preview loader（`duckdb_bars.load_universe_bars` を start/end で呼ぶ。fallback 用に
     min/max を返す新ヘルパ `get_date_range` を `duckdb_bars.py` に追加）＋ preview を IDLE 中に
     `per_id_ohlc_points` へ載せる新 RPC（例 `load_chart_preview(instrument_ids, start, end,
     granularity)`）。RUN 中（RUNNING）は no-op / 後勝ち禁止。
  2. C#: Replay/IDLE で chart spawn 時・universe 変化時・scenario range 変化時に preview RPC を呼ぶ
     トリガを足す。**poll lane（`get_state_json` 50ms）は replay_state 不問で常時走る**（#65 Replay
     portfolio projection が依存）ので、`per_id_ohlc_points` を埋めれば既存 `Update()` の
     `InstrumentOhlcDecoder.Decode`→`ChartView.Render`（`_chartRendered` の本数 dedup）が**そのまま**
     描画する＝**ChartView/Render 経路は無改変**。
  3. file 欠落 instrument は skip（その chart は空のまま・graceful）。no-trade-day（OHLCV 全0）は
     `load_bars` が既に drop。

## 未決（実装 slice で詰める小項目）

- preview 再取得のトリガ粒度: universe 変化・scenario start/end/granularity 変化のどれで再 populate
  するか（最小は spawn 時＋scenario commit 時）。
- preview と RUN の race: RUN 開始直後に in-flight の preview poll が一瞬上書きしないこと（RUNNING
  では preview RPC を弾く / state guard）。
- AFK probe: spawn 直後に `per_instrument[iid].ohlc_points` が非空（D1）・RUN 開始で空→累積（D2）・
  start/end 未入力時に全期間へ落ちる（D3）を `behavior-to-e2e` で固定する。

## 関連

- 描画経路: `BackcastWorkspaceRoot.Update`（per-id render）/ `InstrumentOhlcDecoder` /
  `ChartView.Render`。
- engine: `engine/core.py`（`load_replay_data` / `_load_replay_duckdb_locked` / `start_engine` /
  `_build_trading_state_locked` / `per_id_ohlc_points`）、`engine/kernel/duckdb_bars.py`
  （`load_bars` / `load_universe_bars` / 新 `get_date_range`）。
- 方針: [[ADR-0025]] / ADR-0016。chart⊆universe sync は findings 0091/0095/0099/0100。
