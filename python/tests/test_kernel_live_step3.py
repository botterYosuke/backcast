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


async def test_apply_venue_update_canceled_full_fill_marks_filled():
    """埋め込み約定が注文を完了させたら CANCELED ラベルで上書きせず FILLED 終端する（矛盾イベント防止）。

    venue が CANCELED + cumulative==quantity（完全約定）を運ぶのは矛盾応答だが、約定は実マネーなので
    FILLED を優先する（[OrderFilled(full), OrderCanceled] の矛盾ペアを出さない）。部分約定の CANCELED は従来通り。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="CANCELED", filled_qty=100.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled]  # 完了 → FILLED のみ（OrderCanceled を付けない）
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 100.0


async def test_apply_venue_update_reject_full_fill_marks_filled():
    """REJECTED に完全約定が埋め込まれていたら FILLED 終端する（reject ラベルで上書きしない）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="REJECTED", filled_qty=100.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled]
    assert order.status is OrderStatus.FILLED


async def test_modify_canceled_full_fill_marks_filled():
    """modify→CANCELED で埋め込み約定が訂正後数量を満たしたら FILLED 終端する（減額競合の clamp 約定とは区別）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="CANCELED", filled_qty=80.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=80.0)  # cumulative 80 == 訂正後数量 80（clamp 無し）
    assert _types(events) == [OrderFilled]
    assert order.status is OrderStatus.FILLED
    assert order.filled_qty == 80.0


async def test_cancel_reject_with_embedded_fill_keeps_live_and_books():
    """取消拒否(REJECTED)に埋め込まれた約定は会計し、元注文は live のまま保つ（modify/apply 経路と同型）。

    cancel() だけ REJECTED を apply_venue_update の手前で return していたため、取消待ち中に約定した数量
    （REJECTED+filled_qty）を捨て Portfolio が venue と desync していた。約定を会計しつつ注文は terminal に
    しない（取消拒否は注文自体の拒否ではない）。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_cancel_outcome(status="REJECTED", filled_qty=50.0, avg_price=8.0)
    events = await broker.cancel(order)
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 50.0
    assert order.filled_qty == 50.0
    assert order.status is OrderStatus.PARTIALLY_FILLED  # 50<100 → live のまま（terminal にしない）


async def test_fill_status_nonpositive_qty_is_rejected():
    """FILLED/PARTIALLY_FILLED で非正の累積数量は契約違反として fail-closed REJECT（非 fill の 0 と区別）。

    fill ステータスは正の累積数量を報告するのが契約。0・負は malformed なので未約定から REJECT 終端する
    （`cumulative<=0` を一律 duplicate 扱いして OrderAccepted/数量訂正だけ通すと不整合・High）。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    for bad in (0.0, -1.0):
        order = _order(qty=100.0)
        order.status = OrderStatus.SUBMITTED
        events = broker.apply_venue_update(
            order,
            OrderResult(status="FILLED", filled_qty=bad, avg_price=8.0, client_order_id="O-1"),
            source="submit_result",
        )
        assert _types(events) == [OrderRejected]  # ACCEPTED 経由で素通りさせない
        assert order.status is OrderStatus.REJECTED
        assert order.filled_qty == 0.0


async def test_modify_filled_nonpositive_qty_is_rejected():
    """modify→FILLED の非正数量も fail-closed REJECT（apply_venue_update と一致・events=[] で数量だけ訂正しない）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="FILLED", filled_qty=0.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=80.0)
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED


