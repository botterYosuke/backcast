"""Regression for the Replay positions FLAT-after-close invariant (#9-12).

The deleted `test_gui_bridge_positions.py` pinned "a round-tripped (net-zero) position must
NOT appear as a phantom qty=0 row" for the now-retired GuiBridgeActor. After #50 the DuckDB
Replay path feeds the positions panel via `_finalize_run → compute_portfolio → _net_positions`
(flat-drop is the `abs(signed_qty) > 1e-9` filter). That kept path had no direct test, so this
re-pins the invariant where production now lives.
"""
from __future__ import annotations

from engine.strategy_runtime.portfolio import compute_portfolio
from engine.strategy_runtime.run_buffer_reader import EquityPoint, Fill


def _fill(side: str, qty: float, price: float, ts_ms: int) -> Fill:
    return Fill(instrument_id="8918.TSE", side=side, qty=qty, price=price, ts_event_ms=ts_ms)


def test_round_trip_position_drops_out_of_panel() -> None:
    # BUY 100 then SELL 100 → net flat → NO phantom qty=0 row (the #9-12 bug).
    fills = [_fill("BUY", 100, 8.0, 1000), _fill("SELL", 100, 9.0, 2000)]
    equity = [EquityPoint(ts_event_ms=2000, equity=10_000_100.0, cash=10_000_100.0)]
    pf = compute_portfolio(fills, equity, {"initial_cash": 10_000_000})
    assert pf["positions"] == [], "a net-zero position must not surface as a qty=0 row"
    # The fills still surface as FILLED orders (order history is independent of net position).
    assert len(pf["orders"]) == 2


def test_partial_close_keeps_remaining_position() -> None:
    # BUY 100, SELL 60 → 40 still open at the WAC cost basis.
    fills = [_fill("BUY", 100, 8.0, 1000), _fill("SELL", 60, 9.0, 2000)]
    equity = [EquityPoint(ts_event_ms=2000, equity=10_000_000.0, cash=9_999_460.0)]
    pf = compute_portfolio(fills, equity, {"initial_cash": 10_000_000})
    assert len(pf["positions"]) == 1
    pos = pf["positions"][0]
    assert pos["symbol"] == "8918.TSE"
    assert pos["qty"] == 40.0
    assert pos["avg_price"] == 8.0  # WAC: closing 60 at avg leaves the original 8.0 basis
