"""Unit gate (#49): ReplayKernelObserver bridges KernelRunner events to the #29 seam.

The production observer must turn the kernel's per-event callbacks into the EXISTING
production Replay seam — apply_replay_event (reducer → GetState) + RunBuffer fills/equity —
so the C# decoder is unchanged. Field shapes must match what run_buffer_reader.Fill /
EquityPoint and the reducer expect. Pure-Python — runnable directly or via pytest.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderFilled, OrderSide  # noqa: E402
from engine.kernel.portfolio import Portfolio  # noqa: E402
from engine.reducer import KlineUpdate  # noqa: E402
from engine.strategy_runtime.replay_kernel_observer import ReplayKernelObserver  # noqa: E402


class _FakeEngine:
    def __init__(self) -> None:
        self.events: list = []
        # #65: the observer swaps the running portfolio snapshot here each hook (atomic ref).
        self.last_portfolio = None

    def apply_replay_event(self, event) -> None:
        self.events.append(event)


def _fill(side: OrderSide, qty: float, px: float, ts_ns: int = 1_700_000_000_000_000_000):
    return OrderFilled(
        client_order_id="O-1",
        strategy_id="spike-buy-sell",
        venue_order_id="V-1",
        instrument_id="8918.TSE",
        side=side,
        last_qty=qty,
        last_px=px,
        ts_event_ns=ts_ns,
    )


class _FakeBuf:
    def __init__(self) -> None:
        self.fills: list[dict] = []
        self.equity: list[dict] = []

    def write_fill(self, event: dict) -> None:
        self.fills.append(event)

    def write_equity(self, event: dict) -> None:
        self.equity.append(event)


def _observer():
    eng, buf = _FakeEngine(), _FakeBuf()
    return ReplayKernelObserver(engine=eng, run_buffer=buf), eng, buf


def test_push_bar_emits_klineupdate_from_kernel_bar() -> None:
    obs, eng, _ = _observer()
    bar = Bar(
        instrument_id="8918.TSE",
        ts_event_ns=1_700_000_000_123_000_000,
        open=100.0, high=110.0, low=95.0, close=105.0, volume=4200.0,
    )
    obs.push_bar(bar)

    assert len(eng.events) == 1
    ev = eng.events[0]
    assert isinstance(ev, KlineUpdate)
    expected_ms = bar.ts_event_ns // 1_000_000
    assert ev.timestamp_ms == expected_ms
    assert ev.open_time_ms == expected_ms
    assert (ev.open, ev.high, ev.low, ev.close) == (100.0, 110.0, 95.0, 105.0)
    assert ev.instrument_id == "8918.TSE"
    assert ev.volume == 4200.0


def test_push_order_writes_runbuffer_fill_with_reader_shape() -> None:
    obs, _, buf = _observer()
    fill = OrderFilled(
        client_order_id="O-1",
        strategy_id="spike-buy-sell",
        venue_order_id="V-1",
        instrument_id="8918.TSE",
        side=OrderSide.BUY,
        last_qty=100.0,
        last_px=1234.0,
        ts_event_ns=1_700_000_000_000_000_000,
    )
    obs.push_order(fill)

    assert len(buf.fills) == 1
    rec = buf.fills[0]
    # Shape required by run_buffer_reader.Fill (side∈{BUY,SELL}, qty>0, price>0).
    assert rec["instrument_id"] == "8918.TSE"
    assert rec["side"] == "BUY"
    assert float(rec["qty"]) == 100.0
    assert float(rec["price"]) == 1234.0
    assert rec["ts_event_ms"] == 1_700_000_000_000


def test_on_equity_writes_runbuffer_equity_point() -> None:
    obs, _, buf = _observer()
    # equity = mark-to-market, cash = realized cash — recorded separately (#49 review #2).
    obs.on_equity(1_700_000_000_000, 10_004_700.0, 9_899_600.0)
    assert buf.equity == [
        {"ts_event_ms": 1_700_000_000_000, "equity": 10_004_700.0, "cash": 9_899_600.0}
    ]


# --- #65: running portfolio snapshot (push_portfolio/push_order/on_equity → last_portfolio) ---

_SNAPSHOT_KEYS = {
    "buying_power", "cash", "equity", "positions", "orders",
    "realized_pnl", "unrealized_pnl",
    "clock_ms",   # #185 (findings 0134): replay clock for the Run Result time line
}


def test_push_portfolio_publishes_running_snapshot_positions_and_cash() -> None:
    obs, eng, _ = _observer()
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(OrderSide.BUY, 100.0, 1000.0))  # cash 1_000_000 → 900_000, pos 100@1000

    obs.push_portfolio(pf)

    snap = eng.last_portfolio
    assert snap is not None, "push_portfolio must publish the running snapshot to last_portfolio"
    assert set(snap.keys()) == _SNAPSHOT_KEYS, "snapshot must match compute_portfolio union (#65 §4)"
    # cash/buying_power = portfolio.cash; equity is NOT touched by push (on_equity owns MTM).
    assert snap["cash"] == 900_000.0
    assert snap["buying_power"] == 900_000.0
    assert snap["realized_pnl"] == 0.0
    # positions built from open_positions(); qty int-rounded; unrealized_pnl=0.0 fixed (§3).
    assert snap["positions"] == [
        {"symbol": "8918.TSE", "qty": 100, "avg_price": 1000.0, "unrealized_pnl": 0.0}
    ]
    assert isinstance(snap["positions"][0]["qty"], int)


def test_push_bar_advances_clock_carried_in_running_snapshot() -> None:
    # #185 (findings 0134): the Run Result running time line shows the bar currently under
    # consideration. push_bar (bar-open) advances the clock BEFORE this bar's equity publish, so
    # an observation-only (pass) loop — no fills — still reports a fresh clock via on_equity.
    obs, eng, _ = _observer()
    bar = Bar(
        instrument_id="8918.TSE",
        ts_event_ns=1_700_000_000_123_000_000,
        open=100.0, high=110.0, low=95.0, close=105.0, volume=4200.0,
    )
    obs.push_bar(bar)                                   # no fill / no portfolio publish yet
    obs.on_equity(bar.ts_event_ns // 1_000_000, 1_000_000.0, 1_000_000.0)

    snap = eng.last_portfolio
    assert snap is not None
    assert snap["clock_ms"] == bar.ts_event_ns // 1_000_000   # == 1_700_000_000_123


def test_push_order_appends_filled_row_and_keeps_runbuffer_fill() -> None:
    obs, eng, buf = _observer()
    obs.push_order(_fill(OrderSide.BUY, 100.0, 1234.0))

    # golden #24: the RunBuffer fill write is unchanged (event stream byte-identical).
    assert len(buf.fills) == 1
    # #65: orders grow live in the running snapshot (= TTWR replay Orders panel source).
    snap = eng.last_portfolio
    assert snap is not None
    assert snap["orders"] == [
        {
            "symbol": "8918.TSE", "side": "BUY", "qty": 100.0, "price": 1234.0,
            "status": "FILLED", "ts_ms": 1_700_000_000_000,
        }
    ]


def test_on_equity_publishes_mtm_equity_and_derived_unrealized() -> None:
    obs, eng, buf = _observer()
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(OrderSide.BUY, 100.0, 1000.0))  # cash 900_000, pos 100@1000
    obs.push_portfolio(pf)

    # price 1000 → 1050: MTM equity = cash + 100*1050 = 900_000 + 105_000 = 1_005_000.
    obs.on_equity(1_700_000_000_000, 1_005_000.0, 900_000.0)

    # golden #24: the RunBuffer equity write is unchanged.
    assert buf.equity[-1] == {
        "ts_event_ms": 1_700_000_000_000, "equity": 1_005_000.0, "cash": 900_000.0
    }
    snap = eng.last_portfolio
    assert snap["equity"] == 1_005_000.0
    assert snap["cash"] == 900_000.0
    # unrealized = (MTM_equity − cash) − Σ(qty×avg_px) = 105_000 − 100_000 = 5_000 (§4-b).
    assert snap["unrealized_pnl"] == 5_000.0
    # positions are held through on_equity (push owns them).
    assert snap["positions"][0]["symbol"] == "8918.TSE"


def test_run_complete_stays_inert() -> None:
    obs, eng, buf = _observer()
    obs.push_run_complete("run-1", {"fills_count": 0})
    # push_run_complete owns nothing: no snapshot publish, no reducer/RunBuffer writes.
    assert eng.last_portfolio is None
    assert eng.events == [] and buf.fills == [] and buf.equity == []


if __name__ == "__main__":
    test_push_bar_emits_klineupdate_from_kernel_bar()
    test_push_order_writes_runbuffer_fill_with_reader_shape()
    test_on_equity_writes_runbuffer_equity_point()
    test_push_portfolio_publishes_running_snapshot_positions_and_cash()
    test_push_order_appends_filled_row_and_keeps_runbuffer_fill()
    test_on_equity_publishes_mtm_equity_and_derived_unrealized()
    test_run_complete_stays_inert()
    print("[REPLAY KERNEL OBSERVER PASS] all mappings match the #29 seam")