async def test_partial_fill_then_nonpositive_increment_is_dropped_not_rejected():
    """既に正常な部分約定済みに非正数量の fill が来たら、確定分は保持し不正増分のみ捨てる（REJECT しない）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 50.0

    events = broker.apply_venue_update(
        order,
        OrderResult(status="FILLED", filled_qty=-5.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert events == []  # 不正増分は捨てる
    assert order.filled_qty == 50.0  # 確定分は保持
    assert order.status is OrderStatus.PARTIALLY_FILLED  # REJECT しない


async def test_fill_status_stale_duplicate_is_deduped_not_rejected():
    """fill ステータスでも cumulative>0 かつ delta<=0（再報告）は dedup であって契約違反ではない。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    events = broker.apply_venue_update(
        order,
        OrderResult(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert events == []  # 同一 cumulative の再報告は dedup
    assert order.filled_qty == 50.0
    assert order.status is OrderStatus.PARTIALLY_FILLED  # REJECT しない


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


async def test_cancel_with_embedded_fill_is_accounted():
    """CANCELED に含まれる部分約定を捨てず会計してから終端する（#25 review round8 finding 1）。

    kabu modify は「取消中に 50 株約定・新規失敗」を CANCELED+filled_qty=50 で返す。捨てると Portfolio が
    venue 建玉と desync するため、約定を先に会計してから CANCELED 終端する。
    """
    from engine.kernel.portfolio import Portfolio

    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED  # 取消中の live order
    events = broker.apply_venue_update(
        order,
        OrderResult(status="CANCELED", filled_qty=50.0, avg_price=8.0, client_order_id="O-1"),
        source="cancel_result",
    )
    assert _types(events) == [OrderFilled, OrderCanceled]  # 約定 → 終端の順
    assert events[0].last_qty == 50.0 and events[0].last_px == 8.0
    assert order.status is OrderStatus.CANCELED
    assert order.filled_qty == 50.0
    # Portfolio に建玉 50 が残る（捨てると venue と desync）。
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(events[0])
    assert pf.open_positions()[0].quantity == 50.0


async def test_expired_with_embedded_fill_is_accounted():
    """EXPIRED に含まれる部分約定も捨てず会計してから終端する（#25 review round8 finding 1）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="EXPIRED", filled_qty=30.0, avg_price=9.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled, OrderExpired]
    assert events[0].last_qty == 30.0
    assert order.status is OrderStatus.EXPIRED
    assert order.filled_qty == 30.0


async def test_accepted_status_with_embedded_fill_is_accounted():
    """ACCEPTED status に埋め込まれた約定も捨てず会計する（apply_venue_update・finding 1 と同型）。

    実 adapter の submit ack や async EC（#23）は ACCEPTED と部分約定を同一応答に載せうる。fill ステータスのみ
    会計する旧実装は ACCEPTED を通さないので、約定が黙って捨てられ Portfolio が venue と desync していた。
    fill ステータス以外でも埋め込み約定を会計する（modify 経路の finding 1 と同じ穴が単一入口側にもあった）。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.SUBMITTED  # venue ACK 前
    events = broker.apply_venue_update(
        order,
        OrderResult(status="ACCEPTED", filled_qty=50.0, avg_price=8.0, client_order_id="O-1"),
        source="submit_result",
    )
    assert _types(events) == [OrderAccepted, OrderFilled]  # ACK → 約定の順
    assert events[1].last_qty == 50.0 and events[1].last_px == 8.0
    assert order.status is OrderStatus.PARTIALLY_FILLED  # ACCEPTED-live + 部分約定
    assert order.filled_qty == 50.0


async def test_accepted_embedded_fill_no_duplicate_accept():
    """既 ACCEPTED の注文に ACCEPTED+約定が来ても ACCEPTED は再 emit せず約定だけ会計する。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED  # 既に ACK 済み
    events = broker.apply_venue_update(
        order,
        OrderResult(status="ACCEPTED", filled_qty=40.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled]  # ACCEPTED は再 emit しない
    assert order.filled_qty == 40.0
    assert order.status is OrderStatus.PARTIALLY_FILLED


async def test_accepted_with_malformed_embedded_fill_stays_live():
    """ACCEPTED に malformed な約定（価格欠落）が埋め込まれても order は live のまま（fail-closed・drop）。

    status が authority な経路では、不正な約定は捨てるが注文は REJECTED にしない（fill ステータス経路の
    _invalid_fill_guard が REJECT 終端するのとは異なる）。ACK は emit する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.SUBMITTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="ACCEPTED", filled_qty=50.0, avg_price=None, client_order_id="O-1"),
        source="submit_result",
    )
    assert _types(events) == [OrderAccepted]  # ACK のみ・不正約定は捨てる
    assert order.status is OrderStatus.ACCEPTED  # REJECTED にしない
    assert order.filled_qty == 0.0


