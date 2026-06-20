"""engine.kernel.runner — EventLoop wiring for the Backcast Execution Kernel (#24).

Drives a Replay run deterministically and pushes the existing sink contract, golden-
comparable to the Nautilus oracle. Since #95 Phase 3 the per-bar state machine lives in
``engine.kernel.stepper.KernelStepper``; ``KernelRunner`` is the thin "load every bar →
drive the stepper to the end → return the run result" wrapper. The per-bar order and the
sink contract are unchanged, so the #24 golden stays byte-identical (ADR-0006): the wrapper
and the notebook ``bt`` handle (``Backtester``) hit the same stepper primitives.

``RunResult`` / ``_Context`` moved to ``stepper`` and are re-exported here for back-compat.

Nautilus-free: importing this module loads no Rust core (gated by the Mono teardown test).
"""
from __future__ import annotations

from pathlib import Path
from typing import Optional

from engine.kernel.duckdb_bars import load_universe_bars
from engine.kernel.sink import EventSink
from engine.kernel.stepper import (  # noqa: F401  re-export (back-compat)
    KernelStepper,
    RunResult,
    StepEvent,
    StepHandle,
    _Context,
)
from engine.kernel.strategy import Strategy
from engine.live.safety_rails import SafetyRails


class KernelRunner:
    """Runs one Replay tracer: load bars → drive the stepper to the end → run result."""

    def __init__(
        self,
        *,
        data_root: str | Path,
        instrument_id: str | None = None,
        instrument_ids: Optional[list[str]] = None,
        granularity: str = "Daily",
        start: str,
        end: str,
        initial_cash: float,
        strategy: Strategy,
        push_target=None,
        sink=None,
        rails: Optional[SafetyRails] = None,
        bar_interval_sec: float = 0.0,
        stop_event=None,
    ) -> None:
        self._data_root = data_root  # J-Quants DuckDB root (ADR-0006); <root>/<table>/<code>.duckdb
        # Single instrument (#47) or a universe (#48); universe is time-order-merged into one
        # stream. instrument_id is kept for back-compat with existing single-instrument callers.
        if instrument_ids is None:
            if instrument_id is None:
                raise ValueError("KernelRunner requires instrument_id or instrument_ids")
            instrument_ids = [instrument_id]
        if not instrument_ids:
            raise ValueError("instrument_ids must be non-empty")
        self._instrument_ids = list(instrument_ids)
        self._granularity = granularity
        self._start = start
        self._end = end
        self._initial_cash = float(initial_cash)
        self._strategy = strategy
        # Resolve the sink now so a misconfigured runner fails at CONSTRUCTION (existing
        # contract: test_requires_push_target_or_sink). golden #24 callers pass `push_target`
        # and get the default EventSink; the production Replay seam passes its own `sink`.
        if sink is not None:
            self._sink = sink
        elif push_target is not None:
            self._sink = EventSink(push_target)
        else:
            raise ValueError("KernelRunner requires either push_target or sink")
        self._rails = rails
        self._bar_interval_sec = bar_interval_sec
        self._stop_event = stop_event

    def run(self) -> RunResult:
        bars = load_universe_bars(
            self._data_root,
            self._instrument_ids,
            start=self._start,
            end=self._end,
            granularity=self._granularity,
        )
        stepper = KernelStepper(
            bars=bars,
            instrument_ids=self._instrument_ids,
            initial_cash=self._initial_cash,
            strategy=self._strategy,
            sink=self._sink,
            rails=self._rails,
            bar_interval_sec=self._bar_interval_sec,
            stop_event=self._stop_event,
        )
        # Drive every bar through the same primitives bt.replay()/bt.step() use. The terminal
        # open_next_bar() (END/STOPPED) closes the last bar and finalizes; finalize() returns
        # the already-computed result.
        while stepper.open_next_bar().event is StepEvent.BAR_OPEN:
            pass
        return stepper.finalize()
