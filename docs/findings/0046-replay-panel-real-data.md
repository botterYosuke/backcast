# findings 0046 — Replay 時の base パネル実数値配線（#65・grill-with-docs 設計ドリル）

`grill-with-docs`（2026-06-17・owner インタビュー）で導出。受け皿 issue **#65**（`chart/panels: Replay 時の
base パネル(BuyingPower/Positions/Orders/RunResult)に実数値を配線`）。親 Epic #1 / #5。**#61（findings 0028）の
honest-empty "(no data — Replay)" を実数値に置換する follow-up**。方針: ADR-0006（kernel-native・nautilus 退役）/
ADR-0007（equity/cash/buying_power の 3 値別物・cash 権威）に従属。下位事実は本 findings に固定し ADR は参照のみ。

> **状態: 設計確定（owner 2026-06-17）。** 実装着手時に §6 へ証跡を追記する。

---

## 0. #65 起票時の前提が #72 で変わった（再前裁定）

#65 は #61 直後（#72 着手前）に「engine/Python の深い改造が要る独立 feature」として起票された。しかし **#70/#71/#72/#73
で前提が変わった**:

- kernel `Portfolio`（`python/engine/kernel/portfolio.py`）が **run 中ずっとライブ**に `cash` / `mark_to_market_equity(prices)`
  / `realized_pnl` / `open_positions()` を保持する。#71 の `buying_power()` seam は既にこの `portfolio.cash` を run 中に読む。
- `ReplayKernelObserver`（`replay_kernel_observer.py`）が per-bar で `push_order`(fills) / `on_equity`(cash・equity) を受ける。
- **ただし `get_portfolio`/`last_portfolio` は `_finalize_run` で run 完了後に 1 回だけ `compute_portfolio` で算出**（observer の
  `push_portfolio`/`push_run_complete` は no-op）。→ 16 分の throttle 付き Replay の**走行中**は `get_portfolio` は空。
- `TradingState` poll（`core.py:_build_trading_state_locked`）は price/ohlc/per_instrument を載せるが **portfolio を載せない**。
  これが C# が毎フレーム読む payload（`_host.LatestStateJson`・depth ladder が既に decode）。
- C# の 4 base タイルは Live の `_host.Panel` push のみで駆動。Replay は `PushLiveTiles` が `ShowReplayEmpty()` に短絡。

**結論**: 実データはもう run 中にライブ存在する。#65 は「深い engine 改造」ではなく **ライブ portfolio を poll に相乗りさせて
C# で描く wiring 作業**に縮小した。

## 1. owner 確定の挙動（2026-06-17・AskUserQuestion）

1. **更新粒度 = 走行中ライブ更新**（post-run 一括ではない）。毎 poll、bar が進むごとに cash/equity/建玉/約定/realized が
   育つ。チャートが bar-by-bar で進む体験と同期。
2. **RunResult パネル**: 走行中は **約定数 / realized PnL / equity** を逐次表示。**Sharpe / Sortino / 最大ドローダウンは
   走行完了時に確定**（全 equity カーブが要るため走行中は "—"）。
3. **Orders パネル**: **約定した取引を上から積み上げる取引ログ**（BUY/SELL を約定のたびに 1 行追加、日をまたいで累積）。
   Live の「最新 1 件」ではない。
4. **Positions パネル**: `symbol / qty / avg_px / unrealized_pnl`（含み損益は当該銘柄の最新 close で算出）。
5. **最初の売買前（朝〜10:00 前）**: 実 cash（= `initial_cash`）・建玉 0・約定ログ空を出す。**ここで "(no data — Replay)"
   が消える**＝honest-empty が honest-real-zero になる（owner の「空表示は意図と違う」を直接解消）。
6. **3 値は別物のまま報告**（equity=MTM / cash / buying_power=cash・ADR-0007）。`compute_portfolio` が現状 3 値を cash に
   潰している（CONTEXT「_Avoid_」）轍を踏まない＝走行中 snapshot は live `Portfolio` の `mark_to_market_equity` / `cash` で
   3 値を正しく出す。
7. **取引ログの寿命 = ラン単位**（stop/再 Play でリセット）。

## 2. transport（owner には内部詳細として委任・grill 中に確定）

**TradingState poll に additive**（#65 AC の「additive output to TradingState poll」と一致）。理由:

- C# は既に毎フレーム `_host.LatestStateJson`（GetState poll）を depth ladder で decode 済み＝**同じ payload に portfolio を
  足せば追加 RPC ゼロ・chart/portfolio が原子的に同期**。
- 代替「`get_portfolio` RPC をライブ化して別 poll」は DTO 再利用が効くが poll が 2 本になり round-trip 増＋chart と別 poll で
  skew しうるため不採用。

## 3. 採用設計（実装スケッチ）

### Python（engine/Python 改造・#65 は C#-only 規律を超える additive feature）

- `models.py`: `TradingState` に **optional `replay_portfolio`** を追加（**`live_last_error` は末尾固定なので直前に挿入**・
  §9.14 ADR 準拠）。新 model `ReplayPortfolio { cash, equity, buying_power, realized_pnl, fills_count,
  positions:[{symbol,qty,avg_px,unrealized_pnl}], orders:[{symbol,side,qty,price,ts_ms}],
  sharpe?:Optional, sortino?:Optional, max_drawdown?:Optional }`（指標は走行中 null）。
- `core.py`: engine に `self._replay_portfolio: Optional[ReplayPortfolio]` ＋ `set_replay_portfolio(...)`／run 開始・Live で
  clear。`_build_trading_state_locked` が non-None のとき載せる（Live は None＝C# は既存 `_host.Panel` push を使うので不変）。
- `replay_kernel_observer.py`: `push_order`／`on_equity` のたびに **live snapshot を構築して engine に push**。値は kernel
  `Portfolio`（cash・`mark_to_market_equity(last_prices)`・positions＋unrealized）＋ observer が貯める **累積 fills ログ**＋
  fills_count。Portfolio と last_prices は runner から渡す（kernel 純度は維持＝observer は adapter 層・本ファイル冒頭の不変条件）。
- `_finalize_run`: `compute_summary` 後に **sharpe/sortino/max_drawdown 入りの最終 snapshot** を push（走行完了で指標が埋まる）。

### C#

- `ReplayPanelDecoder`: `replay_portfolio` ブロックを decode するよう拡張（orders リストが now non-empty・run_result 指標・
  positions の unrealized_pnl）。JsonUtility の snake_case 配列要素規約を踏襲（PascalCase 改名＝silent zero-fill 禁止）。
- `LivePanelTileView`: Replay 用 format（`FormatReplay{BuyingPower,Orders,Positions,RunResult}`）を snapshot から。
- `BackcastWorkspaceRoot.PushLiveTiles`: Replay 分岐で `_host.LatestStateJson` を decode →`replay_portfolio` 在れば実描画、
  無ければ（pre-run）`ShowReplayEmpty()`。depth ladder と同じ毎フレーム drive。

## 4. 射程外（follow-up）

- `ReplayPanelDecoder` の名称・責務整理（"Replay" 名で live payload も decode）＝behavior-neutral follow-up。本 #65 で
  「実際に Replay portfolio を decode する」ようになるので名称の齟齬は縮小するが、リネーム自体は別 PR。
- Live 経路の poll 一本化（Live も `_host.Panel` push をやめ poll に寄せる）＝scope 外。#65 は **Replay-only additive**。
- per-day の正確な前日終値（#72 既知の faithfulness 限界）には触れない。

## 5. 検証方針（AFK probe が正本ゲート）

- **Python 特性化テスト**: observer に合成 fill/equity を流し snapshot が bar をまたいで育つ（fills_count 増・positions
  出現→FLAT 復帰・realized 累積）／`_finalize_run` で sharpe 等が埋まる／pre-run は `replay_portfolio` None。
- **凍結 golden #24 不変**: `replay_portfolio` は additive optional・golden は正規化値＋イベント順が parity 条件（raw JSON
  バイト一致ではない・CONTEXT _Avoid_）＝byte-identical 維持を確認。
- **AFK probe（C#）**: poll JSON →`ReplayPanelDecoder`→4 タイル decode の round-trip。pre-run 空・走行中実数値・完了時指標。
- **owner HITL**: Unity で v19 Replay 起動 → 朝は cash=¥1M/建玉0、10:00 で Orders に BUY 群・Positions 出現・BuyingPower 減、
  14:55 で Positions FLAT・Orders に SELL 追加、完了で RunResult に sharpe/sortino/maxDD。

## 6. 実装証跡（着手時に追記）

（未着手）

## 7. ADR 判断

新規 ADR は起こさない。additive・可逆・**depth を poll に載せる既存パターンに前例あり**＝「surprising」要件を欠く。
方針は ADR-0006（kernel-native・nautilus 退役）/ ADR-0007（3 値別物・cash 権威）に従属し、本 findings が下位事実の正本。
