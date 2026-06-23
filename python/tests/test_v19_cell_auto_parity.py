"""#112 S6 centerpiece — the v19 marimo CELL in Auto ≡ the imperative v19_morning.py (ADR-0025).

The parity oracle. ``test_v19_auto_live_afk`` drives the imperative ``v19_morning.py`` through
``KernelLiveEngineController`` + ``MockVenueAdapter`` and pins the #74 ACs. This drives the SAME
ACs through the SAME live seam but with the marimo **cell** (``v19_morning_cell.py``) materialized
as a ``LiveCellBridge`` — the same cell that runs under Replay (``for bar in bt.replay()``), now in
Auto with no edit (ADR-0025 D1). A GREEN here is the binding definition that "Replay and Auto drive
one cell": the cell produces the imperative twin's order/fill sequence bar-for-bar.

  * AC#1: reaches RUNNING; intent → mock fill → tracked position; only confirmed ≥10:00 JST bars drive.
  * AC#2: ``bt.buying_power()`` reads the venue 余力 provider (¥15k), gating top-k to one pick (NOT
    the seeded ¥10M kernel cash) — and the read is marshalled to the live loop (R2).
  * AC#3: a partial 10:00 bar (is_closed=False) never drives the cell (the driver filters it).
  * AC#4: graceful stop (cancel_inflight → detach) releases the driver and joins the cell worker.

Mirrors the oracle's harness; the only change is the materialized strategy (cell bridge vs
imperative class) and the artifact anchor (``V19_ARTIFACTS_DIR`` → the cell's self-load dir).
"""
from __future__ import annotations

import asyncio
import json
import os
import shutil
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
from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader  # noqa: E402

_JST = ZoneInfo("Asia/Tokyo")
_HERE = os.path.dirname(os.path.abspath(__file__))
_V19_DIR = os.path.abspath(os.path.join(_HERE, "..", "strategies", "v19"))
_CELL_PY = os.path.join(_V19_DIR, "v19_morning_cell.py")
_REAL_ARTIFACTS = os.path.join(_V19_DIR, "artifacts")
_MODEL_NAME = "v19_live_model_o3histgb_10h00.joblib"
_MODEL = os.path.join(_REAL_ARTIFACTS, _MODEL_NAME)

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


def _setup_artifacts_dir(tmp_path) -> str:
    """A V19_ARTIFACTS_DIR that mirrors the oracle's inputs: the 3-instrument test universe (same
    as the oracle's ``universe_path`` override) plus the REAL adv_baseline / prev_close / model
    (the oracle leaves those at their real defaults). The cell self-loads ALL of these from here."""
    d = tmp_path / "v19_artifacts"
    d.mkdir()
    (d / "v19_live_universe.json").write_text(
        json.dumps({"instruments": _UNIVERSE, "rs_ref": _RS_REF}), encoding="utf-8"
    )
    for name in ("v19_live_adv_baseline.json", "v19_live_prev_close.json", _MODEL_NAME):
        shutil.copy(os.path.join(_REAL_ARTIFACTS, name), d / name)
    return str(d)


@pytest.mark.skipif(not _model_available(), reason="sklearn / v19 model artifact unavailable")
def test_v19_cell_auto_parity_with_imperative(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("V19_ARTIFACTS_DIR", _setup_artifacts_dir(tmp_path))

    # Materialize the CELL as a live bridge factory (the editor live path · ADR-0025 D4).
    _app, _scn, bridge_factory = build_live_marimo_loader()(_CELL_PY)

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="v19-cell-auto-loop", daemon=True)
    thread.start()

    def run(coro, timeout=15.0):
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
        buying_power_provider=lambda: 15_000.0,  # AC#2: venue 余力 LOW → one pick admitted
    )

    def _fills() -> list:
        return [ev for ev in order_events if ev.status == "FILLED"]

    def _wait(predicate, timeout=30.0, what="condition") -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if predicate():
                return
            time.sleep(0.05)
        raise AssertionError(f"timeout waiting for {what}; fills={len(_fills())}")

    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())

        scenario = {
            "schema_version": 3, "instruments": _UNIVERSE,
            "start": "2025-01-06", "end": "2025-01-06",
            "granularity": "Minute", "initial_cash": 10_000_000,
        }
        controller.attach(
            strategy_cls=bridge_factory,
            scenario=scenario,
            instrument_id=_TRADABLE[0],
            venue="TSE",
            params={},
            nautilus_strategy_id="LIVE-v19cell1",
            session=object(),
        )
        assert controller._driver is not None  # AC#1 reached RUNNING

        # Morning snapshots (09:55..09:59) — confirmed, drifting + per-instrument offset.
        for mm in range(55, 60):
            for i, iid in enumerate(_UNIVERSE):
                px = 100.0 + i + (mm - 55) * 0.1
                loop.call_soon_threadsafe(adapter.inject_tick, _kline(iid, 9, mm, px))
        time.sleep(0.5)
        assert _fills() == [], "no order should fire before the 10:00 entry"

        # AC#3: a PARTIAL 10:00 bar must NOT drive the cell.
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=100.0)
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(_TRADABLE[0], 10, 0, 100.0, is_closed=False))
        time.sleep(0.5)
        assert _fills() == [], "a partial 10:00 bar must not trigger entry"

        # Confirmed 10:00 bars → entry scores, buys top-k, gated to venue 余力 → exactly 1 fill.
        for i, iid in enumerate(_UNIVERSE):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(iid, 10, 0, 100.0 + i))
        _wait(lambda: len(_fills()) >= 1, what="the 10:00 entry fill")
        time.sleep(0.5)

        # AC#2: ¥15k 余力 gate admits exactly one ¥10k pick → one BUY → one long position.
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
        assert portfolio.open_positions() == [], "v19 cell should be flat after the 14:55 exit"
        assert controller._driver.fill_count == 2

        # AC#4: graceful stop releases the driver (and joins the cell worker).
        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-v19cell1")
        controller.detach(nautilus_strategy_id="LIVE-v19cell1")
        assert controller._driver is None
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
