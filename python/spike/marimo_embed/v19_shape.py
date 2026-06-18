"""Spike (#76 S6b driver-shape) — v19-shape multi-instrument marimo parity.

THROWAWAY. Validates the owner's Option-1 hypothesis (findings 0046 grill 2026-06-18):
v19's cross-sectional ML ranker can be authored in marimo WITHOUT a new host-owned
multi-instrument bar driver. The multi-instrument-ness lives entirely in:

  - SUBMISSION:  ``submit_market(qty, instrument_id=iid)`` (S4 Q2 — already supported)
  - HISTORY:     a strategy-owned ``mo.state`` feedback dict ``snaps[iid] -> [closes]``
                 accumulated per single ``get_bar()`` (D4 self-cycle)
  - CROSS-SECTION at decision time: read the accumulated feedback dict (every instrument
                 has streamed its morning by entry time) — no live cross-section read
  - EXIT positions: ``get_portfolio().positions`` (portfolio-driver slice)

Three risks the owner named, killed here through the REAL ``KernelRunner`` + ``MarimoStrategy``
adapter (same recipe as tests/test_marimo_strategy_adapter.py):

  1. the feedback dict persists + updates every bar in the thin-drain hot path
  2. at entry, every instrument's pre-entry snapshot is present AND the entry bar is NOT
     appended (matches imperative v19, which only appends when minute < entry_minute)
  3. multi-iid submission parity (order/fill/equity) vs the imperative twin

Run:  uv run --group spike python -m spike.marimo_embed.v19_shape
"""
from __future__ import annotations

import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from marimo._ast.load import load_app

import engine.kernel.runner as runner_mod
from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.runner import KernelRunner
from engine.kernel.strategy import Strategy
from engine.strategy_runtime.marimo_strategy import MarimoStrategy

# A 3-instrument universe with a clean morning → entry → exit shape. Minute is encoded in
# ts_event_ns (minute = ts // 1000); within a minute the three instruments stream A, B, C.
# Closes are arranged so the rank by last-pre-entry close is unambiguous: A > B > C, so
# top_k=2 always picks [A, B] (C is never bought) — keeps parity crisp (no tie-break path).
UNIVERSE = ["A.T", "B.T", "C.T"]
PRE_ENTRY_MINUTES = (0, 1, 2)
ENTRY_MINUTE = 3
EXIT_MINUTE = 5
LAST_MINUTE = 6
TOP_K = 2
ORDER_QTY = 10.0

# per-(minute, iid) close. A highest, B middle, C lowest, drifting up over the morning.
_CLOSE = {
    "A.T": {0: 1000.0, 1: 1002.0, 2: 1005.0, 3: 1006.0, 4: 1007.0, 5: 1008.0, 6: 1009.0},
    "B.T": {0: 900.0, 1: 901.0, 2: 902.0, 3: 903.0, 4: 904.0, 5: 905.0, 6: 906.0},
    "C.T": {0: 800.0, 1: 801.0, 2: 802.0, 3: 803.0, 4: 804.0, 5: 805.0, 6: 806.0},
}


def _bars():
    bars = []
    for minute in range(LAST_MINUTE + 1):
        for idx, iid in enumerate(UNIVERSE):
            c = _CLOSE[iid][minute]
            ts = minute * 1000 + idx  # strictly increasing across the flat list
            bars.append(
                Bar(instrument_id=iid, ts_event_ns=ts, open=c, high=c, low=c, close=c, volume=100.0)
            )
    return bars


# ---------------------------------------------------------------- imperative twin (oracle)


class _V19ShapeTwin(Strategy):
    """The cross-sectional shape of v19 in the imperative contract: accumulate per-instrument
    morning closes, rank at entry, BUY top-k, flatten at exit. The parity oracle."""

    def __init__(self, **kw):
        super().__init__(**kw)
        self._snaps: dict[str, list[float]] = {}
        self._placed = False
        self._exited = False

    def on_bar(self, bar) -> None:
        minute = bar.ts_event_ns // 1000
        if (not self._placed) and minute >= ENTRY_MINUTE and minute < EXIT_MINUTE:
            scored = {iid: closes[-1] for iid, closes in self._snaps.items() if closes}
            top = sorted(scored, key=lambda k: scored[k], reverse=True)[:TOP_K]
            for iid in top:
                self.submit_market(iid, OrderSide.BUY, ORDER_QTY)
            self._placed = True
            return
        if self._placed and (not self._exited) and minute >= EXIT_MINUTE:
            held = {iid: q for iid, q in self.portfolio_snapshot().positions.items() if q > 0}
            for iid, qty in held.items():
                self.submit_market(iid, OrderSide.SELL, qty)
            self._exited = True
            return
        if (not self._placed) and minute < ENTRY_MINUTE:
            self._snaps.setdefault(bar.instrument_id, []).append(bar.close)


