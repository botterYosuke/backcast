"""v19 marimo↔imperative deterministic parity gate (#76 S6b-α step2 step5 / findings 0046 T5/T7).

The required deliverable of S6b-α step2: the marimo cell-DAG port (v19_morning_cell.py) and
the REAL imperative ``V19MorningStrategy`` produce byte-identical order/fill/equity from the
SAME synthetic bars, the SAME stub scorer, and the SAME cash — mount-independent (no DuckDB,
no real model). Both inject the same stub model, so parity is structurally model-independent
(T5). The fixture spans two JST days so v19's daily reset (re-entry/re-exit) is exercised (T7),
buys multiple instruments (multi-iid routing), and the cash gate BITES (trims the 3rd pick) —
so the gate is not vacuous.
"""
from __future__ import annotations

import os
import sys
from datetime import datetime
from functools import partial
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

# 3 tradable instruments + an rs-ref (never traded). Base prices set the cross-sectional rank
# (9984 > 6758 > 7203 by price_at_decision) so the stub's top-k is unambiguous; the cash gate
# (¥35k) affords the top 2 but not the 3rd → a biting, multi-iid, non-vacuous fixture.
_UNIVERSE = ("7203.TSE", "6758.TSE", "9984.TSE", "1306.TSE")
_RS_REF = "1306.TSE"
_BASE = {"7203.TSE": 100.0, "6758.TSE": 120.0, "9984.TSE": 150.0, "1306.TSE": 200.0}
_INITIAL_CASH = 35_000.0


class _StubModel:
    """Deterministic scorer: rank by the row's price_at_decision (the last z-scored column)."""

    def predict(self, X):
        return [float(row[-1]) for row in X]


def _ts(y: int, mo: int, d: int, hh: int, mm: int) -> int:
    return int(datetime(y, mo, d, hh, mm, 59, 999999, tzinfo=_JST).timestamp() * 1_000_000_000)


def _bar(iid: str, ts: int, close: float) -> Bar:
    return Bar(iid, ts, open=close, high=close, low=close, close=close, volume=1000.0)


def _synthetic_bars():
    """Two JST business days × 4 instruments. Per day: five 09:5x snapshot bars, a 10:00
    entry bar, a 10:30 bar (must NOT re-enter), a 14:55 exit bar. Time-merged (canonical)."""
    bars = []
    for (y, m, d) in [(2025, 1, 6), (2025, 1, 7)]:
        for (hh, mm) in [(9, 55), (9, 56), (9, 57), (9, 58), (9, 59), (10, 0), (10, 30), (14, 55)]:
            for i, iid in enumerate(_UNIVERSE):
                px = _BASE[iid] + (hh * 60 + mm) * 0.01 + i
                bars.append(_bar(iid, _ts(y, m, d, hh, mm), px))
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


def _make_imperative(stub):
    """The REAL V19MorningStrategy with on_start stubbed (no artifact/model I/O) and the stub
    model injected — the parity oracle. Default ctor knobs (top_k=5, 10:00/14:55, order_qty=100,
    cash_gate, margin=0.95, lot=1) match the marimo cell's author constants."""
    strat = V19MorningStrategy(instrument_id=_UNIVERSE[0])

    def fake_on_start() -> None:
        strat._instruments = list(_UNIVERSE)
        strat._rs_ref = _RS_REF
        strat._adv_baseline = {}
        strat._prev_close = {}
        strat._model = stub

    strat.on_start = fake_on_start  # type: ignore[method-assign]
    return strat


def _make_marimo(stub):
    """The marimo cell-DAG port, with the SAME stub bound into the score_v19_rows service and
    the ordered universe / rs-ref as host constants — the public ctor seam (step3)."""
    return MarimoStrategy(
        app=load_app(_CELL),
        strategy_id="strat-marimo",
        instrument_id=_UNIVERSE[0],
        services={"score_v19_rows": partial(v19_core.score_universe, model=stub)},
        constants={"UNIVERSE": _UNIVERSE, "RS_REF": _RS_REF},
    )


def test_v19_marimo_parity_deterministic(monkeypatch):
    # Same stub instance feeds both twins → parity is model-independent (mount-free).
    stub = _StubModel()
    imp_result, imp_sink = _run(_make_imperative(stub), monkeypatch)

    marimo_strat = _make_marimo(stub)
    mar_result, mar_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()

    # ---- fixture guards (non-vacuous): multi-iid, daily reset, biting cash gate ----
    buys = [f for f in imp_sink.fills if f[1] is OrderSide.BUY]
    sells = [f for f in imp_sink.fills if f[1] is OrderSide.SELL]
    # 2 days × 2 affordable picks = 4 BUYs (the 3rd pick is trimmed by the ¥35k cash gate),
    # each flattened at 14:55 = 4 SELLs (daily round-trips, re-entry proves the reset).
    assert len(buys) == 4, imp_sink.fills
    assert len(sells) == 4, imp_sink.fills
    bought = {f[0] for f in buys}
    # Top-2 by price are 9984 then 6758; 7203 is trimmed (cash bite), 1306 is rs-ref (never).
    assert bought == {"9984.TSE", "6758.TSE"}, bought
    assert imp_result.fills == 8

    # ---- the parity claim: marimo == imperative, order/fill/equity ----
    assert mar_sink.fills == imp_sink.fills
    assert mar_sink.equities == imp_sink.equities
    assert (mar_result.fills, mar_result.final_cash, mar_result.realized_pnl) == (
        imp_result.fills,
        imp_result.final_cash,
        imp_result.realized_pnl,
    )
    # Book is flat at the end of each day → realized P&L is the whole cash delta.
    assert mar_result.realized_pnl == pytest.approx(mar_result.final_cash - _INITIAL_CASH)
