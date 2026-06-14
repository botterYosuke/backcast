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
    OrderExpired,
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


async def test_submit_priced_fill_books_fully():
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order()
    events = await broker.submit(order)
    # ACCEPTED then FILLED, in order.
    assert _types(events) == [OrderAccepted, OrderFilled]
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 100.0
    fill = events[1]
    assert fill.last_qty == 100.0
    assert fill.last_px == 8.0
    assert fill.cumulative_filled_qty == 100.0


async def test_submit_unpriced_fill_is_rejected():
    """価格を伴わない fill は 0 円で会計せず fail-closed で REJECTED（#25 review finding 1）。

    MockVenueAdapter の既定 MARKET 約定は avg_price=None を返す。旧実装はこれを order.avg_px
    （初回は 0）で会計し `FILLED qty=100 avg_px=0` と建玉/cash を破損させた。正価格を伴わない
    fill は適用せず注文を REJECTED 終端する（実 venue は必ず価格を報告する・mock 既定は degenerate）。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order()
    events = await broker.submit(order)  # 既定 = avg_price 無しの FILLED
    # REJECTED は ACCEPTED を経由しない（FSM 不変条件）: SUBMITTED から直接 REJECTED。
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED
    assert order.filled_qty == 0.0  # 0 円約定は会計されない
    assert "FILL_WITHOUT_PRICE" in events[0].reason


async def test_unpriced_increment_after_partial_is_dropped():
    """正価格で部分約定済みの後に来た価格欠落の増分は捨て、確定済み fill を保持する（finding 1）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 50.0 and order.status is OrderStatus.PARTIALLY_FILLED

    more = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=100.0, avg_price=None, client_order_id="O-1"),
        source="venue_stream",
    )
    assert more == []  # 価格無しの増分は適用しない
    assert order.filled_qty == 50.0  # 既約定 50@8 を保持
    assert order.status is OrderStatus.PARTIALLY_FILLED  # 注文は live のまま


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
    a.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
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


async def test_cancel_race_fill_is_not_discarded():
    """cancel 応答が約定（キャンセル競合）を運んだら捨てず fill として正規化する（#25 review finding 3）。"""

    class _FillOnCancelAdapter:
        async def cancel_order(self, *, venue, order_id):
            return OrderResult(
                status="FILLED", filled_qty=100.0, avg_price=8.0, client_order_id=order_id
            )

    broker = LiveBroker(adapter=_FillOnCancelAdapter(), venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED  # live order being canceled
    events = await broker.cancel(order)
    assert _types(events) == [OrderFilled]
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 100.0


async def test_reject_after_partial_fill_terminates_as_canceled():
    """部分約定済みに後から REJECTED が来たら既約定を捨てず CANCELED で終端する（#25 review finding 5）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.status is OrderStatus.PARTIALLY_FILLED and order.filled_qty == 50.0

    events = broker.apply_venue_update(
        order,
        OrderResult(
            status="REJECTED", filled_qty=0.0, avg_price=None,
            client_order_id="O-1", reject_reason="STALE",
        ),
        source="venue_stream",
    )
    assert _types(events) == [OrderCanceled]
    assert order.status is OrderStatus.CANCELED
    assert order.filled_qty == 50.0  # 既約定分は保持（注文全体を REJECTED にしない）


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


async def test_overfill_cumulative_is_rejected():
    """累積約定数量が注文数量を超える fill は会計せず REJECTED（#25 review finding 1）。

    order qty=100 に cumulative=150 が来ると、旧実装は建玉 150 を作った。注文数量を超える over-fill は
    venue 契約違反として会計せず fail-closed する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="FILLED", filled_qty=150.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    events = await broker.submit(order)
    assert _types(events) == [OrderRejected]  # REJECTED は ACCEPTED を経由しない
    assert order.status is OrderStatus.REJECTED
    assert order.filled_qty == 0.0  # over-fill は会計されない
    assert "FILL_EXCEEDS_ORDER_QTY" in events[0].reason


async def test_nonfinite_cumulative_qty_is_rejected():
    """非有限の累積約定数量（inf/NaN）は cash=NaN / position=(inf,NaN) を防ぎ REJECTED（finding 1）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.SUBMITTED  # venue 往復中の live order
    events = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=float("inf"), avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED
    assert order.filled_qty == 0.0
    assert "FILL_NONFINITE_QTY" in events[0].reason


async def test_negative_increment_price_is_dropped_after_partial():
    """正の累積平均でも増分価格が負になる fill は会計しない（#25 review finding 2）。

    50株@100 約定後に累積 100株・平均@40 を受けると後半 50 の増分価格 = (100×40 − 50×100)/50 = -20。
    負の増分を BUY fill として適用すると現金が増える誤会計になるため、その増分を捨て確定済みを保持する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=100.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 50.0 and order.avg_px == 100.0

    more = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=100.0, avg_price=40.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert more == []  # 負の増分価格は会計しない
    assert order.filled_qty == 50.0  # 確定済み 50@100 を保持
    assert order.avg_px == 100.0
    assert order.status is OrderStatus.PARTIALLY_FILLED  # 注文は live のまま


async def test_expired_emits_order_expired_not_canceled():
    """EXPIRED は OrderExpired で終端通知し内部 status と外部通知を一致させる（#25 review finding 3）。

    OrderCanceled で代用すると projection が CANCELED に化け、order.status=EXPIRED と矛盾する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    events = broker.apply_venue_update(
        order,
        OrderResult(status="EXPIRED", filled_qty=0.0, avg_price=None, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderExpired]
    assert order.status is OrderStatus.EXPIRED
