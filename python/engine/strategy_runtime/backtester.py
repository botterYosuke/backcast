"""engine.strategy_runtime.backtester — the notebook ``bt`` handle (#95 Phase 3).

``Backtester`` is the single handle a Strategy Editor cell receives as a free ref (``bt``).
It is a thin, marimo-free façade over ``engine.kernel.stepper.KernelStepper`` — the SAME
per-bar Replay state machine ``KernelRunner`` drives — so notebook-authored backtests run
the real engine, byte-identical to the imperative golden (ADR-0016 "notebook = backtest";
#95 findings 0072). The cell author writes either style on the one handle:

    for bar in bt.replay(bars_per_second=2):   # B2 (#97): stream every bar
        if bar.close > bt.portfolio().avg_price and bt.portfolio().position == 0:
            bt.submit_market(100)

    bar = bt.step()                            # B3 (#98): one bar per RUN (bar-by-bar debug)
    if bar is not None and bar.close > bt.portfolio().avg_price:
        bt.submit_market(100)

Phase 3 scope (Q5): the ADR-locked API surface + parity + the ``bars_per_second`` /
``stop_event`` seams. NOT in Phase 3: pacing sleep (``bars_per_second`` is accepted and
stored but inserts no sleep), the host worker-thread driver, the running guard, and the
Phase 2 per-cell-RUN wiring of ``_close_open_bar``. ``Backtester`` is a single-threaded,
thread-agnostic pure-Python object: it advances on the thread that calls it.

Teardown (Q6 α): there is no explicit ``close()`` — the stepper owns no marimo Kernel /
DuckDB connection / OS resource, so the host drops the reference (``stop_event.set()`` →
drop → build a new ``bt``) and GC reclaims it.

Marimo-free (gated by test_strategy_runtime_offline): importing this module pulls no marimo.
"""
from __future__ import annotations

from pathlib import Path
from typing import Iterator, NoReturn, Optional

from typing import Callable

from engine.kernel.duckdb_bars import Bar, load_universe_bars
from engine.kernel.portfolio import PortfolioSnapshot
from engine.kernel.stepper import KernelStepper, RunResult, StepEvent
from engine.live.safety_rails import SafetyRails


