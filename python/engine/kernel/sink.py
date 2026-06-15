"""engine.kernel.sink — EventSink emitting the existing Replay sink JSON contract (#24).

The kernel pushes the SAME JSON payloads as the Nautilus path's GuiBridgeActor
(engine.live.gui_bridge_actor), so the C# decoder reads kernel runs with no change
(AC#4). The push target duck-types the existing RustBacktestSink:
`push_bar(str)` / `push_order(str)` / `push_portfolio(str)` / `push_run_complete(run_id, str)`.

Field-for-field parity with GuiBridgeActor is the contract; payload shapes are pinned in
findings 0008 §3. Nautilus-free.
"""
from __future__ import annotations

import json
from typing import Any

from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderFilled
from engine.kernel.portfolio import Portfolio


class EventSink:
    """Builds and pushes the Replay sink JSON payloads, accumulating bar history."""

    def __init__(self, push_target: Any) -> None:
        self._target = push_target
        self._ohlc_points: list[dict] = []
        self._history: list[float] = []
        self._per_instrument: dict[str, dict[str, Any]] = {}

    def push_bar(self, bar: Bar) -> None:
        ts_ms = bar.ts_event_ns // 1_000_000
        point = {
            "timestamp_ms": ts_ms,
            "open_time_ms": ts_ms,
            "open": bar.open,
            "high": bar.high,
            "low": bar.low,
            "close": bar.close,
            "volume": bar.volume,
        }
        self._ohlc_points.append(point)
        self._history.append(bar.close)

        entry = self._per_instrument.setdefault(
            bar.instrument_id, {"price": None, "ohlc_points": []}
        )
        entry["price"] = bar.close
        entry["ohlc_points"].append(point)

        self._target.push_bar(
            json.dumps(
                {
                    "price": bar.close,
                    "timestamp": ts_ms / 1000.0,
                    "timestamp_ms": ts_ms,
                    "history": self._history,
                    "ohlc_points": self._ohlc_points,
                    "per_instrument": self._per_instrument,
                }
            )
        )

    def push_order(self, fill: OrderFilled) -> None:
        self._target.push_order(
            json.dumps(
                {
                    "symbol": fill.instrument_id,
                    "client_order_id": fill.client_order_id,
                    "venue_order_id": fill.venue_order_id,
                    "strategy_id": fill.strategy_id,
                    "side": fill.side.value,
                    "status": "FILLED",
                    "qty": float(fill.last_qty),
                    "price": float(fill.last_px),
                    "timestamp_ms": fill.ts_event_ns // 1_000_000,
                }
            )
        )

    def push_portfolio(self, portfolio: Portfolio) -> None:
        positions = [
            {"symbol": p.instrument_id, "qty": float(p.quantity), "avg_price": float(p.avg_px)}
            for p in portfolio.open_positions()
        ]
        self._target.push_portfolio(
            json.dumps(
                {
                    "buying_power": portfolio.cash,
                    "equity": portfolio.equity,
                    "positions": positions,
                    "orders": [],
                }
            )
        )

    def push_run_complete(self, run_id: str, summary: dict) -> None:
        self._target.push_run_complete(run_id, json.dumps(summary))

    def on_equity(self, ts_event_ms: int, equity: float, cash: float) -> None:
        # No-op for the golden contract: per-bar equity is summarized internally by the
        # runner (equity_curve_stats), not pushed to the RustBacktestSink target. Declared
        # so KernelRunner can call sink.on_equity unconditionally — a getattr probe would
        # silently drop the production observer's equity curve on a method-name typo.
        pass
