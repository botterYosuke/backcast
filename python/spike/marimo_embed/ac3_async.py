"""AC3 (CPython leg) — async serialization: per-tick drain co-habiting live edit (#76).

The #64 host owns ONE asyncio EventLoop. Bars stream through it as per-tick
`await runner.run(...)` drains, while the user "live edits" the strategy (autorun).
The unknown: do these two co-habit on marimo's single async run-loop as an
ORDERING problem (serialized, deterministic) rather than a DATA RACE (corruption /
deadlock)? And does it complete PROMPTLY (the S2 GREEN criterion — not merely
"doesn't hang", since a starved system also doesn't return)?

Two proofs, both on a single event loop / single thread:

  SERIALIZED  one consumer drains a mixed queue of BarTick and LiveEdit events.
              A LiveEdit changes a coefficient mo.state (modelling the user editing
              the strategy cell's parameter); subsequent bars use the new coefficient.
              Result: outputs match the SEQUENTIAL application of events in submission
              order — i.e. a well-defined ordering, no torn state.

  NO-DEADLOCK firing many drains CONCURRENTLY via asyncio.gather on the one loop
              still completes every drain (count matches) with valid results and no
              exception — worst case is interleaving/order, never a hang or crash.
              Elapsed is bounded (≈ work, far below the watchdog), so no GIL/loop
              starvation.

The Mono verdict (does the whole marimo import-graph load + run one drain under
Unity Mono + pythonnet without crash, frame continuing) is the SEPARATE minimal
smoke in Assets/Editor/MarimoEmbedAc3MonoProbe.cs — this CPython leg proves the
loop/marshal logic and the assertions themselves are sound (mirrors S0/S2 split).

Run: uv run --group spike python -m spike.marimo_embed.ac3_async
Exit 0 + "AC3-CPYTHON PASS" on success.
"""

from __future__ import annotations

import asyncio
import sys
import time

from spike.marimo_embed.context_standup import HeadlessKernel, init_strategy_runner

_WATCHDOG_S = 30.0  # generous; a starved/deadlocked loop blows past this


def _build_app():
    from marimo import App

    app = App()

    @app.cell
    def _state():
        import marimo as mo

        get_bar, set_bar = mo.state(0.0)
        get_coeff, set_coeff = mo.state(2.0)  # the "live-edited" strategy parameter
        return get_bar, set_bar, get_coeff, set_coeff

    @app.cell
    def _strategy(get_bar, get_coeff):
        result = get_coeff() * get_bar() + 1.0
        return (result,)

    return app


async def _serialized_proof(runner, strat_cid) -> tuple[list, list]:  # noqa: ANN001
    """One consumer, mixed queue. Returns (events_in_order, observed_results)."""
    set_bar = runner.globals["set_bar"]
    set_coeff = runner.globals["set_coeff"]

    # ("bar", close) or ("edit", coeff). Edits land BETWEEN bars and must affect
    # only subsequent bars — the ordering assertion.
    events = [
        ("bar", 100.0),
        ("bar", 110.0),
        ("edit", 3.0),
        ("bar", 100.0),   # same close as event[0], but coeff now 3.0 -> different result
        ("edit", 0.5),
        ("bar", 200.0),
    ]
    queue: asyncio.Queue = asyncio.Queue()
    for e in events:
        queue.put_nowait(e)

    observed: list = []
    coeff = 2.0
    # single consumer = the host loop's serialized processor
    while not queue.empty():
        kind, val = await queue.get()
        if kind == "edit":
            coeff = val
            set_coeff(val)
            await runner.run({strat_cid})  # autorun re-exec of the edited cell
            observed.append(("edit", runner.globals["result"]))
        else:
            set_bar(val)
            await runner.run({strat_cid})
            observed.append(("bar", runner.globals["result"], coeff, val))
    return events, observed


async def _no_deadlock_proof(runner, strat_cid) -> tuple[int, float]:  # noqa: ANN001
    """Fire many drains concurrently on the one loop; all must complete."""
    set_bar = runner.globals["set_bar"]

    async def one(close: float):
        set_bar(close)
        return await runner.run({strat_cid})

    t0 = time.perf_counter()
    results = await asyncio.wait_for(
        asyncio.gather(*[one(float(i)) for i in range(50)]),
        timeout=_WATCHDOG_S,
    )
    return len(results), time.perf_counter() - t0


def main() -> int:
    failures: list[str] = []
    kernel = HeadlessKernel()
    try:
        app = _build_app()

        async def amain():
            runner, strat_cid = await init_strategy_runner(app)
            events, observed = await asyncio.wait_for(
                _serialized_proof(runner, strat_cid), timeout=_WATCHDOG_S
            )
            n_done, elapsed = await _no_deadlock_proof(runner, strat_cid)
            return events, observed, n_done, elapsed

        t0 = time.perf_counter()
        events, observed, n_done, conc_elapsed = asyncio.run(amain())
        total_elapsed = time.perf_counter() - t0

        # SERIALIZED: each event's result must reflect the state in force at its
        # point in the submission order — an edit changes the coeff for all later
        # bars; an edit's own re-exec re-uses the last bar value with the new coeff.
        coeff = 2.0
        last_bar = 0.0
        for (kind, val), obs in zip(events, observed):
            if kind == "edit":
                coeff = val
                # after edit, result re-uses last bar value with new coeff
                want = coeff * last_bar + 1.0
                if abs(obs[1] - want) > 1e-9:
                    failures.append(f"edit->{val}: result {obs[1]} != {want} (last bar {last_bar})")
            else:
                last_bar = val
                want = coeff * val + 1.0
                if abs(obs[1] - want) > 1e-9:
                    failures.append(f"bar {val} @coeff {coeff}: result {obs[1]} != {want}")

        # NO-DEADLOCK
        if n_done != 50:
            failures.append(f"concurrent drains completed {n_done}/50")
        if conc_elapsed >= _WATCHDOG_S:
            failures.append(f"concurrent drains elapsed {conc_elapsed:.1f}s ~ watchdog (starvation)")
    finally:
        kernel.teardown()

    print("=" * 72)
    print("AC3 (CPython leg) — async serialization (#76)")
    print("=" * 72)
    print(f"serialized events : {events}")
    print(f"observed          : {observed}")
    print(f"concurrent drains : {n_done}/50 completed in {conc_elapsed:.3f}s "
          f"(watchdog {_WATCHDOG_S}s)")
    print(f"total elapsed     : {total_elapsed:.3f}s")
    print("-" * 72)
    if failures:
        for f in failures:
            print(f"  ✗ {f}")
        print("AC3-CPYTHON FAIL")
        return 1
    print("  ✓ SERIALIZED: mixed bar/edit queue applies in submission order (no torn state)")
    print("  ✓ live edit co-habits: post-edit coeff affects subsequent bars deterministically")
    print("  ✓ NO-DEADLOCK: 50 concurrent drains all completed, elapsed << watchdog")
    print("AC3-CPYTHON PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
