"""engine.kernel.portfolio — cash + position accounting for the kernel (#24).

CASH-account semantics matched to the Nautilus oracle (findings 0008 §2):
a BUY reduces cash by `qty × px`, a SELL increases it; the reported equity is the
cash balance (positions are not added to equity for a CASH account). A position that
returns to zero quantity drops out of the open-positions snapshot (terminal = FLAT).

Single-instrument, single-position (NETTING) — tracer scope. Nautilus-free.
"""
from __future__ import annotations

from dataclasses import dataclass

from engine.kernel.orders import OrderFilled, OrderSide


@dataclass
class Position:
    instrument_id: str
    quantity: float = 0.0   # signed: long > 0, short < 0
    avg_px: float = 0.0


class Portfolio:
    """Tracks cash and one netting position per instrument."""

    def __init__(self, *, initial_cash: float) -> None:
        self._initial_cash = float(initial_cash)
        self._cash = float(initial_cash)
        self._positions: dict[str, Position] = {}

    @property
    def cash(self) -> float:
        return self._cash

    @property
    def equity(self) -> float:
        """Sink-reported equity == cash balance.

        Matches the Nautilus oracle's `account.balance_total` for a CASH account
        (open positions are NOT marked into this number — it is the buying-power view
        the GuiBridge sink emits). For risk/P&L use `mark_to_market_equity` instead.
        """
        return self._cash

    def mark_to_market_equity(self, prices: dict[str, float]) -> float:
        """Risk-side equity == cash + open-position market value at `prices`.

        Distinct from `equity`/`buying_power` (== cash): buying a position moves value
        from cash into holdings, so MTM equity is unchanged at the entry price and only
        moves with price. This is what a daily-loss rail must see — otherwise merely
        *opening* a position would register as a loss equal to its notional.
        """
        total = self._cash
        for p in self._positions.values():
            if p.quantity != 0.0:
                total += p.quantity * prices.get(p.instrument_id, p.avg_px)
        return total

    @property
    def realized_pnl(self) -> float:
        """Realized P&L == cash delta (exact only when the book is flat)."""
        return self._cash - self._initial_cash

    def net_signed_qty(self, instrument_id: str) -> float:
        pos = self._positions.get(instrument_id)
        return pos.quantity if pos else 0.0

    def position_value_jpy(self, instrument_id: str) -> float:
        """|qty| × avg_px — current cost basis of the open position (0 if flat)."""
        pos = self._positions.get(instrument_id)
        if pos is None or pos.quantity == 0.0:
            return 0.0
        return abs(pos.quantity) * pos.avg_px

    def apply_fill(self, fill: OrderFilled) -> None:
        signed = fill.last_qty if fill.side is OrderSide.BUY else -fill.last_qty
        notional = fill.last_qty * fill.last_px
        self._cash += -notional if fill.side is OrderSide.BUY else notional

        pos = self._positions.get(fill.instrument_id)
        if pos is None:
            pos = Position(instrument_id=fill.instrument_id)
            self._positions[fill.instrument_id] = pos

        new_qty = pos.quantity + signed
        if pos.quantity == 0.0:
            pos.avg_px = fill.last_px
        elif (pos.quantity > 0) == (signed > 0):
            # increasing the same direction → weighted-average entry price
            pos.avg_px = (pos.quantity * pos.avg_px + signed * fill.last_px) / new_qty
        elif new_qty != 0.0 and (new_qty > 0) != (pos.quantity > 0):
            # over-filled past flat → residual is a NEW position opened at the fill price
            pos.avg_px = fill.last_px
        # else: reducing the same side (not yet flat) keeps avg_px
        pos.quantity = new_qty
        if pos.quantity == 0.0:
            pos.avg_px = 0.0

    def open_positions(self) -> list[Position]:
        return [p for p in self._positions.values() if p.quantity != 0.0]
