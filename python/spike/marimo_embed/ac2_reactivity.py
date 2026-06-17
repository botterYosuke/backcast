"""AC2 — mo.state reactivity / per-bar stale drain (#76).

Establishes and proves the per-bar drain PROCEDURE the #64 construct needs:

  PROCEDURE  host holds a `set_bar` mo.state setter (no UI element anywhere) and,
             per bar, calls `set_bar(close)` then `await runner.run({strategy_cell})`.
             AppKernelRunner.run forces lazy mode and runs EXACTLY the cells passed,
             so the host — not a UI interaction — drives the drain.

  RE-RUN     change-elision is defeated by HOLDING THE INPUT CONSTANT, not by adding
             an always-changing co-input (that would force a re-run under ANY reactive
             system and prove nothing). The strategy cell appends to a stable `exec_log`
             list on every execution; with `close` pinned to one value across K drains
             (ZERO reactive input change), exec_log must still grow by K → the re-run
             is HOST-FORCED by runner.run({cell}), not triggered by a changed dep.

  NEVER-RUN  spike-0 residual (b): after the whole drain loop, orphan-free still
             holds (active_children==0, no LISTEN socket) and NO new marimo._server.*
             module appeared — import-time observation promoted to runtime-stable.

Run: uv run --group spike python -m spike.marimo_embed.ac2_reactivity
Exit 0 + "AC2 PASS" on success.
"""

from __future__ import annotations

import asyncio
import sys

from spike.marimo_embed.context_standup import (
    HeadlessKernel,
    assert_orphan_free,
    init_strategy_runner,
    resident_server_modules,
)
from spike.marimo_embed.synthetic import strategy_value


def _build_app():
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        exec_log = []  # stable list ref; the strategy cell appends on each execution
        return get_bar, set_bar, exec_log

    @app.cell
    def _strategy(get_bar, exec_log):
        # exec_log is a STABLE binding (the same list object every run): appending
        # mutates it in place and changes NO reactive input, so a re-run recorded
        # here can only have been host-forced — never elision-triggered.
        result = 2.0 * get_bar() + 1.0
        exec_log.append(result)
        return (result,)

    return app


async def _drive(app):  # noqa: ANN001
    runner, strat_cid = await init_strategy_runner(app)
    set_bar = runner.globals["set_bar"]
    exec_log = runner.globals["exec_log"]

    # PROOF A — result tracks each bar's close via the host-driven drain (no UI element).
    closes = [1000.0, 1010.0, 1005.5, 1000.0]
    tracked: list[float] = []
    for close in closes:
        set_bar(close)
        await runner.run({strat_cid})
        tracked.append(runner.globals["result"])

    # PROOF B — change-elision defeat: pin close to its LAST value (zero reactive input
    # change) and force K drains; the cell must re-execute every time (exec_log += K).
    set_bar(closes[-1])
    before = len(exec_log)
    k = 4
    for _ in range(k):
        await runner.run({strat_cid})
    forced_reruns = len(exec_log) - before
    return closes, tracked, k, forced_reruns


def main() -> int:
    failures: list[str] = []
    baseline_modules = resident_server_modules()

    kernel = HeadlessKernel()
    try:
        app = _build_app()
        closes, tracked, k, forced_reruns = asyncio.run(_drive(app))

        # PROOF A) downstream result tracks each bar's close
        for i, (close, result) in enumerate(zip(closes, tracked)):
            if abs(result - strategy_value(close)) > 1e-9:
                failures.append(f"bar {i}: result {result} != {strategy_value(close)}")

        # PROOF B) elision defeat: K drains at a CONSTANT close re-ran the cell K times
        if forced_reruns != k:
            failures.append(
                f"constant-close re-run count {forced_reruns} != {k} "
                "(host-forced drain did NOT defeat change-elision)"
            )

        # never-run / orphan-free after the drain loop
        try:
            evidence = assert_orphan_free()
        except AssertionError as exc:
            failures.append(f"orphan-free (post-drain): {exc}")
            evidence = {}
        new_modules = sorted(set(resident_server_modules()) - set(baseline_modules))
        if new_modules:
            failures.append(f"new marimo._server.* after drain: {new_modules}")
    finally:
        kernel.teardown()

    print("=" * 72)
    print("AC2 — mo.state reactivity / per-bar stale drain (#76)")
    print("=" * 72)
    print(f"closes fed        : {closes}")
    print(f"tracked results   : {tracked}")
    print(f"forced re-runs    : {forced_reruns}/{k} drains at CONSTANT close={closes[-1]}")
    print(f"orphan-free       : {evidence}")
    print(f"new _server.*     : {new_modules}")
    print("-" * 72)
    if failures:
        for f in failures:
            print(f"  ✗ {f}")
        print("AC2 FAIL")
        return 1
    print("  ✓ per-bar drain via set_bar()+runner.run({cell}); NO UI element used")
    print(f"  ✓ change-elision defeated: {k} drains at CONSTANT input re-ran the cell {k}x")
    print("  ✓ never-run: orphan-free post-drain, no new marimo._server.* modules")
    print("AC2 PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
