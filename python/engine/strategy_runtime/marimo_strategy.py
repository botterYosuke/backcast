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

# The canonical host-owned driver getter names (S6-6): the host owns the names; the author
# reads ``get_bar()`` / ``get_portfolio()`` as free refs. Both are seeded speculatively; the
# thin drain declares (and the adapter writes) only the subset a cell actually reads (P4/P5).
_BAR_DRIVER = "get_bar"
_PORTFOLIO_DRIVER = "get_portfolio"


class MarimoStrategy(Strategy):
    """Adapts a marimo cell-DAG ``App`` to the kernel's per-bar Strategy contract."""

    def __init__(
        self,
        *,
        app: Any,
        strategy_id: str,
        instrument_id: str,
        services: "dict[str, Any] | None" = None,
        constants: "dict[str, Any] | None" = None,
    ) -> None:
        super().__init__(strategy_id=strategy_id, instrument_id=instrument_id)
        self._app = app
        # Host-provided cell-facing seams forwarded to the thin drain (findings 0046 T4/T6):
        # ``services`` = live value-returning callables (e.g. the v19 scorer), ``constants`` =
        # live static data (e.g. the ordered universe / rs_ref). Both are read by cells as free
        # refs; the host owns the names. The deterministic v19 parity gate constructs these
        # directly (stub scorer + synthetic universe); the production resolver is a follow-up.
        self._services = services
        self._constants = constants
        self._stack: contextlib.ExitStack | None = None
        self._rt: Any = None
        self._active: frozenset[str] = frozenset()

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
        neutral_bar = Bar(
            instrument_id=self.instrument_id,
            ts_event_ns=0,
            open=0.0,
            high=0.0,
            low=0.0,
            close=0.0,
            volume=0.0,
        )
        # The portfolio driver is seeded with the run-start (flat) snapshot: reference_prices
        # is empty and the book is flat at on_start, so this is cash=initial / equity=initial /
        # position=0 — a valid neutral the cold compile run can read (P3).
        neutral_pf = self._ctx.portfolio_snapshot(self.instrument_id)
        submit = make_submit_market(
            self._ctx, strategy_id=self.id, default_instrument_id=self.instrument_id
        )
        self._stack = contextlib.ExitStack()
        self._rt = self._stack.enter_context(
            open_runtime(
                self._app,
                driver_seeds={_BAR_DRIVER: neutral_bar, _PORTFOLIO_DRIVER: neutral_pf},
                inject={"submit_market": submit},
                services=self._services,
                constants=self._constants,
            )
        )
        # Write only the drivers a cell actually reads (P5): the snapshot is built per bar only
        # when get_portfolio is read. If the strategy reads neither, _compile already raised.
        self._active = self._rt.active_drivers

    def on_bar(self, bar: Bar) -> None:
        # Drain the host-seeded drivers; the cell-DAG recomputes and orders flow through
        # ctx.pending exactly as the imperative on_bar does. The snapshot is captured at
        # on_bar entry — before this bar's fill — so get_portfolio is end-of-prev-bar (P3).
        values: dict[str, Any] = {}
        if _BAR_DRIVER in self._active:
            values[_BAR_DRIVER] = bar
        if _PORTFOLIO_DRIVER in self._active:
            values[_PORTFOLIO_DRIVER] = self._ctx.portfolio_snapshot(self.instrument_id)
        self._rt.drain(values)

    def close(self) -> None:
        """Tear down the headless kernel. Idempotent; the dispatch site calls this in finally."""
        if self._stack is not None:
            stack, self._stack, self._rt = self._stack, None, None
            stack.close()