class Backtester:
    """The ``bt`` handle: replay / step / bar / portfolio / submit_market over one stepper.

    Phase 4 (#95) gives the host two thin seams so a per-cell RUN can drive the full engine run
    without ``Backtester`` ever importing the engine / RunBuffer / observer (it stays marimo-free
    and host-independent — findings 0072 Q6, 0073 §P4-1):

      - ``on_run_begin`` — a host callback fired EXACTLY ONCE, the first time the cell actually
        drives the handle (``replay()`` / ``step()``). The host transitions the engine LOADED →
        RUNNING and clears ``last_portfolio`` there, so the running snapshot starts streaming to
        Hakoniwa. A cell that never drives the handle (pure-compute 土台) never fires it (ADR-0016
        D1: the boundary is the drive call, not the reference).
      - ``was_driven`` / ``result`` — after the cell run, the host reads these to finalize the
        RunBuffer → summary and ``force_stop_replay`` (RUNNING → IDLE).
    """

    def __init__(
        self, stepper: KernelStepper, *, on_run_begin: Optional[Callable[[], None]] = None
    ) -> None:
        self._stepper = stepper
        # Captured by replay() at stream start (immutable for the run — F6). None → full speed.
        self._bars_per_second: Optional[float] = None
        self._on_run_begin = on_run_begin
        self._run_begun = False
        self._result: Optional[RunResult] = None  # set once the run reaches a terminal
        # Cold-run guard (#95 P4-1): the host's NotebookSession DISARMS the handle while it
        # (re)builds the notebook graph — cold-running every cell to populate globals — so a
        # ``for bar in bt.replay()`` cell does NOT drive a backtest just because the notebook was
        # built/edited. Only an explicit RUN press re-arms it. Direct (pytest) use stays armed.
        self._armed = True

    @classmethod
    def from_scenario(
        cls,
        scenario: dict,
        *,
        data_root: str | Path,
        push_target=None,
        sink=None,
        rails: Optional[SafetyRails] = None,
        stop_event=None,
        on_run_begin: Optional[Callable[[], None]] = None,
    ) -> "Backtester":
        """Production wire: build a stepper from a normalized/validated scenario dict.

        ``scenario`` is the shape ``engine.strategy_runtime.scenario.load_scenario`` returns
        (``instruments`` / ``start`` / ``end`` / ``granularity`` / ``initial_cash``). No
        ``ScenarioConfig`` dataclass is introduced here (#95 Q4 — out of Phase 3 scope). The
        host owns the startup-panel config that produced the dict (ADR-0016 D5).
        """
        instrument_ids = list(scenario["instruments"])
        bars = load_universe_bars(
            data_root,
            instrument_ids,
            start=scenario["start"],
            end=scenario["end"],
            granularity=scenario["granularity"],
        )
        stepper = KernelStepper(
            bars=bars,
            instrument_ids=instrument_ids,
            initial_cash=float(scenario["initial_cash"]),
            strategy=None,  # the cell body is the strategy; the stepper owns fills/sink/portfolio
            push_target=push_target,
            sink=sink,
            rails=rails,
            stop_event=stop_event,
        )
        return cls(stepper, on_run_begin=on_run_begin)

    # -- host lifecycle seams (#95 Phase 4) -----------------------------------

    @property
    def was_driven(self) -> bool:
        """True once the cell drove the handle (``replay()`` / ``step()`` opened the run)."""
        return self._run_begun

    @property
    def result(self) -> Optional[RunResult]:
        """The terminal ``RunResult`` once the run reached END / STOPPED, else ``None``. The host
        reads this after the cell run to finalize the RunBuffer summary."""
        return self._result

    def arm(self) -> None:
        """Allow drive operations (an explicit RUN press). See ``_armed``."""
        self._armed = True

    def disarm(self) -> None:
        """Make ``replay()`` / ``step()`` inert (no drive) — used by the host around the cold-run
        graph build so building/editing the notebook never starts a backtest (#95 P4-1)."""
        self._armed = False

    def _begin_run_once(self) -> None:
        if not self._run_begun:
            self._run_begun = True
            if self._on_run_begin is not None:
                self._on_run_begin()

    # -- ADR-0016-locked API --------------------------------------------------

    def replay(self, *, bars_per_second: Optional[float] = None) -> Iterator[Bar]:
        """Stream every bar (B2 / #97). The cell body runs between yields; the next ``next()``
        closes the just-yielded bar (fills @ close, pushes) before yielding the following one —
        and the final ``next()`` closes the last bar and finalizes (StopIteration path), so the
        per-bar order matches the golden (findings 0070 F4/0008 §2).

        ``bars_per_second`` is CAPTURED here, at the start of the stream (#95 Phase 4 / ADR-0016
        D8-D9): the rate is immutable for this run (speed is not a live-mutable register — findings
        0070 F6). ``None`` → full speed (no sleep, GIL handed off by CPython auto-switch); a positive
        rate inserts a per-bar ``sleep(1 / bars_per_second)`` so the playback is watchable.
        """
        if not self._armed:  # cold-run / graph build: yield nothing, do not drive the engine
            return
        self._bars_per_second = bars_per_second
        self._stepper.set_pacing(bars_per_second)
        self._begin_run_once()
        while True:
            handle = self._stepper.open_next_bar()
            if handle.event is not StepEvent.BAR_OPEN:
                self._result = self._stepper.finalize()  # host reads bt.result to finalize summary
                return
            yield handle.bar

    def step(self) -> Optional[Bar]:
        """Advance exactly one bar and return it (B3 / #98). Returns ``None`` at the terminal
        (END / STOPPED). Pressing RUN again after the terminal keeps returning ``None`` (the
        stepper is idempotent once terminal). The previous step's bar is auto-closed here."""
        if not self._armed:  # cold-run / graph build: report no bar, do not drive the engine
            return None
        self._begin_run_once()
        handle = self._stepper.open_next_bar()
        if handle.event is StepEvent.BAR_OPEN:
            return handle.bar
        self._result = self._stepper.finalize()  # terminal reached: host finalizes the summary
        return None

    def bar(self) -> Optional[Bar]:
        """The current bar while one is open, else the last bar opened (``None`` before the
        first step)."""
        return self._stepper.current_or_last_bar()

    def portfolio(self) -> PortfolioSnapshot:
        """A frozen snapshot for the current/last bar's instrument (aggregate before the first
        step). ``.position`` / ``.avg_price`` read the primary instrument; ``.positions`` is the
        full multi-instrument book (#95 Q3)."""
        return self._stepper.portfolio_snapshot()

    def submit_market(self, qty: float) -> None:
        """Submit a signed delta MARKET order to the OPEN bar's instrument. Raises ``ValueError``
        outside an open bar (#95 Q3 a)."""
        self._stepper.submit_market(qty)

    # -- Phase 2 hook (idempotent, under-the-line public) ---------------------

    def _close_open_bar(self) -> None:
        """Close the currently-open bar without opening the next one. The Phase 2 per-cell-RUN
        ``finally`` calls this so a cell's submits settle (and Hakoniwa updates) before the user
        runs the next cell. Idempotent; safe to call when no bar is open (#95 Q2)."""
        self._stepper.close_current_bar()


