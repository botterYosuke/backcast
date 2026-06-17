"""v19 in Auto (LiveAuto) — mock-venue AFK vertical slice (#74).

Drives the SAME kernel-native v19 strategy through KernelLiveEngineController on the real
live seam (background loop thread, shared LiveRunner.bus, MockVenueAdapter, attach via
run_coroutine_threadsafe) and asserts the #74 ACs that are AFK-checkable:

- AC#1: v19 reaches RUNNING in Auto and does intent → mock fill → tracked position, driven
  only by confirmed bars whose ts reconstructs to >= 10:00 JST.
- AC#2: buying_power() reads the injected venue 余力 (buying_power_provider), NOT the seeded
  kernel cash — the gate trims top-k to fit venue 余力. Discriminated by seeding venue cash
  HIGH (10M) but the provider LOW (¥15k): a cash-based gate would admit both ¥10k picks; a
  余力-based gate admits exactly one.
- AC#3: partial bars (is_closed=False) never drive on_bar — a partial 10:00 bar triggers no
  order; the confirmed 10:00 bar does.
- AC#4: graceful stop (cancel_inflight → detach) releases the driver. v19 is market-only, so
  there is no resting order to cancel — the path is exercised and must not error.

demo-venue HITL (owner watching a real fill) is the remaining #74 gate, out of AFK scope.

The model is the real 1.8.0 artifact (loaded ~once at the 10:00 scoring); the universe is a
3-instrument test override via ctor params so the slice stays small.
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import threading
import time
from datetime import datetime
from zoneinfo import ZoneInfo

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

from engine.kernel.live.controller import KernelLiveEngineController  # noqa: E402
from engine.live.adapter import KlineUpdate  # noqa: E402
from engine.live.live_runner import LiveRunner  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.strategy_runtime.strategy_loader import load as load_strategy  # noqa: E402
from engine.kernel.strategy import Strategy as KernelStrategy  # noqa: E402

_JST = ZoneInfo("Asia/Tokyo")
_HERE = os.path.dirname(os.path.abspath(__file__))
_V19_DIR = os.path.abspath(os.path.join(_HERE, "..", "strategies", "v19"))
_V19_PY = os.path.join(_V19_DIR, "v19_morning.py")
_ARTIFACTS = os.path.join(_V19_DIR, "artifacts")
_MODEL = os.path.join(_ARTIFACTS, "v19_live_model_o3histgb_10h00.joblib")

_TRADABLE = ["7203.TSE", "6758.TSE"]
_RS_REF = "1306.TSE"
_UNIVERSE = [*_TRADABLE, _RS_REF]
_DAY = (2025, 1, 6)


def _model_available() -> bool:
    try:
        import sklearn  # noqa: F401
    except Exception:
        return False
    return os.path.exists(_MODEL)


def _ts_ns(hh: int, mm: int) -> int:
    y, m, d = _DAY
    dt = datetime(y, m, d, hh, mm, 59, 999999, tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def _kline(iid: str, hh: int, mm: int, close: float, *, is_closed: bool = True) -> KlineUpdate:
    return KlineUpdate(
        kind="kline", instrument_id=iid, ts_ns=_ts_ns(hh, mm),
        open=close, high=close, low=close, close=close, volume=1000.0, is_closed=is_closed,
    )


@pytest.mark.skipif(not _model_available(), reason="sklearn / v19 model artifact unavailable")
def test_v19_auto_live_buying_power_gated_roundtrip(tmp_path) -> None:
    # 3-instrument test universe so the slice stays small (the model takes any width).
    universe_path = tmp_path / "universe.json"
    universe_path.write_text(json.dumps({"instruments": _UNIVERSE, "rs_ref": _RS_REF}))

    _module, _scn, strategy_cls = load_strategy(_V19_PY, base_cls=KernelStrategy)

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="v19-auto-loop", daemon=True)
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
        # AC#2: venue 余力 is LOW (¥15k → budget ¥14,250 admits one ¥10k pick) while the
        # seeded kernel cash is HIGH (¥10M). A cash-based gate would admit both picks.
        buying_power_provider=lambda: 15_000.0,
    )

    def _fills() -> list:
        # The projected UI event does not carry order side; count FILLED events and read
        # BUY vs SELL from the kernel portfolio state instead.
        return [ev for ev in order_events if ev.status == "FILLED"]

    def _wait(predicate, timeout=25.0, what="condition"):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if predicate():
                return
            time.sleep(0.05)
        raise AssertionError(f"timeout waiting for {what}; fills={[(e.side, e.status) for e in order_events]}")

    try:
        run(adapter.login(None))
        # Seed venue cash HIGH so the discriminator is the provider, not the seed.
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())

        scenario = {
            "schema_version": 3, "instruments": _UNIVERSE,
            "start": "2025-01-06", "end": "2025-01-06",
            "granularity": "Minute", "initial_cash": 10_000_000,
        }
        controller.attach(
            strategy_cls=strategy_cls,
            scenario=scenario,
            instrument_id=_TRADABLE[0],
            venue="TSE",
            params={
                "universe_path": str(universe_path),
                "model_path": _MODEL,
            },
            nautilus_strategy_id="LIVE-v19auto1",
            session=object(),
        )
        # AC#1 reached RUNNING: driver is attached.
        assert controller._driver is not None

        # Morning snapshots (09:55..09:59), confirmed, slightly drifting + per-instrument
        # offset so cross-sectional features are non-degenerate.
        for mm in range(55, 60):
            for i, iid in enumerate(_UNIVERSE):
                px = 100.0 + i + (mm - 55) * 0.1
                loop.call_soon_threadsafe(adapter.inject_tick, _kline(iid, 9, mm, px))
        # Let the morning bars drain; no orders yet (pre-entry).
        time.sleep(0.5)
        assert _fills() == [], "no order should fire before the 10:00 entry"

        # AC#3: a PARTIAL 10:00 bar must NOT drive on_bar (no entry).
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=100.0)
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(_TRADABLE[0], 10, 0, 100.0, is_closed=False))
        time.sleep(0.5)
        assert _fills() == [], "a partial 10:00 bar must not trigger entry"

        # Confirmed 10:00 bars → entry scores + buys top-k, gated to venue 余力 → exactly 1 fill.
        for i, iid in enumerate(_UNIVERSE):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(iid, 10, 0, 100.0 + i))
        _wait(lambda: len(_fills()) >= 1, what="the 10:00 entry fill")
        time.sleep(0.5)  # allow any erroneous extra fills to surface

        # venue-余力 gate (¥15k) admits exactly one ¥10k pick → one BUY fill → one long position.
        assert len(_fills()) == 1, f"gate should admit exactly 1 pick, got {len(_fills())}"
        portfolio = controller._driver._portfolio
        assert len(portfolio.open_positions()) == 1
        assert portfolio.open_positions()[0].quantity == 100.0  # BUY 100 → long

        # Confirmed 14:55 bars → exit flattens the held position (the 2nd fill is the SELL).
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=105.0)
        for iid in _UNIVERSE:
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(iid, 14, 55, 110.0))
        _wait(lambda: len(_fills()) >= 2, what="the 14:55 exit fill")
        time.sleep(0.5)
        assert portfolio.open_positions() == [], "v19 should be flat after the 14:55 exit"
        assert controller._driver.fill_count == 2

        # AC#4: graceful stop. v19 is market-only → no resting order to cancel; the path must
        # run cleanly and release the driver.
        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-v19auto1")
        controller.detach(nautilus_strategy_id="LIVE-v19auto1")
        assert controller._driver is None
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
