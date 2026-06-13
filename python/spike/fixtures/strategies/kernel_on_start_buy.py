"""Test fixture (#25 review): submits a MARKET BUY in `on_start`.

Used to guard that a strategy can place orders from on_start in the full live path. The run
must be RUNNING (not READY) while on_start runs, otherwise the new-order gate denies the order
with STRATEGY_PAUSED and it never reaches the venue. Kernel-native (Nautilus-free) Strategy base
so it runs in the Mono process without the Rust core.
"""
from __future__ import annotations

from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy

# schema v2 — same 6 keys the kernel-native loader accepts (see kernel_spike_buy_sell.py).
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


class KernelOnStartBuy(Strategy):
    """Submits a single 1-lot MARKET BUY in on_start (fills during attach)."""

    def on_start(self) -> None:
        self.submit_market(self.instrument_id, OrderSide.BUY, TRADE_QTY)
        self.log("KernelOnStartBuy: submitted BUY in on_start")
