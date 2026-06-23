"""#112 S4 gates — granularity wiring (ADR-0025 D6).

The live bar cadence comes from the run's ``scenario.granularity`` (single source of truth), not a
session-global 60s. A Daily cell must NOT be silently driven at 1-minute (the accidental-parity
bug D6 kills). Pinned at three altitudes:

  * the helper ``granularity_to_interval_ns`` (Daily/Minute, case-insensitive, fail-closed);
  * ``LiveRunner.set_interval_ns`` rebuilds the aggregators for subscribed instruments;
  * ``controller.attach`` drives the runner to the run's granularity (Daily → 1 day; Minute → 60s).
"""
from __future__ import annotations

import asyncio
import os
import sys
import threading

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.kernel.duckdb_bars import granularity_to_interval_ns  # noqa: E402
from engine.kernel.live.controller import KernelLiveEngineController  # noqa: E402
from engine.kernel.strategy import Strategy as KernelStrategy  # noqa: E402
from engine.live.live_runner import LiveRunner  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.strategy_runtime.strategy_loader import load as load_strategy  # noqa: E402

_MINUTE_NS = 60 * 1_000_000_000
_DAILY_NS = 24 * 60 * 60 * 1_000_000_000
_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_SPIKE = os.path.join(_PYTHON_ROOT, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py")
_IID = "8918.TSE"


# ── helper ────────────────────────────────────────────────────────────────


def test_granularity_to_interval_ns() -> None:
    assert granularity_to_interval_ns("Minute") == _MINUTE_NS
    assert granularity_to_interval_ns("Daily") == _DAILY_NS
    assert granularity_to_interval_ns("daily") == _DAILY_NS  # case-insensitive
    assert granularity_to_interval_ns("  MINUTE ") == _MINUTE_NS  # whitespace
    with pytest.raises(ValueError):
        granularity_to_interval_ns("Hourly")  # fail-closed, never silent 1-minute


# ── LiveRunner.set_interval_ns ───────────────────────────────────────────────


def test_set_interval_ns_rebuilds_subscribed_aggregators() -> None:
    async def _drive():
        adapter = MockVenueAdapter()
        await adapter.login(None)
        runner = LiveRunner(adapter, interval_ns=_MINUTE_NS)
        await runner.subscribe(_IID)
        assert runner._aggregators[_IID][0]._interval_ns == _MINUTE_NS
        runner.set_interval_ns(_DAILY_NS)
        # the already-subscribed instrument's aggregator is rebuilt at the new interval (D6)
        assert runner._intervals_ns == (_DAILY_NS,)
        assert runner._aggregators[_IID][0]._interval_ns == _DAILY_NS
        # idempotent for the same interval
        same = runner._aggregators[_IID][0]
        runner.set_interval_ns(_DAILY_NS)
        assert runner._aggregators[_IID][0] is same

    asyncio.run(_drive())


def test_set_interval_ns_rejects_non_positive() -> None:
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=_MINUTE_NS)
    with pytest.raises(ValueError):
        runner.set_interval_ns(0)


# ── controller.attach drives the run's granularity ───────────────────────────


def _attach_with_granularity(granularity: str) -> tuple:
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="gran-loop", daemon=True)
    thread.start()

    def run(coro, timeout=10.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    adapter = MockVenueAdapter()
    # session default = Minute (the de-magic'd orchestrator default)
    runner = LiveRunner(adapter, interval_ns=granularity_to_interval_ns("Minute"))
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
    )
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())
        _m, _s, strategy_cls = load_strategy(_SPIKE, base_cls=KernelStrategy)
        scenario = {
            "schema_version": 2, "instruments": [_IID],
            "start": "2024-10-01", "end": "2025-01-10",
            "granularity": granularity, "initial_cash": 10_000_000,
        }
        controller.attach(
            strategy_cls=strategy_cls, scenario=scenario, instrument_id=_IID, venue="TSE",
            params={}, nautilus_strategy_id="LIVE-gran", session=object(),
        )
        interval = runner._intervals_ns
        agg_interval = runner._aggregators[_IID][0]._interval_ns
        controller.detach(nautilus_strategy_id="LIVE-gran")
        return interval, agg_interval
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_attach_minute_keeps_60s() -> None:
    session_default, agg_interval = _attach_with_granularity("Minute")
    assert session_default == (_MINUTE_NS,)
    assert agg_interval == _MINUTE_NS


def test_attach_daily_drives_one_day_not_one_minute() -> None:
    # The D6 centerpiece: a Daily run's instrument is driven at a 1-day interval, NOT silently at
    # 1-minute. The session default (`_intervals_ns`, used by manual subscribes) stays Minute — the
    # change is scoped to the run's universe so the shared runner's manual symbols are untouched.
    session_default, agg_interval = _attach_with_granularity("Daily")
    assert session_default == (_MINUTE_NS,)   # session default preserved (manual subscribes stay Minute)
    assert agg_interval == _DAILY_NS          # the run's instrument is driven Daily
    assert agg_interval != _MINUTE_NS


def test_set_interval_ns_scoped_leaves_other_symbols_untouched() -> None:
    # Regression guard: a Daily run must NOT change a manually-watched symbol's cadence (the shared
    # runner is used for UI watchlist subscriptions too).
    async def _drive():
        adapter = MockVenueAdapter()
        await adapter.login(None)
        runner = LiveRunner(adapter, interval_ns=_MINUTE_NS)
        await runner.subscribe("7203.TSE")   # the run's instrument
        await runner.subscribe("9999.TSE")   # a manually-watched (non-run) symbol
        runner.set_interval_ns(_DAILY_NS, instrument_ids=["7203.TSE"])
        assert runner._aggregators["7203.TSE"][0]._interval_ns == _DAILY_NS   # run → Daily
        assert runner._aggregators["9999.TSE"][0]._interval_ns == _MINUTE_NS  # manual → untouched
        assert runner._intervals_ns == (_MINUTE_NS,)  # session default for future subscribes preserved

    asyncio.run(_drive())
