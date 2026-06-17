"""v19 A0_EQUAL_NOMINAL_E1 allocation policy — numeric parity (#75).

Locks the kernel port of `_alloc_a0_equal_nominal_e1` to the _blacksheep numeric
behaviour (equal-yen per pick → lot-floor → rank-order +1-lot redistribute), and proves
the additive knob leaves the v0 default bit-exact.

Pure unit coverage: the alloc functions need only `_current_price` + `_alloc_policy` +
`_lot_size`, so the strategy is built without on_start (no model/artifacts loaded). Prices
are injected via `_snapshots` (what `_current_price` reads); a missing instrument → None
price (the NO_PRICE branch).
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.kernel.strategy import Strategy as KernelStrategy  # noqa: E402
from engine.strategy_runtime.strategy_loader import load as load_strategy  # noqa: E402

_HERE = os.path.dirname(os.path.abspath(__file__))
_V19_PY = os.path.abspath(os.path.join(_HERE, "..", "strategies", "v19", "v19_morning.py"))
_module, _scn, V19 = load_strategy(_V19_PY, base_cls=KernelStrategy)


def _make(*, alloc_policy: str = "", lot_size: str = "1", cash_safety_margin: str = "1.0"):
    s = V19(
        strategy_id="a0",
        instrument_id="A.TSE",
        alloc_policy=alloc_policy,
        lot_size=lot_size,
        cash_gate="1",
        cash_safety_margin=cash_safety_margin,
    )
    return s


def _set_prices(s, prices: dict) -> None:
    # _current_price reads _snapshots[iid][-1]["close"]; absent iid → None (NO_PRICE).
    s._snapshots = {iid: [{"close": float(px)}] for iid, px in prices.items() if px is not None}


def test_a0_equal_nominal_e1_matches_hand_computed_golden() -> None:
    # K=4, budget=1,000,000 → per_pick_budget=250,000, lot_size=100.
    #   A px=1000 (lot 100k): floor(250k/100k)=2 lots → 200sh, leftover +50k
    #   B px=3000 (lot 300k): 250k<300k → BELOW_1_LOT skip, +250k
    #   C px= 500 (lot  50k): floor(250k/50k)=5 lots → 500sh, leftover +0
    #   D px=None           : NO_PRICE skip, +250k
    # remainder after pass1 = 50k+250k+0+250k = 550k.
    # Pass 2 (+1 lot rank-order A,C): A 200→600 (+4 lots, -400k), C 500→800 (+6 lots, -300k),
    # remainder 550k→0. Final: A=600, C=800 (B,D dropped); notional 600k+400k=1,000,000=budget.
    s = _make(alloc_policy="A0_EQUAL_NOMINAL_E1", lot_size="100")
    _set_prices(s, {"A": 1000, "B": 3000, "C": 500, "D": None})
    out = s._alloc_a0_equal_nominal_e1(["A", "B", "C", "D"], budget=1_000_000.0, lot_size=100)
    assert out == [{"iid": "A", "shares": 600}, {"iid": "C", "shares": 800}]


def test_a0_empty_picks_returns_empty() -> None:
    s = _make(alloc_policy="A0_EQUAL_NOMINAL_E1", lot_size="100")
    assert s._alloc_a0_equal_nominal_e1([], budget=1_000_000.0, lot_size=100) == []


def test_a0_all_below_one_lot_returns_empty() -> None:
    # budget/K per pick smaller than a single lot → every pick BELOW_1_LOT, no redistribute.
    s = _make(alloc_policy="A0_EQUAL_NOMINAL_E1", lot_size="100")
    _set_prices(s, {"A": 1000, "B": 2000})
    # per_pick_budget = 50_000/2 = 25_000 < lot_value(100k/200k) → both skipped.
    assert s._alloc_a0_equal_nominal_e1(["A", "B"], budget=50_000.0, lot_size=100) == []


def test_gate_dispatches_to_a0_via_cash_aware_picks() -> None:
    # The whole gate path: cash_gate on, safety_margin 1.0, buying_power = budget → A0.
    s = _make(alloc_policy="A0_EQUAL_NOMINAL_E1", lot_size="100", cash_safety_margin="1.0")
    _set_prices(s, {"A": 1000, "B": 3000, "C": 500, "D": None})
    s.buying_power = lambda: 1_000_000.0  # shadow the seam for the budget read
    assert s._cash_aware_picks(["A", "B", "C", "D"]) == [
        {"iid": "A", "shares": 600},
        {"iid": "C", "shares": 800},
    ]


def test_v0_default_unchanged_when_no_policy() -> None:
    # alloc_policy "" → None → v0 cumulative-greedy (shares=order_qty=100, lot_size=1).
    s = _make(alloc_policy="", cash_safety_margin="1.0")
    _set_prices(s, {"A": 1000, "B": 2000, "C": 500})
    s.buying_power = lambda: 250_000.0
    # budget 250k: A 100*1000=100k (cum 100k), B 100*2000=200k → 300k>250k skip,
    # C 100*500=50k → 150k<=250k take. v0 keeps scanning past a skip (not a prefix cut).
    assert s._cash_aware_picks(["A", "B", "C"]) == [
        {"iid": "A", "shares": 100},
        {"iid": "C", "shares": 100},
    ]


def test_unknown_policy_falls_back_to_v0() -> None:
    s = _make(alloc_policy="BOGUS", cash_safety_margin="1.0")
    _set_prices(s, {"A": 1000})
    s.buying_power = lambda: 250_000.0
    # Unknown policy → WARNING + v0: A 100*1000=100k <= 250k → take.
    assert s._cash_aware_picks(["A"]) == [{"iid": "A", "shares": 100}]
