"""v19_core pure-helper unit gates (#76 S6b-α step2 / findings 0046 T8).

These pin the contracts the imperative ``V19MorningStrategy`` and the marimo cell-DAG port
share through ``strategies/v19/v19_core.py`` — the single source of v19's numeric logic.
The byte-parity gate (deterministic marimo↔imperative) relies on both paths calling THESE
functions, so the column-order / ordering / reset-key contracts are pinned here directly.
"""
from __future__ import annotations

import os
import sys
from datetime import datetime
from zoneinfo import ZoneInfo

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from strategies.v19 import v19_core  # noqa: E402

_JST = ZoneInfo("Asia/Tokyo")


def _ts(y: int, mo: int, d: int, hh: int, mm: int) -> int:
    """JST HH:MM bar-end label (:59.999999) → UTC ns (duckdb_bars convention)."""
    return int(datetime(y, mo, d, hh, mm, 59, 999999, tzinfo=_JST).timestamp() * 1_000_000_000)


def _snap(close: float) -> dict:
    return {"open": close, "high": close, "low": close, "close": close, "volume": 1000.0}


# ---------------------------------------------------------------- jst_day_minute (reset key)


def test_jst_day_minute_floors_minute_and_increments_day_across_boundary():
    # 10:00 JST → minute-of-day 600 (the entry key); :59.999999 must floor, not round up.
    day_d6, min_1000 = v19_core.jst_day_minute(_ts(2025, 1, 6, 10, 0))
    assert min_1000 == 600
    _, min_0955 = v19_core.jst_day_minute(_ts(2025, 1, 6, 9, 55))
    assert min_0955 == 595
    # The day-ordinal is the daily-reset key: it increments by exactly 1 across the JST date
    # boundary (so the marimo cell's reset fires on the same bar v19._reset_day does).
    day_d7, _ = v19_core.jst_day_minute(_ts(2025, 1, 7, 10, 0))
    assert day_d7 == day_d6 + 1


# ---------------------------------------------------------------- compute_features


def test_compute_features_requires_three_snaps_and_reports_price_at_decision():
    assert v19_core.compute_features([_snap(1.0), _snap(2.0)]) is None  # < 3 snaps
    feat = v19_core.compute_features([_snap(100.0), _snap(101.0), _snap(102.0)])
    assert feat is not None
    # The feature dict carries every model column, and price_at_decision is the last close.
    assert set(feat) == set(v19_core._FEATURES)
    assert feat["price_at_decision"] == 102.0


# ---------------------------------------------------------------- build_rows (ordering contract)


def test_build_rows_preserves_universe_order_skips_rsref_and_drops_thin():
    snapshots = {
        "A": [_snap(100.0 + i) for i in range(5)],
        "B": [_snap(90.0 + i) for i in range(5)],
        "D": [_snap(50.0), _snap(51.0)],  # < 3 snaps → features None → dropped
        "R": [_snap(200.0 + i) for i in range(5)],
    }
    rows = v19_core.build_rows(snapshots, ["A", "B", "D", "R"], "R")
    # Universe order preserved, rs-ref (R) skipped, thin instrument (D) dropped.
    assert list(rows.keys()) == ["A", "B"]


# ---------------------------------------------------------------- score_universe (column contract)


class _LastColModel:
    """Ranks by the LAST z-scored feature column (= price_at_decision) — the v19 replay stub."""

    def predict(self, X):
        return [float(row[-1]) for row in X]


def test_score_universe_empty_rows_and_column_order_contract():
    assert v19_core.score_universe({}, _LastColModel()) == {}
    snapshots = {
        "A": [_snap(100.0 + i) for i in range(5)],  # higher price_at_decision
        "B": [_snap(90.0 + i) for i in range(5)],
    }
    rows = v19_core.build_rows(snapshots, ["A", "B"], "R")
    scores = v19_core.score_universe(rows, _LastColModel())
    # The matrix is built as df[_FEATURES]; if the last column were not price_at_decision the
    # stub would rank differently. A's higher last close → higher z-score → higher stub score.
    assert scores["A"] > scores["B"]


# ---------------------------------------------------------------- cash_aware_picks (pure path)


def test_cash_aware_picks_gate_off_takes_every_pick_at_order_qty():
    out = v19_core.cash_aware_picks(
        ["A", "B"], cash_gate=False, order_qty=100, safety_margin=0.95,
        alloc_policy=None, lot_size=1, buying_power=0.0, prices={},
    )
    assert out == [{"iid": "A", "shares": 100}, {"iid": "B", "shares": 100}]


def test_cash_aware_picks_v0_cumulative_greedy_stops_at_budget():
    # budget = 15000; A notional 10000 fits (cum 10000), B notional 10000 would exceed → skip.
    out = v19_core.cash_aware_picks(
        ["A", "B"], cash_gate=True, order_qty=100, safety_margin=1.0,
        alloc_policy=None, lot_size=1, buying_power=15_000.0,
        prices={"A": 100.0, "B": 100.0},
    )
    assert out == [{"iid": "A", "shares": 100}]
