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
from engine.reducer import KlineUpdate  # noqa: E402
from engine.strategy_runtime.replay_kernel_observer import ReplayKernelObserver  # noqa: E402


class _FakeEngine:
    def __init__(self) -> None:
        self.events: list = []

    def apply_replay_event(self, event) -> None:
        self.events.append(event)


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
    obs.on_equity(1_700_000_000_000, 9_990_000.0)
    assert buf.equity == [{"ts_event_ms": 1_700_000_000_000, "equity": 9_990_000.0}]


def test_push_portfolio_and_run_complete_are_inert() -> None:
    obs, eng, buf = _observer()
    obs.push_portfolio(object())
    obs.push_run_complete("run-1", {"fills_count": 0})
    assert eng.events == [] and buf.fills == [] and buf.equity == []


if __name__ == "__main__":
    test_push_bar_emits_klineupdate_from_kernel_bar()
    test_push_order_writes_runbuffer_fill_with_reader_shape()
    test_on_equity_writes_runbuffer_equity_point()
    test_push_portfolio_and_run_complete_are_inert()
    print("[REPLAY KERNEL OBSERVER PASS] all mappings match the #29 seam")
