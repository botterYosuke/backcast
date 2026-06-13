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
ON_START_BUY_FILE = "spike/fixtures/strategies/kernel_on_start_buy.py"


def test_on_start_order_reaches_venue_full_path():
    """on_start 発注が full-chain で venue に届く（run は attach 前に RUNNING・#25 review finding 1）。

    READY のまま attach すると run gate が on_start 発注を STRATEGY_PAUSED で DENIED にし venue 未到達
    （venue_calls=0）になる回帰を塞ぐ。start_run が attach 前に RUNNING へ遷移することで gate を通す。
    """
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
        reg = mgr.register_live_strategy(strategy_file=ON_START_BUY_FILE, original_path=ON_START_BUY_FILE)
        assert reg.success, reg.error_code
        assert mgr.venue_login("MOCK", "env", None).success
        assert mgr.set_execution_mode("LiveAuto").success
        shared_mock.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)

        start = mgr.start_live_strategy(reg.strategy_id, IID, "MOCK", safety_limits_dict={})
        assert start.success, start.error_code

        deadline = time.time() + 5.0
        while time.time() < deadline and shared_mock.submit_order_call_count == 0:
            time.sleep(0.02)
        assert shared_mock.submit_order_call_count >= 1, "on_start order never reached the venue (gated)"
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


def test_post_trade_fires_on_loss_at_run_start_without_extra_snapshot():
    """run 開始時点で既に max_daily_loss を超える損失があれば、追加 snapshot 無しで STOP する（#25 finding 2）。

    rails + baseline の登録は `start_run`（attach→on_start）の **後** に行われるため、start window 内で
    走る post-trade 評価（on_start 約定の force_resync など）は rails 未登録でスキップされ、初回損失が
    取りこぼされる。修正は登録直後に最新 snapshot を **必ず再評価**する。本テストは start_live_strategy
    後に **force_account_snapshot を呼ばず**（次の interval poll は 30s 先）に STOPPED + 違反を観測する。
    再評価が無いと素通りする回帰ガード。

    注: 本テストは on_start 約定に依存せず、「baseline 確定後〜run 開始の間に venue equity が損失へ
    動いた」状態を再現する（baseline=最初の fetch=10,000,000、登録直後の再評価 fetch=9,990,000）。
    on_start 約定自体が venue に届くことは別テスト（test_on_start_order_reaches_venue_full_path）が担う。

    start_live_strategy 内で venue を fetch するのは順に ① baseline（_resolve_post_trade_baseline_snapshot）
    ② attach の portfolio seed ③ 登録直後の再評価。①だけ 10,000,000、②以降を損失にすれば、baseline は
    汚れず再評価のみが損失を観測する（決定的・emit タイミング非依存）。
    """
    class _LossAfterFirstFetchMock(MockVenueAdapter):
        def __init__(self) -> None:
            super().__init__()
            self._fetch_calls = 0

        async def fetch_account(self):
            self._fetch_calls += 1
            if self._fetch_calls >= 2:  # baseline(1) の後は損失（-10,000 > 1,000 limit）
                self.set_account_snapshot(cash=9_990_000.0, buying_power=9_990_000.0, positions=())
            return await super().fetch_account()

    shared_mock = _LossAfterFirstFetchMock()
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

        # 追加の force_account_snapshot は **呼ばない**。登録直後の再評価だけで STOP すること。
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
        assert violations, "run-start loss did not fire post-trade without an extra snapshot"
        assert violations[0].kind == "MAX_DAILY_LOSS"
        assert stopped, "run was not stopped after the run-start post-trade violation"
        # 再評価は RUNNING/STARTED の publish **後**に走るので、UI は STARTED→STOPPED の順で受ける。
        statuses = [e.status for e in events if isinstance(e, backend_events.LiveStrategyEvent)]
        assert "RUNNING" in statuses, statuses
        assert statuses.index("RUNNING") < statuses.index("STOPPED"), statuses
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


def _live_auto_manager(shared_mock):
    """LiveAuto まで進めた LiveLoopManager と events リストを返す（#25 orchestrator テスト用）。"""
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
    assert mgr.venue_login("MOCK", "env", None).success
    assert mgr.set_execution_mode("LiveAuto").success
    return mgr, events


def test_strategy_error_releases_run_rails():
    """戦略例外で run が落ちたら rails+baseline が対で解放される（リークしない・#25 review finding 2）。"""
    shared_mock = MockVenueAdapter()
    shared_mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    mgr, _events = _live_auto_manager(shared_mock)
    try:
        reg = mgr.register_live_strategy(strategy_file=SCENARIO_FILE, original_path=SCENARIO_FILE)
        assert reg.success, reg.error_code
        start = mgr.start_live_strategy(
            reg.strategy_id, IID, "MOCK", safety_limits_dict={"max_daily_loss_jpy": 1000}
        )
        assert start.success, start.error_code
        run_id = start.run_id
        assert run_id in mgr._run_rails  # start で登録済み
        assert run_id in mgr._run_equity_baseline

        mgr._fail_run_for_strategy_error(run_id)

        assert run_id not in mgr._run_rails, "rails leaked after strategy-error termination"
        assert run_id not in mgr._run_equity_baseline, "baseline leaked after strategy-error termination"
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


def test_loss_violation_published_even_if_fail_run_errors():
    """fail_run が失敗（unknown run 等）でも max_daily_loss 違反は必ず surface される（#25 review finding 6）。"""
    from engine.live.safety_rails import KIND_MAX_DAILY_LOSS, RailViolation

    shared_mock = MockVenueAdapter()
    shared_mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    mgr, events = _live_auto_manager(shared_mock)
    try:
        # 存在しない run を fail させようとすると host が LiveStrategyHostError を投げる。
        mgr._fail_run_for_loss("nonexistent-run", RailViolation(KIND_MAX_DAILY_LOSS, "loss limit breached"))
        violations = [e for e in events if isinstance(e, backend_events.SafetyRailViolation)]
        assert violations, "loss violation was swallowed when fail_run errored"
        assert violations[0].kind == "MAX_DAILY_LOSS"
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
