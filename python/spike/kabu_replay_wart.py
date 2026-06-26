"""採取した kabu mock を実パイプラインで再生し、「bar が減る」ワートを数値再現する。

経路は production 忠実（再実装ではなく実クラスを使う）:
  raw frame → KabuPushFrameProcessor.process (実 codec)
            → TickBarAggregator.on_tick / build_now (実 aggregator, interval=Minute)
            → live_kline_to_reducer_kline (実 bridge 変換)
            → apply_event into ReducerState (実 reducer, max_history_len=1000)

partial-push は live_orchestrator と同じ毎 1.0s。LiveRunner._partial_push の
「前回と同一 snapshot なら publish しない」変更検出ガードも再現する。

可視カラム数 = per_id_ohlc_points 内の distinct open_time_ms（ChartView の X 配置は
open_time_ms 正規化なので同一分は 1 カラムに重なる）。これが時間とともにどう動くかを出す。

Run: cd python && ./.venv/Scripts/python.exe spike/kabu_replay_wart.py [CAPTURE.json] [SYMBOL]
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


def distinct_minutes(points) -> int:
    return len({p.open_time_ms for p in points})


def main() -> int:
    cap = sys.argv[1] if len(sys.argv) > 1 else sorted(
        glob.glob(str(Path(__file__).parent / "captures" / "kabu_mock_*.json"))
    )[-1]
    target_sym = sys.argv[2] if len(sys.argv) > 2 else "7203"
    cap_override = int(sys.argv[3]) if len(sys.argv) > 3 else None
    data = json.load(open(cap, encoding="utf-8"))
    frames = data["frames"]
    print(f"[replay] capture={Path(cap).name} symbol={target_sym} frames={len(frames)}")

    # raw "Symbol" field is the bare code (e.g. "7203"); the reducer keys per-id by it.
    proc = KabuPushFrameProcessor(symbol=target_sym)
    agg = TickBarAggregator(instrument_id=target_sym, interval_ns=MINUTE_NS)
    state = ReducerState(timestamp_ms=0, price=0.0)
    if cap_override is not None:
        state.max_history_len = cap_override  # demo: shrink ring to force the 1000-cap dynamic

    # single instrument → primary_id = target so is_primary path fills per_id_* (and global).
    primary = target_sym

    def push_kline(lk: LiveKline) -> None:
        apply_event(state, live_kline_to_replay_time_updated(lk), primary)
        apply_event(state, live_kline_to_reducer_kline(lk), primary)

    next_partial_ms = PARTIAL_PUSH_S * 1000.0
    last_partial: LiveKline | None = None
    n_ticks = n_closed = n_partial = 0
    timeline: list[tuple[float, int, int]] = []  # (t_s, total_points, distinct_minutes)

    for rec in frames:
        f = rec["frame"]
        if str(f.get("Symbol")) != target_sym:
            continue
        t_ms = rec["t_ms"]

        # --- partial-push cadence: fire all 1s boundaries up to this frame's arrival ---
        while t_ms >= next_partial_ms:
            partial = agg.build_now()
            if partial is not None and partial != last_partial:
                last_partial = partial
                push_kline(partial)
                n_partial += 1
            next_partial_ms += PARTIAL_PUSH_S * 1000.0

        # --- decode frame via real codec; feed trade into aggregator ---
        trade, _depth = proc.process(f)
        if trade is not None:
            tu = TradesUpdate(
                kind="trades",
                instrument_id=target_sym,
                ts_ns=trade["ts_ns"],
                price=trade["price"],
                size=trade["size"],
                aggressor_side=trade["aggressor_side"],
            )
            n_ticks += 1
            closed = agg.on_tick(tu)
            if closed is not None:
                push_kline(closed)
                n_closed += 1
                last_partial = None  # new bucket → forming bar resets

        pts = state.per_id_ohlc_points.get(target_sym, [])
        timeline.append((t_ms / 1000.0, len(pts), distinct_minutes(pts)))

    pts = state.per_id_ohlc_points.get(target_sym, [])
    print("\n================ REPLAY RESULT ================")
    print(f"trade ticks fed          : {n_ticks}")
    print(f"closed bars emitted      : {n_closed}")
    print(f"partial snapshots pushed : {n_partial}")
    print(f"per_id_ohlc_points appended (closed+partial): {n_closed + n_partial}")
    print(f"per_id_ohlc_points len now: {len(pts)}  (cap={state.max_history_len})")
    print(f"distinct minutes (= chart columns) now: {distinct_minutes(pts)}")
    print("\n-- visible chart columns over time (every ~15s) --")
    last_print = -999.0
    for t_s, total, cols in timeline:
        if t_s - last_print >= 15.0:
            print(f"  t={t_s:6.1f}s  buffer_points={total:4d}  visible_columns={cols}")
            last_print = t_s
    if timeline:
        t_s, total, cols = timeline[-1]
        print(f"  t={t_s:6.1f}s  buffer_points={total:4d}  visible_columns={cols}  (final)")
    print("===============================================")
    print("WART: partial を毎秒 append するため buffer が同一分の重複点で膨らみ、活発化すると")
    print("      1000点窓が覆う『分』の数 = 可視カラム数 が減る → ユーザー報告『bar が減った』")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
