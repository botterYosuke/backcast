"""#34 D4 (findings 0101) — ManualOrderFacade.modify の took-effect ゲート。

訂正後の new_qty/new_price を `_intents` に反映するのは訂正が **実際に効いた** ときだけ。
manual 経路の adapter は訂正を同期確定する（mock=ACCEPTED / kabu=cancel+new の終端 /
tachibana=atomic ACCEPTED）。kabu は「取消 → 新規発注」変換で実現し、取消成功＋新規失敗で
`CANCELED`（reject_reason="MODIFY_NEW_FAILED:…"）を返す＝元注文は取消済みで代替注文が無い。
このとき new_qty を書くと終端注文に幻の新数量が載る → 書かないことを特性化する。

設計の正本: docs/findings/0101-issue34-modify-order-ui.md（D3/D4）/ CONTEXT.md「訂正受付」。
"""
from __future__ import annotations

import pytest

from engine.live.order_facade import ManualOrderFacade, OrderFacadeError
from engine.live.order_types import OrderIntent, OrderResult, OrderState, is_terminal


class _FakeAdapter:
    """modify_order の返り OrderResult を固定する throwaway adapter。"""

    def __init__(self, result: OrderResult) -> None:
        self._result = result
        self.calls: list[dict] = []

    async def modify_order(
        self, *, venue: str, order_id: str,
        new_price: float | None = None, new_qty: float | None = None,
    ) -> OrderResult:
        self.calls.append({"new_price": new_price, "new_qty": new_qty})
        return self._result


def _facade(result: OrderResult, *, prior_qty: float = 100.0, prior_price: float | None = 2500.0,
            filled: float = 0.0) -> ManualOrderFacade:
    facade = ManualOrderFacade(_FakeAdapter(result))  # type: ignore[arg-type]
    facade._states["m1"] = OrderState(
        order_id="m1", venue_order_id="V1", client_order_id="m1",
        status="ACCEPTED", filled_qty=filled, avg_price=prior_price, ts_ms=0,
    )
    facade._intents["m1"] = OrderIntent(
        symbol="7203.TSE", side="BUY", qty=prior_qty, price=prior_price,
    )
    return facade


@pytest.mark.scenario("MODIFY-21")
async def test_modify_accepted_reflects_new_qty_in_intent() -> None:
    """ACCEPTED（訂正確定）は new_qty/new_price を _intents に反映する。"""
    facade = _facade(
        OrderResult(status="ACCEPTED", filled_qty=0.0, avg_price=None, client_order_id="m1")
    )
    ev = await facade.modify(venue="MOCK", order_id="m1", new_qty=60.0, new_price=2400.0)

    assert ev.status == "ACCEPTED"
    intent = facade.get_intent("m1")
    assert intent is not None
    assert intent.qty == 60.0
    assert intent.price == 2400.0


@pytest.mark.scenario("MODIFY-20")
async def test_modify_canceled_does_not_write_phantom_new_qty() -> None:
    """kabu 取消成立＋新規失敗の CANCELED は終端化し、_intents に幻の new_qty を書かない。"""
    facade = _facade(
        OrderResult(
            status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="m1",
            reject_reason="MODIFY_NEW_FAILED:原注文は取消済みです。再発注してください",
        )
    )
    ev = await facade.modify(venue="KABU", order_id="m1", new_qty=60.0, new_price=2400.0)

    # 注文は終端（取消成立）。
    assert ev.status == "CANCELED"
    assert is_terminal(ev.status)
    # intent は **据え置き**（原数量・原価格のまま）＝幻の新数量を載せない。
    intent = facade.get_intent("m1")
    assert intent is not None
    assert intent.qty == 100.0
    assert intent.price == 2500.0
    # 終端なので working list からは消える。
    assert facade.list_orders() == []


async def test_modify_rejected_raises_and_keeps_intent() -> None:
    """訂正拒否は MODIFY_REJECTED を raise し、state も intent も変更しない。"""
    facade = _facade(
        OrderResult(status="REJECTED", filled_qty=0.0, avg_price=None, client_order_id="m1")
    )
    with pytest.raises(OrderFacadeError) as ei:
        await facade.modify(venue="KABU", order_id="m1", new_qty=60.0)
    assert ei.value.error_code == "MODIFY_REJECTED"
    assert facade._states["m1"].status == "ACCEPTED"
    intent = facade.get_intent("m1")
    assert intent is not None and intent.qty == 100.0


async def test_modify_price_only_keeps_prior_qty() -> None:
    """価格のみ訂正（new_qty=None）は qty 据え置き・price だけ反映。"""
    facade = _facade(
        OrderResult(status="ACCEPTED", filled_qty=0.0, avg_price=None, client_order_id="m1")
    )
    await facade.modify(venue="TACHIBANA", order_id="m1", new_price=2450.0)
    intent = facade.get_intent("m1")
    assert intent is not None
    assert intent.qty == 100.0       # 据え置き
    assert intent.price == 2450.0    # 反映
