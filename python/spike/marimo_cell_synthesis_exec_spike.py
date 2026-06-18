"""THROWAWAY spike (#81 Slice 1) — R2: does generate_filecontents OUTPUT execute?

The synthesis spike proved form/idempotency, but it surfaced a twist: generate_filecontents
promotes host-seeded free refs (get_bar/submit_market) into cell ARGS (`def _(get_bar):`),
whereas backcast's existing prod form keeps them as free refs. This spike proves the new
arg-form .py still runs under backcast's host-seeding (KernelRunner + MarimoStrategy adapter)
and produces order-for-order parity with the imperative twin — mirroring the production
parity gate test_marimo_strategy_adapter.py, but with a generate_filecontents-built .py
that ALSO splits the logic across two cells (a real cross-cell DAG edge: qty).

Run: uv run --group spike python -m spike.marimo_cell_synthesis_exec_spike
"""
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from marimo._ast.cell import CellConfig
from marimo._ast.codegen import generate_filecontents
from marimo._ast.load import load_app

import engine.kernel.runner as runner_mod
from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.runner import KernelRunner
from engine.kernel.strategy import Strategy
from engine.strategy_runtime.marimo_strategy import MarimoStrategy

IID = "7203.T"

# Two raw cell bodies (as two C# windows would hold them). Cell 2 depends on `qty` from cell 1
# (cross-cell DAG edge). get_bar/submit_market are host APIs -> generate_filecontents makes them args.
BODIES = [
    "bar = get_bar()  # noqa: F821\n"
    "qty = (1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)) * 10.0",
    "submit_market(qty)  # noqa: F821",
]


class _ImperativeTwin(Strategy):
    def on_bar(self, bar) -> None:
        qty = (1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)) * 10.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)
        elif qty < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(qty))


class _RecSink:
    def __init__(self):
        self.fills = []; self.equities = []
    def push_bar(self, b): pass
    def push_order(self, f): self.fills.append((f.instrument_id, f.side, f.last_qty, f.last_px))
    def push_portfolio(self, p): pass
    def on_equity(self, ts, eq, cash): self.equities.append((eq, cash))
    def push_run_complete(self, r, s): pass


def _bars():
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(300)]
    closes = [980.0 if i % 5 == 0 else (1000.0 if i % 5 == 1 else c) for i, c in enumerate(closes)]
    return [Bar(instrument_id=IID, ts_event_ns=1_000 + i, open=c, high=c, low=c, close=c, volume=100.0)
            for i, c in enumerate(closes)]


def _run(strategy):
    sink = _RecSink()
    orig = runner_mod.load_universe_bars
    runner_mod.load_universe_bars = lambda *a, **k: _bars()
    try:
        result = KernelRunner(data_root="/unused", instrument_ids=[IID], start="2024-01-01",
                              end="2024-12-31", initial_cash=10_000_000.0,
                              strategy=strategy, sink=sink).run()
    finally:
        runner_mod.load_universe_bars = orig
    return result, sink


def main():
    py = generate_filecontents(codes=list(BODIES), names=["_", "_"],
                               cell_configs=[CellConfig(), CellConfig()], config=None)
    print("===== generated .py (2-cell, host APIs as args) =====")
    print(py)
    print("=====================================================")

    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "strat.py"
        p.write_text(py, encoding="utf-8")
        marimo_strat = MarimoStrategy(app=load_app(str(p)), strategy_id="m", instrument_id=IID)
        m_result, m_sink = _run(marimo_strat)
        marimo_strat.close()

    t_result, t_sink = _run(_ImperativeTwin(strategy_id="t", instrument_id=IID))

    sides = {o[1] for o in t_sink.fills}
    fixture_ok = sides == {OrderSide.BUY, OrderSide.SELL} and 0 < len(t_sink.fills) < 300
    fills_match = m_sink.fills == t_sink.fills
    eq_match = m_sink.equities == t_sink.equities
    res_match = (m_result.fills, m_result.final_cash, m_result.realized_pnl) == \
                (t_result.fills, t_result.final_cash, t_result.realized_pnl)

    print(f"[CHK fixture places both-side orders]: {'PASS' if fixture_ok else 'FAIL'} "
          f"({len(t_sink.fills)} fills)")
    print(f"[CHK marimo(generated) fills == imperative twin]: {'PASS' if fills_match else 'FAIL'}")
    print(f"[CHK equities match]: {'PASS' if eq_match else 'FAIL'}")
    print(f"[CHK result tuple match]: {'PASS' if res_match else 'FAIL'}")
    ok = fixture_ok and fills_match and eq_match and res_match
    print(f"\n[SPIKE #81 exec] {'ALL PASS - generate_filecontents output runs under host-seeding' if ok else 'FAIL'}")


if __name__ == "__main__":
    main()
