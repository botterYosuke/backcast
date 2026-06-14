"""engine.kernel.orders — order model + OrderEngine for the Backcast Execution Kernel (#24).

Tracer-thin (findings 0008 §5): submit → (pre-trade gate) → ACCEPTED → FILLED, plus a
duplicate-client_order_id guard and the risk-denial path. Partial fills, cancels, venue
rejects, modify and the tick path are deferred to follow-up issues.

Nautilus-free. The pre-trade rail is the same `evaluate_pre_trade` logic the live path
uses (engine.live.pre_trade_gate), now import-pure (#24).
"""
from __future__ import annotations

import enum
from dataclasses import dataclass

from engine.kernel.risk import RiskEngine
from engine.live.safety_rails import RailViolation


class OrderSide(enum.Enum):
    BUY = "BUY"
    SELL = "SELL"


class OrderStatus(enum.Enum):
    INITIALIZED = "INITIALIZED"
    SUBMITTED = "SUBMITTED"
    ACCEPTED = "ACCEPTED"
    PARTIALLY_FILLED = "PARTIALLY_FILLED"
    FILLED = "FILLED"
    REJECTED = "REJECTED"
    CANCELED = "CANCELED"
    EXPIRED = "EXPIRED"
    PENDING_UPDATE = "PENDING_UPDATE"
    PENDING_CANCEL = "PENDING_CANCEL"
    DENIED = "DENIED"


@dataclass
class Order:
    client_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    quantity: float
    status: OrderStatus = OrderStatus.INITIALIZED
    filled_qty: float = 0.0
    avg_px: float = 0.0
    venue_order_id: str = ""
    ts_last_ns: int = 0
    denied_reason: str = ""


@dataclass
class OrderFilled:
    """Fill event handed to Strategy.on_order and the EventSink.

    `last_qty` is the INCREMENTAL fill quantity applied to the Portfolio (D6). For a Live
    partial-fill stream the cumulative venue-reported quantity is carried separately in
    `cumulative_filled_qty` so the dedup (delta = incoming_cumulative - order.filled_qty)
    can run without changing `last_qty`'s meaning. Replay (ReplayBroker, full fill) leaves
    `cumulative_filled_qty` at its default and only `last_qty` is read by the sink.
    """

    client_order_id: str
    venue_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    last_qty: float
    last_px: float
    ts_event_ns: int
    cumulative_filled_qty: float = 0.0


@dataclass
class OrderAccepted:
    """Venue-accepted event (Live FSM, SUBMITTED → ACCEPTED). Handed to Strategy.on_order."""

    client_order_id: str
    venue_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    ts_event_ns: int


@dataclass
class OrderRejected:
    """Venue/adapter reject (Live FSM, SUBMITTED → REJECTED). Handed to Strategy.on_order."""

    client_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    reason: str
    ts_event_ns: int


@dataclass
class OrderCanceled:
    """Cancel confirmation (Live FSM → CANCELED). Handed to Strategy.on_order."""

    client_order_id: str
    venue_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    ts_event_ns: int


@dataclass
class OrderExpired:
    """Expiry confirmation (Live FSM → EXPIRED). Handed to Strategy.on_order.

    Distinct from OrderCanceled so the external projection (UI / strategy.on_order) reports
    EXPIRED, matching the internal `order.status` — collapsing it into OrderCanceled would make
    the FSM state and the notified status disagree (#25 review finding 3).
    """

    client_order_id: str
    venue_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    ts_event_ns: int


@dataclass
class OrderDenied:
    """Risk-denied event handed to Strategy.on_order (kind matches RailViolation.kind)."""

    client_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    quantity: float
    kind: str
    reason: str
    ts_event_ns: int


class OrderEngine:
    """Owns order identity, the duplicate guard, the pre-trade rail, and acceptance.

    Filling is performed by the ReplayBroker against a bar; the engine just tracks
    state. `rails` may be None (no independent rails configured) — the gate then only
    skips the allowlist/cap checks, exactly like the live caller.
    """

    def __init__(self, *, risk_engine: RiskEngine, venue: str) -> None:
        self._risk = risk_engine
        self._venue = venue
        self._seen_ids: set[str] = set()
        self._seq = 0

    def next_venue_order_id(self) -> str:
        self._seq += 1
        return f"{self._venue}-{self._seq:03d}"

    @property
    def requires_reference_price(self) -> bool:
        """True if a notional-dependent pre-trade rail is configured (delegates to RiskEngine).

        When such a rail is set but the order's reference price is unknown (e.g. an `on_start`
        order before any market data), the caller must DENY rather than pass through a 0-JPY
        notional that silently bypasses the cap (#25 review finding 1).
        """
        return self._risk.requires_reference_price

    def precheck(
        self,
        order: Order,
        *,
        net_signed_qty: float,
        reference_price: float | None,
        order_notional_jpy: float = 0.0,
    ) -> RailViolation | None:
        """Reserve the client_order_id (dup guard) + evaluate the pre-trade rail.

        On denial the order transitions to DENIED with `denied_reason` set and the
        violation is returned. On pass the order is **left at INITIALIZED** and None is
        returned — the caller decides the next transition (Replay → ACCEPTED via submit();
        Live → SUBMITTED via the LiveBroker before the venue round-trip). A duplicate
        client_order_id raises ValueError (a strategy programming error, not a rail
        violation). The PAUSE/run gate is NOT evaluated here — that is the LiveBroker's
        responsibility before the venue send (D6/D8).
        """
        if order.client_order_id in self._seen_ids:
            raise ValueError(f"duplicate client_order_id: {order.client_order_id}")
        self._seen_ids.add(order.client_order_id)

        violation = self._risk.check_pre_trade(
            instrument_id=order.instrument_id,
            is_buy=order.side is OrderSide.BUY,
            qty=order.quantity,
            net_signed_qty=net_signed_qty,
            reference_price=reference_price,
            order_notional_jpy=order_notional_jpy,
        )
        if violation is not None:
            order.status = OrderStatus.DENIED
            order.denied_reason = violation.detail
        return violation

    def submit(
        self,
        order: Order,
        *,
        net_signed_qty: float,
        reference_price: float | None,
        order_notional_jpy: float = 0.0,
    ) -> RailViolation | None:
        """Replay submit: precheck + (on pass) transition to ACCEPTED.

        Observable behaviour is unchanged from #24 (DENIED on violation, ACCEPTED on pass,
        ValueError on duplicate) so the committed Replay golden is bit-identical. Live does
        NOT call this — it uses precheck() then drives SUBMITTED→… via the LiveBroker (D6).
        """
        violation = self.precheck(
            order,
            net_signed_qty=net_signed_qty,
            reference_price=reference_price,
            order_notional_jpy=order_notional_jpy,
        )
        if violation is None:
            order.status = OrderStatus.ACCEPTED
        return violation
