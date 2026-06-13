"""spike.kernel_live.run_mock_live — mock venue LiveAuto を **full chain** で 1 本完走させる headless ハーネス (#25)。

本番と同じ orchestrator 経路を通す（codex review #25 finding 3）:
    LiveLoopManager → register_live_strategy（StrategyRegistry → kernel loader）
                    → venue_login(MOCK) → set_execution_mode(LiveAuto)
                    → start_live_strategy（LiveStrategyHost → KernelLiveEngineController.attach）
                    → bar 注入 → 同期 OrderResult fill → stop_live_strategy → teardown
そして **stop/detach 後** に `nautilus_trader*` が sys.modules に載っていないことを検査する。controller を
直接生成する旧ハーネスは orchestrator/loader/host 結線の退行（例: registry が nautilus loader のまま・
controller 未 swap）を検出できなかったため、full chain に置換した。

用途:
- CPython fresh subprocess gate（`test_kernel_live_purity.py`）— exit 0 + "[KERNEL LIVE PURITY PASS]"。
- Unity-Mono full Live probe（`Assets/Editor/KernelLiveProbe.cs`）— 同 run() を Mono CPython で走らせる（D5 layer 3）。

PASS = 2 fills・終端 FLAT・realized=200・nautilus 非ロード。FAIL は AssertionError で非 0 exit。
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

from engine.core import DataEngine
from engine.live import backend_events
from engine.live.live_orchestrator import LiveLoopManager
from engine.live.mock_adapter import MockVenueAdapter
from engine.live.state_machine import VenueStateMachine
from engine.mode_manager import ModeManager

IID = "8918.TSE"
DAY_NS = 86_400 * 1_000_000_000
_NSID_PREFIX = "LIVE-"  # host issues "LIVE-{run_id[:8]}"

_TWIN_PATH = str(
    Path(__file__).resolve().parents[1] / "fixtures" / "strategies" / "kernel_spike_buy_sell.py"
)


def _kline(i: int, close: float):
    from engine.live.adapter import KlineUpdate

    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def run() -> dict:
    """full LiveLoopManager chain を完走して結果 dict を返す（fills/net_after_buy/final_net/realized/leaked）。"""
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
        live_adapter_factory=lambda env_hint=None: shared_mock,  # 共有 mock（inject 用ハンドル）
        live_venue_id="MOCK",
        engine_controller=None,  # → 既定 KernelLiveEngineController（本番経路）
        publish_backend_event_callback=events.append,
    )

    def _fills() -> int:
        return sum(
            1 for e in events if isinstance(e, backend_events.OrderEvent) and e.status == "FILLED"
        )

    def inject_and_wait(bars, want_filled):
        for i, close in bars:
            mgr._live_loop.call_soon_threadsafe(shared_mock.inject_tick, _kline(i, close))
        deadline = time.time() + 5.0
        while time.time() < deadline:
            if _fills() >= want_filled:
                return
            time.sleep(0.02)
        raise AssertionError(f"timeout waiting for {want_filled} fills (have {_fills()})")

    run_id = None
    result: dict = {}
    try:
        reg = mgr.register_live_strategy(strategy_file=_TWIN_PATH, original_path=_TWIN_PATH)
        assert reg.success, f"register failed: {reg.error_code} {reg.error_message}"

        login = mgr.venue_login("MOCK", "env", None)
        assert login.success, f"venue_login failed: {login.error_code}"

        mode = mgr.set_execution_mode("LiveAuto")
        assert mode.success, f"set_execution_mode failed: {mode.error_code}"

        shared_mock.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        start = mgr.start_live_strategy(reg.strategy_id, IID, "MOCK")
        assert start.success, f"start_live_strategy failed: {start.error_code} {start.error_message}"
        run_id = start.run_id

        inject_and_wait([(i, 8.0) for i in range(1, 4)], want_filled=1)
        driver = mgr._strategy_host._controller._driver  # white-box: read kernel state pre-stop
        net_after_buy = driver._portfolio.net_signed_qty(IID)

        shared_mock.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=10.0)
        inject_and_wait([(i, 10.0) for i in range(4, 41)], want_filled=2)

        portfolio = driver._portfolio
        result = {
            "fills": driver.fill_count,
            "net_after_buy": net_after_buy,
            "final_net": portfolio.net_signed_qty(IID),
            "realized": portfolio.realized_pnl,
            "strategy_id": driver._strategy.id,
        }
    finally:
        try:
            if run_id:
                mgr.stop_live_strategy(run_id)
        except Exception:
            pass
        try:
            mgr.set_execution_mode("Replay")
            mgr.venue_logout()
        except Exception:
            pass
        try:
            mgr.stop_live_loop(timeout=3.0)
        except Exception:
            pass

    # purity 検査は **stop/detach 後**（遅延 import されるコードも対象にする・D5）。
    from spike.kernel_golden.purity import leaked_nautilus_modules

    result["leaked"] = list(leaked_nautilus_modules(sys.modules))
    return result


def main() -> int:
    r = run()
    assert r.get("net_after_buy") == 100.0, r
    assert r.get("final_net") == 0.0, r
    assert r.get("fills") == 2, r
    assert r.get("realized") == 200.0, r
    assert str(r.get("strategy_id", "")).startswith(_NSID_PREFIX), r  # run identity injected
    assert r.get("leaked") == [], f"nautilus leaked into the full LiveAuto chain: {r['leaked']}"
    print(
        "[KERNEL LIVE PURITY PASS] full-chain "
        f"fills={r['fills']} final_net={r['final_net']} realized={r['realized']} "
        "nautilus_leaked=0"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
