"""event_wire — BackendEvent → externally-tagged wire dict (ADR-0018 A2).

Issue #217: wire serialization を _backend_impl から分離。
"""
from engine.live import backend_events


def _order_event_wire(ev) -> dict:
    return {
        "order_id":        ev.order_id,
        "venue_order_id":  ev.venue_order_id,
        "client_order_id": ev.client_order_id,
        "status":          ev.status,
        "filled_qty":      ev.filled_qty,
        "avg_price":       ev.avg_price,
        "ts_ms":           ev.ts_ms,
        "strategy_id":     ev.strategy_id,
    }


def _account_event_wire(ev) -> dict:
    return {
        "cash":         ev.cash,
        "buying_power": ev.buying_power,
        "positions":    [
            {
                "symbol":         p.symbol,
                "qty":            p.qty,
                "avg_price":      p.avg_price,
                "unrealized_pnl": p.unrealized_pnl,
            }
            for p in ev.positions
        ],
        "ts_ms": ev.ts_ms,
    }


def _secret_required_wire(ev) -> dict:
    return {
        "request_id": ev.request_id,
        "venue":      ev.venue,
        "kind":       ev.kind,
        "purpose":    ev.purpose,
    }


def _venue_logout_detected_wire(ev) -> dict:
    return {"venue": ev.venue}


def _live_strategy_event_wire(ev) -> dict:
    return {
        "run_id":      ev.run_id,
        "strategy_id": ev.strategy_id,
        "status":      ev.status,
        "ts_ms":       ev.ts_ms,
    }


def _safety_rail_violation_wire(ev) -> dict:
    return {
        "run_id": ev.run_id,
        "kind":   ev.kind,
        "detail": ev.detail,
        "ts_ms":  ev.ts_ms,
    }


def _strategy_log_message_wire(ev) -> dict:
    return {
        "run_id":  ev.run_id,
        "level":   ev.level,
        "message": ev.message,
        "ts_ms":   ev.ts_ms,
    }


def _live_strategy_telemetry_wire(ev) -> dict:
    return {
        "run_id":         ev.run_id,
        "strategy_id":    ev.strategy_id,
        "realized_pnl":   ev.realized_pnl,
        "unrealized_pnl": ev.unrealized_pnl,
        "order_count":    ev.order_count,
        "fill_count":     ev.fill_count,
        "ts_ms":          ev.ts_ms,
    }


def _backend_error_wire(ev) -> dict:
    return {
        "source": ev.source,
        "detail": ev.detail,
        "ts_ms":  ev.ts_ms,
    }


_WIRE = {
    backend_events.OrderEvent:            ("OrderEvent", _order_event_wire),
    backend_events.AccountEvent:          ("AccountEvent", _account_event_wire),
    backend_events.SecretRequired:        ("SecretRequired", _secret_required_wire),
    backend_events.VenueLogoutDetected:   ("VenueLogoutDetected", _venue_logout_detected_wire),
    backend_events.LiveStrategyEvent:     ("LiveStrategyEvent", _live_strategy_event_wire),
    backend_events.SafetyRailViolation:   ("SafetyRailViolation", _safety_rail_violation_wire),
    backend_events.StrategyLogMessage:    ("StrategyLogMessage", _strategy_log_message_wire),
    backend_events.LiveStrategyTelemetry: ("LiveStrategyTelemetry", _live_strategy_telemetry_wire),
    backend_events.BackendError:          ("BackendError", _backend_error_wire),
}


def _backend_event_to_wire_dict(event) -> dict:
    """ADR-0018 A2 event edge: typed BackendEvent → 外部タグ付き wire dict
    (例 {"OrderEvent": {...}})。Rust serde::Deserialize<BackendEvent> が期待する形。

    type(event) の完全一致で明示 _WIRE registry を引く。各 emitter が全 wire キーを
    明示するので Rust 必須 field 契約がコード上で可視、かつ将来 domain dataclass に
    field を足しても wire に漏れない。JSON encode は publish_backend_event に残す。
    """
    entry = _WIRE.get(type(event))
    if entry is None:
        raise ValueError(f"Unmapped BackendEvent type: {type(event)!r}")
    tag, emit = entry
    return {tag: emit(event)}
