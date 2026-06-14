"""(c-1) ManualOrderFacade.cancel — 取消受付 PENDING_CANCEL を非終端で honor する。

findings 0014・#23 cancel-ACK (c-1)。manual-cancel（UI ボタン）の sync facade は
従来 `status="CANCELED"` をハードコードしていた。ack-then-poll venue（kabu）が受付を
`PENDING_CANCEL` で返すようになった（(a)）ので、facade は受付を**非終端**として注文を
open に保つ。終端 CANCELED の訂正は backend event（poll → _publish_order_event）経由＝
(b) 配線とセット。instant-confirm な mock venue は受付＝確定で即 CANCELED を返す。
"""
from __future__ import annotations

from types import SimpleNamespace

from engine.live.order_facade import ManualOrderFacade, OrderFacadeError
from engine.live.order_types import OrderResult, OrderState, is_terminal


class _FakeAdapter:
    """cancel_order の返り OrderResult を固定する throwaway adapter。"""

    def __init__(self, result: OrderResult) -> None:
        self._result = result

    async def cancel_order(self, *, venue: str, order_id: str) -> OrderResult:
        return self._result


def _facade(result: OrderResult) -> ManualOrderFacade:
    facade = ManualOrderFacade(_FakeAdapter(result))  # type: ignore[arg-type]
    facade._states["c1"] = OrderState(
        order_id="c1",
        venue_order_id="V1",
        client_order_id="c1",
        status="ACCEPTED",
        filled_qty=30.0,
        avg_price=1000.0,
        ts_ms=0,
    )
    return facade


async def test_cancel_pending_ack_keeps_order_open() -> None:
    """取消受付 PENDING_CANCEL は非終端で返り、store も open のまま。"""
    facade = _facade(
        OrderResult(
            status="PENDING_CANCEL", filled_qty=0.0, avg_price=None, client_order_id="c1"
        )
    )
    ev = await facade.cancel(venue="KABU", order_id="c1")

    assert ev.status == "PENDING_CANCEL"
    assert not is_terminal(ev.status)
    # 既存の約定量 / 平均価格は維持（取消は約定済み数量を巻き戻さない）。
    assert ev.filled_qty == 30.0
    assert ev.avg_price == 1000.0
    # store は終端化せず open のまま（poll 確定が後追いするまで）。
    assert facade._states["c1"].status == "PENDING_CANCEL"


async def test_cancel_instant_confirm_canceled_is_terminal() -> None:
    """instant-confirm venue（mock）は受付＝確定で CANCELED を終端化する。"""
    facade = _facade(
        OrderResult(
            status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="c1"
        )
    )
    ev = await facade.cancel(venue="MOCK", order_id="c1")

    assert ev.status == "CANCELED"
    assert is_terminal(ev.status)
    assert ev.filled_qty == 30.0
    assert facade._states["c1"].status == "CANCELED"


async def test_cancel_reject_raises_and_keeps_state() -> None:
    """取消拒否は CANCEL_REJECTED を raise し store を変更しない。"""
    facade = _facade(
        OrderResult(
            status="REJECTED", filled_qty=0.0, avg_price=None, client_order_id="c1"
        )
    )
    try:
        await facade.cancel(venue="KABU", order_id="c1")
    except OrderFacadeError as exc:
        assert exc.error_code == "CANCEL_REJECTED"
    else:
        raise AssertionError("expected CANCEL_REJECTED")
    assert facade._states["c1"].status == "ACCEPTED"


# ── apply_venue_event: poll/EC 確定を _states に反映（#23・(c-1) confirmation path） ──

def _seed_facade(status: str = "PENDING_CANCEL", filled: float = 0.0) -> ManualOrderFacade:
    facade = ManualOrderFacade(
        _FakeAdapter(OrderResult(status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="c1"))
    )  # type: ignore[arg-type]
    facade._states["c1"] = OrderState(
        order_id="c1", venue_order_id="V1", client_order_id="c1",
        status=status, filled_qty=filled, avg_price=1000.0, ts_ms=0,
    )
    return facade


def _ev(status: str, filled_qty: float, avg_price: float | None = 1000.0):
    return SimpleNamespace(
        client_order_id="c1", status=status, filled_qty=filled_qty, avg_price=avg_price
    )


def test_apply_venue_event_confirms_pending_cancel_to_terminal() -> None:
    """poll の終端 CANCELED が PENDING_CANCEL を確定し、store から working order が消える。"""
    facade = _seed_facade(status="PENDING_CANCEL", filled=0.0)
    assert facade.apply_venue_event(_ev("CANCELED", 0.0)) is True
    assert facade._states["c1"].status == "CANCELED"
    # list_orders（reconcile primitive）は終端を working から除外する → 取り残しなし。
    assert facade.list_orders() == []


def test_apply_venue_event_books_race_fill_during_pending_cancel() -> None:
    """受付〜確定の隙間の競合約定（累積 30）が store に反映される。"""
    facade = _seed_facade(status="PENDING_CANCEL", filled=0.0)
    assert facade.apply_venue_event(_ev("PARTIALLY_FILLED", 30.0)) is True
    st = facade._states["c1"]
    assert st.status == "PARTIALLY_FILLED"
    assert st.filled_qty == 30.0


def test_apply_venue_event_ignores_stale_lower_cumulative() -> None:
    """累積数量が後退する stale poll は filled_qty を巻き戻さない。"""
    facade = _seed_facade(status="PARTIALLY_FILLED", filled=50.0)
    facade.apply_venue_event(_ev("PARTIALLY_FILLED", 30.0))
    assert facade._states["c1"].filled_qty == 50.0


def test_apply_venue_event_ignores_terminal_and_unknown() -> None:
    """終端済み注文・未追跡 cid は触らず False。"""
    facade = _seed_facade(status="CANCELED", filled=0.0)
    assert facade.apply_venue_event(_ev("PARTIALLY_FILLED", 30.0)) is False
    assert facade._states["c1"].status == "CANCELED"
    assert facade.apply_venue_event(SimpleNamespace(
        client_order_id="unknown", status="FILLED", filled_qty=1.0, avg_price=1.0
    )) is False
