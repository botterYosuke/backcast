"""v19 marimo↔imperative deterministic parity gate (#76 S6b-α step2 / findings 0046 T5/T7/R2).

The required deliverable of S6b-α step2: the marimo cell-DAG port (v19_morning_cell.py) and
the REAL imperative ``V19MorningStrategy`` produce byte-identical order/fill/equity from the
SAME synthetic bars, the SAME stub scorer, the SAME cash, and the SAME artifacts (universe /
adv_baseline / prev_close) — mount-independent (no DuckDB, no real model). Both inject the same
stub, so parity is model-independent (T5). The fixture spans two JST days so v19's daily reset
is exercised (T7), buys multiple instruments (multi-iid), and the cash gate BITES (trims the
3rd pick).

NON-VACUOUS w.r.t. adv/prev_close (R2): the stub ranks by the **gap** feature (a function of
prev_close), and the gap-rank is deliberately NOT the universe order — so if the marimo cell
failed to thread prev_close into build_rows, the picks would change and parity would break. A
sensitivity litmus proves it (imperative with empty prev_close picks a different set), and a
feature guard proves rel_turnover (adv) and gap (prev_close) are non-zero.
"""
from __future__ import annotations

import os
import sys
from datetime import datetime
from functools import partial
from types import MappingProxyType
from zoneinfo import ZoneInfo

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from marimo._ast.load import load_app  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar, merge_bars_by_ts  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402
from strategies.v19 import v19_core  # noqa: E402
from strategies.v19.v19_morning import V19MorningStrategy  # noqa: E402

pytestmark = pytest.mark.marimo

_JST = ZoneInfo("Asia/Tokyo")
_CELL = os.path.join(_PY, "strategies", "v19", "v19_morning_cell.py")

# Universe ORDER (7203, 6758, 9984) + rs-ref (1306, never traded). The gap-rank below is
# 9984 > 7203 > 6758 — deliberately NOT the universe order, so a dropped prev_close (gap→0,
# ties broken by universe order) changes the picks.
_UNIVERSE = ("7203.TSE", "6758.TSE", "9984.TSE", "1306.TSE")
_RS_REF = "1306.TSE"
_IDX = {iid: i for i, iid in enumerate(_UNIVERSE)}
_INITIAL_CASH = 260_000.0

# Morning closes ≈ 1000 (so the three picks have near-equal notionals → a clean cash gate),
# with a tiny per-instrument offset for non-degenerate features and a +0.01/min drift.
_OPEN_MIN = 9 * 60 + 55  # 09:55 JST


def _close(iid: str, minute: int) -> float:
    return 1000.0 + _IDX[iid] * 1.0 + (minute - _OPEN_MIN) * 0.01


# prev_close chosen so gap = o0/prev_close - 1 ranks 9984(0.10) > 7203(0.05) > 6758(0.01).
# o0 = the 09:55 close = 1000 + idx.
_GAP_TARGET = {"9984.TSE": 0.10, "7203.TSE": 0.05, "6758.TSE": 0.01}
_PREV_CLOSE = MappingProxyType(
    {iid: (1000.0 + _IDX[iid]) / (1.0 + g) for iid, g in _GAP_TARGET.items()}
)
# Non-zero ADV so rel_turnover is a real number (does not affect the gap-rank).
_ADV_BASELINE = MappingProxyType({iid: 1_000_000.0 for iid in _GAP_TARGET})

_GAP_COL = v19_core._FEATURES.index("gap")  # the stub ranks by this z-scored column


class _StubModel:
    """Deterministic scorer: rank by the z-scored ``gap`` column (a function of prev_close).

    gap depends on prev_close, so this makes parity SENSITIVE to whether prev_close was
    threaded into the features on both paths."""

    def predict(self, X):
        return [float(row[_GAP_COL]) for row in X]


def _ts(y: int, mo: int, d: int, hh: int, mm: int) -> int:
    return int(datetime(y, mo, d, hh, mm, 59, 999999, tzinfo=_JST).timestamp() * 1_000_000_000)


def _bar(iid: str, ts: int, close: float) -> Bar:
    return Bar(iid, ts, open=close, high=close, low=close, close=close, volume=1000.0)


def _synthetic_bars():
    bars = []
    for (y, m, d) in [(2025, 1, 6), (2025, 1, 7)]:
        for (hh, mm) in [(9, 55), (9, 56), (9, 57), (9, 58), (9, 59), (10, 0), (10, 30), (14, 55)]:
            minute = hh * 60 + mm
            for iid in _UNIVERSE:
                bars.append(_bar(iid, _ts(y, m, d, hh, mm), _close(iid, minute)))
    return merge_bars_by_ts([bars])


class _RecSink:
    def __init__(self):
        self.fills = []
        self.equities = []

    def push_bar(self, bar):
        pass

    def push_order(self, fill):
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))

    def push_portfolio(self, pf):
        pass

    def on_equity(self, ts_ms, equity, cash):
        self.equities.append((equity, cash))

    def push_run_complete(self, run_id, summary):
        pass


