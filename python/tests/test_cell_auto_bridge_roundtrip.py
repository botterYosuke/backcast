"""#112 S6 — a marimo cell driven in Auto through the FULL live seam (ADR-0025).

The S2 gate proved the rendezvous in isolation (manual drive_bar). This drives the SAME path the
production controller does — ``KernelLiveEngineController.attach`` → ``driver._consume`` →
``await bridge.drive_bar`` → worker cell loop → buffered submit → ``_drain`` → ``MockVenueAdapter``
— with REAL injected bars, and asserts the cell produces the imperative twin's order/fill sequence
(``kernel_spike_buy_sell.py``: BUY 1 lot at bar 3, SELL at bar 40). This closes the bridge ⇄
controller integration the inproc lifecycle gate left untested (it only idled).

Deterministic (no model) — the v19 parity centerpiece (``test_v19_cell_auto_parity``) reuses this
harness with the real v19 cell.
"""
from __future__ import annotations

import asyncio
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.kernel.live.controller import KernelLiveEngineController  # noqa: E402
from engine.live.adapter import KlineUpdate  # noqa: E402
from engine.live.live_runner import LiveRunner  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader  # noqa: E402

_IID = "8918.TSE"
_CELL = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "spike", "fixtures", "strategies", "kernel_spike_buy_sell_cell.py",
)


def _kline(ts_ns: int, close: float, *, is_closed: bool = True) -> KlineUpdate:
    return KlineUpdate(
        kind="kline", instrument_id=_IID, ts_ns=ts_ns,
        open=close, high=close, low=close, close=close, volume=1000.0, is_closed=is_closed,
    )


def test_spike_cell_auto_roundtrip_matches_imperative_plan() -> None:
    app, scenario, bridge_factory = build_live_marimo_loader()(_CELL)
    assert scenario["instruments"] == [_IID]

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="cell-auto-loop", daemon=True)
    thread.start()

    def run(coro, timeout=10.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    order_events: list = []
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=60 * 1_000_000_000)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=lambda ev, sid: order_events.append(ev),
    )

    def _fills() -> list:
        return [ev for ev in order_events if ev.status == "FILLED"]

    def _wait(predicate, timeout=25.0, what="condition") -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if predicate():
                return
            time.sleep(0.02)
        raise AssertionError(f"timeout waiting for {what}; fills={len(_fills())}")

    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())

        controller.attach(
            strategy_cls=bridge_factory,
            scenario=scenario,
            instrument_id=_IID,
            venue="TSE",
            params={},
            nautilus_strategy_id="LIVE-cellspike",
            session=object(),
        )
        assert controller._driver is not None  # reached RUNNING

        base = 1_700_000_000 * 1_000_000_000  # arbitrary monotonic start (ns)
        day = 24 * 60 * 60 * 1_000_000_000

        # Bars 1..3: the BUY fires on the 3rd bar (n_bars == BUY_AT_BAR).
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=100.0)
        for i in range(1, 4):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(base + i * day, 100.0 + i))
        _wait(lambda: len(_fills()) >= 1, what="the bar-3 BUY fill")
        time.sleep(0.3)
        assert len(_fills()) == 1, f"only the bar-3 BUY should have filled, got {len(_fills())}"
        portfolio = controller._driver._portfolio
        assert portfolio.open_positions()[0].quantity == 100.0  # long 1 lot

        # Bars 4..40: nothing until the 40th bar triggers the SELL (flatten).
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=110.0)
        for i in range(4, 41):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(base + i * day, 110.0))
        _wait(lambda: len(_fills()) >= 2, what="the bar-40 SELL fill")
        time.sleep(0.3)

        assert len(_fills()) == 2, f"BUY@3 + SELL@40 = 2 fills, got {len(_fills())}"
        assert portfolio.open_positions() == [], "cell should be flat after the bar-40 SELL"
        assert controller._driver.fill_count == 2

        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-cellspike")
        controller.detach(nautilus_strategy_id="LIVE-cellspike")
        assert controller._driver is None
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
