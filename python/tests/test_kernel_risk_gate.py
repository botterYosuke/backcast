"""Risk-gate firing (#24, AC#3): the existing pre-trade rail denies via the kernel path.

The kernel reuses `evaluate_pre_trade` (engine.live.pre_trade_gate) through OrderEngine.
This pins that a configured SafetyRails actually fires on the kernel order path: an order
for an instrument outside `allowed_instruments` is DENIED, never fills, never reaches the
EventSink (the sink only carries FILLED orders, mirroring the oracle), and the denial is
delivered to Strategy.on_order. Leaves the book FLAT.

Pure-Python, no Nautilus, deterministic.
"""
from __future__ import annotations

import os
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine.kernel.orders import OrderDenied, OrderFilled, OrderSide
from engine.kernel.runner import KernelRunner
from engine.live.safety_rails import (
    KIND_ALLOWED_INSTRUMENTS,
    KIND_MAX_DAILY_LOSS,
    SafetyLimits,
    SafetyRails,
)
from spike.fixtures.strategies.kernel_spike_buy_sell import KernelSpikeBuySell
from spike.kernel_golden import scenario


class _RecordingBuySell(KernelSpikeBuySell):
    """Same plan, but records every on_order event for assertions."""

    def __init__(self) -> None:
        super().__init__()
        self.orders: list = []

    def on_order(self, event) -> None:
        self.orders.append(event)


class _CountingSink:
    def __init__(self) -> None:
        self.bars = self.orders = self.portfolios = self.run_completes = 0

    def push_bar(self, _: str) -> None:
        self.bars += 1

    def push_order(self, _: str) -> None:
        self.orders += 1

    def push_portfolio(self, _: str) -> None:
        self.portfolios += 1

    def push_run_complete(self, *_: object) -> None:
        self.run_completes += 1


def test_allowlist_denies_order_through_kernel_path() -> None:
    strategy = _RecordingBuySell()
    sink = _CountingSink()
    # 8918.TSE is NOT in the allowlist → every order must be denied pre-trade.
    rails = SafetyRails(SafetyLimits(allowed_instruments=("9999.TSE",)))

    result = KernelRunner(
        catalog_path=scenario.CATALOG,
        instrument_id=scenario.INSTRUMENT,
        start=scenario.START,
        end=scenario.END,
        initial_cash=scenario.INITIAL_CASH,
        strategy=strategy,
        push_target=sink,
        rails=rails,
    ).run()

    denials = [e for e in strategy.orders if isinstance(e, OrderDenied)]
    fills = [e for e in strategy.orders if isinstance(e, OrderFilled)]

    assert denials, "the pre-trade rail never fired — expected an ALLOWED_INSTRUMENTS denial"
    assert denials[0].kind == KIND_ALLOWED_INSTRUMENTS
    assert denials[0].side is OrderSide.BUY
    assert not fills, "a denied order must never fill"
    assert sink.orders == 0, "denied orders must not reach the sink (FILLED-only contract)"
    assert sink.portfolios == 0, "no portfolio push without a fill"
    assert result.fills == 0
    assert result.final_cash == float(scenario.INITIAL_CASH), "book must stay FLAT after denial"


def test_post_trade_daily_loss_halts_run_on_price_drop() -> None:
    """AC#3 post-trade leg: max_daily_loss fires on a real MTM loss, not on the purchase.

    The BUY at bar 3 (fill 8.0) must NOT trip the rail — opening a position is not a
    loss (mark-to-market equity is flat at the entry price). Holding through bar 6,
    where 8918.TSE closes at 7.0, marks the 100-share position down 100 JPY. With a
    50 JPY daily-loss cap the post-trade rail then breaches and halts the run before
    the SELL — proving post_trade_gate fires via the kernel path AND that the purchase
    principal is not mistaken for a loss.
    """
    strategy = _RecordingBuySell()
    sink = _CountingSink()
    rails = SafetyRails(SafetyLimits(max_daily_loss_jpy=50))

    result = KernelRunner(
        catalog_path=scenario.CATALOG,
        instrument_id=scenario.INSTRUMENT,
        start=scenario.START,
        end=scenario.END,
        initial_cash=scenario.INITIAL_CASH,
        strategy=strategy,
        push_target=sink,
        rails=rails,
    ).run()

    assert result.stopped_reason == KIND_MAX_DAILY_LOSS, (
        f"post-trade rail did not halt the run (stopped_reason={result.stopped_reason!r})"
    )
    assert result.fills == 1, "run should stop while holding (after BUY, before SELL)"
    # bars == 6 (PROCESSED): halt at the bar-6 price drop, NOT at the BUY (bar 3). If a
    # regression reverted the rail to cash-equity, opening the 800-JPY position at bar 3
    # would trip first and this would read 3 — so the exact count is the guard.
    assert result.bars == 6, f"loss should trip at bar 6 (price drop), got bar {result.bars}"
    assert sink.run_completes == 1, "a halted run still emits run_complete"


def test_post_trade_does_not_halt_on_purchase() -> None:
    """Opening a position must not trip a daily-loss rail sized below the notional.

    The BUY costs 800 JPY of cash but MTM equity is unchanged at the entry price, so a
    500 JPY cap must NOT halt at the purchase. (The flat-end spike nets zero P&L, so a
    cap of 500 is never breached and the run completes.)
    """
    strategy = _RecordingBuySell()
    sink = _CountingSink()
    rails = SafetyRails(SafetyLimits(max_daily_loss_jpy=500))

    result = KernelRunner(
        catalog_path=scenario.CATALOG,
        instrument_id=scenario.INSTRUMENT,
        start=scenario.START,
        end=scenario.END,
        initial_cash=scenario.INITIAL_CASH,
        strategy=strategy,
        push_target=sink,
        rails=rails,
    ).run()

    # 8918 dips to 7.0 (−100 MTM) at bar 6, which is within the 500 cap, so the run
    # must reach the SELL and finish flat without a daily-loss halt.
    assert result.stopped_reason == "", (
        f"daily-loss rail wrongly halted a within-cap run (stopped_reason={result.stopped_reason!r})"
    )
    assert result.fills == 2, "both BUY and SELL should fill"


def test_no_rails_allows_fills() -> None:
    """Control: with rails=None the same strategy fills (proves the denial is the rail's doing)."""
    strategy = _RecordingBuySell()
    sink = _CountingSink()
    result = KernelRunner(
        catalog_path=scenario.CATALOG,
        instrument_id=scenario.INSTRUMENT,
        start=scenario.START,
        end=scenario.END,
        initial_cash=scenario.INITIAL_CASH,
        strategy=strategy,
        push_target=sink,
        rails=None,
    ).run()
    assert result.fills == 2
    assert sink.orders == 2


if __name__ == "__main__":
    failures = []
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
            except AssertionError as exc:
                failures.append(f"{name}: {exc}")
    if failures:
        print("[KERNEL RISK GATE FAIL]")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("[KERNEL RISK GATE PASS] allowlist denies via kernel path; control fills")
