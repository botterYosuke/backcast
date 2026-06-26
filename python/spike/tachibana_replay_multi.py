"""採取 tachibana mock を「4 銘柄同時更新」シナリオとして実パイプラインで再生する。

production の multi-instrument 配線を忠実に再現 (kabu_replay_multi.py と対称):
  - 銘柄ごとに 1 個の FdFrameProcessor (adapter._processors[ticker] と同じ row="1")
  - 銘柄ごとに 1 個の TickBarAggregator (LiveRunner._aggregators[iid] と同じ)
  - 共有 1 個の ReducerState (per_id_ohlc_points が iid 別に育つ)
  - primary_id = 1 銘柄。非 primary も per-id 蓄積 (reducer Finding #4)
  - partial-push は全銘柄ぶん毎 1.0s (LiveRunner._partial_push 相当・変更検出ガード付き)

到着順インターリーブされた 1 ストリームを時刻どおりに流すので、4 銘柄の
ohlc_points が同時に更新される様子＝Unity ChartView 4 枚同時描画の Python 側真値が出る。

Run: cd python && ./.venv/Scripts/python.exe spike/tachibana_replay_multi.py [CAPTURE.json]
"""
from __future__ import annotations

import glob
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

from engine.exchanges.tachibana_ws_codec import FdFrameProcessor
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
        glob.glob(str(Path(__file__).parent / "captures" / "tachibana_mock_*.json"))
    )[-1]
    data = json.load(open(cap, encoding="utf-8"))
    frames = data["frames"]
    instrument_ids = data["meta"]["symbols"]  # ["7203.TSE", ...]
    tickers = [s.split(".")[0] for s in instrument_ids]
    primary = instrument_ids[-1]  # e.g. 285A.TSE — user's foreground chart
    print(
        f"[multi] capture={Path(cap).name} symbols={instrument_ids} "
        f"primary={primary} frames={len(frames)}"
    )

    # adapter は FdFrameProcessor を ticker 単位で 1 個保持する (tachibana.py L398)。
    # row="1" は p_gyou_no=1 と同じく adapter.subscribe の固定値。
    procs = {t: FdFrameProcessor(row="1") for t in tickers}
    aggs = {iid: TickBarAggregator(instrument_id=iid, interval_ns=MINUTE_NS) for iid in instrument_ids}
    state = ReducerState(timestamp_ms=0, price=0.0)
    last_partial: dict[str, LiveKline | None] = {iid: None for iid in instrument_ids}
    nclosed = {iid: 0 for iid in instrument_ids}
    npartial = {iid: 0 for iid in instrument_ids}

    def push(lk: LiveKline) -> None:
        apply_event(state, live_kline_to_replay_time_updated(lk), primary)
        apply_event(state, live_kline_to_reducer_kline(lk), primary)

    next_partial_ms = PARTIAL_PUSH_S * 1000.0
    timeline: list[tuple[float, dict[str, int]]] = []
    skipped_non_fd = 0

    for rec in frames:
        frame_type = rec.get("frame_type")
        if frame_type != "FD":
            # KP/ST/EC/SS/US は account-level / 接続層イベントで chart 再生対象外。
            # raw artifact には残しているが replay では skip。
            skipped_non_fd += 1
            continue
        iid = rec["instrument_id"]
        if iid not in aggs:
            continue
        ticker = iid.split(".")[0]
        fields = rec["fields"]
        t_ms = rec["t_ms"]
        # recv_ts_ms はキャプチャ時の `recv` ISO から復元 (raw t_ms は capture 内相対なので
        # FdFrameProcessor の ts_ms fallback には使えない=絶対 epoch ms が要る)。
        recv_dt = datetime.fromisoformat(rec["recv"])
        if recv_dt.tzinfo is None:
            recv_dt = recv_dt.replace(tzinfo=timezone.utc)
        recv_ts_ms = int(recv_dt.timestamp() * 1000)

        # partial-push cadence for ALL symbols at each 1s boundary
        while t_ms >= next_partial_ms:
            for iid2 in instrument_ids:
                p = aggs[iid2].build_now()
                if p is not None and p != last_partial[iid2]:
                    last_partial[iid2] = p
                    push(p)
                    npartial[iid2] += 1
            next_partial_ms += PARTIAL_PUSH_S * 1000.0

        trade, _ = procs[ticker].process(fields, recv_ts_ms)
        if trade is not None and trade.get("side") != "unknown":
            tu = TradesUpdate(
                kind="trades",
                instrument_id=iid,
                ts_ns=int(trade["ts_ms"]) * 1_000_000,
                price=float(trade["price"]),
                size=float(trade["qty"]),
                aggressor_side=trade["side"],
            )
            closed = aggs[iid].on_tick(tu)
            if closed is not None:
                push(closed)
                nclosed[iid] += 1
                last_partial[iid] = None

        # snapshot every ~25s for the timeline
        if not timeline or t_ms - timeline[-1][0] >= 25_000:
            timeline.append(
                (t_ms, {iid2: cols(state.per_id_ohlc_points.get(iid2, [])) for iid2 in instrument_ids})
            )

    print("\n================ MULTI-SYMBOL REPLAY ================")
    print(f"skipped non-FD frames (KP/ST/EC/SS/US): {skipped_non_fd}")
    hdr = "  t(s) | " + " | ".join(f"{iid:>8}" for iid in instrument_ids)
    print("visible chart columns per symbol over time")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t_ms, m in timeline:
        row = f" {t_ms/1000:5.0f} | " + " | ".join(f"{m[iid]:>8}" for iid in instrument_ids)
        print(row)
    print("\nfinal per-symbol state (= Unity per_instrument[id].ohlc_points):")
    print(f'{"iid":>10} {"closed":>7} {"partial":>8} {"pts":>5} {"columns":>8}')
    all_populated = True
    for iid in instrument_ids:
        pts = state.per_id_ohlc_points.get(iid, [])
        print(f"{iid:>10} {nclosed[iid]:>7} {npartial[iid]:>8} {len(pts):>5} {cols(pts):>8}")
        if not pts:
            all_populated = False
    print("====================================================")
    print(f"{len(instrument_ids)} symbols all populated concurrently: {all_populated}")
    return 0 if all_populated else 1


if __name__ == "__main__":
    raise SystemExit(main())