# ---------------------------------------------------------------- marimo twin (Option 1)

_MARIMO_SRC = f"""
import marimo

app = marimo.App()


@app.cell
def _state():
    import marimo as mo
    get_snaps, set_snaps = mo.state({{}})       # iid -> [closes], strategy-owned feedback
    get_placed, set_placed = mo.state(False)
    get_exited, set_exited = mo.state(False)
    return get_snaps, set_snaps, get_placed, set_placed, get_exited, set_exited


@app.cell
def _accumulate(get_snaps, set_snaps):
    bar = get_bar()  # noqa: F821  host-seeded driver
    minute = bar.ts_event_ns // 1000
    snaps = get_snaps()
    if minute < {ENTRY_MINUTE}:
        nxt = {{k: list(v) for k, v in snaps.items()}}
        nxt[bar.instrument_id] = nxt.get(bar.instrument_id, []) + [bar.close]
        set_snaps(nxt)
    return


@app.cell
def _decide(get_snaps, get_placed, set_placed, get_exited, set_exited):
    bar = get_bar()       # noqa: F821
    pf = get_portfolio()  # noqa: F821
    minute = bar.ts_event_ns // 1000
    snaps = get_snaps()
    placed = get_placed()
    exited = get_exited()
    if (not placed) and minute >= {ENTRY_MINUTE} and minute < {EXIT_MINUTE}:
        scored = {{iid: closes[-1] for iid, closes in snaps.items() if closes}}
        top = sorted(scored, key=lambda k: scored[k], reverse=True)[:{TOP_K}]
        for iid in top:
            submit_market({ORDER_QTY}, instrument_id=iid)  # noqa: F821
        set_placed(True)
    elif placed and (not exited) and minute >= {EXIT_MINUTE}:
        for iid, qty in pf.positions.items():
            if qty > 0:
                submit_market(-qty, instrument_id=iid)  # noqa: F821
        set_exited(True)
    return
"""


# ---------------------------------------------------------------- run harness (real runner)


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


def _run(strategy):
    sink = _RecSink()
    orig = runner_mod.load_universe_bars
    runner_mod.load_universe_bars = lambda *a, **k: _bars()
    try:
        result = KernelRunner(
            data_root="/unused",
            instrument_ids=list(UNIVERSE),
            start="2024-01-01",
            end="2024-12-31",
            initial_cash=10_000_000.0,
            strategy=strategy,
            sink=sink,
        ).run()
    finally:
        runner_mod.load_universe_bars = orig
    return result, sink


def main():
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "v19_shape.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(_MARIMO_SRC)

        marimo_strat = MarimoStrategy(
            app=load_app(path), strategy_id="strat-marimo", instrument_id="A.T"
        )
        twin = _V19ShapeTwin(strategy_id="strat-imp", instrument_id="A.T")

        m_result, m_sink = _run(marimo_strat)
        marimo_strat.close()
        t_result, t_sink = _run(twin)

    # fixture guard: must place real BUYs (entry top-2) AND SELLs (exit flatten), bounded
    sides = {o[1] for o in t_sink.fills}
    assert sides == {OrderSide.BUY, OrderSide.SELL}, f"fixture weak: sides={sides}"
    buys = [o for o in t_sink.fills if o[1] is OrderSide.BUY]
    sells = [o for o in t_sink.fills if o[1] is OrderSide.SELL]
    assert len(buys) == TOP_K, f"expected {TOP_K} entry BUYs, got {len(buys)}"
    assert len(sells) == TOP_K, f"expected {TOP_K} exit SELLs, got {len(sells)}"
    # the bought iids are the rank top-2 = A, B (C never bought)
    bought_iids = {o[0] for o in buys}
    assert bought_iids == {"A.T", "B.T"}, f"expected top-2 [A,B], got {bought_iids}"

    print("imperative twin fills:")
    for o in t_sink.fills:
        print("   ", o)
    print("marimo      fills:")
    for o in m_sink.fills:
        print("   ", o)

    assert m_sink.fills == t_sink.fills, "ORDER/FILL PARITY FAILED"
    assert m_sink.equities == t_sink.equities, "EQUITY PARITY FAILED"
    assert (m_result.fills, m_result.final_cash, m_result.realized_pnl) == (
        t_result.fills,
        t_result.final_cash,
        t_result.realized_pnl,
    ), "RESULT PARITY FAILED"

    print()
    print(f"fills={m_result.fills} final_cash={m_result.final_cash} realized={m_result.realized_pnl}")
    print("[V19-SHAPE PASS] multi-iid marimo == imperative twin (order/fill/equity)")


if __name__ == "__main__":
    main()
