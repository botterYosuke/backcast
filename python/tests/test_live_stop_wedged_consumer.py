"""#112 HITL regression — stop must not hang on a wedged (uncancellable) venue submit.

The Tachibana demo HITL froze: a live cell run's per-cell-RUN ■ never restored to ▶. faulthandler
showed the bus consumer wedged in ``await broker.submit`` (an order submit blocked on the venue's
second-password / slow HTTP that ignores cancellation), so ``driver.stop()``'s un-timed
``await task`` never returned → ``on_stop`` never ran → the cell-bridge stop sentinel was never put
→ the worker was leaked forever and the live loop teardown hung (nautilus live-loop teardown
contract). The fix bounds the consumer-stop wait with ``asyncio.wait`` (NOT ``await task`` /
``wait_for``, which both re-await a cancellation-suppressing task and hang the same way).

RED before the fix: ``stop_live_strategy`` never returns (this test times out). GREEN after: it
returns within a couple seconds even though the submit stays wedged.

No marimo/venue specifics — a MockVenueAdapter with a cancellation-suppressing ``submit_order`` plus
a one-shot-buy marimo cell reproduces the exact wedge deterministically and in-process.
"""
from __future__ import annotations

import asyncio
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

from engine.core import DataEngine  # noqa: E402
from engine.live.live_orchestrator import LiveLoopManager  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.live.state_machine import VenueStateMachine  # noqa: E402
from engine.mode_manager import ModeManager  # noqa: E402

IID = "8918.TSE"
DAY_NS = 86_400 * 1_000_000_000
_CELL = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "spike", "fixtures", "strategies", "kernel_buy_once_cell.py",
)


def _kline(i: int, close: float):
    from engine.live.adapter import KlineUpdate
    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def test_stop_does_not_hang_on_wedged_uncancellable_submit() -> None:
    mock = MockVenueAdapter()
    mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())

    # Worst-case real venue: a submit that blocks AND ignores cancellation (e.g. a second-password
    # await / sync HTTP). This wedges the driver's bus consumer in ``await broker.submit``.
    async def _hang_submit(*_a, **_k):
        while True:
            try:
                await asyncio.sleep(3600)
            except asyncio.CancelledError:
                continue  # suppress cancel
    mock.submit_order = _hang_submit

    de = DataEngine()
    vsm = VenueStateMachine()
    de.state_machine = vsm
    mm = ModeManager(venue_sm=vsm, replay_engine=de)
    de.attach_mode_manager(mm)
    mgr = LiveLoopManager(
        engine=de, mode_manager=mm, venue_sm=vsm,
        live_adapter_factory=lambda env_hint=None: mock,
        live_venue_id="MOCK", engine_controller=None,
        publish_backend_event_callback=lambda e: None,
    )

    try:
        reg = mgr.register_live_strategy(strategy_file=_CELL, original_path=_CELL)
        assert reg.success, (reg.error_code, reg.error_message)
        assert mgr.venue_login("MOCK", "env", None).success
        assert mgr.set_execution_mode("LiveAuto").success
        start = mgr.start_live_strategy(reg.strategy_id, IID, "MOCK")
        assert start.success, (start.error_code, start.error_message)
        run_id = start.run_id

        # A bar that makes the cell submit → the consumer wedges in the hanging submit.
        for i in range(1, 3):
            mgr._live_loop.call_soon_threadsafe(mock.inject_tick, _kline(i, 8.0))
        time.sleep(1.0)

        # Stop on a watchdog thread so a regression (infinite hang) fails the test instead of
        # hanging the whole suite.
        result: dict = {}

        def _do_stop():
            result["ack"] = mgr.stop_live_strategy(run_id)

        t = threading.Thread(target=_do_stop, name="stop-watchdog", daemon=True)
        t.start()
        t.join(timeout=8.0)
        assert not t.is_alive(), "stop_live_strategy hung on the wedged consumer (teardown deadlock)"
        assert result["ack"].success, result["ack"].error_code
    finally:
        try:
            mgr.set_execution_mode("Replay")
            mgr.venue_logout()
            mgr.stop_live_loop(timeout=3.0)
        except Exception:
            pass
