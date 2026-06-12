"""ADR-0018 A2: 純 typed event model。

variant 名は Rust `BackendEvent` enum と 1:1。JSON/serializer は持たない
（event edge=`_backend_impl._backend_event_to_wire_dict` が所有）。
"""

from __future__ import annotations

from dataclasses import dataclass

from engine.live.order_types import AccountPositionData


@dataclass(frozen=True, slots=True)
class OrderEvent:
    order_id: str
    venue_order_id: str
    client_order_id: str
    status: str
    filled_qty: float
    avg_price: float
    ts_ms: int
    strategy_id: str


@dataclass(frozen=True, slots=True)
class AccountEvent:
    cash: float
    buying_power: float
    positions: tuple[AccountPositionData, ...]
    ts_ms: int


@dataclass(frozen=True, slots=True)
class SecretRequired:
    request_id: str
    venue: str
    kind: str
    purpose: str


@dataclass(frozen=True, slots=True)
class VenueLogoutDetected:
    venue: str


@dataclass(frozen=True, slots=True)
class LiveStrategyEvent:
    run_id: str
    strategy_id: str
    status: str
    ts_ms: int


@dataclass(frozen=True, slots=True)
class SafetyRailViolation:
    run_id: str
    kind: str
    detail: str
    ts_ms: int


@dataclass(frozen=True, slots=True)
class StrategyLogMessage:
    run_id: str
    level: str
    message: str
    ts_ms: int


@dataclass(frozen=True, slots=True)
class LiveStrategyTelemetry:
    run_id: str
    strategy_id: str
    realized_pnl: float
    unrealized_pnl: float
    order_count: int
    fill_count: int
    ts_ms: int


@dataclass(frozen=True, slots=True)
class BackendError:
    source: str
    detail: str
    ts_ms: int


BackendEvent = (
    OrderEvent | AccountEvent | SecretRequired | VenueLogoutDetected
    | LiveStrategyEvent | SafetyRailViolation | StrategyLogMessage
    | LiveStrategyTelemetry | BackendError
)
