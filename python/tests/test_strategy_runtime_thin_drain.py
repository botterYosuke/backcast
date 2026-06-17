"""S1 thin-drain runtime gates (#76 / findings 0046).

Graduates the throwaway #76 spike probes (``spike/marimo_embed/ac1_thin_drain``) into
durable regression gates on the production ``engine.strategy_runtime.thin_drain``:

  PERF        50k bars through the host-owned thin drain complete at native speed (the
              documented AppKernelRunner.run path needed ~235s for the same).
  D6 PROD     the per-bar primitive installs the execution context, so ``mo.output`` is
              published to the kernel stream and ``mo.ui`` does not hard-error — every cell
              behaves like a normal marimo cell (findings 0046 D6 / redesign 追補).
  CONTROL     the context-less BARE ``executor.execute_cell`` drops ``mo.output`` silently
              and hard-errors on ``mo.ui`` — kept only to motivate D6, NOT what production does.
  PRECOMPUTE  the static dirty-set/topo (D2) is what marimo's own functions return:
              driver-rooted, topo-ordered, with the state-definition cell auto-excluded.
  PARITY      a multi-cell reactive DAG (bar→signal→order→portfolio, with a self-cycle
              feedback — D1/D4-A) produces a byte-identical result sequence to the
              imperative on_bar twin.

marimo is a prod dependency since S3 (ADR-0012), so these gates run in the default test
run. Run them explicitly with::

    uv run python -m pytest tests/test_strategy_runtime_thin_drain.py
"""

from __future__ import annotations

import statistics
import time

import pytest

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from engine.kernel.orders import OrderSide  # noqa: E402
from engine.strategy_runtime.cell_api import make_submit_market  # noqa: E402
from engine.strategy_runtime.thin_drain import (  # noqa: E402
    HeadlessKernel,
    _execute_hot_cell,
    open_runtime,
)

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


def _single_cell_app(kind: str):
    """A one-cell app whose single cell exercises ``mo.output`` or ``mo.ui`` — the shared
    fixture for both the context-less control (`_hot_path_behavior`) and the D6 production
    gate (`_production_hot_path`). The two helpers differ only in HOW they drive this cell
    (bare ``execute_cell`` vs the context-installing ``_execute_hot_cell``) and what they
    assert; the cell itself is identical, so it lives here once."""
    from marimo import App

    app = App()
    if kind == "output":

        @app.cell
        def _c():
            import marimo as mo

            mo.output.replace("hello")
            result = 1
            return (result,)
    else:  # "ui"

        @app.cell
        def _c():
            import marimo as mo

            result = mo.ui.slider(0, 10)
            return (result,)

    return app


def _hot_path_behavior(kind: str) -> str:
    """Drive a single cell through a BARE ``executor.execute_cell`` — no execution context
    — and report its behavior. This is the CONTROL that motivates D6.

    It deliberately calls ``executor.execute_cell`` directly rather than the production
    ``_execute_hot_cell`` primitive: post-D6 the primitive INSTALLS the context, so the
    context-less footgun (silent ``mo.output`` / hard ``mo.ui`` error) is no longer what
    production does — it survives here only as the control showing why D6 is needed. The
    full cold run is avoided on purpose (it would install a context that masks this, and
    for mo.ui/output cells would not populate globals at all). Mirrors the #76 spike
    ``_boundary`` probe.
    """
    from marimo._runtime.executor import ExecutionConfig, get_executor

    app = _single_cell_app(kind)
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
    """CONTROL (context-less, demoted): a BARE ``executor.execute_cell`` — no execution
    context — drops ``mo.output`` silently. This is NOT what the production drain does
    post-D6 (it installs the context); it is kept only to show WHY D6 is needed. It
    therefore calls ``executor.execute_cell`` directly, not the production primitive."""
    assert _hot_path_behavior("output") == "ran-no-error"


