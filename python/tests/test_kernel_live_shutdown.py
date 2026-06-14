"""#22 — Live safety & graceful shutdown: production kernel-live 経路の検証/クローズ。

#24/#25 で配線済みの safety/shutdown 機構が **production kernel-live 経路で実際に発火**する
ことを証明する（新規 safety 配線は無い）。本書がカバーする AC:

- **AC3 (Gap1) resting-order best-effort 取消**: 同期 FILLED の mock 既定では shutdown 時に
  resting order が 0 で cancel_inflight が空振りする。1 注文を ACCEPTED 未約定で残し、graceful 停止
  （stop_run: STOPPING → cancel_inflight_orders → detach → STOPPED）で CANCELED 遷移・resting==0・
  venue cancel_order 到達を確認する。順序の Mono 再現（→ PythonEngine.Shutdown）は KernelLiveProbe。
- **AC2 (Gap3) orphan 不在の構造不変条件**: kill テストではなく構造 assert（同一 PID・live loop が
  daemon thread・out-of-process order pump 不在）。ADR-0001 decision 3。
- **AC4 (Gap4) join-timeout fail-safe**: stop_live_loop は hung worker（join timeout 後も is_alive）
  なら False を返し handle を保持する。この bool が C# finalize gate の「finalize しない」契約。

import-purity（nautilus 非ロード）は fresh subprocess の test_kernel_live_purity.py が gate する。
"""
from __future__ import annotations

import asyncio
import multiprocessing
import os
import threading
import time

import pytest

from engine.kernel.live.controller import KernelLiveEngineController
from engine.kernel.orders import OrderSide, OrderStatus
from engine.kernel.strategy import Strategy as KernelStrategyBase
from engine.live.adapter import KlineUpdate
from engine.live.live_orchestrator import LiveLoopManager
from engine.live.live_runner import LiveRunner
from engine.live.mock_adapter import MockVenueAdapter

IID = "8918.TSE"
DAY_NS = 86_400 * 1_000_000_000
SCENARIO = {
    "schema_version": 2,
    "instruments": [IID],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


def _kline(i: int, close: float) -> KlineUpdate:
    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def _live_harness():
    """controller + background live loop + MockVenueAdapter を組む（step5_afk と同型）。"""
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
    )
    run(adapter.login(None))
    adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    run(runner.start())
    return loop, thread, run, adapter, runner, controller


class _BuyAndRest(KernelStrategyBase):
    """最初の bar で BUY を 1 回だけ出し、以後は何もしない（注文を resting させる）。"""

    def __init__(self, **kw):
        super().__init__(**kw)
        self._submitted = False

    def on_bar(self, bar):
        if not self._submitted:
            self._submitted = True
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)


# ── AC3 / Gap1: resting-order best-effort 取消 ───────────────────────────────