# #95 Phase 5 (#98 / findings 0074): guidance text the placeholder ``bt`` emits.
_NO_SCENARIO_GUIDANCE = (
    "no active scenario; commit the startup panel first, then press RUN again"
)


class NoScenarioBacktester:
    """The fail-closed ``bt`` the host injects BEFORE the startup panel commits (#95 Phase 5 Q5).

    Every cell-facing drive / read operation raises a ``RuntimeError`` whose message tells the
    user how to recover ("commit the startup panel first").  Mirrors the Phase 3 ``submit_market``
    context-out fail-closed pattern (ADR-0016 D1).  ``_close_open_bar`` is a no-op so the Phase 2
    per-cell-RUN ``finally`` works without scenario-aware guarding.  ``arm`` / ``disarm`` are
    no-ops so ``NotebookSession._apply_inject`` can call them uniformly.

    The placeholder is type-compatible with ``Backtester`` on the union the cell body uses
    (``bt.step / bt.replay / bt.bar / bt.portfolio / bt.submit_market / bt._close_open_bar``) —
    so a cell written for the real ``bt`` surfaces the guidance INSTEAD of a NameError.
    """

    @staticmethod
    def _raise(method: str) -> NoReturn:
        raise RuntimeError(f"bt.{method}(): {_NO_SCENARIO_GUIDANCE}")

    def step(self) -> Optional[Bar]:
        self._raise("step")

    def replay(self, *, bars_per_second: Optional[float] = None) -> Iterator[Bar]:
        # Raised on call, not on iteration — ``for bar in bt.replay():`` sees the error before
        # the loop body, so the cell never enters a body that might observe undefined state.
        # Kept as a regular function (no ``yield``) so the error fires immediately; a generator
        # body would defer the raise until ``next()``, hiding the cause.
        self._raise("replay")

    def bar(self) -> Optional[Bar]:
        self._raise("bar")

    def portfolio(self) -> PortfolioSnapshot:
        self._raise("portfolio")

    def submit_market(self, qty: float) -> None:
        self._raise("submit_market")

    def _close_open_bar(self) -> None:
        # No bar is open — the Phase 2 finally must not raise here.
        return None

    def arm(self) -> None:
        # NotebookSession._apply_inject calls arm/disarm on the injected bt uniformly; on the
        # placeholder they are no-ops (there is no run to arm/disarm).
        return None

    def disarm(self) -> None:
        return None
