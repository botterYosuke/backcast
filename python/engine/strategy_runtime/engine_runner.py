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
    """Wraps RunBufferLike as a ReplaySink for replay_runner.run().

    When ``on_bar`` is supplied, exposes get_extra_subscriptions() so the runner
    subscribes a per-instrument bar handler that forwards each bar to ``on_bar``
    INSIDE the 1-bar-at-a-time streaming loop (issue #29: bar-by-bar live
    following). Without ``on_bar`` the adapter subscribes nothing extra and the
    runner behaves exactly as before (backward compatible).
    """

    def __init__(self, run_buffer: Any, on_bar=None) -> None:
        self._buf = run_buffer
        self._on_bar = on_bar

    def on_equity(self, event: dict) -> None:
        self._buf.write_equity(event)

    def on_fill(self, event: dict) -> None:
        self._buf.write_fill(event)

    def on_complete(self, engine: Any) -> None:
        pass  # RunBuffer.finish() is called by the caller, not the runner

    def get_extra_subscriptions(
        self, *, engine, instruments, granularity, strategy_id_str, cache, venue_str
    ) -> dict:
        if self._on_bar is None:
            return {}
        from engine.strategy_runtime.catalog_data_loader import bar_type_for_instrument

        subs: dict = {}
        for symbol in instruments:
            bar_type_str = bar_type_for_instrument(symbol, granularity)
            subs[f"data.bars.{bar_type_str}"] = self._make_bar_handler(symbol)
        return subs

    def _make_bar_handler(self, iid_str: str):
        def _handler(bar) -> None:
            try:
                self._on_bar(bar, iid_str)
            except Exception:
                log.warning("[engine_runner] on_bar failed: instrument=%r", iid_str, exc_info=True)

        return _handler


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
    on_bar=None,
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
    on_bar:
        Optional callback ``on_bar(bar, instrument_id)`` invoked per bar inside
        the streaming loop (issue #29: bar-by-bar live chart following). None =
        no per-bar streaming (legacy behavior).
    """
    from engine.strategy_runtime.replay_runner import run as _replay_run

    _replay_run(
        strategy_cls=strategy_cls,
        scenario=scenario,
        bars_by_instrument=bars_by_instrument,
        sink=_RunBufferAdapter(run_buffer, on_bar=on_bar),
        instruments_override=None,
        strategy_init_kwargs=strategy_init_kwargs,
        run_event=run_event,
        bar_interval_sec=bar_interval_sec,
    )
