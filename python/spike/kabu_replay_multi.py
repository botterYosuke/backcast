"""採取 mock を「4銘柄同時更新」シナリオとして実パイプラインで再生する。

production の multi-instrument 配線を忠実に再現:
  - 銘柄ごとに 1 個の TickBarAggregator（LiveRunner._aggregators[iid] と同じ）
  - 共有 1 個の ReducerState（per_id_ohlc_points が iid 別に育つ）
  - primary_id = 1 銘柄。非 primary も per-id 蓄積（reducer Finding #4）
  - partial-push は全銘柄ぶん毎 1.0s（LiveRunner._partial_push 相当・変更検出ガード付き）

到着順にインターリーブされた 1 ストリームを時刻どおりに流すので、4銘柄の
ohlc_points が同時に更新される様子＝Unity ChartView 4枚同時描画の Python 側真値が出る。

Run: cd python && ./.venv/Scripts/python.exe spike/kabu_replay_multi.py [CAPTURE.json]
"""
from __future__ import annotations

import glob
import json
import sys
from pathlib import Path

from engine.exchanges.kabusapi_ws_codec import KabuPushFrameProcessor
from engine.live.adapter import KlineUpdate as LiveKline, TradesUpdate
from engine.live.aggregator import TickBarAggregator
from engine.live.reducer_bridge import (
    live_kline_to_reducer_kline,
    live_kline_to_replay_time_updated,
)
from engine.reducer import ReducerState, apply_event

MINUTE_NS = 60 * 1_000_000_000
PARTIAL_PUSH_S = 1.0


def cols(points) -> int:
    return len({p.open_time_ms for p in points})


def main() -> int:
    cap = sys.argv[1] if len(sys.argv) > 1 else sorted(
        glob.glob(str(Path(__file__).parent / "captures" / "kabu_mock_*.json"))
    )[-1]
    data = json.load(open(cap, encoding="utf-8"))
    frames = data["frames"]
    syms = [s.split(".")[0] for s in data["meta"]["symbols"]]  # bare codes
    primary = syms[-1]  # e.g. 285A — the user's foreground chart
    print(f"[multi] capture={Path(cap).name} symbols={syms} primary={primary} frames={len(frames)}")

    procs = {s: KabuPushFrameProcessor(symbol=s) for s in syms}
    aggs = {s: TickBarAggregator(instrument_id=s, interval_ns=MINUTE_NS) for s in syms}
    state = ReducerState(timestamp_ms=0, price=0.0)
    last_partial: dict[str, LiveKline | None] = {s: None for s in syms}
    nclosed = {s: 0 for s in syms}
    npartial = {s: 0 for s in syms}

    def push(lk: LiveKline) -> None:
        apply_event(state, live_kline_to_replay_time_updated(lk), primary)
        apply_event(state, live_kline_to_reducer_kline(lk), primary)

    next_partial_ms = PARTIAL_PUSH_S * 1000.0
    timeline = []

    for rec in frames:
        f = rec["frame"]
        s = str(f.get("Symbol"))
        if s not in procs:
            continue
        t_ms = rec["t_ms"]

        # partial-push cadence for ALL symbols at each 1s boundary
        while t_ms >= next_partial_ms:
            for sym in syms:
                p = aggs[sym].build_now()
                if p is not None and p != last_partial[sym]:
                    last_partial[sym] = p
                    push(p)
                    npartial[sym] += 1
            next_partial_ms += PARTIAL_PUSH_S * 1000.0

        trade, _ = procs[s].process(f)
        if trade is not None:
            tu = TradesUpdate(
                kind="trades", instrument_id=s, ts_ns=trade["ts_ns"],
                price=trade["price"], size=trade["size"],
                aggressor_side=trade["aggressor_side"],
            )
            closed = aggs[s].on_tick(tu)
            if closed is not None:
                push(closed)
                nclosed[s] += 1
                last_partial[s] = None

        # snapshot every ~25s for the timeline
        if not timeline or t_ms - timeline[-1][0] >= 25_000:
            timeline.append((t_ms, {sym: cols(state.per_id_ohlc_points.get(sym, [])) for sym in syms}))

    print("\n================ MULTI-SYMBOL REPLAY ================")
    hdr = "  t(s) | " + " | ".join(f"{sym:>6}" for sym in syms)
    print("visible chart columns per symbol over time")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t_ms, m in timeline:
        row = f" {t_ms/1000:5.0f} | " + " | ".join(f"{m[sym]:>6}" for sym in syms)
        print(row)
    print("\nfinal per-symbol state (= Unity per_instrument[id].ohlc_points):")
    print(f'{"sym":>6} {"closed":>7} {"partial":>8} {"pts":>5} {"columns":>8}')
    all_populated = True
    for sym in syms:
        pts = state.per_id_ohlc_points.get(sym, [])
        print(f"{sym:>6} {nclosed[sym]:>7} {npartial[sym]:>8} {len(pts):>5} {cols(pts):>8}")
        if not pts:
            all_populated = False
    print("====================================================")
    print(f"4 symbols all populated concurrently: {all_populated}")
    return 0 if all_populated else 1


if __name__ == "__main__":
    raise SystemExit(main())
