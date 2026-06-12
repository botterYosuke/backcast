"""engine.strategy_runtime.engine_runner — BacktestEngine streaming runner (Step 3A).

Thin wrapper around replay_runner.run() that adapts the RunBufferLike sink interface.
The full lifecycle (BacktestEngine setup, streaming loop, cleanup) now lives in replay_runner.

Public API:
    RunBufferLike   — write_fill / write_equity Protocol (kept for backward compat)
    run(...)        — streaming loop entry point (signature unchanged)
"""
from __future__ import annotations

import logging
import threading
from typing import Any, Protocol, runtime_checkable

log = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Protocol (kept for backward compatibility)
# ---------------------------------------------------------------------------


@runtime_checkable
class RunBufferLike(Protocol):
    def write_fill(self, event: dict) -> None: ...  # noqa: E704
    def write_equity(self, event: dict) -> None: ...  # noqa: E704


# ---------------------------------------------------------------------------
# Adapter: RunBufferLike → ReplaySink
# ---------------------------------------------------------------------------


class _RunBufferAdapter:
    """Wraps RunBufferLike as a ReplaySink for replay_runner.run()."""

    def __init__(self, run_buffer: Any) -> None:
        self._buf = run_buffer

    def on_equity(self, event: dict) -> None:
        self._buf.write_equity(event)

    def on_fill(self, event: dict) -> None:
        self._buf.write_fill(event)

    def on_complete(self, engine: Any) -> None:
        pass  # RunBuffer.finish() is called by the caller, not the runner


# ---------------------------------------------------------------------------
# Runner (public API unchanged)
# ---------------------------------------------------------------------------


def run(
    *,
    strategy_cls,
    scenario: dict,
    bars_by_instrument: dict,
    run_buffer: RunBufferLike,
    strategy_init_kwargs: dict | None = None,
    run_event: threading.Event | None = None,
    bar_interval_sec: float = 0.0,
) -> None:
    """BacktestEngine streaming runner — delegates to replay_runner.run().

    Parameters
    ----------
    strategy_cls:
        nautilus_trader.trading.strategy.Strategy subclass.
    scenario:
        SCENARIO dict. instrument/instruments / granularity / initial_cash.
    bars_by_instrument:
        {InstrumentId: list[Bar]} — from catalog_data_loader or synthetic bars.
    run_buffer:
        RunBufferLike to receive fill / equity events.
    strategy_init_kwargs:
        strategy_cls(**strategy_init_kwargs). None → {}.
    run_event:
        threading.Event; run_event.wait() called before each bar. None = unthrottled.
    bar_interval_sec:
        Wallclock sleep in seconds after each bar. 0 = disabled.
    """
    from engine.strategy_runtime.replay_runner import run as _replay_run

    _replay_run(
        strategy_cls=strategy_cls,
        scenario=scenario,
        bars_by_instrument=bars_by_instrument,
        sink=_RunBufferAdapter(run_buffer),
        instruments_override=None,
        strategy_init_kwargs=strategy_init_kwargs,
        run_event=run_event,
        bar_interval_sec=bar_interval_sec,
    )
