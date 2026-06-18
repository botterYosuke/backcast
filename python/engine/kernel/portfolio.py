"""engine.kernel.portfolio — cash + position accounting for the kernel (#24).

CASH-account semantics matched to the Nautilus oracle (findings 0008 §2):
a BUY reduces cash by `qty × px`, a SELL increases it; the reported equity is the
cash balance (positions are not added to equity for a CASH account). A position that
returns to zero quantity drops out of the open-positions snapshot (terminal = FLAT).

Single-instrument, single-position (NETTING) — tracer scope. Nautilus-free.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Mapping, Optional

from engine.kernel.orders import OrderFilled, OrderSide


@dataclass
class Position:
    instrument_id: str
    quantity: float = 0.0   # signed: long > 0, short < 0
    avg_px: float = 0.0


@dataclass(frozen=True)
class PortfolioSnapshot:
    """Immutable per-bar portfolio view a marimo cell reads via ``get_portfolio()`` (#76).

    The glossary 3-value model (CONTEXT.md): ``equity`` = mark-to-market (cash + Σ position×
    price), ``cash`` = realized cash, plus signed positions. ``position`` is the primary
    instrument's signed net qty (the symmetric read of the signed-delta ``submit_market``);
    ``net_qty(iid)`` / ``positions`` cover multi-instrument. Frozen so a cell cannot mutate
    the host's accounting and so the reactive value semantics hold (the snapshot IS a value).
    """

    cash: float
    equity: float
    realized_pnl: float
    # Cash-aware-sizing seam (v19 ``_cash_aware_picks`` reads ``self.buying_power()``). In
    # Replay it == cash; the ctx seam overrides it with the venue value in Live. On the
    # snapshot so a cell sizes off ``get_portfolio().buying_power`` (#76 S6b-α).
    buying_power: float
    position: float
    # Excluded from the frozen dataclass's generated __hash__ (a MappingProxyType is
    # unhashable, which would make hashing the "value" snapshot raise TypeError) but kept in
    # __eq__ — so reactive value-equality still sees position changes, and `pf in a_set` works.
    positions: Mapping[str, float] = field(hash=False)

    def net_qty(self, instrument_id: str) -> float:
        return self.positions.get(instrument_id, 0.0)


class Portfolio:
    """Tracks cash and one netting position per instrument."""

    def __init__(self, *, initial_cash: float) -> None:
        self._initial_cash = float(initial_cash)
        self._cash = float(initial_cash)
        self._positions: dict[str, Position] = {}
        # Explicitly accrued realized P&L (D7). The flat-book `cash - initial_cash` shortcut
        # is wrong once the book is seeded with venue positions or left non-flat, so Live
        # accrues realized P&L on each reducing fill. For a flat round-trip this equals the
        # cash delta, so the Replay golden is unchanged.
        self._realized: float = 0.0

    def seed_position(self, instrument_id: str, quantity: float, avg_px: float) -> None:
        """Seed an existing venue position at attach (D7). Does NOT touch cash or realized.

        The venue AccountSnapshot is the authority for the opening book; the kernel Portfolio
        mirrors it so the pre-trade position cap and MTM telemetry see real holdings. A zero
        quantity is ignored (flat).
        """
        if quantity == 0.0:
            return
        self._positions[instrument_id] = Position(
            instrument_id=instrument_id, quantity=float(quantity), avg_px=float(avg_px)
        )

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
        """Realized P&L accrued on reducing fills (correct with seeded / non-flat books).

        Equals `cash - initial_cash` for a flat round-trip (the Replay tracer case), so the
        committed Replay golden is unchanged; diverges only once the book is seeded or left
        open, where the cash-delta shortcut would be wrong (D7)."""
        return self._realized

    def net_signed_qty(self, instrument_id: str) -> float:
        pos = self._positions.get(instrument_id)
        return pos.quantity if pos else 0.0

    def apply_fill(self, fill: OrderFilled) -> None:
        signed = fill.last_qty if fill.side is OrderSide.BUY else -fill.last_qty
        notional = fill.last_qty * fill.last_px
        self._cash += -notional if fill.side is OrderSide.BUY else notional

        pos = self._positions.get(fill.instrument_id)
        if pos is None:
            pos = Position(instrument_id=fill.instrument_id)
            self._positions[fill.instrument_id] = pos

        # Accrue realized P&L on the portion of this fill that REDUCES the open position
        # (opposite direction). Closing a long realizes (fill_px - avg_px) per share; closing
        # a short realizes (avg_px - fill_px). The residual past flat opens a new position at
        # the fill price and does not realize. (D7)
        if pos.quantity != 0.0 and (pos.quantity > 0) != (signed > 0):
            reduce_qty = min(abs(signed), abs(pos.quantity))
            if pos.quantity > 0:
                self._realized += reduce_qty * (fill.last_px - pos.avg_px)
            else:
                self._realized += reduce_qty * (pos.avg_px - fill.last_px)

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

    def snapshot(
        self,
        prices: dict[str, float],
        primary_instrument_id: Optional[str] = None,
        *,
        buying_power: Optional[float] = None,
    ) -> PortfolioSnapshot:
        """Build the frozen cell-facing snapshot (#76 portfolio-driver slice).

        ``equity`` is marked to market at ``prices`` (the kernel's ``reference_prices`` at
        ``on_bar`` entry — the bar close orders fill at), ``cash`` / ``realized_pnl`` are the
        live book, ``position`` is the primary instrument's signed net qty. Called at
        ``on_bar(N)`` entry (before this bar's fill) → the book is end-of-(N-1) = no-look-ahead.

        ``buying_power`` is the cash-aware-sizing seam; it defaults to cash (the Replay
        accounting value) and the Live ctx passes the venue value explicitly (#76 S6b-α).
        """
        positions = {p.instrument_id: p.quantity for p in self.open_positions()}
        primary = (
            positions.get(primary_instrument_id, 0.0)
            if primary_instrument_id is not None
            else 0.0
        )
        return PortfolioSnapshot(
            cash=self._cash,
            equity=self.mark_to_market_equity(prices),
            realized_pnl=self._realized,
            buying_power=self._cash if buying_power is None else float(buying_power),
            position=primary,
            positions=MappingProxyType(positions),
        )