async def test_modify_cancel_with_embedded_fill_is_accounted():
    """modify が CANCELED+部分約定を返したら約定を会計してから終端する（finding 1・kabu modify 経路）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_modify_outcome(status="CANCELED", filled_qty=50.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=80.0)
    assert _types(events) == [OrderFilled, OrderCanceled]
    assert events[0].last_qty == 50.0
    assert order.status is OrderStatus.CANCELED
    assert order.filled_qty == 50.0


async def test_modify_increase_cancel_embedded_fill_above_old_qty():
    """数量増の訂正で取消約定が旧数量を超えても会計する（over-fill 判定が訂正後数量を使う・finding 1/2）。

    qty=100 を 150 へ訂正中、venue が CANCELED+filled_qty=140 を返す。旧数量 100 で over-fill 判定すると
    140>100 で取りこぼすが、訂正後数量 150 を使えば 140<=150 で会計される。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_modify_outcome(status="CANCELED", filled_qty=140.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=150.0)
    assert _types(events) == [OrderFilled, OrderCanceled]
    assert events[0].last_qty == 140.0  # 旧数量100なら over-fill で drop されていた
    assert order.status is OrderStatus.CANCELED
    assert order.filled_qty == 140.0


async def test_modify_new_qty_drives_filled_determination():
    """数量訂正後の FILLED 判定は旧数量でなく訂正後数量を使う（#25 review round8 finding 2）。

    100 株中 50 約定後、目標を 75 へ訂正し venue が cumulative 75 を FILLED で返すと、旧数量 100 と比較すると
    PARTIALLY_FILLED に誤判定する。訂正後数量 75 を FSM 判定に反映して FILLED にする。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 50.0 and order.quantity == 100.0

    a.set_next_modify_outcome(status="FILLED", filled_qty=75.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=75.0)
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 25.0  # 50 → 75 の増分
    assert order.quantity == 75.0  # 訂正後数量を反映
    assert order.filled_qty == 75.0
    assert order.status is OrderStatus.FILLED  # 旧数量100比較なら PARTIALLY_FILLED の誤判定


async def test_modify_new_qty_below_filled_is_rejected():
    """既約定数量を下回る数量訂正は venue へ送らず据え置く（#25 review round8 finding 2）。

    既約定 50 に対し new_qty=25 を受理すると quantity=25 / filled_qty=50 の不整合になる。送信前に弾く。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=50.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 50.0

    # venue へ届いたら下の outcome が消費され quantity が 25 に化けるはず → 据え置きを証明。
    a.set_next_modify_outcome(status="ACCEPTED")
    events = await broker.modify(order, new_qty=25.0)
    assert events == []
    assert order.quantity == 100.0  # 旧数量のまま（25 に化けない）
    assert order.filled_qty == 50.0
    assert order.status is OrderStatus.PARTIALLY_FILLED


async def test_modify_accepted_with_embedded_fill_is_accounted():
    """訂正成功(ACCEPTED)に embedded された取消待ち約定を捨てず会計する（#25 review finding 1）。

    kabu の cancel-replace 訂正は成立しても（status=ACCEPTED）、取消待ち中に約定した数量を
    `filled_qty`/`avg_price` に載せて返す（kabusapi_execution.py:457）。CANCELED/FILLED 以外の
    status だと会計されず、venue は 50 株約定済みなのに kernel Portfolio は未約定のまま desync する。
    任意 status の embedded fill を先に会計してから status を処理する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    a.set_next_modify_outcome(status="ACCEPTED", filled_qty=50.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=120.0)
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 50.0
    assert order.filled_qty == 50.0
    assert order.avg_px == 8.0
    assert order.quantity == 120.0  # 訂正後数量を反映
    assert order.status is OrderStatus.PARTIALLY_FILLED  # ACCEPTED-live + 部分約定


async def test_modify_reduce_race_fill_above_new_qty_is_accounted():
    """減額訂正中に訂正後数量を超える約定が走っても会計する（over-fill 上限は max(旧,新)数量・finding 2）。

    kabu の cancel-replace は merged_qty<=0（既に目標超過約定）や新規失敗を、訂正後数量を上回る
    cumulative filled_qty + 終端 status で返す（kabusapi_execution.py:409-447）。over-fill 判定に
    訂正後数量(50)だけを使うと 80>50 で取りこぼし Portfolio が desync する。約定は旧注文(100)に対して
    起きうるので上限は max(旧100, 新50)=100。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=40.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)
    assert order.filled_qty == 40.0

    a.set_next_modify_outcome(status="CANCELED", filled_qty=80.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=50.0)  # 50 >= 既約定 40 なので line134 ガードは通過
    assert _types(events) == [OrderFilled, OrderCanceled]
    assert events[0].last_qty == 40.0  # 40 → 80 の増分（訂正後数量50で over-fill 判定すると drop された）
    assert order.filled_qty == 80.0
    assert order.status is OrderStatus.CANCELED


