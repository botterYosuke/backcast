"""engine.strategy_runtime.portfolio — build a portfolio snapshot from run-buffer files."""
from __future__ import annotations

import logging
from typing import Optional

from engine.strategy_runtime.run_buffer_reader import EquityPoint, Fill

log = logging.getLogger(__name__)


def _net_positions(fills: list[Fill]) -> list[dict]:
    """Compute net positions with WAC avg cost basis, instrument-scoped."""
    net: dict[str, dict] = {}
    for fill in fills:
        sym = fill.instrument_id
        if sym not in net:
            net[sym] = {"signed_qty": 0.0, "cost": 0.0}
        sign = 1.0 if fill.side == "BUY" else -1.0
        sq = net[sym]["signed_qty"]
        net[sym]["signed_qty"] += sign * fill.qty
        # Closing (or flipping) an existing position: use WAC avg to avoid
        # mixing realized PnL into the remaining cost basis.
        if sign * sq < 0:
            avg = net[sym]["cost"] / sq
            close_qty = min(fill.qty, abs(sq))
            flip_qty = fill.qty - close_qty
            net[sym]["cost"] += sign * close_qty * avg
            if flip_qty > 1e-9:
                net[sym]["cost"] += sign * flip_qty * fill.price
        else:
            net[sym]["cost"] += sign * fill.qty * fill.price

    positions: list[dict] = []
    for sym, d in net.items():
        sq = d["signed_qty"]
        if abs(sq) > 1e-9:
            avg = d["cost"] / sq
            positions.append({
                "symbol": sym,
                "qty": int(round(sq)),
                "avg_price": avg,
                "unrealized_pnl": 0.0,
            })
    return positions


def compute_portfolio(
    fills: list[Fill],
    equity_points: list[EquityPoint],
    scenario: Optional[dict] = None,
) -> dict:
    """Build a portfolio snapshot dict from typed Fill / EquityPoint records.

    Returns:
        {buying_power, cash, equity, positions: list[dict], orders: list[dict]}
    """
    orders: list[dict] = [
        {
            "symbol": f.instrument_id,
            "side": f.side,
            "qty": f.qty,
            "price": f.price,
            "status": "FILLED",
            "ts_ms": f.ts_event_ms,
        }
        for f in fills
    ]

    positions = _net_positions(fills)

    initial_cash = float((scenario or {}).get("initial_cash", 0) or 0)
    last = equity_points[-1] if equity_points else None
    # equity = mark-to-market (cash + open-position value). cash = realized cash, recorded
    # separately by the DuckDB/kernel path (#49 review #2); the legacy equity-only path leaves
    # cash=None, so fall back to equity (cash==equity for a flat round-trip). buying_power for a
    # CASH account == cash (live margin venues report 余力 authoritatively — out of scope here).
    last_equity = last.equity if last else initial_cash
    last_cash = last.cash if (last and last.cash is not None) else last_equity

    return {
        "buying_power": last_cash,
        "cash": last_cash,
        "equity": last_equity,
        "positions": positions,
        "orders": orders,
    }