def test_hot_path_contract_mo_ui_hard_error():
    """CONTROL (context-less, demoted): a BARE ``executor.execute_cell`` hard-errors on
    ``mo.ui``. Kept as the control that motivates D6 (see the sibling test)."""
    assert _hot_path_behavior("ui").startswith("raised:")


def _production_hot_path(kind: str) -> dict:
    """Drive a single cell through the PRODUCTION primitive ``_execute_hot_cell(ctx, ...)``
    — the exact context-installing call ``StrategyRuntime.step`` makes per bar — and report
    whether ``mo.output`` reached the kernel stream and whether it raised.

    Cold-running an mo.output/mo.ui app does not populate globals (the cells fall over), so
    — like the context-less control helper — this drives a single cell directly rather than
    via ``open_runtime``. Routing through the SAME ``_execute_hot_cell`` the drain uses binds
    this gate to production: if ``step`` stops installing the context, this goes RED. Output
    is observed on the kernel stream (the user-visible publish), not on the internal
    ``execution_context.output`` attribute, which is restored on block exit.
    """
    from marimo._runtime.context.types import get_context
    from marimo._runtime.executor import ExecutionConfig, get_executor

    app = _single_cell_app(kind)
    host = HeadlessKernel()
    try:
        runner = app._get_kernel_runner()
        cid = next(c for c, _ in app._cell_manager.valid_cells())
        executor = get_executor(ExecutionConfig())
        cell = runner._kernel.graph.cells[cid]
        ctx = get_context()
        before = len(host.stream.messages)
        try:
            _execute_hot_cell(ctx, executor, cell, runner.globals, runner._kernel.graph)
            behavior = "ran-no-error"
        except BaseException as exc:  # marimo raises MarimoRuntimeException(BaseException)
            behavior = f"raised:{type(exc).__name__}"
        return {"behavior": behavior, "published": len(host.stream.messages) > before}
    finally:
        host.teardown()


def test_production_context_publishes_mo_output():
    """D6 production-output: the per-bar primitive ``step`` uses installs the execution
    context, so ``mo.output`` reaches the kernel stream (published) — the footgun the
    context-less control suffers is gone. Routes through the production primitive, so it
    goes RED if production stops installing the context."""
    r = _production_hot_path("output")
    assert r["behavior"] == "ran-no-error", r
    assert r["published"], "mo.output did not reach the kernel stream (context not installed?)"


def test_production_context_mo_ui_no_hard_error():
    """D6 production-ui: with the context installed, ``mo.ui`` behaves like a normal marimo
    cell — no hard error (the context-less control hard-errors)."""
    assert _production_hot_path("ui")["behavior"] == "ran-no-error"


def test_precompute_hot_list_is_driver_rooted_topo_ordered():
    """PRECOMPUTE (D2): the hot list is exactly the cells reachable from the host-driven
    state, topo-ordered, with the state-definition cell auto-excluded (it is neither a
    root nor a descendant of one)."""
    with open_runtime(_dag_app(), drivers=["get_bar"]) as rt:
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


def test_compile_rejects_driver_with_no_reader_cell():
    """FAIL-CLOSED: a declared driver that no cell reads is rejected at compile, not left to
    silently no-op every bar (a dead strategy that looks live)."""
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        get_unused, set_unused = mo.state(0.0)  # declared driver below, read by nobody
        return get_bar, set_bar, get_unused, set_unused

    @app.cell
    def _strategy(get_bar):
        result = 2.0 * get_bar()
        return (result,)

    with pytest.raises(ValueError, match="no reader cell"):
        with open_runtime(app, drivers=["get_unused"]):
            pass


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


# --------------------------------------------------------------- S4 injected API


class _RecordingCtx:
    """Fake StrategyContext: records submit_market calls (the kernel per-bar contract
    surface). S4 drives the adapter standalone — wiring the real kernel _Context is S6."""

    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def submit_market(self, *, strategy_id, instrument_id, side, quantity) -> None:
        self.calls.append((instrument_id, side, quantity))


