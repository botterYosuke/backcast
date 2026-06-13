"""Step 3 (#25) — LiveBroker order FSM の決定的テスト（AC: accepted/partial/filled/rejected/
canceled/modify + 重複排除）。

mock venue tracer の authoritative fill source は同期 OrderResult（D1）。fill 重複排除は
累積約定数量 delta（受信イベント数ではない）。
"""
from __future__ import annotations

import pytest

from engine.kernel.live.broker import LiveBroker
from engine.kernel.orders import (
    Order,
    OrderAccepted,
    OrderCanceled,
    OrderFilled,
    OrderRejected,
    OrderSide,
    OrderStatus,
)
from engine.live.mock_adapter import MockVenueAdapter
from engine.live.order_types import OrderResult

pytestmark = pytest.mark.asyncio


def _order(cid="O-1", side=OrderSide.BUY, qty=100.0, instrument="8918.TSE") -> Order:
    return Order(
        client_order_id=cid, strategy_id="LIVE-ab12cd34", instrument_id=instrument,
        side=side, quantity=qty,
    )


def _types(events):
    return [type(e) for e in events]


async def test_submit_default_fills_fully():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order()
    events = await broker.submit(order)
    # ACCEPTED then FILLED, in order.
    assert _types(events) == [OrderAccepted, OrderFilled]
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 100.0
    fill = events[1]
    assert fill.last_qty == 100.0
    assert fill.cumulative_filled_qty == 100.0


async def test_submit_reject_does_not_pass_accepted():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="REJECTED", reject_reason="NO_BUYING_POWER")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order()
    events = await broker.submit(order)
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED
    assert "NO_BUYING_POWER" in events[0].reason


async def test_submit_adapter_exception_rejects():
    a = MockVenueAdapter()  # not logged in → submit_order raises RuntimeError
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order()
    events = await broker.submit(order)
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED
    assert "ADAPTER_ERROR" in events[0].reason


async def test_partial_then_full_via_async_stream():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    events = await broker.submit(order)
    assert _types(events) == [OrderAccepted, OrderFilled]
    assert order.status is OrderStatus.PARTIALLY_FILLED
    assert order.filled_qty == 50.0
    assert events[1].last_qty == 50.0

    # 後続の（非同期 EC 相当）累積 fill = 100 → 差分 50 だけ反映、FILLED へ。ACCEPTED は再 emit しない。
    more = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=100.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(more) == [OrderFilled]
    assert more[0].last_qty == 50.0
    assert more[0].cumulative_filled_qty == 100.0
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 100.0


async def test_partial_then_full_incremental_price_from_cumulative_notional():
    # venue は累積平均価格を報告する。50@8 の後に累積 100@**9** が来たら後半 50 は @10。
    # 増分価格に累積平均 9 を使うと Portfolio が平均 8.5 になり誤る（finding 1）。
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    events = await broker.submit(order)
    assert events[1].last_qty == 50.0 and events[1].last_px == 8.0  # first 50 @ 8

    more = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=100.0, avg_price=9.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(more) == [OrderFilled]
    # 増分 50 の価格 = (100×9 − 50×8) / 50 = 10、累積平均（order.avg_px）= 9。
    assert more[0].last_qty == 50.0
    assert more[0].last_px == 10.0
    assert order.avg_px == 9.0

    # Portfolio に両 fill を適用すると建玉平均は venue 累積平均 9.0 に一致する（8.5 にならない）。
    from engine.kernel.portfolio import Portfolio

    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(events[1])   # 50 @ 8
    pf.apply_fill(more[0])     # 50 @ 10
    assert pf.open_positions()[0].avg_px == 9.0


async def test_cumulative_dedup_ignores_repeated_fill():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)  # → FILLED, cumulative 100
    assert order.status is OrderStatus.FILLED
    # 同じ約定が共有 adapter の EC stream で再配送されても二重建玉しない（delta<=0 → 無視）。
    dup = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=100.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert dup == []


async def test_cancel_confirms_then_idempotent():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")  # accepted, no fill → live order
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    events = await broker.submit(order)
    assert order.status is OrderStatus.ACCEPTED
    assert _types(events) == [OrderAccepted]

    cancel_events = await broker.cancel(order)
    assert _types(cancel_events) == [OrderCanceled]
    assert order.status is OrderStatus.CANCELED
    # 終端後の再 cancel は no-op（idempotent）。
    assert await broker.cancel(order) == []


async def test_cancel_reject_keeps_order_live():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_cancel_outcome(status="REJECTED", reject_reason="ALREADY_FILLED")
    events = await broker.cancel(order)
    assert events == []
    assert order.status is OrderStatus.ACCEPTED  # 取消拒否 → live 据え置き


async def test_modify_reject_restores_prior_state():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_modify_outcome(status="REJECTED", reject_reason="BAD_PRICE")
    events = await broker.modify(order, new_price=9.0)
    assert events == []
    assert order.status is OrderStatus.ACCEPTED  # 注文全体を REJECTED にしない（D6）


async def test_modify_canceled_terminalizes():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_modify_outcome(status="CANCELED")
    events = await broker.modify(order, new_qty=50.0)
    assert _types(events) == [OrderCanceled]
    assert order.status is OrderStatus.CANCELED
