"""(c-2) legacy NautilusVenueExecClient._cancel_order — 取消受付 PENDING_CANCEL を honor。

findings 0014・#23 cancel-ACK (c-2)。legacy auto 経路（Rust core ロード）は非 REJECTED で
無条件に generate_order_canceled していた。kabu adapter が取消受付を PENDING_CANCEL で返す
ようになった（(a)）ので、受付を**終端 CANCELED と誤認しない**。終端 CANCELED は polling/EC が
後追いする。本経路は現 live 経路から orphan（orchestrator は KernelLiveEngineController のみ
生成）であり、本修正は削除までの間 residual code を既知の誤契約にしないための保守修正。
production HITL gate には含めない focused unit test。

`NautilusVenueExecClient` は `LiveExecutionClient`（Cython）基底の重い ctor を持ち通常生成
できないため、plain-Python async メソッド `_cancel_order` を **unbound で duck-typed self に
適用**してロジックだけを隔離検証する（generate_order_* は Mock で観測）。
"""
from __future__ import annotations

from types import SimpleNamespace
from unittest.mock import MagicMock

from engine.live.nautilus_exec_client import NautilusVenueExecClient
from engine.live.order_types import OrderResult


def _client(cancel_result: OrderResult) -> SimpleNamespace:
    async def _cancel(*, venue: str, order_id: str) -> OrderResult:
        return cancel_result

    return SimpleNamespace(
        _venue_str="KABU",
        _clock=SimpleNamespace(timestamp_ns=lambda: 123),
        _adapter=SimpleNamespace(cancel_order=_cancel),
        generate_order_canceled=MagicMock(),
        generate_order_rejected=MagicMock(),
    )


def _command() -> SimpleNamespace:
    return SimpleNamespace(
        client_order_id=SimpleNamespace(value="c1"),
        strategy_id="S1",
        instrument_id="7203.KABU",
        venue_order_id="V1",  # truthy → _synth を呼ばない
    )


async def test_pending_cancel_does_not_generate_canceled() -> None:
    """取消受付 PENDING_CANCEL では generate_order_canceled を呼ばない（非終端で待つ）。"""
    client = _client(
        OrderResult(status="PENDING_CANCEL", filled_qty=0.0, avg_price=None, client_order_id="c1")
    )
    await NautilusVenueExecClient._cancel_order(client, _command())

    client.generate_order_canceled.assert_not_called()
    client.generate_order_rejected.assert_not_called()


async def test_canceled_generates_canceled() -> None:
    """instant-confirm な CANCELED は従来どおり generate_order_canceled を 1 回呼ぶ。"""
    client = _client(
        OrderResult(status="CANCELED", filled_qty=0.0, avg_price=None, client_order_id="c1")
    )
    await NautilusVenueExecClient._cancel_order(client, _command())

    client.generate_order_canceled.assert_called_once()


async def test_rejected_generates_nothing() -> None:
    """取消拒否 REJECTED は元注文を live に保ち何も emit しない。"""
    client = _client(
        OrderResult(status="REJECTED", filled_qty=0.0, avg_price=None, client_order_id="c1")
    )
    await NautilusVenueExecClient._cancel_order(client, _command())

    client.generate_order_canceled.assert_not_called()
    client.generate_order_rejected.assert_not_called()
