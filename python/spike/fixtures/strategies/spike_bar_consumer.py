"""S1 spike fixture strategy — minimal no-trade bar consumer (issue #9 / Milestone 2).

Self-contained replay fixture for the adapter-smoke gate. Loaded by
engine.strategy_runtime.strategy_loader.load(): it requires exactly ONE
nautilus Strategy subclass defined in this module plus a SCENARIO source.
We embed SCENARIO inline (the loader's legacy .py path) so the fixture is a
single file with no sidecar — load_scenario() emits a benign
"SCENARIO loaded from .py (legacy)" WARNING, which is expected here.

The strategy never trades; it only subscribes to its instrument's daily bars
and counts on_bar calls. The adapter smoke does NOT need fills — it proves the
Python->sink.push_bar() seam, which fires via GuiBridgeActor's direct
data.bars.{bar_type} msgbus subscription, independent of this strategy's orders.

instrument_id defaults to a staged S0 fixture symbol (8918.TSE) so the strategy
is no-arg instantiable: engine.strategy_runtime.replay_runner.run() builds it as
strategy_cls(**kwargs) with empty kwargs.
"""
from __future__ import annotations

from nautilus_trader.config import StrategyConfig
from nautilus_trader.model.data import Bar, BarType
from nautilus_trader.model.identifiers import InstrumentId
from nautilus_trader.trading.strategy import Strategy

# Inline SCENARIO (schema v2). The 6 keys are exact — the loader rejects any
# unknown/missing keys. Dates span the staged S0 fixture catalog
# (python/spike/fixtures/jquants-catalog: 8918/6740/3823 TSE-1-DAY parquet).
SCENARIO: dict = {
    "schema_version": 2,
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


class SpikeBarConsumerStrategy(Strategy):
    """Subscribes to one instrument's daily bars and counts them. Never trades."""

    def __init__(
        self,
        *,
        instrument_id: str = "8918.TSE",
        bar_type_str: str | None = None,
    ) -> None:
        super().__init__(config=StrategyConfig(strategy_id="spike-bar-consumer"))
        self.instrument_id = InstrumentId.from_str(instrument_id)
        self.bar_type_str = bar_type_str or f"{instrument_id}-1-DAY-LAST-EXTERNAL"
        self.n_bars = 0

    def on_start(self) -> None:
        instrument = self.cache.instrument(self.instrument_id)
        if instrument is None:
            self.log.error(f"Instrument not found: {self.instrument_id}")
            return
        self.subscribe_bars(BarType.from_str(self.bar_type_str))
        self.log.info(f"SpikeBarConsumerStrategy started: {self.bar_type_str}")

    def on_bar(self, bar: Bar) -> None:
        self.n_bars += 1
