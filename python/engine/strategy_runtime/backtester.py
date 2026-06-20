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
from typing import Iterator, Optional

from engine.kernel.duckdb_bars import Bar, load_universe_bars
from engine.kernel.portfolio import PortfolioSnapshot
from engine.kernel.stepper import KernelStepper, StepEvent
from engine.live.safety_rails import SafetyRails


class Backtester:
    """The ``bt`` handle: replay / step / bar / portfolio / submit_market over one stepper."""

    def __init__(self, stepper: KernelStepper) -> None:
        self._stepper = stepper
        # Stored for Phase 4 pacing (per-bar sleep). Phase 3 accepts it but never sleeps.
        self._bars_per_second: Optional[float] = None

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
        return cls(stepper)

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
        self._bars_per_second = bars_per_second
        self._stepper.set_pacing(bars_per_second)
        while True:
            handle = self._stepper.open_next_bar()
            if handle.event is not StepEvent.BAR_OPEN:
                return
            yield handle.bar

    def step(self) -> Optional[Bar]:
        """Advance exactly one bar and return it (B3 / #98). Returns ``None`` at the terminal
        (END / STOPPED). Pressing RUN again after the terminal keeps returning ``None`` (the
        stepper is idempotent once terminal). The previous step's bar is auto-closed here."""
        handle = self._stepper.open_next_bar()
        return handle.bar if handle.event is StepEvent.BAR_OPEN else None

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
