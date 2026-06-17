"""MarimoStrategy adapter — drive a marimo cell-DAG through the kernel contract (#76 S6a).

ADR-0012 Decision 2 fixes the kernel's per-bar contract (``register`` → ``on_start`` →
``on_bar`` → ``on_stop`` + ``ctx.submit_market``) as the immutable adaptation boundary. This
adapter is a ``Strategy`` that makes a marimo App conform to it, so ``KernelRunner`` runs a
marimo strategy and an imperative one through the SAME per-bar loop, unchanged — keeping the
#24 golden byte-identical (ADR-0006).

Lifecycle (findings 0046 S6-1/S6-2):
  register(ctx)  capture the kernel ``_Context`` (the order/portfolio seam) — base Strategy.
  on_start()     LAZILY import the thin drain (lazy-import discipline,
                 test_strategy_runtime_offline), build the cell-facing ``submit_market`` bound
                 to ctx (S4), and open the thin-drain runtime — owning the headless kernel's
                 lifetime via an ExitStack.
  on_bar(bar)    drain the bar into the host-seeded ``get_bar`` driver; the cell-DAG
                 recomputes and any ``submit_market(qty)`` routes through ctx.pending — the
                 SAME fill path the imperative loop uses.
  close()        tear down the headless kernel. ``KernelRunner.run`` does NOT wrap its loop
                 in try/finally, so the dispatch site (``_backend_impl``) calls close() in a
                 finally — a mid-bar cell raise must not leak the thread-local marimo context
                 ("RuntimeContext already initialized" on the next run). Idempotent.

The App is loaded (parsed) by the dispatch site and passed in, so this module top-imports
nothing from marimo / thin_drain (open_runtime is imported lazily in on_start) — the runtime
seam stays marimo-free at module load.
"""
from __future__ import annotations

import contextlib
from typing import Any

from engine.kernel.duckdb_bars import Bar
from engine.kernel.strategy import Strategy

# The canonical host-owned driver getter name (S6-6): the host owns the name; the author
# reads ``get_bar()`` as a free ref. The portfolio driver joins this set in a later slice.
_BAR_DRIVER = "get_bar"


class MarimoStrategy(Strategy):
    """Adapts a marimo cell-DAG ``App`` to the kernel's per-bar Strategy contract."""

    def __init__(self, *, app: Any, strategy_id: str, instrument_id: str) -> None:
        super().__init__(strategy_id=strategy_id, instrument_id=instrument_id)
        self._app = app
        self._stack: contextlib.ExitStack | None = None
        self._rt: Any = None

    def on_start(self) -> None:
        # Lazy: keep marimo off the module-load path (lazy-import discipline). thin_drain
        # top-imports marimo via the narrow-submodule style; cell_api is marimo-free.
        from engine.strategy_runtime.cell_api import make_submit_market
        from engine.strategy_runtime.thin_drain import open_runtime

        if self._ctx is None:
            raise RuntimeError("MarimoStrategy.on_start called before register()")

        # Cold compile runs every cell once before any bar; the driver is seeded with a neutral
        # bar (close=0.0) so the cold run executes without a live bar. The result is discarded —
        # the hot drain re-runs the cells with the real bar. submit_market is inert during the
        # cold run (thin_drain arms it after), so no spurious order fires.
        neutral = Bar(
            instrument_id=self.instrument_id,
            ts_event_ns=0,
            open=0.0,
            high=0.0,
            low=0.0,
            close=0.0,
            volume=0.0,
        )
        submit = make_submit_market(
            self._ctx, strategy_id=self.id, default_instrument_id=self.instrument_id
        )
        self._stack = contextlib.ExitStack()
        self._rt = self._stack.enter_context(
            open_runtime(
                self._app, driver_seeds={_BAR_DRIVER: neutral}, inject={"submit_market": submit}
            )
        )

    def on_bar(self, bar: Bar) -> None:
        # Drain the bar into the host-seeded driver; the cell-DAG recomputes and orders flow
        # through ctx.pending exactly as the imperative on_bar does.
        self._rt.drain({_BAR_DRIVER: bar})

    def close(self) -> None:
        """Tear down the headless kernel. Idempotent; the dispatch site calls this in finally."""
        if self._stack is not None:
            stack, self._stack, self._rt = self._stack, None, None
            stack.close()