def test_graceful_stop_cancels_resting_order_on_live_path():
    """ACCEPTED 未約定の resting order が graceful 停止で CANCELED され venue cancel_order が呼ばれる。

    production 停止順序 stop_run(STOPPING → cancel_inflight_orders → detach → STOPPED) を
    controller seam で再現する。同期 FILLED の既定だと resting=0 で cancel 経路が空振りするので、
    mock を ACCEPTED(filled_qty=0.0) に仕込んで「取消すべき注文」を作る（#22 の核心）。
    """
    loop, thread, run, adapter, runner, controller = _live_harness()
    try:
        # submit は ACCEPTED で venue に受理されるが約定しない → resting order。
        adapter.set_next_order_outcome(status="ACCEPTED", filled_qty=0.0, avg_price=8.0)
        controller.attach(
            strategy_cls=_BuyAndRest, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-rest0001", session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))

        # order が ACCEPTED (resting) に到達するまで待つ。submit_order_call_count は
        # ACCEPTED 遷移の適用より先に増えるので、counter で待って status を即読むと race する。
        deadline = time.time() + 5.0
        resting = None
        while time.time() < deadline:
            # snapshot: the live-loop thread mutates _orders concurrently.
            resting = next(
                (o for o in list(controller._driver._orders.values())
                 if o.status == OrderStatus.ACCEPTED),
                None,
            )
            if resting is not None:
                break
            time.sleep(0.02)
        assert resting is not None, "no ACCEPTED resting order appeared"
        assert len(controller._driver._orders) == 1
        assert controller._driver._portfolio.open_positions() == []  # 建玉は無い

        # graceful 停止の取消フェーズ（stop_run の _teardown 第一段＝cancel_inflight_orders）。
        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-rest0001")

        # best-effort 取消が venue へ届き、order が CANCELED 終端し resting==0。
        assert adapter.cancel_order_call_count == 1, "venue cancel_order was not called"
        assert resting.status == OrderStatus.CANCELED
        open_after = [o for o in controller._driver._orders.values()
                      if o.status in (OrderStatus.SUBMITTED, OrderStatus.ACCEPTED,
                                      OrderStatus.PARTIALLY_FILLED, OrderStatus.PENDING_UPDATE)]
        assert open_after == [], "a resting order survived graceful cancel"

        # detach（_teardown 第二段）は冪等に完走する。
        controller.detach(nautilus_strategy_id="LIVE-rest0001")
        assert controller._driver is None
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


# ── AC2 / Gap3: orphan 不在の構造不変条件 ────────────────────────────────────


def _make_loop_manager() -> LiveLoopManager:
    """_ensure_live_loop / stop_live_loop だけを使う最小 LiveLoopManager（他 attr は未使用）。"""
    return LiveLoopManager(
        engine=None,
        mode_manager=None,
        venue_sm=None,
        live_adapter_factory=None,
        live_venue_id="MOCK",
        engine_controller=None,
        publish_backend_event_callback=lambda *a, **k: None,
    )


def test_orphan_absence_structural_invariants():
    """執行を担う live loop は本プロセスの daemon thread 上にあり、out-of-process な order pump は無い。

    「Unity kill → 両方死ぬ」を観測する非決定的 kill テストではなく、構造不変条件を assert する
    （ADR-0001 decision 3・CONTEXT.md orphan-absence invariant）:
      ① live loop は本プロセス内で回る（coro が返す os.getpid() が一致 = 同一 PID）
      ② live loop thread は daemon（プロセスを延命しない → host 終了で道連れ）
      ③ multiprocessing child process 0（裏で実弾を出し続ける別プロセスが無い）
    """
    mgr = _make_loop_manager()
    children_before = multiprocessing.active_children()
    loop = mgr._ensure_live_loop()
    try:
        # ① 同一 PID: loop 上で実行した coro が本テストプロセスと同じ PID を返す。
        async def _pid():
            return os.getpid()

        loop_pid = asyncio.run_coroutine_threadsafe(_pid(), loop).result(timeout=2.0)
        assert loop_pid == os.getpid(), "live loop runs in a different process (orphan risk)"

        # ② daemon thread: 単独でプロセスを生かし続けない。
        t = mgr._live_thread
        assert t is not None and t.is_alive()
        assert t.daemon is True, "live loop thread is non-daemon (could outlive host)"
        assert t.name == "phase8-live-loop"

        # ③ execution subprocess 不在: live loop 起動で child process が増えていない。
        assert multiprocessing.active_children() == children_before
    finally:
        assert mgr.stop_live_loop(timeout=2.0) is True
        assert mgr._live_thread is None


# ── AC4 / Gap4: join-timeout fail-safe ───────────────────────────────────────


def test_stop_live_loop_returns_true_on_clean_join():
    """正常停止: loop thread が join できたら True を返し handle をクリアする。"""
    mgr = _make_loop_manager()
    mgr._ensure_live_loop()
    assert mgr._live_thread is not None
    assert mgr.stop_live_loop(timeout=2.0) is True
    assert mgr._live_loop is None
    assert mgr._live_thread is None


def test_stop_live_loop_fails_closed_when_worker_hangs():
    """hung worker（join timeout 後も is_alive）なら **False**（=finalize させない）を返す。

    この bool が C# finalize gate（KernelLiveProbe）の「Python runtime を finalize しない」契約。
    生存スレッドが GIL を握ったまま PythonEngine.Shutdown するとデッドロックするため、安全側に倒す。
    handle 自体は両経路でクリアする（次の _ensure_live_loop が hung/dead loop を再利用しないため）——
    fail-closed の信号は **戻り値**であって handle 保持ではない。
    """
    mgr = _make_loop_manager()

    release = threading.Event()
    stop_calls: list = []

    def _hang():
        # loop.stop を無視してハングし続けるワーカ（GIL を握ったまま止まらない状況の代理）。
        release.wait()

    hung = threading.Thread(target=_hang, name="hung-live-loop", daemon=True)
    hung.start()

    class _FakeLoop:
        def stop(self):
            # 実 loop の stop。hung worker はこれを観測しても止まらない状況を模す。
            stop_calls.append(True)

        def call_soon_threadsafe(self, fn):
            fn()  # loop.stop を呼ぶが、worker は止まらない。
            return None

    mgr._live_loop = _FakeLoop()
    mgr._live_thread = hung
    try:
        result = mgr.stop_live_loop(timeout=0.2)
        assert result is False, "hung worker must report unsafe (False)"
        assert stop_calls == [True], "loop.stop was not signaled before join"
        # 戻り値が信号。handle は再利用回避のため両経路でクリアされる。
        assert mgr._live_loop is None
        assert mgr._live_thread is None
        # idempotent fail-closed: a retried call must NOT fail open to True while
        # the GIL-holding thread is still alive (handles are already None).
        assert mgr.stop_live_loop(timeout=0.05) is False
    finally:
        release.set()
        hung.join(timeout=1.0)
    # once the hung thread actually dies, the signal clears to True.
    assert mgr.stop_live_loop(timeout=0.05) is True
