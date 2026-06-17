"""S1 thin-drain runtime gates (#76 / findings 0046).

Graduates the throwaway #76 spike probes (``spike/marimo_embed/ac1_thin_drain``) into
durable regression gates on the production ``engine.strategy_runtime.thin_drain``:

  PERF        50k bars through the host-owned thin drain complete at native speed (the
              documented AppKernelRunner.run path needed ~235s for the same).
  CONTRACT    the hot-path cell contract (D-grill): pure compute + mo.state read/write
              WORK; ``mo.output`` is a SILENT no-op; ``mo.ui`` is a HARD error. Per-bar
              cells are pure compute; UI/output belong on the cold (live-edit) path.
  PRECOMPUTE  the static dirty-set/topo (D2) is what marimo's own functions return:
              driver-rooted, topo-ordered, with the state-definition cell auto-excluded.
  PARITY      a multi-cell reactive DAG (bar→signal→order→portfolio, with a self-cycle
              feedback — D1/D4-A) produces a byte-identical result sequence to the
              imperative on_bar twin.

marimo is a spike-only dependency, so this whole module is skipped unless it is
installed. Run the gate explicitly with::

    uv run --group spike python -m pytest tests/test_strategy_runtime_thin_drain.py
"""

from __future__ import annotations

import statistics
import time

import pytest

pytest.importorskip("marimo", reason="#76 S1 gate: marimo is a spike-only dependency")

from engine.strategy_runtime.thin_drain import HeadlessKernel, open_runtime  # noqa: E402

pytestmark = pytest.mark.marimo


# --------------------------------------------------------------------------- apps


def _compute_app():
    """Single strategy cell that reads a host-driven bar, computes, and writes a
    feedback state from the hot path (`result = 2*bar + 1`)."""
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        get_sig, set_sig = mo.state(0.0)
        return get_bar, set_bar, get_sig, set_sig

    @app.cell
    def _strategy(get_bar, set_sig):
        result = 2.0 * get_bar() + 1.0
        set_sig(result)  # hot-path mo.state WRITE
        return (result,)

    return app


def _dag_app():
    """Multi-cell forward DAG with a self-cycle feedback (findings 0046 D1/D4-A):

      bar (host-driven mo.state)
        → signal   (cell return var)
        → qty      (cell return var)
        → portfolio (self-cycle mo.state: reads its own prev value + qty + bar)
    """
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        # allow_self_loops: the portfolio cell reads AND writes pf in the same cell
        # (D4 shape A) — the prev-bar value carried in globals, no look-ahead (D3).
        get_pf, set_pf = mo.state(0.0, allow_self_loops=True)
        return get_bar, set_bar, get_pf, set_pf

    @app.cell
    def _signal(get_bar):
        signal = 1.0 if get_bar() > 1000.0 else -1.0
        return (signal,)

    @app.cell
    def _order(signal):
        qty = signal * 10.0
        return (qty,)

    @app.cell
    def _portfolio(get_bar, get_pf, set_pf, qty):
        new_pf = get_pf() + qty * get_bar()
        set_pf(new_pf)
        return (new_pf,)

    return app


def _dag_twin(closes):
    """Imperative on_bar twin of `_dag_app` — the parity oracle."""
    pf = 0.0
    out = []
    for close in closes:
        signal = 1.0 if close > 1000.0 else -1.0
        qty = signal * 10.0
        pf = pf + qty * close
        out.append((signal, qty, pf))
    return out


# --------------------------------------------------------------------------- gates


def test_thin_drain_native_speed_and_hot_state_write():
    """PERF + hot-path mo.state: 50k extrapolation under a native-speed budget, and a
    hot-path state write takes effect."""
    n = 5000
    budget_50k_s = 5.0  # ~25x margin over measured ~0.19s; ~50x under the orchestrated path
    with open_runtime(_compute_app(), drivers=["get_bar"]) as rt:
        for i in range(50):  # warm
            rt.drain({"get_bar": float(i)})

        per: list[float] = []
        for i in range(n):
            t = time.perf_counter()
            rt.drain({"get_bar": float(i)})
            per.append(time.perf_counter() - t)

        rt.drain({"get_bar": 123.5})
        result = rt.globals["result"]
        sig_getter = rt.globals["get_sig"]

    per_bar_50k_s = sum(per) / n * 50_000
    assert per_bar_50k_s < budget_50k_s, (
        f"50k extrapolated {per_bar_50k_s:.2f}s exceeds native budget {budget_50k_s}s "
        f"(median {statistics.median(per) * 1e6:.2f}us/bar)"
    )
    assert result == pytest.approx(248.0)  # 2*123.5 + 1
    assert sig_getter() == pytest.approx(248.0), "hot-path mo.state WRITE did not take effect"


