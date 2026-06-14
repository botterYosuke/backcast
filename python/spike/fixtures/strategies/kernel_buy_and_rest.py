"""Kernel-native strategy that submits one BUY and leaves it resting (#22 Gap1).

Unlike kernel_spike_buy_sell (which round-trips to FLAT), this strategy buys a
single lot on the first bar and never sells. Paired with a mock venue that ACKs
the order without filling it (set_next_order_outcome ACCEPTED, filled_qty=0.0),
it leaves an open/resting order so the graceful-shutdown cancel path
(stop_run → cancel_inflight_orders → venue cancel_order) does real work.

Deliberately separate from the golden twin so the oracle/kernel golden parity
(kernel_spike_buy_sell vs spike_buy_sell) stays untouched.
"""
from __future__ import annotations

from engine.kernel.bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy

# Inline SCENARIO (schema v2) — the kernel-native loader resolves the run window from this
# (exactly the 6 keys the loader accepts; do NOT add account_type).
SCENARIO: dict = {
    "schema_version": 2,
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}

INSTRUMENT_ID = "8918.TSE"
TRADE_QTY = 100  # exactly one lot (lot_size=100)


class KernelBuyAndRest(Strategy):
    """Buys 1 lot on the first bar and never sells (leaves a resting order)."""

    def __init__(self, *, instrument_id: str = INSTRUMENT_ID) -> None:
        super().__init__(strategy_id="buy-and-rest")
        self.instrument_id = instrument_id
        self._submitted = False

    def on_start(self) -> None:
        self.log(f"KernelBuyAndRest started: {self.instrument_id}")

    def on_bar(self, bar: Bar) -> None:
        if not self._submitted:
            self._submitted = True
            self.submit_market(self.instrument_id, OrderSide.BUY, TRADE_QTY)
            self.log(f"submit resting BUY {TRADE_QTY}")
