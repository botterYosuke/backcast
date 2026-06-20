"""Cell-facing injected API for marimo strategies (#76 S4 / ADR-0012 / findings 0046).

A marimo strategy cell places orders by calling an injected ``submit_market(qty)`` — a
host-bound adapter that hides the kernel's per-bar contract (``OrderSide`` enum + positive
quantity + host-owned ``strategy_id``). This module is the ADAPTATION BOUNDARY (ADR-0012
Decision 2): marimo cells adapt to the immutable kernel contract through it.

It is deliberately **marimo-free** (it only wraps a ``StrategyContext`` callable + the
``OrderSide`` enum), so the runtime seam can import it without pulling marimo — proven by
``tests/test_strategy_runtime_offline.py``. The thin drain injects the adapter into the cell
globals: ``open_runtime(app, drivers=[...], inject={"submit_market": make_submit_market(...)})``.

Scope: S4 builds the adapter standalone (driven by a fake ``StrategyContext`` in the gates).
Wiring it to the real kernel ``_Context`` is S6.
"""

from __future__ import annotations

from typing import Any, Callable, Optional

from engine.kernel.orders import signed_qty_to_side


def make_submit_market(
    ctx: Any, *, strategy_id: str, default_instrument_id: str
) -> Callable[..., None]:
    """Build the cell-facing ``submit_market(qty, *, instrument_id=None)``.

    The cell passes a SIGNED quantity — the reactive idiom ``qty = signal * size``: the sign
    is the side (``> 0`` BUY, ``< 0`` SELL), ``abs(qty)`` is the order size, and ``0`` is a
    no-op (flat = no order). ``qty`` is a DELTA order — the quantity to trade THIS bar, not a
    target position — the same contract as the imperative ``on_bar``.

    The adapter is the single point that hands the kernel a positive quantity, so all
    host-owned validation lives here (the kernel has no positivity guard, and
    ``notional = price * quantity`` would go 0/negative on a bad value):

      - ``0`` / ``-0.0`` → no-op (no order submitted)
      - ``NaN`` / ``±inf`` → fail-closed ``ValueError`` (a reactive div-by-zero must never
        reach the broker)

    ``strategy_id`` is host-bound; ``instrument_id`` defaults to the strategy's primary
    instrument and may be overridden per call (multi-instrument strategies). Quantity is NOT
    lot-rounded here — venue lot sizing is a separate layer.
    """

    def submit_market(qty: float, *, instrument_id: Optional[str] = None) -> None:
        resolved = signed_qty_to_side(qty)  # NaN/inf → ValueError; 0/-0.0 → None (no-op)
        if resolved is None:
            return
        side, quantity = resolved
        ctx.submit_market(
            strategy_id=strategy_id,
            instrument_id=instrument_id if instrument_id is not None else default_instrument_id,
            side=side,
            quantity=quantity,
        )

    return submit_market
