"""S2/#11 spike fixture — deterministic buy→sell trading strategy.

Companion to spike_bar_consumer.py (#9, no-trade). This fixture DOES trade so
the panels slice (#11) can exercise the order/position/run_result seams that
spike_bar_consumer leaves empty (push_order/push_portfolio fire only on
OrderFilled / PositionEvent). spike_bar_consumer is left UNCHANGED and is not
used by #11.

Deterministic plan on 8918.TSE Daily (staged catalog, no new market data):
  - bar #3  -> 1-lot (100 share) MARKET BUY  -> fills next bar open
  - bar #40 -> 1-lot MARKET SELL (close)     -> fills next bar open
Over this 68-bar window both fills land well inside the range, yielding:
  push_order == 2 (buy fill + sell fill), push_portfolio >= 2 (open + close),
  summary.fills_count == 2. CASH account + single-digit-yen price => no
  balance/lot rejection.

Loaded by engine.strategy_runtime.strategy_loader.load(): exactly ONE Strategy
subclass + inline SCENARIO (legacy .py path -> benign "SCENARIO loaded from .py"
WARNING, expected). No-arg instantiable: replay_runner builds strategy_cls().
"""
from __future__ import annotations

from nautilus_trader.config import StrategyConfig
from nautilus_trader.model.data import Bar, BarType
from nautilus_trader.model.enums import OrderSide
from nautilus_trader.model.identifiers import InstrumentId
from nautilus_trader.trading.strategy import Strategy

# Inline SCENARIO (schema v2). Exactly the 6 keys the loader accepts — do NOT
# add account_type (loader rejects unknown keys; replay_runner defaults to CASH).
SCENARIO: dict = {
    "schema_version": 2,
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}

# Deterministic trade schedule (1-based bar count). 68 bars in window; both
# fills (BUY_AT_BAR+1, SELL_AT_BAR+1) land inside with generous margin.
BUY_AT_BAR = 3
SELL_AT_BAR = 40
TRADE_QTY = 100  # exactly one lot (lot_size=100)


class SpikeBuySellStrategy(Strategy):
    """Buys 1 lot at bar 3, sells it at bar 40. Deterministic, fill-certain."""

    def __init__(
        self,
        *,
        instrument_id: str = "8918.TSE",
        bar_type_str: str | None = None,
    ) -> None:
        super().__init__(config=StrategyConfig(strategy_id="spike-buy-sell"))
        self.instrument_id = InstrumentId.from_str(instrument_id)
        self.bar_type_str = bar_type_str or f"{instrument_id}-1-DAY-LAST-EXTERNAL"
        self.instrument = None
        self.n_bars = 0
        self._bought = False
        self._sold = False

    def on_start(self) -> None:
        self.instrument = self.cache.instrument(self.instrument_id)
        if self.instrument is None:
            self.log.error(f"Instrument not found: {self.instrument_id}")
            return
        self.subscribe_bars(BarType.from_str(self.bar_type_str))
        self.log.info(f"SpikeBuySellStrategy started: {self.bar_type_str}")

    def on_bar(self, bar: Bar) -> None:
        self.n_bars += 1
        if self.instrument is None:
            return

        if not self._bought and self.n_bars == BUY_AT_BAR:
            order = self.order_factory.market(
                instrument_id=self.instrument_id,
                order_side=OrderSide.BUY,
                quantity=self.instrument.make_qty(TRADE_QTY),
            )
            self.submit_order(order)
            self._bought = True
            self.log.info(f"submit BUY {TRADE_QTY} at bar {self.n_bars}")
        elif self._bought and not self._sold and self.n_bars == SELL_AT_BAR:
            order = self.order_factory.market(
                instrument_id=self.instrument_id,
                order_side=OrderSide.SELL,
                quantity=self.instrument.make_qty(TRADE_QTY),
            )
            self.submit_order(order)
            self._sold = True
            self.log.info(f"submit SELL {TRADE_QTY} at bar {self.n_bars}")
