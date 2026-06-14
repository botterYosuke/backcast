"""(a) kabu cancel_order — 取消受付は PENDING_CANCEL（終端 CANCELED は poll が後追い）。

findings 0014・#23 cancel-ACK (a)。ack-then-poll venue では PUT /cancelorder 成立は
取消「受付」にすぎず、終端 CANCELED は GET /orders polling が後追いで運ぶ。受付を終端
CANCELED として返すと、受付〜確定の隙間で起きた競合約定を consumer が取りこぼす
（CONTEXT.md「取消受付 / 取消確定」）。取消拒否は従来どおり REJECTED。
"""
from __future__ import annotations

from engine.exchanges import kabusapi_orders as _orders
from engine.exchanges.kabusapi_execution import KabuExecutionEngine, _KabuOrderRef


def _engine() -> KabuExecutionEngine:
    eng = KabuExecutionEngine(
        client=object(),  # cancel をスタブするため未使用
        rl=object(),
        env="verify",
        time_source=lambda: 0.0,
    )
    eng._token = "tok"
    return eng


def _register(eng: KabuExecutionEngine, *, cid: str = "c1", venue_id: str = "V1") -> None:
    eng._register_order(
        _KabuOrderRef(
            client_order_id=cid,
            order_id=venue_id,
            symbol="7203",
            exchange=1,
            side="BUY",
            qty=100.0,
            price=1000.0,
            order_type="LIMIT",
            time_in_force="DAY",
            account_type=0,
        )
    )


async def test_cancel_ack_returns_pending_cancel() -> None:
    """取消受付成立は PENDING_CANCEL を返す（終端 CANCELED ではない）。"""
    eng = _engine()
    _register(eng)
    captured: list[str] = []

    async def fake_cancel(order_id: str) -> _orders.SendOrderAck:
        captured.append(order_id)
        return _orders.SendOrderAck(rejected=False, order_id="V1")

    eng._cancel_venue_order = fake_cancel  # type: ignore[method-assign]
    res = await eng.cancel_order(venue="KABU", order_id="c1")

    assert res.status == "PENDING_CANCEL"
    assert res.filled_qty == 0.0
    assert res.client_order_id == "c1"
    # venue OrderId（ref.order_id）で取消を叩く。
    assert captured == ["V1"]


async def test_cancel_reject_still_rejected() -> None:
    """取消拒否は従来どおり REJECTED（元注文は live のまま）。"""
    eng = _engine()
    _register(eng)

    async def fake_cancel(order_id: str) -> _orders.SendOrderAck:
        return _orders.SendOrderAck(rejected=True, reject_code="4", reject_text="too late")

    eng._cancel_venue_order = fake_cancel  # type: ignore[method-assign]
    res = await eng.cancel_order(venue="KABU", order_id="c1")

    assert res.status == "REJECTED"


async def test_cancel_unknown_order_rejected() -> None:
    """未追跡 order の取消は REJECTED（UNKNOWN_VENUE_ORDER）。受付化しない。"""
    eng = _engine()
    res = await eng.cancel_order(venue="KABU", order_id="missing")
    assert res.status == "REJECTED"
    assert res.reject_reason == "UNKNOWN_VENUE_ORDER"
