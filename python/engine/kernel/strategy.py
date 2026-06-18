"""engine.kernel.strategy — Strategy base + context for the kernel (#24).

The Strategy lifecycle hooks mirror the issue's API surface: `on_start` / `on_bar` /
`on_tick` / `on_order` / `on_stop`. Replay and Live drive the SAME API. The clock/
order-submission context is injected at `register()` (like Nautilus injects clock/cache
at register, never via config), so a strategy never branches on mode.

Tracer scope: `on_tick` is a reserved no-op (Daily bars only), `on_stop` is called once
on normal completion. Nautilus-free.
"""
from __future__ import annotations

from typing import Protocol

from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.portfolio import PortfolioSnapshot


class StrategyContext(Protocol):
    """Injected at register(): how a strategy submits orders, logs, and reads its book."""

    def submit_market(
        self, *, strategy_id: str, instrument_id: str, side: OrderSide, quantity: float
    ) -> None: ...  # noqa: E704

    def log(self, message: str) -> None: ...  # noqa: E704

    def buying_power(self) -> float: ...  # noqa: E704

    # Read seam (#76 portfolio-driver slice): a frozen pre-fill snapshot (cash / MTM equity /
    # realized / signed positions). Additive to ADR-0012 D2's adaptation boundary — the
    # marimo adapter pipes it into the get_portfolio driver; the imperative golden never
    # calls it (byte-identical). Replay and Live both implement it (Live = kernel mirror).
    def portfolio_snapshot(
        self, instrument_id: "str | None" = None
    ) -> PortfolioSnapshot: ...  # noqa: E704


class Strategy:
    """Base strategy. Subclasses override the hooks they need.

    Common construction contract (#25): the engine instantiates a strategy as
    `strategy_cls(instrument_id=..., **params)` (same shape the backtest runner uses), so the
    base accepts `instrument_id` + arbitrary `params` and a minimal subclass that only overrides
    hooks is constructible without a bespoke `__init__`. `strategy_id` is the RUN identity — in
    Live the controller injects the run's `nautilus_strategy_id` after construction (mirroring
    Nautilus `change_id`); in Replay the twin hardcodes it. Defaulted so the engine never has to
    pass it at construction.
    """

    def __init__(self, *, strategy_id: str = "", instrument_id: str = "", **params) -> None:
        self.id = strategy_id
        self.instrument_id = instrument_id
        self.params: dict = dict(params)
        self._ctx: StrategyContext | None = None

    def register(self, ctx: StrategyContext) -> None:
        self._ctx = ctx

    # --- order submission helper (delegates to the injected context) ----------
    def submit_market(self, instrument_id: str, side: OrderSide, quantity: float) -> None:
        if self._ctx is None:
            raise RuntimeError("Strategy.submit_market called before register()")
        self._ctx.submit_market(
            strategy_id=self.id,
            instrument_id=instrument_id,
            side=side,
            quantity=quantity,
        )

    def log(self, message: str) -> None:
        if self._ctx is not None:
            self._ctx.log(message)

    def buying_power(self) -> float:
        if self._ctx is None:
            raise RuntimeError("buying_power called before register()")
        return float(self._ctx.buying_power())

    def portfolio_snapshot(self) -> PortfolioSnapshot:
        """Frozen pre-fill book snapshot for the strategy's primary instrument (#76).

        Fail-loud before register (mirrors buying_power): a strategy that read positions/
        cash off a default snapshot before wiring would size silently wrong."""
        if self._ctx is None:
            raise RuntimeError("portfolio_snapshot called before register()")
        return self._ctx.portfolio_snapshot(self.instrument_id)

    # --- lifecycle hooks (no-op defaults) -------------------------------------
    def on_start(self) -> None: ...  # noqa: E704
    def on_bar(self, bar: Bar) -> None: ...  # noqa: E704
    def on_tick(self, tick) -> None: ...  # noqa: E704
    def on_order(self, event) -> None: ...  # noqa: E704
    def on_stop(self) -> None: ...  # noqa: E704
