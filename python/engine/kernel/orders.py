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
    ACCEPTED = "ACCEPTED"
    FILLED = "FILLED"
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
    """Terminal fill event handed to Strategy.on_order and the EventSink."""

    client_order_id: str
    venue_order_id: str
    strategy_id: str
    instrument_id: str
    side: OrderSide
    last_qty: float
    last_px: float
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

    def submit(
        self,
        order: Order,
        *,
        net_signed_qty: float,
        current_position_value_jpy: float,
        order_notional_jpy: float = 0.0,
    ) -> RailViolation | None:
        """Apply the duplicate guard + pre-trade rail. Returns a RailViolation if denied.

        On acceptance the order transitions to ACCEPTED. On denial it transitions to
        DENIED with `denied_reason` set. A duplicate client_order_id raises ValueError
        (a programming error in the strategy, not a rail violation).
        """
        if order.client_order_id in self._seen_ids:
            raise ValueError(f"duplicate client_order_id: {order.client_order_id}")
        self._seen_ids.add(order.client_order_id)

        violation = self._risk.check_pre_trade(
            instrument_id=order.instrument_id,
            is_buy=order.side is OrderSide.BUY,
            qty=order.quantity,
            net_signed_qty=net_signed_qty,
            current_position_value_jpy=current_position_value_jpy,
            order_notional_jpy=order_notional_jpy,
        )
        if violation is not None:
            order.status = OrderStatus.DENIED
            order.denied_reason = violation.detail
            return violation

        order.status = OrderStatus.ACCEPTED
        return None