def _run(strategy, monkeypatch):
    sink = _RecSink()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(_synthetic_bars()))
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=list(_UNIVERSE),
        granularity="Minute",
        start="2025-01-06",
        end="2025-01-07",
        initial_cash=_INITIAL_CASH,
        strategy=strategy,
        sink=sink,
    ).run()
    return result, sink


def _make_imperative(stub, *, adv=_ADV_BASELINE, prev_close=_PREV_CLOSE):
    """The REAL V19MorningStrategy with on_start stubbed (no artifact/model I/O) and the stub
    model + artifacts injected — the parity oracle. Default ctor knobs match the cell's."""
    strat = V19MorningStrategy(instrument_id=_UNIVERSE[0])

    def fake_on_start() -> None:
        strat._instruments = list(_UNIVERSE)
        strat._rs_ref = _RS_REF
        strat._adv_baseline = dict(adv)
        strat._prev_close = dict(prev_close)
        strat._model = stub

    strat.on_start = fake_on_start  # type: ignore[method-assign]
    return strat


def _make_marimo(stub):
    """The marimo cell-DAG port — SAME stub in the score_v19_rows service, SAME ordered universe
    / rs-ref / adv / prev_close as host constants (the public ctor seam, step3 + R2)."""
    return MarimoStrategy(
        app=load_app(_CELL),
        strategy_id="strat-marimo",
        instrument_id=_UNIVERSE[0],
        services={"score_v19_rows": partial(v19_core.score_universe, model=stub)},
        constants={
            "V19_UNIVERSE": _UNIVERSE,
            "V19_RS_REF": _RS_REF,
            "V19_ADV_BASELINE": _ADV_BASELINE,
            "V19_PREV_CLOSE": _PREV_CLOSE,
        },
    )


def _bought(sink):
    return {f[0] for f in sink.fills if f[1] is OrderSide.BUY}


def test_v19_marimo_parity_deterministic(monkeypatch):
    stub = _StubModel()
    imp_result, imp_sink = _run(_make_imperative(stub), monkeypatch)

    marimo_strat = _make_marimo(stub)
    mar_result, mar_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()

    # ---- fixture guards (non-vacuous): multi-iid, daily reset, biting cash gate ----
    buys = [f for f in imp_sink.fills if f[1] is OrderSide.BUY]
    sells = [f for f in imp_sink.fills if f[1] is OrderSide.SELL]
    # 2 days × 2 affordable gap-ranked picks = 4 BUYs (the 3rd is trimmed by the ¥260k cash
    # gate), each flattened at 14:55 = 4 SELLs (daily round-trips prove the reset).
    assert len(buys) == 4, imp_sink.fills
    assert len(sells) == 4, imp_sink.fills
    # gap-rank top-2 = 9984, 7203; 6758 is trimmed (cash bite), 1306 is rs-ref (never traded).
    assert _bought(imp_sink) == {"9984.TSE", "7203.TSE"}, _bought(imp_sink)
    assert imp_result.fills == 8

    # ---- the parity claim: marimo == imperative, order/fill/equity ----
    assert mar_sink.fills == imp_sink.fills
    assert mar_sink.equities == imp_sink.equities
    assert (mar_result.fills, mar_result.final_cash, mar_result.realized_pnl) == (
        imp_result.fills,
        imp_result.final_cash,
        imp_result.realized_pnl,
    )
    assert mar_result.realized_pnl == pytest.approx(mar_result.final_cash - _INITIAL_CASH)


def test_prev_close_changes_the_picks_so_the_parity_is_not_vacuous(monkeypatch):
    """Sensitivity litmus (delete-the-production-logic): with prev_close the gap-rank buys
    {9984, 7203}; with EMPTY prev_close (gap→0, ties broken by universe order) the imperative
    twin buys a DIFFERENT set. So prev_close demonstrably drives the outcome — the parity gate
    above is not vacuous w.r.t. the R2 adv/prev_close threading."""
    stub = _StubModel()
    _r1, with_pc = _run(_make_imperative(stub), monkeypatch)
    _r2, no_pc = _run(_make_imperative(stub, prev_close={}), monkeypatch)
    assert _bought(with_pc) == {"9984.TSE", "7203.TSE"}
    assert _bought(no_pc) != _bought(with_pc), _bought(no_pc)


def test_adv_and_prev_close_reach_nonzero_features():
    """Feature guard: the injected adv_baseline / prev_close actually flow into build_rows and
    produce non-zero rel_turnover / gap (so the constants are not silently dropped)."""
    snaps = {
        iid: [
            {"open": _close(iid, m), "high": _close(iid, m), "low": _close(iid, m),
             "close": _close(iid, m), "volume": 1000.0}
            for m in range(_OPEN_MIN, _OPEN_MIN + 5)
        ]
        for iid in _UNIVERSE
    }
    rows = v19_core.build_rows(
        snaps, _UNIVERSE, _RS_REF, adv_baseline=_ADV_BASELINE, prev_close=_PREV_CLOSE
    )
    assert rows["9984.TSE"]["rel_turnover"] != 0.0
    assert rows["9984.TSE"]["gap"] != 0.0
