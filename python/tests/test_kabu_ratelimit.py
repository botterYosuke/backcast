"""INV-K2-RATE — kabu R5 token-bucket 流量抑制の契約 (findings/0009)。

skill R5: 発注 5 req/s・余力 10 req/s・情報 10 req/s。register は 5 req/s かつ
no-burst (capacity=1) で 4001006 (API実行回数エラー) を事前抑制する。

決定論テスト: time_source を t=0 に固定 (トークン補充ゼロ) し、injectable な
sleep が記録した待機量で burst 容量とスペーシングを検証する。
"""
from __future__ import annotations

import pytest

from engine.exchanges.kabusapi_ratelimit import (
    _ORDER_RATE_PER_SEC,
    _INFO_RATE_PER_SEC,
    _WALLET_RATE_PER_SEC,
    _REGISTER_RATE_PER_SEC,
    KabuRateLimits,
)


def test_rate_constants() -> None:
    assert _ORDER_RATE_PER_SEC == 5
    assert _INFO_RATE_PER_SEC == 10
    assert _WALLET_RATE_PER_SEC == 10
    assert _REGISTER_RATE_PER_SEC == 5


def _make(sleeps: list[float]) -> KabuRateLimits:
    async def fake_sleep(d: float) -> None:
        sleeps.append(d)

    return KabuRateLimits(time_source=lambda: 0.0, sleep=fake_sleep)


async def test_order_bucket_bursts_up_to_5_then_spaces() -> None:
    """order バケットは capacity=rate=5。5 連続は待ちなし、6 発目で待つ。"""
    sleeps: list[float] = []
    rl = _make(sleeps)
    for _ in range(5):
        await rl.gate("sendorder")
    assert sleeps == []  # burst 5 までは即時
    await rl.gate("sendorder")
    assert len(sleeps) == 1
    assert sleeps[0] == pytest.approx(1.0 / 5)  # 1 トークン回復ぶん待つ


async def test_register_bucket_is_no_burst_capacity_1() -> None:
    """register バケットは capacity=1 (no-burst): 2 発目から必ずスペーシング。"""
    sleeps: list[float] = []
    rl = _make(sleeps)
    await rl.gate("register")
    assert sleeps == []  # 初回は即時
    await rl.gate("register")
    assert len(sleeps) == 1
    assert sleeps[0] == pytest.approx(1.0 / _REGISTER_RATE_PER_SEC)


async def test_endpoint_category_routing() -> None:
    """endpoint → バケット対応 (sendorder/cancelorder=order, board/orders=info,
    wallet/*=wallet, register/unregister=register)。order だけ容量 5。"""
    sleeps: list[float] = []
    rl = _make(sleeps)
    # info(10) と wallet(10) は 10 連続まで burst → 待ちなし。
    for _ in range(10):
        await rl.gate("board")
    for _ in range(10):
        await rl.gate("wallet/cash")
    assert sleeps == []
    # cancelorder は order バケット (容量 5): 5 連続のあと 6 発目で待つ。
    for _ in range(5):
        await rl.gate("cancelorder")
    assert sleeps == []
    await rl.gate("cancelorder")
    assert len(sleeps) == 1


async def test_unknown_endpoint_raises() -> None:
    rl = _make([])
    with pytest.raises(ValueError):
        await rl.gate("not_a_real_endpoint")
