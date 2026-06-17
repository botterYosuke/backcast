"""Redesign probe — uniform cell model via per-cell execution context (#76).

The S1 thin drain runs per-bar cells WITHOUT marimo's execution context
(`executor.execute_cell` directly). That made the cost native, but it leaked a
USER-FACING split the owner rejected: a cell that reads the bar ("hot") behaves
differently from one that does not ("cold") — `mo.output` is a SILENT no-op
(footgun) and `mo.ui` is a HARD error on the hot path only.

Alternative A: keep the thin drain (precompute the cell list once, bypass marimo's
Kernel.run orchestration), but wrap each per-bar `execute_cell` in the kernel
context's `with_cell_id(cell_id)`. `with_cell_id` (kernel_context.py:118) just
builds an ExecutionContext dataclass and swaps one attribute — it is NOT what made
Kernel.run slow (that was entry-point disk scan + graph mutation + lint + topo).
With the context installed, `mo.output.replace` no longer hits its
`if ctx.execution_context is None: return` early-out (_output.py:42) — it populates
the cell output and writes to the stream, exactly like a normal marimo cell. So
EVERY cell behaves like a normal marimo cell; the precomputed list is a pure
internal optimization the user never sees. No hot/cold concept, no footgun, no guard.

This probe measures the two claims that decide Alternative A:

  COST        installing `with_cell_id` per cell per bar keeps 50k at native speed
              (the context install is sub-microsecond, not the old 4.5ms/bar).
  UNIFORMITY  WITH the context, `mo.output` WORKS (populates output instead of the
              silent no-op) and `mo.ui` behaves like a normal marimo cell — i.e. the
              hot/cold behavioral split disappears.

Run: uv run --group spike python -m spike.marimo_embed.context_uniform
Exit 0 + "CTX-UNIFORM PASS" on success.
"""

from __future__ import annotations

import asyncio
import statistics
import sys
import time

from marimo._runtime.context.types import get_context
from marimo._runtime.executor import ExecutionConfig, get_executor
from spike.marimo_embed.context_standup import HeadlessKernel, init_strategy_runner

# 50k native should be << 1s; the full AppKernelRunner.run path needs ~235s. Same 5s
# budget as ac1_thin_drain — Alternative A must NOT regress the thin drain's speed.
# This ABSOLUTE budget is the COST gate. The bare-vs-ctx ratio is printed for color
# only, NOT gated: both medians are sub-microsecond, so a ratio over that denominator
# is dominated by timer jitter (the "分母を疑う" trap) — it would false-fail on a quiet
# box or rubber-stamp a real slowdown the absolute budget already catches.
_BUDGET_50K_S = 5.0


def _build_compute_app():
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
        set_sig(result)  # mo.state WRITE from the hot path
        return (result,)

    return app


async def _speed(n: int, *, with_context: bool) -> dict:
    """Drain a pure-compute cell n times; optionally wrap each run in with_cell_id.

    The two arms are written as separate branch-free loops (not a per-iteration
    `if`/closure call) so the ONLY thing inside the bare arm's timed region is
    `execute_cell`, and the only delta in the ctx arm is the `with_cell_id`
    enter/exit. A shared per-iteration call frame would dilute both medians and
    understate the very overhead this probe measures.
    """
    app = _build_compute_app()
    runner, strat_cid = await init_strategy_runner(app)
    set_bar = runner.globals["set_bar"]
    get_sig = runner.globals["get_sig"]
    glbls = runner.globals
    graph = runner._kernel.graph
    executor = get_executor(ExecutionConfig())  # built ONCE (entry-point scan once)
    cell = graph.cells[strat_cid]
    execute = executor.execute_cell  # bound once; never resolved in the timed region
    per: list[float] = []

    if with_context:
        with_cell_id = get_context().with_cell_id  # bound once
        for i in range(50):  # warm
            set_bar(float(i))
            with with_cell_id(strat_cid):
                execute(cell, glbls, graph)
        for i in range(n):
            set_bar(float(i))
            t = time.perf_counter()
            with with_cell_id(strat_cid):
                execute(cell, glbls, graph)
            per.append(time.perf_counter() - t)
        set_bar(123.5)
        with with_cell_id(strat_cid):
            execute(cell, glbls, graph)
    else:
        for i in range(50):  # warm
            set_bar(float(i))
            execute(cell, glbls, graph)
        for i in range(n):
            set_bar(float(i))
            t = time.perf_counter()
            execute(cell, glbls, graph)
            per.append(time.perf_counter() - t)
        set_bar(123.5)
        execute(cell, glbls, graph)

    return {
        "median_us": statistics.median(per) * 1e6,
        "per_bar_50k_s": sum(per) / n * 50_000,
        "result": glbls["result"],
        "sig_after_write": get_sig(),
    }