def _hot_path_behavior(kind: str) -> str:
    """Drive a single cell through the bare ``executor.execute_cell`` primitive — the
    exact context-less call the thin-drain hot path makes — and report its behavior.

    This deliberately does NOT cold-run the app: the contract is a property of the
    context-less HOT path, and a full cold run would install an execution context that
    masks it (and, for mo.ui/output cells, would not populate globals at all). Mirrors
    the #76 spike ``_boundary`` probe, on the production HeadlessKernel.
    """
    from marimo import App
    from marimo._runtime.executor import ExecutionConfig, get_executor

    app = App()
    if kind == "output":

        @app.cell
        def _c():
            import marimo as mo

            mo.output.replace("hello")  # expected: silent no-op (no exec-context)
            result = 1
            return (result,)
    else:  # "ui"

        @app.cell
        def _c():
            import marimo as mo

            result = mo.ui.slider(0, 10)  # expected: hard error
            return (result,)

    host = HeadlessKernel()
    try:
        runner = app._get_kernel_runner()
        cid = next(c for c, _ in app._cell_manager.valid_cells())
        executor = get_executor(ExecutionConfig())
        cell = runner._kernel.graph.cells[cid]
        try:
            executor.execute_cell(cell, runner.globals, runner._kernel.graph)
            return "ran-no-error"
        except BaseException as exc:  # marimo raises MarimoRuntimeException(BaseException)
            return f"raised:{type(exc).__name__}"
    finally:
        host.teardown()


def test_hot_path_contract_mo_output_silent():
    """CONTRACT: ``mo.output`` from a per-bar cell is a SILENT no-op (no exec-context on
    the thin drain) — a footgun S3 will fail-closed, here pinned as current behavior."""
    assert _hot_path_behavior("output") == "ran-no-error"


def test_hot_path_contract_mo_ui_hard_error():
    """CONTRACT: ``mo.ui`` from a per-bar cell is a HARD error (fail-closed)."""
    assert _hot_path_behavior("ui").startswith("raised:")


def test_precompute_hot_list_is_driver_rooted_topo_ordered():
    """PRECOMPUTE (D2): the hot list is exactly the cells reachable from the host-driven
    state, topo-ordered, with the state-definition cell auto-excluded (it is neither a
    root nor a descendant of one)."""
    with open_runtime(_dag_app(), drivers=["get_bar"]) as rt:
        glbls = rt.globals
        hot_ids = list(rt.hot_cell_ids)

        # Map each hot cell id to the variable it defines, to assert order semantically
        # without coupling to marimo's opaque cell ids.
        graph = rt._c.graph  # test-only introspection
        defined = {cid: set(graph.cells[cid].defs) for cid in hot_ids}

        def pos(var):
            return next(i for i, cid in enumerate(hot_ids) if var in defined[cid])

        # bar→signal→qty→portfolio: forward edges are honored by the topo sort.
        assert pos("signal") < pos("qty") < pos("new_pf")
        # The state-definition cell (defines the getters/setters) must NOT be in the hot
        # list — D1: state-def cells are siblings, auto-excluded from the drain.
        state_vars = {"get_bar", "set_bar", "get_pf", "set_pf"}
        for cid in hot_ids:
            assert not (defined[cid] & state_vars), (
                f"state-definition cell {cid} leaked into the hot list"
            )
        # Sanity: the three compute cells are present.
        assert {"signal", "qty", "new_pf"} <= set().union(*defined.values())
        assert glbls is rt.globals


def test_dag_byte_identical_to_imperative_twin():
    """PARITY: the reactive multi-cell DAG drained per-bar matches the imperative twin
    byte-for-byte over a moving synthetic close series."""
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(500)]
    # vary above/below the 1000 signal threshold so both branches are exercised
    closes = [c if i % 3 else 999.0 for i, c in enumerate(closes)]

    expected = _dag_twin(closes)

    got = []
    with open_runtime(_dag_app(), drivers=["get_bar"]) as rt:
        for close in closes:
            rt.drain({"get_bar": close})
            got.append((rt.globals["signal"], rt.globals["qty"], rt.globals["new_pf"]))

    assert got == expected
