"""PortfolioSnapshot — the cell-facing read seam's accounting (#76 portfolio-driver slice).

Pure unit coverage for ``Portfolio.snapshot(prices, primary_instrument_id)``: the frozen
snapshot a marimo cell reads via ``get_portfolio()``. The glossary 3-value model
(CONTEXT.md): equity = mark-to-market (cash + Σ position×price), cash = realized cash, plus
signed positions. ``position`` is the primary instrument's signed net qty; ``net_qty(iid)`` /
``positions`` cover multi-instrument. marimo-free.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import dataclasses  # noqa: E402

import pytest  # noqa: E402

from engine.kernel.orders import OrderFilled, OrderSide  # noqa: E402
from engine.kernel.portfolio import Portfolio, PortfolioSnapshot  # noqa: E402

A = "7203.T"
B = "9984.T"


def _fill(iid: str, side: OrderSide, qty: float, px: float) -> OrderFilled:
    return OrderFilled(
        client_order_id="c",
        venue_order_id="v",
        strategy_id="s",
        instrument_id=iid,
        side=side,
        last_qty=qty,
        last_px=px,
        ts_event_ns=0,
    )


def test_flat_snapshot_is_initial_cash():
    snap = Portfolio(initial_cash=1_000_000.0).snapshot({}, A)
    assert isinstance(snap, PortfolioSnapshot)
    assert snap.cash == 1_000_000.0
    assert snap.equity == 1_000_000.0  # flat → MTM == cash
    assert snap.realized_pnl == 0.0
    assert snap.position == 0.0
    assert dict(snap.positions) == {}
    assert snap.net_qty(A) == 0.0


def test_snapshot_reflects_position_cash_and_mtm_equity():
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(A, OrderSide.BUY, 10.0, 100.0))  # cash -1000 → 999000, +10 @100
    # equity marks the held position to a DIFFERENT price (MTM): 999000 + 10*120 = 1_000_200.
    snap = pf.snapshot({A: 120.0}, A)
    assert snap.cash == pytest.approx(999_000.0)
    assert snap.position == pytest.approx(10.0)
    assert snap.net_qty(A) == pytest.approx(10.0)
    assert dict(snap.positions) == {A: pytest.approx(10.0)}
    assert snap.equity == pytest.approx(999_000.0 + 10.0 * 120.0)


def test_snapshot_primary_position_is_per_instrument():
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(A, OrderSide.BUY, 10.0, 100.0))
    pf.apply_fill(_fill(B, OrderSide.SELL, 5.0, 200.0))  # short 5 of B
    snap = pf.snapshot({A: 100.0, B: 200.0}, primary_instrument_id=A)
    assert snap.position == pytest.approx(10.0)  # primary = A
    assert snap.net_qty(B) == pytest.approx(-5.0)  # B is short
    assert dict(snap.positions) == {A: pytest.approx(10.0), B: pytest.approx(-5.0)}
    # No primary bound → position 0.0 (the per-instrument net is still on net_qty/positions).
    assert pf.snapshot({A: 100.0, B: 200.0}, None).position == 0.0


def test_snapshot_buying_power_defaults_to_cash_and_accepts_override():
    """buying_power = the cash-aware-sizing seam (v19 ``_cash_aware_picks`` reads
    ``self.buying_power()``). In Replay it == cash (pure accounting default); the ctx seam
    passes a Live venue value explicitly. Exposed on the snapshot so a marimo cell can size
    off ``get_portfolio().buying_power`` (#76 S6b-α)."""
    pf = Portfolio(initial_cash=1_000_000.0)
    assert pf.snapshot({}, A).buying_power == 1_000_000.0  # flat → cash
    pf.apply_fill(_fill(A, OrderSide.BUY, 10.0, 100.0))  # cash -1000 → 999000
    assert pf.snapshot({A: 100.0}, A).buying_power == pytest.approx(999_000.0)
    # Live venue buying power differs from cash → the ctx provider overrides it explicitly.
    assert pf.snapshot({A: 100.0}, A, buying_power=500_000.0).buying_power == 500_000.0


def test_snapshot_is_frozen():
    snap = Portfolio(initial_cash=1.0).snapshot({}, A)
    with pytest.raises(dataclasses.FrozenInstanceError):
        snap.cash = 2.0  # type: ignore[misc]


def test_snapshot_is_hashable_value():
    """The snapshot is marketed as a value — a cell may put it in a set / use it as a key.
    The MappingProxyType positions field would make the generated __hash__ raise, so it is
    excluded from the hash (but still compared in __eq__)."""
    snap = Portfolio(initial_cash=1.0).snapshot({}, A)
    assert snap in {snap}  # hashable, no TypeError
    # positions still participates in equality even though it is excluded from the hash
    pf = Portfolio(initial_cash=1.0)
    pf.apply_fill(_fill(A, OrderSide.BUY, 1.0, 1.0))
    assert pf.snapshot({A: 1.0}, A) != Portfolio(initial_cash=0.0).snapshot({}, A)
