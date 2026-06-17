"""Cell-facing injected API adapter gates (#76 S4 / ADR-0012 / findings 0046).

The marimo strategy cell places orders by calling an injected ``submit_market(qty)`` — a
host-bound adapter (``engine.strategy_runtime.cell_api.make_submit_market``) that hides the
kernel per-bar contract. These are marimo-free unit gates driven by a fake StrategyContext:

  CONVERT     signed qty → (side, abs(qty)); strategy_id host-bound; instrument_id primary
              default or explicit override.
  VALIDATE    the adapter is the single point handing the kernel a positive quantity, so all
              host-owned validation lives here: 0 / -0.0 are a no-op; NaN / ±inf fail-closed.
"""

from __future__ import annotations

import math

import pytest

from engine.kernel.orders import OrderSide
from engine.strategy_runtime.cell_api import make_submit_market


class _FakeCtx:
    """Records submit_market calls (the kernel _Context's per-bar contract surface)."""

    def __init__(self) -> None:
        self.calls: list[dict] = []

    def submit_market(self, *, strategy_id, instrument_id, side, quantity) -> None:
        self.calls.append(
            {
                "strategy_id": strategy_id,
                "instrument_id": instrument_id,
                "side": side,
                "quantity": quantity,
            }
        )


def _adapter(ctx, *, strategy_id="strat-1", default_instrument_id="7203.T"):
    return make_submit_market(
        ctx, strategy_id=strategy_id, default_instrument_id=default_instrument_id
    )


def test_positive_qty_is_a_buy_of_abs_quantity():
    ctx = _FakeCtx()
    _adapter(ctx)(12.0)
    assert ctx.calls == [
        {"strategy_id": "strat-1", "instrument_id": "7203.T", "side": OrderSide.BUY, "quantity": 12.0}
    ]


def test_negative_qty_is_a_sell_of_abs_quantity():
    ctx = _FakeCtx()
    _adapter(ctx)(-3.5)
    assert ctx.calls == [
        {"strategy_id": "strat-1", "instrument_id": "7203.T", "side": OrderSide.SELL, "quantity": 3.5}
    ]


def test_explicit_instrument_id_overrides_the_primary_default():
    ctx = _FakeCtx()
    _adapter(ctx)(1.0, instrument_id="9984.T")
    assert ctx.calls[0]["instrument_id"] == "9984.T"


def test_zero_qty_is_a_no_op():
    ctx = _FakeCtx()
    _adapter(ctx)(0.0)
    assert ctx.calls == []


def test_negative_zero_qty_is_a_no_op():
    ctx = _FakeCtx()
    _adapter(ctx)(-0.0)
    assert ctx.calls == []


@pytest.mark.parametrize("bad", [math.nan, math.inf, -math.inf])
def test_non_finite_qty_is_fail_closed(bad):
    ctx = _FakeCtx()
    with pytest.raises(ValueError, match="non-finite"):
        _adapter(ctx)(bad)
    assert ctx.calls == [], "a non-finite quantity must never reach the broker"
