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

import multiprocessing
import os
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
_REST_PATH = str(
    Path(__file__).resolve().parents[1] / "fixtures" / "strategies" / "kernel_buy_and_rest.py"
)


def _kline(i: int, close: float):
    from engine.live.adapter import KlineUpdate

    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def _await_resting_order(mgr, *, timeout_s: float = 5.0):
    """Poll until an order has reached ACCEPTED (resting) on the loop thread.

    Gating on ``submit_order_call_count`` races: the mock increments it before
    the driver applies the ACCEPTED transition, so the order can still be
    SUBMITTED when the counter flips. Poll the real terminal-of-interest state.
    """
    from engine.kernel.orders import OrderStatus

    deadline = time.time() + timeout_s
    while time.time() < deadline:
        host = getattr(mgr, "_strategy_host", None)
        controller = getattr(host, "_controller", None) if host is not None else None
        driver = getattr(controller, "_driver", None) if controller is not None else None
        if driver is not None:
            # snapshot: the live-loop thread mutates _orders concurrently, so
            # iterating the live dict can raise "changed size during iteration".
            for order in list(driver._orders.values()):
                if order.status == OrderStatus.ACCEPTED:
                    return order
        time.sleep(0.02)
    raise AssertionError("no ACCEPTED resting order appeared within timeout")