async def test_modify_reject_with_embedded_fill_keeps_prior_qty():
    """訂正拒否(REJECTED)に約定が embedded されていても訂正後数量を適用しない（finding 1）。

    REJECTED は訂正リクエスト自体の拒否で、元注文は venue に旧数量のまま live。約定が報告されたら
    それは旧注文への実約定なので会計するが、order.quantity は旧数量を維持し、約定が旧数量未満なら
    PARTIALLY_FILLED に留める（訂正後数量で FILLED に誤判定しない・注文全体を terminal にしない D6）。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    a.set_next_modify_outcome(status="REJECTED", filled_qty=60.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=60.0)
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 60.0
    assert order.filled_qty == 60.0
    assert order.quantity == 100.0  # 訂正は不成立: 旧数量を維持（60 に化けない）
    assert order.status is OrderStatus.PARTIALLY_FILLED  # 60 < 100 → 旧数量で FILLED 誤判定しない


async def _accepted_broker(qty=100.0):
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=qty)
    await broker.submit(order)
    return a, broker, order


async def test_modify_expired_terminalizes_with_order_expired():
    """modify が EXPIRED を返したら OrderExpired で終端する（status と外部通知を一致・apply_venue_update と同型）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="EXPIRED")
    events = await broker.modify(order, new_qty=50.0)
    assert _types(events) == [OrderExpired]
    assert order.status is OrderStatus.EXPIRED


async def test_modify_expired_with_embedded_fill_is_accounted():
    """modify→EXPIRED に埋め込まれた約定も捨てず会計してから終端する（finding 1）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="EXPIRED", filled_qty=30.0, avg_price=9.0)
    events = await broker.modify(order, new_qty=80.0)
    assert _types(events) == [OrderFilled, OrderExpired]
    assert events[0].last_qty == 30.0
    assert order.filled_qty == 30.0
    assert order.status is OrderStatus.EXPIRED


async def test_modify_denied_restores_prior_like_reject():
    """modify→DENIED は訂正リクエストの拒否として元状態へ復帰する（注文全体を terminal にしない・D6）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="DENIED")
    events = await broker.modify(order, new_qty=50.0)
    assert events == []
    assert order.status is OrderStatus.ACCEPTED  # 復帰
    assert order.quantity == 100.0  # 訂正不成立: 旧数量維持


