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


def _run(strategy, monkeypatch, initial_cash=10_000_000.0):
    sink = _RecSink()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: _bars())
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=[IID],
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=initial_cash,
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


# ----------------------------------------- portfolio-driver target-position parity (P3)

_MARIMO_PF_SRC = """
import marimo

app = marimo.App()


@app.cell
def _rebal():
    bar = get_bar()               # noqa: F821  host-seeded bar driver
    pf = get_portfolio()          # noqa: F821  host-seeded portfolio driver
    target = 10.0 if bar.close > 1010.0 else (-10.0 if bar.close < 990.0 else 0.0)
    delta = target - pf.position  # delta to reach target off the PRE-FILL position
    submit_market(delta)          # noqa: F821  S4 injected signed-qty adapter
    return (delta,)
"""


class _TargetPositionTwin(Strategy):
    """Imperative twin: same target bands, sizes the delta off the same pre-fill net position
    (read through the new ctx.portfolio_snapshot seam) — the no-look-ahead parity oracle."""

    def on_bar(self, bar) -> None:
        target = 10.0 if bar.close > 1010.0 else (-10.0 if bar.close < 990.0 else 0.0)
        delta = target - self.portfolio_snapshot().position
        if delta > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, delta)
        elif delta < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(delta))


def test_marimo_get_portfolio_target_position_parity(tmp_path, monkeypatch):
    """A marimo strategy reading get_portfolio().position to size a target-position delta
    matches the imperative twin order-for-order. If get_portfolio leaked the POST-fill
    position (look-ahead), the marimo delta would diverge after the first fill — so this is
    also the no-look-ahead gate (snapshot captured at on_bar entry = end-of-prev-bar)."""
    path = tmp_path / "strat_pf.py"
    path.write_text(_MARIMO_PF_SRC, encoding="utf-8")

    marimo_strat = MarimoStrategy(
        app=load_app(str(path)), strategy_id="strat-marimo", instrument_id=IID
    )
    twin = _TargetPositionTwin(strategy_id="strat-imp", instrument_id=IID)

    m_result, m_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()
    t_result, t_sink = _run(twin, monkeypatch)

    # guard the fixture: a target-position strategy must place real BUYs and SELLs, bounded
    sides = {o[1] for o in t_sink.fills}
    assert sides == {OrderSide.BUY, OrderSide.SELL} and 0 < len(t_sink.fills) < 300

    assert m_sink.fills == t_sink.fills
    assert m_sink.equities == t_sink.equities
    assert (m_result.fills, m_result.final_cash, m_result.realized_pnl) == (
        t_result.fills,
        t_result.final_cash,
        t_result.realized_pnl,
    )


# ----------------------------------------- buying_power cash-aware sizing parity (S6b-α)

_MARIMO_BP_SRC = """
import marimo

app = marimo.App()


@app.cell
def _buy_while_affordable():
    bar = get_bar()       # noqa: F821  host-seeded bar driver
    pf = get_portfolio()  # noqa: F821  host-seeded portfolio driver
    # v19-style cash-aware sizing: buy 1 share whenever the PRE-FILL buying power covers it.
    # buying_power shrinks as cash is spent, so this BLOCKS once the book is broke — proving
    # get_portfolio().buying_power flows to the cell as the same pre-fill value the imperative
    # self.buying_power() reads.
    qty = 1.0 if pf.buying_power >= bar.close else 0.0
    submit_market(qty)    # noqa: F821
    return (qty,)
"""


class _BuyingPowerTwin(Strategy):
    """Imperative twin: same affordability gate via self.buying_power() (the seam v19's
    _cash_aware_picks reads)."""

    def on_bar(self, bar) -> None:
        if self.buying_power() >= bar.close:
            self.submit_market(self.instrument_id, OrderSide.BUY, 1.0)


def test_marimo_buying_power_cash_aware_sizing_parity(tmp_path, monkeypatch):
    """A marimo cell sizing off get_portfolio().buying_power matches the imperative twin that
    reads self.buying_power(). Small initial_cash makes the gate BITE (buying stops once broke,
    not a vacuous always-affordable gate) — so this pins that buying_power is the same pre-fill
    value on both paths (#76 S6b-α)."""
    path = tmp_path / "strat_bp.py"
    path.write_text(_MARIMO_BP_SRC, encoding="utf-8")

    marimo_strat = MarimoStrategy(
        app=load_app(str(path)), strategy_id="strat-marimo", instrument_id=IID
    )
    twin = _BuyingPowerTwin(strategy_id="strat-imp", instrument_id=IID)

    m_result, m_sink = _run(marimo_strat, monkeypatch, initial_cash=5_000.0)
    marimo_strat.close()
    t_result, t_sink = _run(twin, monkeypatch, initial_cash=5_000.0)

    # fixture guard: buying_power must actually BITE — a handful of BUYs then a block (the
    # strategy keeps wanting to buy every bar, but runs out of cash), never every bar.
    assert all(o[1] is OrderSide.BUY for o in t_sink.fills)
    assert 0 < len(t_sink.fills) < 10, f"buying_power gate did not bite: {len(t_sink.fills)} fills"

    assert m_sink.fills == t_sink.fills
    assert m_sink.equities == t_sink.equities
    assert (m_result.fills, m_result.final_cash, m_result.realized_pnl) == (
        t_result.fills,
        t_result.final_cash,
        t_result.realized_pnl,
    )


# ----------------------------------------- services=/constants= ctor passthrough (S6b-α step2 step3)

_MARIMO_SVC_SRC = """
import marimo

app = marimo.App()


@app.cell
def _svc():
    bar = get_bar()                           # noqa: F821  host-seeded driver
    # host-injected SERVICE (value-returning) + CONSTANT (static data), both free refs —
    # the seam the v19 parity gate uses to inject the stub scorer + ordered universe.
    score = score_rows({"close": bar.close})  # noqa: F821  injected service
    lots = len(UNIVERSE)                       # noqa: F821  injected constant (tuple)
    qty = float(lots) if score > 1000.0 else 0.0
    submit_market(qty)                         # noqa: F821
    return (qty,)
"""


class _SvcConstTwin(Strategy):
    """Imperative twin with the service/constant inlined (score = close, len(UNIVERSE) = 2)."""

    def on_bar(self, bar) -> None:
        qty = 2.0 if bar.close > 1000.0 else 0.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)


def test_marimo_adapter_services_and_constants_ctor_passthrough(tmp_path, monkeypatch):
    """MarimoStrategy forwards services= / constants= to open_runtime, so a cell reads a
    host-injected scorer service and a static constant as free refs (the public ctor seam the
    deterministic v19 parity gate drives). Parity with the imperative twin proves the injected
    values reach the cell unchanged; without the passthrough the cold compile would NameError."""
    path = tmp_path / "strat_svc.py"
    path.write_text(_MARIMO_SVC_SRC, encoding="utf-8")

    marimo_strat = MarimoStrategy(
        app=load_app(str(path)),
        strategy_id="strat-marimo",
        instrument_id=IID,
        services={"score_rows": lambda rows: rows["close"]},
        constants={"UNIVERSE": ("A", "B")},
    )
    twin = _SvcConstTwin(strategy_id="strat-imp", instrument_id=IID)

    m_result, m_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()
    t_result, t_sink = _run(twin, monkeypatch)

    # fixture guard: the service/constant gate actually fires BUYs (close>1000 bars exist).
    sides = {o[1] for o in t_sink.fills}
    assert sides == {OrderSide.BUY} and 0 < len(t_sink.fills) < 300

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
