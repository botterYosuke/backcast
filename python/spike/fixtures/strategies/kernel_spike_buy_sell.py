"""Kernel-native twin of spike_buy_sell.py (#24).

Same deterministic plan as the Nautilus oracle strategy
(spike/fixtures/strategies/spike_buy_sell.py), expressed against the Backcast
Execution Kernel's Nautilus-free Strategy base so it can run in the Mono process
without the Rust core:

  - bar #3  -> 1-lot (100 share) MARKET BUY  -> fills same bar at close
  - bar #40 -> 1-lot MARKET SELL (close)     -> fills same bar at close

The oracle file stays the comparison oracle (findings 0008 §1.2). Keep the trade
schedule (BUY_AT_BAR / SELL_AT_BAR / TRADE_QTY) in lock-step with the oracle.
"""
from __future__ import annotations

from engine.kernel.bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy

INSTRUMENT_ID = "8918.TSE"
BUY_AT_BAR = 3
SELL_AT_BAR = 40
TRADE_QTY = 100  # exactly one lot (lot_size=100)


class KernelSpikeBuySell(Strategy):
    """Buys 1 lot at bar 3, sells it at bar 40. Deterministic, fill-certain."""

    def __init__(self, *, instrument_id: str = INSTRUMENT_ID) -> None:
        super().__init__(strategy_id="spike-buy-sell")
        self.instrument_id = instrument_id
        self.n_bars = 0
        self._bought = False
        self._sold = False

    def on_start(self) -> None:
        self.log(f"KernelSpikeBuySell started: {self.instrument_id}")

    def on_bar(self, bar: Bar) -> None:
        self.n_bars += 1
        if not self._bought and self.n_bars == BUY_AT_BAR:
            self.submit_market(self.instrument_id, OrderSide.BUY, TRADE_QTY)
            self._bought = True
            self.log(f"submit BUY {TRADE_QTY} at bar {self.n_bars}")
        elif self._bought and not self._sold and self.n_bars == SELL_AT_BAR:
            self.submit_market(self.instrument_id, OrderSide.SELL, TRADE_QTY)
            self._sold = True
            self.log(f"submit SELL {TRADE_QTY} at bar {self.n_bars}")