async def test_modify_accepted_full_embedded_fill_marks_filled():
    """ACCEPTED に訂正後数量を満たす約定が埋め込まれていたら FILLED 終端する（境界: cumulative>=訂正後数量）。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="ACCEPTED", filled_qty=80.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=80.0)
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 80.0
    assert order.status is OrderStatus.FILLED  # 80 >= 訂正後数量 80
    assert order.quantity == 80.0


async def test_modify_price_only_success_keeps_live():
    """価格のみの訂正が成立（ACCEPTED・約定なし）したら live のまま、イベントは出さない。"""
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="ACCEPTED")
    events = await broker.modify(order, new_price=9.0)
    assert events == []
    assert order.status is OrderStatus.ACCEPTED
    assert order.quantity == 100.0


async def test_modify_invalid_new_qty_is_not_sent():
    """非有限/非正の new_qty は venue へ送らず据え置く（不正値を adapter に渡さない・finding 2）。"""
    import math as _math

    for bad in (0.0, -5.0, _math.nan, _math.inf):
        a, broker, order = await _accepted_broker()
        # 届いたら下の outcome が消費され quantity が化けるはず → 据え置きを証明。
        a.set_next_modify_outcome(status="CANCELED", filled_qty=10.0, avg_price=8.0)
        events = await broker.modify(order, new_qty=bad)
        assert events == []
        assert order.status is OrderStatus.ACCEPTED
        assert order.quantity == 100.0
        assert order.filled_qty == 0.0  # outcome 未消費（約定が会計されていない）


async def test_modify_invalid_new_price_is_not_sent():
    """非有限/非正の new_price は venue へ送らず据え置く（finding 2）。"""
    import math as _math

    for bad in (0.0, -1.0, _math.nan):
        a, broker, order = await _accepted_broker()
        a.set_next_modify_outcome(status="CANCELED", filled_qty=10.0, avg_price=8.0)
        events = await broker.modify(order, new_price=bad)
        assert events == []
        assert order.status is OrderStatus.ACCEPTED
        assert order.filled_qty == 0.0


async def test_modify_adapter_exception_restores_prior():
    """modify の adapter 例外は live 据え置き（注文を terminal にしない）。"""
    a, broker, order = await _accepted_broker()

    async def _boom(**kwargs):
        raise RuntimeError("venue down")

    a.modify_order = _boom  # type: ignore[assignment]
    events = await broker.modify(order, new_qty=50.0)
    assert events == []
    assert order.status is OrderStatus.ACCEPTED
    assert order.quantity == 100.0  # 例外時は訂正後数量を適用しない


async def test_modify_on_terminal_order_is_noop():
    """既に terminal な注文への modify は no-op（終端後の操作を弾く）。"""
    a, broker, order = await _accepted_broker()
    order.status = OrderStatus.FILLED  # terminal
    a.set_next_modify_outcome(status="CANCELED", filled_qty=10.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=50.0)
    assert events == []
    assert order.status is OrderStatus.FILLED  # 不変
    assert order.filled_qty == 0.0  # outcome 未消費


async def test_modify_rejected_increase_overfill_is_dropped():
    """訂正不成立(REJECTED)の増額では over-fill 上限は旧数量。旧数量を超える約定は会計しない（max review）。

    100→300 への増額が REJECTED なら元注文は venue に旧数量 100 のまま。venue が 250 約定を報告しても
    100 株注文に 250 約定はあり得ないので fail-closed で捨てる（ceiling=max(旧,新) を訂正不成立にも使うと
    250<=300 で通り filled_qty 250 > quantity 100 の over-fill になる）。
    """
    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="REJECTED", filled_qty=250.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=300.0)
    assert events == []  # 旧数量 100 超の約定は捨てる
    assert order.status is OrderStatus.ACCEPTED
    assert order.quantity == 100.0
    assert order.filled_qty == 0.0


async def test_modify_filled_nonfinite_qty_is_rejected():
    """modify→FILLED に非有限 filled_qty が来たら fail-closed で REJECT（apply_venue_update と同じ）。"""
    import math as _math

    a, broker, order = await _accepted_broker()
    a.set_next_modify_outcome(status="FILLED", filled_qty=_math.nan, avg_price=8.0)
    events = await broker.modify(order, new_qty=100.0)
    assert _types(events) == [OrderRejected]
    assert order.status is OrderStatus.REJECTED


async def test_apply_venue_update_reject_with_embedded_fill_is_accounted():
    """REJECTED 結果に埋め込まれた約定も捨てず会計してから終端する（CANCELED/EXPIRED と同型・finding 1）。

    async EC（#23）や cancel-replace が「約定→残数量 REJECTED」を REJECTED+filled_qty で運んだとき、
    未約定扱いで OrderRejected だけ出すと約定が宙に浮き Portfolio が desync する。約定を会計し、既約定>0 なら
    CANCELED で終端化して fill を保持する。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="REJECTED", filled_qty=50.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled, OrderCanceled]  # 約定 → 終端
    assert events[0].last_qty == 50.0
    assert order.filled_qty == 50.0
    assert order.status is OrderStatus.CANCELED  # 既約定ありなので REJECTED でなく CANCELED


