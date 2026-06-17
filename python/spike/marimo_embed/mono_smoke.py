"""AC3 Mono-leg entry (#76) — driven by Assets/Editor/MarimoEmbedAc3MonoProbe.cs.

The genuinely-new risk beyond S0/S2 (spike-0 residual (a)): does marimo's WHOLE
import graph — which now includes a Rust extension (``loro``), plus msgspec /
pydantic-core / starlette — LOAD under Unity Mono + pythonnet, and can one embed
reactive drain RUN, without crashing Mono or stalling the frame?

This module exposes ONE synchronous function the C# worker calls under ``Py.GIL()``.
It internally drives the same headless-context + mo.state drain the CPython legs
use (so a Mono-only failure isolates the fault to the Mono/pythonnet load seam,
mirroring the S0/S2 split). It returns the drained result as a float; C# asserts
it equals the expected value and that its GIL-free heartbeat never stalled.

Kept deliberately MINIMAL (one drain), per the spike-0 scope: "full C# harness は
不要、最小・無条件・1 回の Mono smoke". The GIL band / continuous ping-pong is
already owned by S2 (findings 0005 §8.1); this leg only adds the marimo load+run.
"""

from __future__ import annotations


def run_one_drain(close: float) -> float:
    """Stand up a headless marimo context, run ONE reactive drain, return result.

    Synchronous: the C# worker calls this under the GIL; asyncio is driven
    internally. Imports are done inside the function so the cost of loading the
    marimo graph is paid HERE, under Mono, where the probe can observe a crash.
    """
    import asyncio

    from spike.marimo_embed.context_standup import HeadlessKernel
    from marimo._runtime.context.utils import running_in_notebook
    from marimo import App

    kernel = HeadlessKernel()
    try:
        if not running_in_notebook():
            raise RuntimeError("headless context did not flip running_in_notebook() True")

        app = App()

        @app.cell
        def _state():
            import marimo as mo

            get_bar, set_bar = mo.state(0.0)
            return get_bar, set_bar

        @app.cell
        def _strategy(get_bar):
            result = 2.0 * get_bar() + 1.0
            return (result,)

        async def _drive() -> float:
            from spike.marimo_embed.context_standup import init_strategy_runner

            runner, strat_cid = await init_strategy_runner(app)
            runner.globals["set_bar"](close)
            await runner.run({strat_cid})
            return float(runner.globals["result"])

        return asyncio.run(_drive())
    finally:
        kernel.teardown()


def expected(close: float) -> float:
    return 2.0 * close + 1.0