def test_injected_callable_is_callable_from_cell():
    """INJECT (S4): a host callable seeded via ``open_runtime(.., inject=...)`` lands in the
    cell globals and a per-bar cell can call it (resolved in both the cold run and the hot
    drain) — the mechanism the injected ``submit_market`` rides on."""
    from marimo import App

    recorded: list[float] = []

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        return get_bar, set_bar

    @app.cell
    def _call(get_bar):
        host_fn(get_bar() * 2.0)  # noqa: F821 — host-injected free name
        return ()

    with open_runtime(app, drivers=["get_bar"], inject={"host_fn": recorded.append}) as rt:
        rt.drain({"get_bar": 5.0})
        rt.drain({"get_bar": 7.0})

    assert recorded == [10.0, 14.0]


def test_compile_rejects_inject_name_colliding_with_cell_def():
    """FAIL-CLOSED (S4): an injected name that a cell ALSO defines would be silently clobbered
    when the real callable is armed after the cold run — shadowing the author's value/helper
    with an action (or firing a spurious order). Reject at compile instead of clobbering."""
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        return get_bar, set_bar

    @app.cell
    def _helper(get_bar):
        submit_market = 42.0 + get_bar() * 0.0  # a cell DEFINES the same name the host injects
        return (submit_market,)

    with pytest.raises(ValueError, match="also defined by a cell"):
        with open_runtime(
            app, drivers=["get_bar"], inject={"submit_market": lambda *a, **k: None}
        ):
            pass


def _order_dag_app():
    """A signed-quantity order DAG: bar→signal→qty→``submit_market(qty)``. The order cell
    references the injected ``submit_market`` as a free name (findings 0046 S4)."""
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        return get_bar, set_bar

    @app.cell
    def _signal(get_bar):
        bar = get_bar()
        # three-way: long / flat / short — flat (0.0) exercises the adapter's no-op branch
        signal = 1.0 if bar > 1010.0 else (-1.0 if bar < 990.0 else 0.0)
        return (signal,)

    @app.cell
    def _order(signal):
        qty = signal * 10.0
        submit_market(qty)  # noqa: F821 — host-injected signed-qty adapter
        return (qty,)

    return app


def _order_twin(closes):
    """Imperative on_bar twin: the signed→(side, abs) order oracle (0 → no order)."""
    out = []
    for close in closes:
        signal = 1.0 if close > 1010.0 else (-1.0 if close < 990.0 else 0.0)
        qty = signal * 10.0
        # mirror the adapter: sign → side, abs → quantity, 0 → no order
        if qty != 0.0:
            out.append(
                ("7203.T", OrderSide.BUY if qty > 0.0 else OrderSide.SELL, abs(qty))
            )
    return out


def test_injected_submit_market_order_parity_with_imperative_twin():
    """PARITY (S4): a signed-qty marimo cell-DAG calling the injected ``submit_market`` emits
    the same order sequence as the imperative on_bar twin — proving the signed→(side, abs)
    adapter + injection end-to-end (the existing _dag_app parity gate never submits orders)."""
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(300)]
    # span all three bands so BUY (>1010), SELL (<990), and flat/no-op (990..1010) all occur
    closes = [
        980.0 if i % 5 == 0 else (1000.0 if i % 5 == 1 else c) for i, c in enumerate(closes)
    ]

    expected = _order_twin(closes)
    # guard the gate itself: the series must exercise all three adapter branches
    sides = {o[1] for o in expected}
    assert sides == {OrderSide.BUY, OrderSide.SELL} and len(expected) < len(closes), (
        "parity series must include BUY, SELL, and at least one flat (no-op) bar"
    )

    ctx = _RecordingCtx()
    adapter = make_submit_market(ctx, strategy_id="strat-1", default_instrument_id="7203.T")
    with open_runtime(
        _order_dag_app(), drivers=["get_bar"], inject={"submit_market": adapter}
    ) as rt:
        for close in closes:
            rt.drain({"get_bar": close})

    assert ctx.calls == expected