async def test_apply_venue_update_denied_unfilled_terminalizes_rejected():
    """DENIED 結果（約定なし）は REJECTED 終端として扱う（ACCEPTED 経由・phantom fill にしない）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.SUBMITTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="DENIED", filled_qty=0.0, avg_price=None, client_order_id="O-1"),
        source="submit_result",
    )
    assert _types(events) == [OrderRejected]  # ACCEPTED+OrderFilled の phantom にしない
    assert order.status is OrderStatus.REJECTED
    assert order.filled_qty == 0.0


async def test_modify_reduce_race_filled_status_keeps_qty_invariant():
    """減額訂正の競合約定が fill ステータスで訂正後数量を超えても filled<=quantity を保つ（max review）。

    kabu の cancel-replace は merged_qty<=0（目標超過約定）を FILLED+cumulative で返す（kabusapi_execution.py:
    409-423）。訂正後数量 50 を目標にしつつ実約定 70 を会計すると filled(70) > quantity(50) の不整合になる。
    会計側で quantity を実約定まで引き上げ、FILLED 判定と filled<=quantity 不変条件を両立させる。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="PARTIALLY_FILLED", filled_qty=40.0, avg_price=8.0)
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    a.set_next_modify_outcome(status="FILLED", filled_qty=70.0, avg_price=8.0)
    events = await broker.modify(order, new_qty=50.0)  # 50 >= 既約定 40 なので line134 ガードは通過
    assert _types(events) == [OrderFilled]
    assert events[0].last_qty == 30.0  # 40 → 70 の増分
    assert order.filled_qty == 70.0
    assert order.status is OrderStatus.FILLED
    assert order.quantity >= order.filled_qty  # filled<=quantity 不変条件
    assert order.quantity == 70.0  # 訂正後数量 50 でなく実約定 70 まで引き上げ


async def test_modify_canceled_no_fill_persists_venue_order_id():
    """約定なしの modify→CANCELED でも order.venue_order_id を確定する（apply_venue_update と対称）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(cid="O-7", qty=100.0)
    order.venue_order_id = None  # 明示
    await broker.submit(order)
    order.venue_order_id = None  # ACCEPTED で付くので modify 経路の確定を見るため一旦クリア
    a.set_next_modify_outcome(status="CANCELED")
    events = await broker.modify(order, new_qty=50.0)
    assert _types(events) == [OrderCanceled]
    assert order.venue_order_id == "O-7"  # client_order_id フォールバックで確定


async def test_apply_venue_update_denied_with_embedded_fill_is_accounted():
    """DENIED に埋め込まれた約定は会計し CANCELED で終端する（phantom OrderAccepted を出さない）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.SUBMITTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="DENIED", filled_qty=40.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled, OrderCanceled]  # 約定 → 終端（ACCEPTED 経由しない）
    assert order.filled_qty == 40.0
    assert order.status is OrderStatus.CANCELED


# ── #25 review: 取消受付 / 訂正受付（PENDING_CANCEL / PENDING_UPDATE）─────────────
# kabu の取消 ACK は「取消受付」にすぎず終端ではない（確定は GET /orders polling が後追い）。
# 受付を終端と誤認すると受付〜確定の隙間の競合約定を取りこぼす（finding 1）。訂正受付
# （PENDING_UPDATE）も成立扱いせず new_qty を確定まで反映しない（finding 2）。


