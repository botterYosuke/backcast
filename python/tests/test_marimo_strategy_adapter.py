"""S6a MarimoStrategy adapter — production-binding parity + teardown (#76 / findings 0046).

These gates drive the REAL ``KernelRunner`` + ``_Context`` + fill path (bars monkeypatched
so no DuckDB mount is needed, the same recipe as test_kernel_buying_power_seam). They prove
the marimo cell-DAG, loaded from a ``.py`` and driven through the adapter, produces the same
order/fill/equity sequence as the imperative on_bar twin — i.e. the adapter is the immutable
kernel contract's adaptation boundary (ADR-0012 Decision 2), KernelRunner unchanged.

This is the production-binding parity gate (S6a): unlike test_strategy_runtime_thin_drain's
order-parity (fake StrategyContext, mechanism unit), it runs the actual runtime seam.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from marimo._ast.load import load_app  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402

pytestmark = pytest.mark.marimo

IID = "7203.T"

# A host-seed marimo strategy: reads the bar as a free ref, signs the order by band.
_MARIMO_SRC = """
import marimo

app = marimo.App()


@app.cell
def _signal():
    bar = get_bar()  # noqa: F821  host-seeded driver
    signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
    qty = signal * 10.0
    submit_market(qty)  # noqa: F821  S4 injected signed-qty adapter
    return (qty,)
"""


class _ImperativeTwin(Strategy):
    """Same band logic, expressed in the imperative contract (positive qty + side)."""

    def on_bar(self, bar) -> None:
        signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
        qty = signal * 10.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)
        elif qty < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(qty))


class _RecSink:
    """Records the per-fill order line + the run's equity track for parity comparison."""

    def __init__(self) -> None:
        self.fills: list[tuple] = []
        self.equities: list[tuple] = []

    def push_bar(self, bar) -> None:
        pass

    def push_order(self, fill) -> None:
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))

    def push_portfolio(self, pf) -> None:
        pass

    def on_equity(self, ts_ms: int, equity: float, cash: float) -> None:
        self.equities.append((equity, cash))

    def push_run_complete(self, run_id, summary) -> None:
        pass


def _bars():
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(300)]
    closes = [
        980.0 if i % 5 == 0 else (1000.0 if i % 5 == 1 else c) for i, c in enumerate(closes)
    ]
    return [
        Bar(instrument_id=IID, ts_event_ns=1_000 + i, open=c, high=c, low=c, close=c, volume=100.0)
        for i, c in enumerate(closes)
    ]


def _run(strategy, monkeypatch):
    sink = _RecSink()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: _bars())
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=[IID],
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=10_000_000.0,
        strategy=strategy,
        sink=sink,
    ).run()
    return result, sink


def test_marimo_adapter_order_fill_parity_with_imperative_twin(tmp_path, monkeypatch):
    path = tmp_path / "strat.py"
    path.write_text(_MARIMO_SRC, encoding="utf-8")

    marimo_strat = MarimoStrategy(
        app=load_app(str(path)), strategy_id="strat-marimo", instrument_id=IID
    )
    twin = _ImperativeTwin(strategy_id="strat-imp", instrument_id=IID)

    m_result, m_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()  # release the headless kernel (the dispatch site's finally owns this)
    t_result, t_sink = _run(twin, monkeypatch)

    # guard the fixture: the series must place real orders on both sides
    sides = {o[1] for o in t_sink.fills}
    assert sides == {OrderSide.BUY, OrderSide.SELL} and 0 < len(t_sink.fills) < 300

    assert m_sink.fills == t_sink.fills
    assert m_sink.equities == t_sink.equities
    assert (m_result.fills, m_result.final_cash, m_result.realized_pnl) == (
        t_result.fills,
        t_result.final_cash,
        t_result.realized_pnl,
    )


def test_marimo_adapter_teardown_allows_a_second_run(tmp_path, monkeypatch):
    """The adapter owns the headless-kernel lifetime: after close() a second run stands up a
    fresh kernel (no 'RuntimeContext already initialized'). The dispatch site calls close()."""
    path = tmp_path / "strat.py"
    path.write_text(_MARIMO_SRC, encoding="utf-8")

    for _ in range(2):
        strat = MarimoStrategy(app=load_app(str(path)), strategy_id="s", instrument_id=IID)
        result, _sink = _run(strat, monkeypatch)
        strat.close()
        assert result.success


# --------------------------------------------------------- S6a dispatch routing (helper)

import json  # noqa: E402

from engine._backend_impl import _select_replay_strategy  # noqa: E402

_IMPERATIVE_SRC = """
from engine.kernel.strategy import Strategy


class ImpStrat(Strategy):
    def on_bar(self, bar):
        pass
"""


def _write_sidecar(path):
    path.with_name(path.stem + ".json").write_text(
        json.dumps(
            {
                "scenario": {
                    "schema_version": 3,
                    "instruments": [IID],
                    "start": "2024-01-01",
                    "end": "2024-12-31",
                    "granularity": "Daily",
                    "initial_cash": 1_000_000,
                }
            }
        ),
        encoding="utf-8",
    )


def test_dispatch_routes_marimo_file_to_adapter(tmp_path):
    py = tmp_path / "strat.py"
    py.write_text(_MARIMO_SRC, encoding="utf-8")
    _write_sidecar(py)

    # Pass a STR (production gives a str via cfg.get) — guards the str→Path coercion in the
    # marimo branch (load_scenario/load_app need a Path).
    scenario, factory, label = _select_replay_strategy(str(py))
    assert label == "marimo:strat"
    assert scenario["instruments"] == [IID]
    assert isinstance(factory(IID), MarimoStrategy)


def test_dispatch_routes_imperative_file_to_loader(tmp_path):
    py = tmp_path / "imp.py"
    py.write_text(_IMPERATIVE_SRC, encoding="utf-8")
    _write_sidecar(py)

    scenario, factory, label = _select_replay_strategy(py)
    assert label == "ImpStrat"
    strat = factory(IID)
    assert isinstance(strat, Strategy) and not isinstance(strat, MarimoStrategy)
