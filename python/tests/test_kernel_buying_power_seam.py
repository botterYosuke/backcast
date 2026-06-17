"""buying_power() strategy seam (#71).

Pure-Python unit coverage for the additive `StrategyContext.buying_power()` seam:
- Replay context returns the live `portfolio.cash` (authority per ADR-0007), tracked
  across a fill.
- Calling `Strategy.buying_power()` before `register()` is a fail-loud RuntimeError
  (matches `submit_market`; returning 0.0 would be misread as "no buying power" and
  silently skip every pick).
- The Live driver context returns the injected `buying_power_provider` (the #74
  venue-余力 extension point), falling back to kernel `portfolio.cash` when unset.

The runner's bar stream is monkeypatched so the test does not depend on the owner's
DuckDB mount. The frozen golden (#24) is unaffected because no golden strategy calls
buying_power() — covered separately by test_kernel_golden_cpython.py.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.live.driver import KernelLiveDriver  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.portfolio import Portfolio  # noqa: E402
from engine.kernel.risk import RiskEngine  # noqa: E402
from engine.kernel.orders import OrderEngine  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402


A = "1111.TSE"


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
    def push_bar(self, bar) -> None:
        pass

    def push_order(self, fill) -> None:
        pass

    def push_portfolio(self, pf) -> None:
        pass

    def on_equity(self, ts_ms: int, equity: float, cash: float) -> None:
        pass

    def push_run_complete(self, run_id, summary) -> None:
        pass


class _ReadBuyingPower(Strategy):
    """Records buying_power() on each bar and buys 2 @ 100 on the first bar."""

    def __init__(self) -> None:
        super().__init__(strategy_id="bp", instrument_id=A)
        self.readings: list[float] = []
        self._bought = False

    def on_bar(self, bar: Bar) -> None:
        self.readings.append(self.buying_power())
        if not self._bought:
            self._bought = True
            self.submit_market(A, OrderSide.BUY, 2)


def test_replay_buying_power_tracks_portfolio_cash(monkeypatch) -> None:
    strategy = _ReadBuyingPower()
    bars = [_bar(A, 1_000, 100.0), _bar(A, 2_000, 100.0)]
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))

    result = KernelRunner(
        data_root="/unused",
        instrument_ids=[A],
        start="2024-01-01",
        end="2024-01-01",
        initial_cash=1_000.0,
        strategy=strategy,
        sink=_Sink(),
    ).run()

    # First bar reads cash BEFORE the fill (1000); second bar reads it AFTER the
    # 2 @ 100 buy (1000 - 200 = 800) — i.e. it tracks portfolio.cash live.
    assert strategy.readings == [1_000.0, 800.0]
    assert result.final_cash == 800.0


def test_buying_power_before_register_raises() -> None:
    strategy = Strategy()
    with pytest.raises(RuntimeError, match="buying_power called before register"):
        strategy.buying_power()


def _make_live_driver(*, provider, cash: float) -> KernelLiveDriver:
    return KernelLiveDriver(
        strategy=Strategy(strategy_id="x"),
        order_engine=OrderEngine(risk_engine=RiskEngine(None), venue="TSE"),
        portfolio=Portfolio(initial_cash=cash),
        broker=None,
        bus=None,
        instrument_ids=[A],
        nautilus_strategy_id="x",
        emit_order=lambda order, event: None,
        buying_power_provider=provider,
    )


def test_live_ctx_buying_power_defaults_to_kernel_cash() -> None:
    driver = _make_live_driver(provider=None, cash=777.0)
    assert driver.ctx.buying_power() == 777.0


def test_live_ctx_buying_power_uses_provider() -> None:
    # The #74 extension point: a venue-余力 provider overrides the kernel cash mirror.
    driver = _make_live_driver(provider=lambda: 12_345.0, cash=777.0)
    assert driver.ctx.buying_power() == 12_345.0