async def test_cancel_pending_ack_keeps_order_open_for_race_fill():
    """取消受付（PENDING_CANCEL）を終端と誤認せず注文を open に保ち、確定前の競合約定を会計する（finding 1）。

    旧実装は cancel ACK を CANCELED 終端にし、後続 polling の PARTIALLY_FILLED を
    apply_venue_update 冒頭の terminal early-return で捨て、filled_qty=0 のまま Portfolio が
    venue 建玉と desync した。受付は PENDING_CANCEL（非終端）で order を open に保つ。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    a.set_next_cancel_outcome(status="PENDING_CANCEL")
    cancel_events = await broker.cancel(order)
    assert cancel_events == []  # 受付では終端イベントを出さない
    assert order.status is OrderStatus.PENDING_CANCEL  # 非終端（open）

    # 確定 polling 前の競合約定（PARTIALLY_FILLED 30@8）は捨てず会計する。
    fill_events = broker.apply_venue_update(
        order,
        OrderResult(status="PARTIALLY_FILLED", filled_qty=30.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(fill_events) == [OrderFilled]
    assert fill_events[0].last_qty == 30.0 and fill_events[0].last_px == 8.0
    assert order.filled_qty == 30.0


async def test_cancel_pending_then_poll_confirms_terminal_cancel():
    """取消受付（PENDING_CANCEL）の後、確定 polling の CANCELED で終端化する（受付→確定の二段）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    a.set_next_cancel_outcome(status="PENDING_CANCEL")
    await broker.cancel(order)
    assert order.status is OrderStatus.PENDING_CANCEL

    confirm = broker.apply_venue_update(
        order,
        OrderResult(status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(confirm) == [OrderCanceled]
    assert order.status is OrderStatus.CANCELED
    # 終端後の遅延イベントは無視（idempotent）。
    assert broker.apply_venue_update(
        order,
        OrderResult(status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="O-1"),
        source="venue_stream",
    ) == []


async def test_apply_venue_update_pending_cancel_books_embedded_fill():
    """単一入口（async EC #23）が PENDING_CANCEL を非終端として通し埋め込み約定を会計する（finding 1 sweep）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    events = broker.apply_venue_update(
        order,
        OrderResult(status="PENDING_CANCEL", filled_qty=30.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled]  # 約定は会計（終端化しない）
    assert order.filled_qty == 30.0
    assert order.status is OrderStatus.PARTIALLY_FILLED  # fill 進行（非終端を維持）


async def test_modify_pending_update_keeps_pending_and_defers_qty():
    """訂正受付（PENDING_UPDATE）を成立扱いせず、new_qty を確定まで反映しない（finding 2）。

    旧実装は modify_took_effect=（status not in REJECTED/DENIED）で PENDING_UPDATE を成立とみなし
    order.quantity=new_qty に更新、約定が無いため status を prior（ACCEPTED）へ復帰させ
    `status=ACCEPTED, quantity=50` という偽の成立を作っていた。受付は確定ではないので
    status=PENDING_UPDATE を維持し、数量は確定イベントまで据え置く。
    """
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    a.set_next_order_outcome(status="ACCEPTED")
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    await broker.submit(order)

    a.set_next_modify_outcome(status="PENDING_UPDATE")
    events = await broker.modify(order, new_qty=50.0)
    assert events == []  # 受付では終端/約定イベントを出さない
    assert order.status is OrderStatus.PENDING_UPDATE  # 受付＝非終端・成立ではない
    assert order.quantity == 100.0  # 確定まで new_qty を反映しない


async def test_apply_venue_update_pending_update_stays_open_books_fill():
    """単一入口の PENDING_UPDATE も非終端として通し埋め込み約定を会計する（finding 2 sweep）。"""
    a = MockVenueAdapter()
    await a.login(None)  # type: ignore[arg-type]
    broker = LiveBroker(adapter=a, venue="MOCK")
    order = _order(qty=100.0)
    order.status = OrderStatus.ACCEPTED
    # 約定無しの PENDING_UPDATE → status=PENDING_UPDATE（open）を維持。
    no_fill = broker.apply_venue_update(
        order,
        OrderResult(status="PENDING_UPDATE", filled_qty=0.0, avg_price=None, client_order_id="O-1"),
        source="venue_stream",
    )
    assert no_fill == []
    assert order.status is OrderStatus.PENDING_UPDATE
    # 埋め込み約定があれば会計（fill が status を進める・非終端）。
    events = broker.apply_venue_update(
        order,
        OrderResult(status="PENDING_UPDATE", filled_qty=40.0, avg_price=8.0, client_order_id="O-1"),
        source="venue_stream",
    )
    assert _types(events) == [OrderFilled]
    assert order.filled_qty == 40.0
    assert order.status is OrderStatus.PARTIALLY_FILLED
