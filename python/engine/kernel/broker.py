"""engine.kernel.broker — deterministic Replay fill model for the kernel (#24).

Matched to the Nautilus oracle (findings 0008 §2, record-from-oracle): a MARKET order
accepted during `on_bar(N)` fills **immediately at the order instrument's latest close**,
in full, with zero slippage and zero commission. For a single-instrument run this is bar
N's close; in a universe it is the target instrument's latest known close.
`OrderFilled.ts_event = bar N.ts_event`.

LiveBroker (real kabu/tachibana adapter fills) is deferred to a follow-up — Replay only
for this tracer. Nautilus-free.
"""
from __future__ import annotations

from engine.kernel.orders import Order, OrderEngine, OrderFilled, OrderStatus


class ReplayBroker:
    """Fills ACCEPTED MARKET orders against the target instrument's latest close."""

    def __init__(self, order_engine: OrderEngine) -> None:
        self._engine = order_engine

    def fill_market(self, order: Order, *, price: float, ts_event_ns: int) -> OrderFilled:
        """Fill an ACCEPTED MARKET order at `price`, mutating the order to FILLED."""
        order.venue_order_id = self._engine.next_venue_order_id()
        order.status = OrderStatus.FILLED
        order.filled_qty = order.quantity
        order.avg_px = price
        order.ts_last_ns = ts_event_ns
        return OrderFilled(
            client_order_id=order.client_order_id,
            venue_order_id=order.venue_order_id,
            strategy_id=order.strategy_id,
            instrument_id=order.instrument_id,
            side=order.side,
            last_qty=order.quantity,
            last_px=price,
            ts_event_ns=ts_event_ns,
        )
