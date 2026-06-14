"""(b) venue 非同期確定配線 — poll event → kernel broker.apply_venue_update（#23・findings 0014）。

Auto roundtrip の取消競合を end-to-end で塞ぐ配線を、本番と同じ seam（background live loop・
共有 LiveRunner.bus・MockVenueAdapter・controller）で駆動する:

  resting ACCEPTED order
    → cancel_inflight: broker.cancel が PENDING_CANCEL 受付（注文は open のまま）
    → venue poll の async 競合約定 PARTIALLY_FILLED → controller.apply_venue_async_event
       → broker が増分を会計（Portfolio に反映・受付 status を保持）
    → venue poll の async 終端 CANCELED → broker terminal・driver が _orders から除去（open 0）

受付を終端と誤認する旧契約では、この競合約定（30 株）が取りこぼされ Portfolio が venue と
desync した（CONTEXT.md「取消受付 / 取消確定」）。本テストはその取りこぼしが起きないことを固定する。
"""
from __future__ import annotations

import asyncio
import threading
import time
from types import SimpleNamespace

from engine.kernel.live.controller import KernelLiveEngineController
from engine.kernel.orders import OrderSide, OrderStatus
from engine.kernel.strategy import Strategy as KernelStrategyBase
from engine.live.adapter import KlineUpdate
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


class _BuyOnceRest(KernelStrategyBase):
    """bar 1 で 100 株 BUY を 1 回だけ出す（mock を ACCEPTED/未約定にして resting させる）。"""

    def __init__(self, **kw):
        super().__init__(**kw)
        self._done = False

    def on_bar(self, bar):
        if not self._done:
            self._done = True
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)


def _kline(i: int, close: float) -> KlineUpdate:
    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def _venue_event(cid: str, status: str, filled_qty: float, avg_price: float | None):
    """adapter on_order_event が運ぶ OrderEventData 互換（driver が読む 4 field のみ）。"""
    return SimpleNamespace(
        client_order_id=cid, status=status, filled_qty=filled_qty, avg_price=avg_price
    )


def test_cancel_race_fill_is_accounted_via_async_poll() -> None:
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="test-live-loop", daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    def feed(ev) -> None:
        # production と同じく live loop thread 上で sync 実行する。
        async def _call() -> None:
            controller.apply_venue_async_event(ev)

        run(_call())

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

    try:
        # resting order: mock を ACCEPTED・未約定にする（poll が後で約定/取消を運ぶ）。
        adapter.set_next_order_outcome(status="ACCEPTED", filled_qty=0.0, avg_price=None)
        controller.attach(
            strategy_cls=_BuyOnceRest, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-async001",
            session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))

        driver = controller._driver
        deadline = time.time() + 5.0
        while time.time() < deadline and not driver._orders:
            time.sleep(0.02)
        assert driver._orders, "resting order が driver に現れない"
        cid = next(iter(driver._orders))
        assert driver._orders[cid].status is OrderStatus.ACCEPTED

        # --- 取消受付（sync broker.cancel → PENDING_CANCEL、注文は open のまま） ---
        adapter.set_next_cancel_outcome(status="PENDING_CANCEL")
        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-async001")
        assert driver._orders[cid].status is OrderStatus.PENDING_CANCEL
        assert driver._portfolio.net_signed_qty(IID) == 0.0

        # --- 未追跡 cid の async event は無視される（manual / 別 run の取り違え防止） ---
        feed(_venue_event("FOREIGN-cid", "FILLED", 100.0, 8.0))
        assert driver._portfolio.net_signed_qty(IID) == 0.0

        # --- 受付〜確定の隙間の競合約定（async poll の PARTIALLY_FILLED） ---
        feed(_venue_event(cid, "PARTIALLY_FILLED", 30.0, 8.0))
        # 競合約定 30 株が Portfolio に反映される（取りこぼさない）。約定が status を進めるので
        # 受付（PENDING_CANCEL）→ PARTIALLY_FILLED へ（いずれも open・取消は venue で進行中）。
        assert driver._portfolio.net_signed_qty(IID) == 30.0
        assert cid in driver._orders  # まだ open（終端なら _orders から除去される）
        assert driver._orders[cid].status is OrderStatus.PARTIALLY_FILLED

        # --- poll の取消確定（async CANCELED・累積 30 は既会計＝新規 fill 無し） ---
        feed(_venue_event(cid, "CANCELED", 30.0, 8.0))
        assert driver._portfolio.net_signed_qty(IID) == 30.0  # 競合約定は保持
        assert cid not in driver._orders  # 終端化で _orders から除去（open leak 防止）
        assert len(driver._orders) == 0

        controller.detach(nautilus_strategy_id="LIVE-async001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


class _BuyThenHedgeOnFill(KernelStrategyBase):
    """bar 1 で BUY、その約定（on_order の OrderFilled）に反応して SELL を 1 回出す。

    entry は async poll fill 経由で来る前提（mock を ACCEPTED 据え置きにし、約定は
    apply_venue_async_event で運ぶ）。反応注文が venue に到達するかを見る。
    """

    def __init__(self, **kw):
        super().__init__(**kw)
        self._bought = False
        self._hedged = False

    def on_bar(self, bar):
        if not self._bought:
            self._bought = True
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)

    def on_order(self, event):
        from engine.kernel.orders import OrderFilled

        if isinstance(event, OrderFilled) and not self._hedged:
            self._hedged = True
            self.submit_market(self.instrument_id, OrderSide.SELL, 40)


def test_reaction_order_from_async_fill_reaches_venue() -> None:
    """async fill に反応して on_order が積んだ注文が drain され venue に到達する（review finding 2）。"""
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="test-live-loop2", daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    def feed(ev) -> None:
        async def _call() -> None:
            controller.apply_venue_async_event(ev)

        run(_call())

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

    try:
        adapter.set_next_order_outcome(status="ACCEPTED", filled_qty=0.0, avg_price=None)
        controller.attach(
            strategy_cls=_BuyThenHedgeOnFill, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-react001",
            session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))

        driver = controller._driver
        deadline = time.time() + 5.0
        while time.time() < deadline and not driver._orders:
            time.sleep(0.02)
        assert driver._orders, "entry order が現れない"
        entry_cid = next(iter(driver._orders))
        submit_calls_before = adapter.submit_order_call_count

        # 反応注文の hedge SELL は ACCEPTED（resting）にして、drain されて venue 到達のみを見る。
        adapter.set_next_order_outcome(status="ACCEPTED", filled_qty=0.0, avg_price=None)
        # entry が async poll fill で約定 → on_order(OrderFilled) → SELL 40 を enqueue → drain。
        feed(_venue_event(entry_cid, "FILLED", 100.0, 8.0))

        deadline = time.time() + 5.0
        while time.time() < deadline and adapter.submit_order_call_count <= submit_calls_before:
            time.sleep(0.02)
        # 反応 SELL が adapter（venue）へ到達した（drain が走った証拠）。
        assert adapter.submit_order_call_count == submit_calls_before + 1
        assert driver._portfolio.net_signed_qty(IID) == 100.0  # entry 約定のみ反映（hedge は resting）

        controller.detach(nautilus_strategy_id="LIVE-react001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