async def _boundary(kind: str, *, with_context: bool) -> dict:
    """Run a single cell using `kind`, with or without the execution context.

    Returns the raised-exception name (if any) and, for output, whether the cell's
    output was actually populated (the proof that it is NO LONGER a silent no-op).
    """
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

    runner = app._get_kernel_runner()
    cid = next(c for c, _ in app._cell_manager.valid_cells())
    executor = get_executor(ExecutionConfig())
    cell = runner._kernel.graph.cells[cid]
    ctx = get_context()

    behavior = "ran-no-error"
    output_populated = False
    try:
        if with_context:
            with ctx.with_cell_id(cid):
                executor.execute_cell(cell, runner.globals, runner._kernel.graph)
                # read evidence INSIDE the block (execution_context is restored on exit)
                ec = ctx.execution_context
                output_populated = bool(ec is not None and ec.output)
        else:
            executor.execute_cell(cell, runner.globals, runner._kernel.graph)
    except BaseException as exc:  # noqa: BLE001 — marimo raises MarimoRuntimeException(BaseException)
        behavior = f"raised:{type(exc).__name__}"
    return {"behavior": behavior, "output_populated": output_populated}


def main() -> int:
    failures: list[str] = []
    kernel = HeadlessKernel()
    try:
        bare = asyncio.run(_speed(5000, with_context=False))
        ctxd = asyncio.run(_speed(5000, with_context=True))
        out_no_ctx = asyncio.run(_boundary("output", with_context=False))
        out_ctx = asyncio.run(_boundary("output", with_context=True))
        ui_no_ctx = asyncio.run(_boundary("ui", with_context=False))
        ui_ctx = asyncio.run(_boundary("ui", with_context=True))
    finally:
        kernel.teardown()

    # COST gate: the context-installed drain stays native (absolute 50k budget).
    if ctxd["per_bar_50k_s"] > _BUDGET_50K_S:
        failures.append(
            f"with-context 50k {ctxd['per_bar_50k_s']:.2f}s > budget {_BUDGET_50K_S}s"
        )
    overhead_x = ctxd["median_us"] / bare["median_us"] if bare["median_us"] else float("inf")
    # correctness preserved with context
    if abs(ctxd["result"] - 248.0) > 1e-9 or abs(ctxd["sig_after_write"] - 248.0) > 1e-9:
        failures.append(f"with-context compute wrong: {ctxd}")

    # UNIFORMITY: the hot/cold behavioral split must DISAPPEAR with the context. Both
    # the controls (no-ctx) AND the treatments (+ctx) are gated, so a PASS means the
    # full differential held — not just the half the probe happened to read.
    # mo.output: silent (no raise) WITHOUT context; POPULATES output WITH it.
    if out_no_ctx["behavior"] != "ran-no-error":
        failures.append(f"control broken: mo.output raised without context: {out_no_ctx}")
    if not out_ctx["output_populated"]:
        failures.append(
            f"mo.output still a no-op WITH context: {out_ctx} (Alternative A premise FAILED)"
        )
    # mo.ui: HARD error WITHOUT context; behaves like a normal cell (no raise) WITH it.
    if not ui_no_ctx["behavior"].startswith("raised:"):
        failures.append(f"control broken: mo.ui did not hard-error without context: {ui_no_ctx}")
    ui_uniform = ui_ctx["behavior"] == "ran-no-error"
    if not ui_uniform:
        failures.append(
            f"mo.ui still not uniform WITH context: {ui_ctx} (Alternative A premise FAILED)"
        )

    print("=" * 72)
    print("Redesign — uniform cell model via per-cell execution context (#76)")
    print("=" * 72)
    print(f"thin drain (bare)    : median={bare['median_us']:.2f}us  50k≈{bare['per_bar_50k_s']:.3f}s")
    print(f"thin drain (+context): median={ctxd['median_us']:.2f}us  50k≈{ctxd['per_bar_50k_s']:.3f}s")
    print(f"context overhead     : {overhead_x:.2f}x  (info only; COST gate is {_BUDGET_50K_S}s/50k absolute)")
    print("-" * 72)
    print(f"mo.output  no-ctx : {out_no_ctx}   (control: silent, output empty)")
    print(f"mo.output +ctx    : {out_ctx}   (Alt-A: output POPULATED = works)")
    print(f"mo.ui      no-ctx : {ui_no_ctx}   (control: hard error)")
    print(f"mo.ui      +ctx   : {ui_ctx}   (uniform with marimo? {ui_uniform})")
    print("-" * 72)
    if failures:
        for f in failures:
            print(f"  ✗ {f}")
        print("CTX-UNIFORM FAIL")
        return 1
    print("  ✓ context-installed drain stays native (50k under budget, small overhead)")
    print("  ✓ mo.output WORKS with context (no longer a silent no-op) — footgun gone")
    print(f"  ✓ mo.ui with context behaves uniformly: {'ran (no hard error)' if ui_uniform else ui_ctx['behavior']}")
    print("CTX-UNIFORM PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
