"""Replay universe MARKET pricing (#70).

Pure-Python unit coverage for cross-instrument orders. The runner's bar stream is
monkeypatched so the test does not depend on the owner's DuckDB mount.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderDenied, OrderFilled, OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402
from engine.live.safety_rails import (  # noqa: E402
    KIND_NO_REFERENCE_PRICE,
    SafetyLimits,
    SafetyRails,
)


A = "1111.TSE"
B = "2222.TSE"


def _bar(iid: str, ts: int, close: float) -> Bar:
    return Bar(
        instrument_id=iid,
        ts_event_ns=ts,
        open=close,
        high=close,
        low=close,
        close=close,
        volume=100.0,
    )


class _Sink:
    def __init__(self) -> None:
        self.orders: list[OrderFilled] = []
        self.portfolios = 0

    def push_bar(self, bar) -> None:
        pass

    def push_order(self, fill) -> None:
        self.orders.append(fill)

    def push_portfolio(self, pf) -> None:
        self.portfolios += 1

    def on_equity(self, ts_ms: int, equity: float, cash: float) -> None:
        pass

    def push_run_complete(self, run_id, summary) -> None:
        pass


class _BuyBOnA(Strategy):
    def __init__(self) -> None:
        super().__init__(strategy_id="cross", instrument_id=A)
        self.events: list[object] = []

    def on_bar(self, bar: Bar) -> None:
        if bar.instrument_id == A:
            self.submit_market(B, OrderSide.BUY, 2)

    def on_order(self, event) -> None:
        self.events.append(event)


def _run(monkeypatch, bars: list[Bar], strategy: Strategy, sink: _Sink, **kwargs):
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    return KernelRunner(
        data_root="/unused",
        instrument_ids=[A, B],
        start="2024-01-01",
        end="2024-01-01",
        initial_cash=1_000.0,
        strategy=strategy,
        sink=sink,
        **kwargs,
    ).run()


def test_cross_instrument_market_uses_order_instrument_latest_close(monkeypatch) -> None:
    strategy = _BuyBOnA()
    sink = _Sink()
    bars = [
        _bar(B, 1_000, 50.0),
        _bar(A, 2_000, 100.0),
    ]
    rails = SafetyRails(SafetyLimits(max_position_size_jpy=150, max_order_value_jpy=150))

    result = _run(monkeypatch, bars, strategy, sink, rails=rails)

    fills = [e for e in strategy.events if isinstance(e, OrderFilled)]
    denials = [e for e in strategy.events if isinstance(e, OrderDenied)]
    assert not denials
    assert len(fills) == 1
    assert fills[0].instrument_id == B
    assert fills[0].last_px == 50.0
    assert fills[0].ts_event_ns == 2_000
    assert result.fills == 1
    assert result.final_cash == 900.0
    assert sink.orders == fills


def test_cross_instrument_market_without_target_price_is_denied(monkeypatch) -> None:
    strategy = _BuyBOnA()
    sink = _Sink()
    bars = [
        _bar(A, 1_000, 100.0),
        _bar(B, 2_000, 50.0),
    ]

    result = _run(monkeypatch, bars, strategy, sink)

    denials = [e for e in strategy.events if isinstance(e, OrderDenied)]
    fills = [e for e in strategy.events if isinstance(e, OrderFilled)]
    assert len(denials) == 1
    assert denials[0].instrument_id == B
    assert denials[0].kind == KIND_NO_REFERENCE_PRICE
    assert denials[0].ts_event_ns == 1_000
    assert not fills
    assert sink.orders == []
    assert sink.portfolios == 0
    assert result.fills == 0
    assert result.final_cash == 1_000.0
