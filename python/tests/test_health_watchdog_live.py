"""#22 (Gap2) — VenueHealthWatchdog が live 経路で VenueLogoutDetected を発火する。

機構は #24/#25 + Phase 9 Step 7 で配線済み（live_orchestrator が watchdog を構築し
on_venue_logout=_publish_venue_logout を繋ぐ）。本書はその発火を production 部品の組合せで証明する:

  MockVenueAdapter.check_health(=False) → VenueHealthWatchdog._tick → on_venue_logout
    → LiveLoopManager._publish_venue_logout → emit(VenueLogoutDetected)

スコープは VenueLogoutDetected の emit まで（debounce 1 回・復旧で re-arm）。logout を受けて run を
止める owner は下流 UI（#21/#23）。watchdog が乗る backend-event marshal 境界は #20/#25 の既存
Mono event テストでゲート済みのため Mono レグは不要（grill 確定）。
"""
from __future__ import annotations

import asyncio
import threading
import time

from engine.live import backend_events
from engine.live.health_watchdog import VenueHealthWatchdog
from engine.live.live_orchestrator import LiveLoopManager
from engine.live.mock_adapter import MockVenueAdapter


def _make_loop_manager(emitted: list) -> LiveLoopManager:
    return LiveLoopManager(
        engine=None,
        mode_manager=None,
        venue_sm=None,
        live_adapter_factory=None,
        live_venue_id="MOCK",
        engine_controller=None,
        publish_backend_event_callback=emitted.append,
    )


def _count_logouts(emitted: list) -> int:
    return sum(1 for e in emitted if isinstance(e, backend_events.VenueLogoutDetected))


def test_watchdog_fires_venue_logout_on_live_path_with_debounce_and_rearm():
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    emitted: list = []
    mgr = _make_loop_manager(emitted)
    adapter = MockVenueAdapter()
    run(adapter.login(None))

    watchdog = VenueHealthWatchdog(
        adapter,
        venue_id="MOCK",
        on_venue_logout=mgr._publish_venue_logout,
        interval_s=0.05,
    )

    def _wait_until(pred, timeout=3.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if pred():
                return True
            time.sleep(0.02)
        return False

    try:
        run(watchdog.start())

        # healthy の間は何も emit しない。
        time.sleep(0.2)
        assert _count_logouts(emitted) == 0

        # 本体ログアウト → ちょうど 1 回だけ VenueLogoutDetected。
        adapter.set_health(False)
        assert _wait_until(lambda: _count_logouts(emitted) >= 1), "watchdog did not fire VenueLogoutDetected"
        assert _count_logouts(emitted) == 1
        first = next(e for e in emitted if isinstance(e, backend_events.VenueLogoutDetected))
        assert first.venue == "MOCK"

        # ログアウト継続中は debounce で再通知しない（modal 連打回避）。
        time.sleep(0.25)
        assert _count_logouts(emitted) == 1, "watchdog re-fired while still logged out (debounce broken)"

        # 復旧 → debounce 解除（re-arm）。復旧自体は emit しない。
        adapter.set_health(True)
        time.sleep(0.2)
        assert _count_logouts(emitted) == 1

        # 再ログアウト → 再び通知できる。
        adapter.set_health(False)
        assert _wait_until(lambda: _count_logouts(emitted) >= 2), "watchdog did not re-fire after recovery"
        assert _count_logouts(emitted) == 2
    finally:
        try:
            run(watchdog.stop())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