def _build_live_manager():
    """Construct the production LiveLoopManager chain over a shared MockVenueAdapter.

    Returns ``(mgr, shared_mock, events)``: ``shared_mock`` is the inject handle,
    ``events`` accumulates published backend events. ``engine_controller=None`` →
    既定 KernelLiveEngineController（本番経路・Rust core 非ロード）。
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
        live_adapter_factory=lambda env_hint=None: shared_mock,  # 共有 mock（inject 用ハンドル）
        live_venue_id="MOCK",
        engine_controller=None,
        publish_backend_event_callback=events.append,
    )
    return mgr, shared_mock, events


def run() -> dict:
    """full LiveLoopManager chain を完走して結果 dict を返す（fills/net_after_buy/final_net/realized/leaked）。"""
    mgr, shared_mock, events = _build_live_manager()

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


def run_shutdown_cancel() -> dict:
    """resting order を残し、graceful 停止で best-effort 取消されることを full chain で実演 (#22 Gap1)。

    mock を ACCEPTED(filled_qty=0.0) に仕込んで「取消すべき注文」を作り、stop_live_strategy の
    graceful 経路（stop_run: STOPPING → cancel_inflight_orders → detach → STOPPED）で venue へ
    cancel_order が届き order が CANCELED 終端することを確認する。同走で:
      - orphan 不在の構造不変条件 (#22 Gap3): live loop が本プロセスの daemon thread 上で回り
        out-of-process な order pump（multiprocessing child）が無い。
      - stop_live_loop の clean-join 契約 (#22 Gap4 happy path): 正常停止で True を返す。
    """
    mgr, shared_mock, _events = _build_live_manager()

    run_id = None
    result: dict = {}
    loop_stopped_clean = None
    children_before = len(multiprocessing.active_children())
    try:
        reg = mgr.register_live_strategy(strategy_file=_REST_PATH, original_path=_REST_PATH)
        assert reg.success, f"register failed: {reg.error_code} {reg.error_message}"
        login = mgr.venue_login("MOCK", "env", None)
        assert login.success, f"venue_login failed: {login.error_code}"
        mode = mgr.set_execution_mode("LiveAuto")
        assert mode.success, f"set_execution_mode failed: {mode.error_code}"

        # ACK only — 約定しない → venue に resting する注文を作る。
        shared_mock.set_next_order_outcome(status="ACCEPTED", filled_qty=0.0, avg_price=8.0)
        start = mgr.start_live_strategy(reg.strategy_id, IID, "MOCK")
        assert start.success, f"start_live_strategy failed: {start.error_code} {start.error_message}"
        run_id = start.run_id

        for i in range(1, 4):
            mgr._live_loop.call_soon_threadsafe(shared_mock.inject_tick, _kline(i, 8.0))

        # Poll for the order to actually reach ACCEPTED on the loop thread.
        # submit_order_call_count increments BEFORE the driver applies the
        # ACCEPTED transition, so gating on the counter and reading status
        # immediately races (the order may still be SUBMITTED).
        resting = _await_resting_order(mgr)
        driver = mgr._strategy_host._controller._driver  # white-box: pre-stop kernel state
        assert len(driver._orders) == 1, f"expected exactly 1 resting order, got {len(driver._orders)}"

        # orphan 不在の構造不変条件 (#22 Gap3): loop が生きている間に観測する。
        loop_thread = mgr._live_thread
        result["python_pid"] = os.getpid()
        result["loop_daemon"] = loop_thread is not None and loop_thread.daemon
        result["loop_alive"] = loop_thread is not None and loop_thread.is_alive()
        result["child_count"] = len(multiprocessing.active_children()) - children_before
        result["resting_before_stop"] = resting.status.value  # ACCEPTED (polled above)
        cancel_calls_before = shared_mock.cancel_order_call_count

        # graceful 停止: cancel_inflight_orders → detach が走り、resting order を取消す。
        stop = mgr.stop_live_strategy(run_id)
        assert stop.success, f"stop_live_strategy failed: {stop.error_code}"
        run_id = None  # 既に停止済み — finally の再停止を避ける。

        result["cancel_calls"] = shared_mock.cancel_order_call_count - cancel_calls_before
        result["resting_after_stop"] = resting.status.value
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
            loop_stopped_clean = mgr.stop_live_loop(timeout=3.0)
        except Exception:
            loop_stopped_clean = None

    result["loop_stopped_clean"] = bool(loop_stopped_clean)

    # purity 検査は teardown 後（このシナリオ単体で Mono probe が回せるよう self-contained に）。
    from spike.kernel_golden.purity import leaked_nautilus_modules

    result["leaked"] = list(leaked_nautilus_modules(sys.modules))
    return result


def run_all() -> dict:
    """twin roundtrip（purity gate・#25 不変）と shutdown-cancel + orphan シナリオ（#22）を 1 本で通す。

    Mono probe / CPython purity gate の単一エントリ。両シナリオ完走後に nautilus 非ロードを最終確認する。
    """
    merged = dict(run())            # fills/net_after_buy/final_net/realized/strategy_id/leaked
    # run_shutdown_cancel() runs last and computes `leaked` after its own teardown,
    # so the merged `leaked` is already the authoritative post-both-chains value.
    merged.update(run_shutdown_cancel())  # resting cancel + orphan + Gap4（`leaked` 含め衝突は意図的に上書き）
    return merged


def main() -> int:
    r = run_all()
    # twin roundtrip（purity gate・#25 不変）
    assert r.get("net_after_buy") == 100.0, r
    assert r.get("final_net") == 0.0, r
    assert r.get("fills") == 2, r
    assert r.get("realized") == 200.0, r
    assert str(r.get("strategy_id", "")).startswith(_NSID_PREFIX), r  # run identity injected
    assert r.get("leaked") == [], f"nautilus leaked into the full LiveAuto chain: {r['leaked']}"
    # #22 Gap1: resting order best-effort cancel（graceful 停止で CANCELED + venue 到達）
    assert r.get("resting_before_stop") == "ACCEPTED", r
    assert r.get("cancel_calls") == 1, r
    assert r.get("resting_after_stop") == "CANCELED", r
    # #22 Gap3: orphan 不在の構造不変条件
    assert r.get("loop_daemon") is True, r
    assert r.get("loop_alive") is True, r
    assert r.get("child_count") == 0, r
    # #22 Gap4: clean-join 契約（happy path で True）
    assert r.get("loop_stopped_clean") is True, r
    print(
        "[KERNEL LIVE PURITY PASS] full-chain "
        f"fills={r['fills']} final_net={r['final_net']} realized={r['realized']} "
        f"resting={r['resting_before_stop']}->{r['resting_after_stop']} cancel_calls={r['cancel_calls']} "
        f"loop_daemon={r['loop_daemon']} child_count={r['child_count']} loop_clean={r['loop_stopped_clean']} "
        "nautilus_leaked=0"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
