"""post-trade gate（max_daily_loss）が **live 経路** で発火することを full chain で確認する（#25 AC ③）。

LiveLoopManager → register → venue_login(MOCK) → set LiveAuto → start_live_strategy(rails) の後、
venue 口座スナップショットが損失を示すと AccountSync→_evaluate_post_trade_loss→fail_run が走り、
run が STOPPED に落ち SafetyRailViolation が push される。run 開始時 baseline を既存 snapshot から即時
確立する修正（#25 D7）がないと初回損失を取りこぼすため、その回帰ガードも兼ねる。
"""
from __future__ import annotations

import threading
import time

import pytest

from engine.core import DataEngine
from engine.live import backend_events
from engine.live.live_orchestrator import LiveLoopManager
from engine.live.mock_adapter import MockVenueAdapter
from engine.live.state_machine import VenueStateMachine
from engine.mode_manager import ModeManager

IID = "8918.TSE"
SCENARIO_FILE = "spike/fixtures/strategies/kernel_spike_buy_sell.py"


def test_post_trade_max_daily_loss_fires_on_live_path():
    shared_mock = MockVenueAdapter()
    shared_mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())

    events: list = []
    data_engine = DataEngine()
    venue_sm = VenueStateMachine()
    data_engine.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=data_engine)
    data_engine.attach_mode_manager(mode_manager)
    mgr = LiveLoopManager(
        engine=data_engine,
        mode_manager=mode_manager,
        venue_sm=venue_sm,
        live_adapter_factory=lambda env_hint=None: shared_mock,
        live_venue_id="MOCK",
        engine_controller=None,
        publish_backend_event_callback=events.append,
    )
    try:
        reg = mgr.register_live_strategy(strategy_file=SCENARIO_FILE, original_path=SCENARIO_FILE)
        assert reg.success, reg.error_code
        assert mgr.venue_login("MOCK", "env", None).success
        assert mgr.set_execution_mode("LiveAuto").success
        start = mgr.start_live_strategy(
            reg.strategy_id, IID, "MOCK", safety_limits_dict={"max_daily_loss_jpy": 1000}
        )
        assert start.success, start.error_code

        # baseline は start 時の 10,000,000（login 後 forced fetch の last_snapshot）。
        # 損失スナップショット（-10,000 > 1,000 limit）を仕込み force_resync で post-trade を回す。
        shared_mock.set_account_snapshot(cash=9_990_000.0, buying_power=9_990_000.0, positions=())
        mgr.force_account_snapshot()

        deadline = time.time() + 5.0
        violations = []
        stopped = []
        while time.time() < deadline:
            violations = [e for e in events if isinstance(e, backend_events.SafetyRailViolation)]
            stopped = [
                e for e in events
                if isinstance(e, backend_events.LiveStrategyEvent) and e.status == "STOPPED"
            ]
            if violations and stopped:
                break
            time.sleep(0.02)
        assert violations, "post-trade max_daily_loss did not fire on the live path"
        assert violations[0].kind == "MAX_DAILY_LOSS"
        assert stopped, "run was not stopped after the post-trade violation"
    finally:
        try:
            mgr.set_execution_mode("Replay")
            mgr.venue_logout()
        except Exception:
            pass
        try:
            mgr.stop_live_loop(timeout=3.0)
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
