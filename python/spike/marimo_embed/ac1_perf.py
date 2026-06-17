"""AC1 — per-tick performance: embed reactive drain vs direct on_bar (#76).

Three measurements, all over the SAME synthetic bar stream:

  HEADLINE   real ``KernelRunner.run()`` over 50k synthetic bars — proves the
             current ("直 on_bar") path completes N bars and gives its wall time.
  MECHANISM  an apples-to-apples harness whose surrounding per-bar loop is identical
             and that swaps ONLY the on_bar dispatch:
               * direct : await a coroutine that calls strategy_value(close)
               * embed  : await set_bar(close) -> runner.run({strategy_cell})
             Both wrapped in `await dispatch(bar)` so the async machinery cost is
             constant; the delta is the reactive-drain overhead. This is the
             slowdown MULTIPLIER the owner gate is about.

(The HEADLINE leg IS the real KernelRunner over the same synthetic stream, so it
doubles as the representativeness check — no separate 68-bar calibration needed.)

Reported per leg: steady-state MEDIAN per-bar (primary), COLD first-bar (compile,
broken out), and TAIL max per-bar. Determinism: fixed synthetic generator; numbers
vary run-to-run only by wall-clock noise (medians are stable).

Run: uv run --group spike python -m spike.marimo_embed.ac1_perf [N]
"""

from __future__ import annotations

import asyncio
import statistics
import sys
import time
from typing import Awaitable, Callable

from spike.marimo_embed.context_standup import HeadlessKernel, init_strategy_runner
from spike.marimo_embed.synthetic import (
    INSTRUMENT,
    MinimalStrategy,
    NoopSink,
    make_bars,
    strategy_value,
)


# ---------------------------------------------------------------------------
# HEADLINE — real KernelRunner over synthetic bars (load_universe_bars patched).
# ---------------------------------------------------------------------------


def run_real_kernel(bars) -> tuple[float, int]:
    """Run the actual production KernelRunner.run() over `bars`. Returns (wall_s, bars)."""
    import engine.kernel.runner as runner_mod
    from engine.kernel.runner import KernelRunner

    orig = runner_mod.load_universe_bars
    runner_mod.load_universe_bars = lambda *a, **k: list(bars)  # inject synthetic
    try:
        kr = KernelRunner(
            data_root="<synthetic>",
            instrument_id=INSTRUMENT,
            start="2024-01-01",
            end="2099-01-01",
            initial_cash=1_000_000.0,
            strategy=MinimalStrategy(),
            sink=NoopSink(),
        )
        t0 = time.perf_counter()
        result = kr.run()
        wall = time.perf_counter() - t0
        return wall, result.bars
    finally:
        runner_mod.load_universe_bars = orig


# ---------------------------------------------------------------------------
# MECHANISM — identical loop, only the dispatch differs. Per-bar timings.
# ---------------------------------------------------------------------------


async def bench_dispatch(
    bars, dispatch: Callable[[object], Awaitable[None]]
) -> list[float]:
    """Time each bar's `await dispatch(bar)`. Surrounding loop identical per leg."""
    per_bar: list[float] = []
    append = per_bar.append
    perf = time.perf_counter
    for bar in bars:
        t0 = perf()
        await dispatch(bar)
        append(perf() - t0)
    return per_bar


def _build_embed_app():
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        return get_bar, set_bar

    @app.cell
    def _strategy(get_bar):
        result = 2.0 * get_bar() + 1.0  # MUST mirror synthetic.strategy_value
        return (result,)

    return app


def _summarize(per_bar: list[float]) -> dict:
    cold = per_bar[0]
    steady = per_bar[1:] if len(per_bar) > 1 else per_bar
    return {
        "bars": len(per_bar),
        "cold_first_bar_us": cold * 1e6,
        "steady_median_us": statistics.median(steady) * 1e6,
        "steady_mean_us": statistics.fmean(steady) * 1e6,
        "tail_max_us": max(steady) * 1e6,
        "total_steady_s": sum(steady),
    }


async def run_mechanism(n: int) -> dict:
    bars = make_bars(n)

    # direct leg — plain strategy call inside a coroutine
    strat = MinimalStrategy()

    async def direct_dispatch(bar) -> None:  # noqa: ANN001
        strat.on_bar(bar)

    direct = await bench_dispatch(bars, direct_dispatch)

    # embed leg — set_bar -> drain the strategy cell
    app = _build_embed_app()
    runner, strat_cid = await init_strategy_runner(app)
    set_bar = runner.globals["set_bar"]

    async def embed_dispatch(bar) -> None:  # noqa: ANN001
        set_bar(bar.close)
        await runner.run({strat_cid})

    embed = await bench_dispatch(bars, embed_dispatch)

    # correctness: embed result must equal the direct computation on the last bar
    last_close = bars[-1].close
    embed_last = runner.globals["result"]
    assert abs(embed_last - strategy_value(last_close)) < 1e-9, (
        f"embed result {embed_last} != {strategy_value(last_close)}"
    )

    return {"direct": _summarize(direct), "embed": _summarize(embed)}


def main() -> int:
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 50_000

    # HEADLINE first (no kernel context needed).
    headline_wall, headline_bars = run_real_kernel(make_bars(n))

    kernel = HeadlessKernel()
    try:
        mech = asyncio.run(run_mechanism(n))
    finally:
        kernel.teardown()

    direct, embed = mech["direct"], mech["embed"]
    # Two DIFFERENT ratios — keep them distinct so the gate isn't read off the wrong one:
    #   isolated-dispatch tax = embed median / a trivial isolated on_bar (denominator is
    #     ~0.3us, so this ratio is the pure marimo machinery tax and is worst-case-large);
    #   vs-production           = embed wall / the REAL KernelRunner wall over the same N
    #     (the slowdown an owner actually feels vs today's path).
    mult_dispatch = embed["steady_median_us"] / direct["steady_median_us"]
    embed_total = embed["total_steady_s"] + embed["cold_first_bar_us"] / 1e6
    mult_vs_real = embed_total / headline_wall if headline_wall > 0 else float("inf")

    print("=" * 72)
    print(f"AC1 — per-tick performance (#76)   N = {n:,} bars")
    print("=" * 72)
    print(f"HEADLINE  real KernelRunner.run(): {headline_wall:.3f}s "
          f"for {headline_bars:,} bars ({headline_wall / headline_bars * 1e6:.2f} us/bar)")
    print("-" * 72)
    print("MECHANISM (identical loop, swap only on_bar dispatch):")
    for label, s in (("direct on_bar", direct), ("embed drain ", embed)):
        print(f"  {label}: cold={s['cold_first_bar_us']:.1f}us  "
              f"median={s['steady_median_us']:.2f}us  "
              f"mean={s['steady_mean_us']:.2f}us  "
              f"tail={s['tail_max_us']:.1f}us")
    print("-" * 72)
    print(f"  embed drain completed {embed['bars']:,} bars in "
          f"{embed_total:.3f}s (steady+cold)")
    print("-" * 72)
    print(f"  SLOWDOWN vs PRODUCTION (embed wall / real KernelRunner wall): "
          f"{mult_vs_real:,.0f}x  ({embed_total:.1f}s vs {headline_wall:.3f}s)")
    print(f"  isolated-dispatch tax (embed median / trivial direct median): "
          f"{mult_dispatch:,.0f}x")
    print("=" * 72)
    print("AC1 DONE (the load-bearing figure is ~ms/bar absolute + the vs-PRODUCTION x;")
    print("          gate N× is owner-confirmed against these. dispatch-tax is diagnostic.)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
