"""AC1 redesign (spike-2) — host-owned THIN drain hits native speed (#76).

#76 AC1 measured the documented `AppKernelRunner.run` per-bar drain at ~4.5ms/bar
(~235s for 50k). Profiling showed that cost is 100% marimo `Kernel.run`
orchestration (entry-point disk scan + graph mutation + lint + topo-sort +
execution-context install) — NONE of which is needed when the cell-DAG is STATIC.

The thin drain precomputes the executor + the dirty cell ONCE, then per bar calls
`executor.execute_cell(cell, glbls, graph)` directly (= `exec(cell.body, glbls);
eval(cell.last_expr, glbls)`). This probe proves two things:

  SPEED       50k thin drains complete at ~native speed (well under a budget that the
              full AppKernelRunner.run path would blow by ~1000x).
  CONTRACT    the hot-path cell contract boundary (owner-measured, locked here as a
              regression gate): pure compute + state read/write WORK; mo.output is a
              SILENT no-op; mo.ui is a HARD error. => per-bar cells must be pure
              compute; UI/output belong on the cold (live-edit) path only.

Run: uv run --group spike python -m spike.marimo_embed.ac1_thin_drain
Exit 0 + "AC1-THIN PASS" on success.
"""

from __future__ import annotations

import asyncio
import statistics
import sys
import time

from marimo._runtime.executor import ExecutionConfig, get_executor
from spike.marimo_embed.context_standup import HeadlessKernel, init_strategy_runner

# 50k native should be << 1s; the full AppKernelRunner.run path needs ~235s. A 5s
# budget is ~50x margin over native yet ~50x under the orchestrated path — either
# verdict is unambiguous.
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
        set_sig(result)  # mo.state WRITE from the hot path (reactivity body)
        return (result,)

    return app


async def _speed_and_state(n: int) -> dict:
    app = _build_compute_app()
    runner, strat_cid = await init_strategy_runner(app)
    set_bar = runner.globals["set_bar"]
    get_sig = runner.globals["get_sig"]
    glbls = runner.globals
    graph = runner._kernel.graph
    executor = get_executor(ExecutionConfig())  # built ONCE (entry-point scan once)
    cell = graph.cells[strat_cid]

    for i in range(50):  # warm
        set_bar(float(i))
        executor.execute_cell(cell, glbls, graph)

    per: list[float] = []
    for i in range(n):
        set_bar(float(i))
        t = time.perf_counter()
        executor.execute_cell(cell, glbls, graph)
        per.append(time.perf_counter() - t)

    set_bar(123.5)
    executor.execute_cell(cell, glbls, graph)
    return {
        "median_us": statistics.median(per) * 1e6,
        "total_s": sum(per),
        "per_bar_50k_s": sum(per) / n * 50_000,
        "result": glbls["result"],
        "sig_after_write": get_sig(),  # proves hot-path mo.state WRITE took effect
    }


async def _boundary(kind: str) -> str:
    """Run a single cell that uses `kind` via the thin drain; report behavior."""
    from marimo import App

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

    # Drive ONLY via the thin drain (no full init run, which would install an
    # execution context and mask the context-less hot-path behavior). _get_kernel_runner
    # registers the cells without running them.
    runner = app._get_kernel_runner()
    cid = next(c for c, _ in app._cell_manager.valid_cells())
    executor = get_executor(ExecutionConfig())
    cell = runner._kernel.graph.cells[cid]
    try:
        executor.execute_cell(cell, runner.globals, runner._kernel.graph)
        return "ran-no-error"
    except BaseException as exc:  # noqa: BLE001 — marimo raises MarimoRuntimeException(BaseException)
        return f"raised:{type(exc).__name__}"


def main() -> int:
    failures: list[str] = []
    kernel = HeadlessKernel()
    try:
        speed = asyncio.run(_speed_and_state(5000))
        out_behavior = asyncio.run(_boundary("output"))
        ui_behavior = asyncio.run(_boundary("ui"))
    finally:
        kernel.teardown()

    # SPEED
    if speed["per_bar_50k_s"] > _BUDGET_50K_S:
        failures.append(f"50k extrapolated {speed['per_bar_50k_s']:.2f}s > budget {_BUDGET_50K_S}s")
    # pure compute correctness + hot-path mo.state WRITE
    if abs(speed["result"] - 248.0) > 1e-9:
        failures.append(f"result {speed['result']} != 248.0")
    if abs(speed["sig_after_write"] - 248.0) > 1e-9:
        failures.append(f"hot-path mo.state write failed: sig={speed['sig_after_write']} != 248.0")
    # CONTRACT asymmetry: output silent (no raise), ui hard error (raises)
    if out_behavior != "ran-no-error":
        failures.append(f"mo.output expected silent no-op, got {out_behavior}")
    if not ui_behavior.startswith("raised:"):
        failures.append(f"mo.ui expected hard error, got {ui_behavior}")

    print("=" * 72)
    print("AC1 redesign (spike-2) — host-owned THIN drain (#76)")
    print("=" * 72)
    print(f"thin drain    : median={speed['median_us']:.2f}us  "
          f"50k≈{speed['per_bar_50k_s']:.3f}s  (budget {_BUDGET_50K_S}s)")
    print(f"pure compute  : result={speed['result']} (expect 248.0)")
    print(f"hot mo.state W: sig={speed['sig_after_write']} (expect 248.0)")
    print(f"mo.output     : {out_behavior}  (expect silent ran-no-error)")
    print(f"mo.ui         : {ui_behavior}  (expect raised:*)")
    print("-" * 72)
    if failures:
        for f in failures:
            print(f"  ✗ {f}")
        print("AC1-THIN FAIL")
        return 1
    print("  ✓ thin drain at native speed (50k well under budget)")
    print("  ✓ pure compute + hot-path mo.state read/write work")
    print("  ✓ contract boundary: mo.output silent no-op / mo.ui hard error (pure-compute only)")
    print("AC1-THIN PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
