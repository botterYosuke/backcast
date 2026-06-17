"""spike-0 — F1 reachability (#76, the load-bearing de-risk).

F1 question: can a marimo ``KernelRuntimeContext`` be stood up HEADLESS, in-process,
under plain CPython such that ``App.embed()`` takes its reactive (AppKernelRunner)
branch and a downstream cell **re-executes reactively** when an upstream def
changes? If no, the entire #64 construct (per-bar reactive recompute) is
unreachable and AC1-3 are moot.

PASS criteria — the REAL invariants (per the F1 adjudication; the original strict
"no marimo._server in sys.modules" bar was a proxy that over-approximated the true
constraints and is replaced here):

  F1-POSITIVE  running_in_notebook() is True after headless stand-up, AND
               App.embed() reactively recomputes a downstream cell for >=2 distinct
               upstream seeds (a constant output can satisfy at most one seed).
  ORPHAN-FREE  ADR-0001: no spawned child process, no LISTEN socket (asserted both
               before and after the embed run — import-time AND runtime-stable).
  SERVER-LOAD  the marimo._server.* modules that are import-resident are RECORDED
               (not failed on); pure-Python / never-run / zero native-teardown, so
               they do not transfer the nautilus_pyo3 Mono-crash risk the proxy
               guarded. The Mono load itself is a separate leg (AC3 smoke).

Run:  uv run --group spike python -m spike.marimo_embed.spike0_context_standup
Exit 0 + "SPIKE0 PASS" marker on success; non-zero + "SPIKE0 FAIL" otherwise.
"""

from __future__ import annotations

import asyncio
import sys


def _build_app():
    """Two-cell app: an overridable `seed`, and a downstream `result = 2*seed + 1`.

    embed(defs={"seed": S}) prunes the seed-defining cell and runs the downstream
    one against the injected value, so `result` is a pure function of the override
    — the minimal reactive-recompute witness.
    """
    from marimo import App

    app = App()

    @app.cell
    def _seed():
        seed = 1
        return (seed,)

    @app.cell
    def _double(seed):
        result = 2 * seed + 1
        return (result,)

    return app


async def _embed_with_seed(app, seed: int) -> int:
    res = await app.embed(defs={"seed": seed})
    return res.defs["result"]


def main() -> int:
    from spike.marimo_embed.context_standup import (
        HeadlessKernel,
        assert_orphan_free,
        resident_server_modules,
    )
    from marimo._runtime.context.utils import running_in_notebook

    failures: list[str] = []

    # Orphan-free at IMPORT time (marimo fully imported, before any context).
    try:
        before = assert_orphan_free()
    except AssertionError as exc:
        failures.append(f"orphan-free (import-time): {exc}")
        before = {}

    server_modules = resident_server_modules()

    kernel = HeadlessKernel()
    try:
        # F1 gate: the headless stand-up must flip running_in_notebook() True, else
        # App.embed() falls into the run-once branch and reactivity is unreachable.
        in_notebook = running_in_notebook()
        if not in_notebook:
            failures.append("running_in_notebook() is False — embed reactive branch unreachable")

        # Reactive recompute across 2 distinct seeds (constant can match <=1).
        app = _build_app()
        seeds = [20, 100]
        expected = {s: 2 * s + 1 for s in seeds}  # 20->41, 100->201
        got = {s: asyncio.run(_embed_with_seed(app, s)) for s in seeds}
        if got != expected:
            failures.append(f"reactive recompute mismatch: got {got}, expected {expected}")

        # Orphan-free AFTER the embed run — import-time guarantee promoted to
        # runtime-stable (no socket / child spawned by running cells).
        try:
            after = assert_orphan_free()
        except AssertionError as exc:
            failures.append(f"orphan-free (post-embed): {exc}")
            after = {}

        # No NEW marimo._server.* modules appeared from running the embed.
        new_server = sorted(set(resident_server_modules()) - set(server_modules))
        if new_server:
            failures.append(f"new marimo._server.* modules after embed: {new_server}")
    finally:
        kernel.teardown()

    # ---- report ----
    print("=" * 72)
    print("spike-0 — F1 reachability (#76)")
    print("=" * 72)
    print(f"running_in_notebook()         : {in_notebook}")
    print(f"reactive embed (seed->result) : {got}")
    print(f"orphan-free (import-time)     : {before}")
    print(f"orphan-free (post-embed)      : {after}")
    print(f"marimo._server.* resident     : {len(server_modules)} modules")
    for m in server_modules:
        print(f"    - {m}")
    print("-" * 72)

    if failures:
        for f in failures:
            print(f"  ✗ {f}")
        print("SPIKE0 FAIL")
        return 1

    print("  ✓ F1-POSITIVE: headless context → reactive embed recompute (2 seeds)")
    print("  ✓ ORPHAN-FREE: no child process / no LISTEN socket (import + post-embed)")
    print(f"  ✓ SERVER-LOAD recorded: {len(server_modules)} pure-Python never-run modules")
    print("SPIKE0 PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
