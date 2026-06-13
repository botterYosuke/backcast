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

from engine.kernel.bars import Bar
from engine.kernel.orders import OrderSide


class StrategyContext(Protocol):
    """Injected at register(): how a strategy submits orders and logs."""

    def submit_market(
        self, *, strategy_id: str, instrument_id: str, side: OrderSide, quantity: float
    ) -> None: ...  # noqa: E704

    def log(self, message: str) -> None: ...  # noqa: E704


class Strategy:
    """Base strategy. Subclasses override the hooks they need."""

    def __init__(self, *, strategy_id: str) -> None:
        self.id = strategy_id
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

    # --- lifecycle hooks (no-op defaults) -------------------------------------
    def on_start(self) -> None: ...  # noqa: E704
    def on_bar(self, bar: Bar) -> None: ...  # noqa: E704
    def on_tick(self, tick) -> None: ...  # noqa: E704
    def on_order(self, event) -> None: ...  # noqa: E704
    def on_stop(self) -> None: ...  # noqa: E704
