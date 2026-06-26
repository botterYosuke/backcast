"""Generate the committed 4-symbol kabu live-state JSON fixture for KabuLiveChartRenderE2ERunner
(CHARTRENDER-04/05) — findings 0117 #4 / findings 0118.

Replays the committed lightweight kabu mock (CI-reproducible; identical trades to the raw capture)
through the SAME production pipeline as kabu_replay_multi (KabuPushFrameProcessor → TickBarAggregator
→ live_kline_to_reducer_kline → apply_event), then serialises the resulting per-instrument OHLC into
the production TradingState boundary model (.model_dump_json) so the fixture is the EXACT shape C#
polls. All 4 symbols are present (the during-churn moment where add brought 9984/285A live), each with
its own ohlc series — the 4-chart-concurrent truth that the single-symbol 9501 fixture cannot pin.

Run: cd python && ./.venv/Scripts/python.exe spike/gen_kabu_4sym_chart_state.py
"""
from __future__ import annotations

import json
from pathlib import Path

from engine.exchanges.kabusapi_ws_codec import KabuPushFrameProcessor
from engine.live.adapter import KlineUpdate as LiveKline, TradesUpdate
from engine.live.aggregator import TickBarAggregator
from engine.live.reducer_bridge import live_kline_to_reducer_kline, live_kline_to_replay_time_updated
from engine.models import DepthLevel, DepthSnapshot, OhlcPoint, PerInstrumentState, TradingState
from engine.reducer import ReducerState, apply_event

_MINUTE_NS = 60 * 1_000_000_000
_PARTIAL_PUSH_S = 1.0
_CAP_POINTS = 10_000  # effectively uncapped: keep the REAL per-symbol density differences
                      # (findings 0117: 71/60/148/149) so the locator-disambiguation floor is non-vacuous
_REPO = Path(__file__).resolve().parents[2]
_CAPTURE = _REPO / "python" / "tests" / "fixtures" / "kabu_live_mock_4sym.json"
_OUT = _REPO / "Assets" / "Tests" / "E2E" / "Editor" / "Fixtures" / "KabuMock4SymChartState.json"


def main() -> int:
    data = json.load(open(_CAPTURE, encoding="utf-8"))
    frames = data["frames"]
    full_syms = data["meta"]["symbols"]              # ["7203.TSE", ...]
    bare = [s.split(".")[0] for s in full_syms]
    bare_to_full = dict(zip(bare, full_syms))
    primary = bare[-1]

    procs = {s: KabuPushFrameProcessor(symbol=s) for s in bare}
    aggs = {s: TickBarAggregator(instrument_id=s, interval_ns=_MINUTE_NS) for s in bare}
    state = ReducerState(timestamp_ms=0, price=0.0)
    last_partial: dict[str, LiveKline | None] = {s: None for s in bare}
    last_quote: dict[str, dict] = {s: {} for s in bare}

    def push(lk: LiveKline) -> None:
        apply_event(state, live_kline_to_replay_time_updated(lk), primary)
        apply_event(state, live_kline_to_reducer_kline(lk), primary)

    next_partial_ms = _PARTIAL_PUSH_S * 1000.0
    for rec in frames:
        f = rec["frame"]
        s = str(f.get("Symbol"))
        if s not in procs:
            continue
        t_ms = rec["t_ms"]
        while t_ms >= next_partial_ms:
            for sym in bare:
                p = aggs[sym].build_now()
                if p is not None and p != last_partial[sym]:
                    last_partial[sym] = p
                    push(p)
            next_partial_ms += _PARTIAL_PUSH_S * 1000.0
        trade, depth = procs[s].process(f)
        if depth is not None and depth.get("bids") and depth.get("asks"):
            last_quote[s] = depth
        if trade is not None:
            tu = TradesUpdate(kind="trades", instrument_id=s, ts_ns=trade["ts_ns"],
                              price=trade["price"], size=trade["size"],
                              aggressor_side=trade["aggressor_side"])
            closed = aggs[s].on_tick(tu)
            if closed is not None:
                push(closed)
                last_partial[s] = None

    per_instrument: dict[str, PerInstrumentState] = {}
    last_prices: dict[str, float] = {}
    for sym in bare:
        full = bare_to_full[sym]
        pts = state.per_id_ohlc_points.get(sym, [])[-_CAP_POINTS:]
        if not pts:
            continue
        ohlc = [OhlcPoint(timestamp_ms=p.timestamp_ms, open_time_ms=p.open_time_ms,
                          open=p.open, high=p.high, low=p.low, close=p.close, volume=p.volume)
                for p in pts]
        price = pts[-1].close
        last_prices[full] = price
        q = last_quote[sym]
        depth = None
        if q.get("bids") and q.get("asks"):
            depth = DepthSnapshot(
                bids=[DepthLevel(price=q["bids"][0][0], size=q["bids"][0][1])],
                asks=[DepthLevel(price=q["asks"][0][0], size=q["asks"][0][1])],
            )
        per_instrument[full] = PerInstrumentState(price=price, ohlc_points=ohlc, depth=depth)

    primary_full = bare_to_full[primary]
    pp = per_instrument[primary_full]
    ts = TradingState(
        price=pp.price, timestamp_ms=pp.ohlc_points[-1].timestamp_ms,
        ohlc_points=pp.ohlc_points, open=pp.ohlc_points[-1].open, high=pp.ohlc_points[-1].high,
        low=pp.ohlc_points[-1].low, close=pp.ohlc_points[-1].close,
        open_time_ms=pp.ohlc_points[-1].open_time_ms, replay_state="IDLE",
        execution_mode="LiveAuto", venue_state="CONNECTED", venue_id="KABU",
        configured_venue="KABU", subscribed_instruments=list(last_prices.keys()),
        instruments_loaded=len(per_instrument), last_prices=last_prices,
        per_instrument=per_instrument,
    )
    _OUT.parent.mkdir(parents=True, exist_ok=True)
    _OUT.write_text(ts.model_dump_json(), encoding="utf-8")
    counts = {k: len(v.ohlc_points) for k, v in per_instrument.items()}
    print(f"wrote {_OUT.relative_to(_REPO)}  ({_OUT.stat().st_size} bytes)")
    print(f"per_instrument ids = {list(per_instrument)}")
    print(f"ohlc_points counts = {counts}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
