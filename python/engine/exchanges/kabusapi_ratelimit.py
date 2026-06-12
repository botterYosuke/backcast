"""KabuStation R5 rate-limit helper — _TokenBucket と KabuRateLimits。

kabusapi.py / kabusapi_execution.py から _TokenBucket と rate 定数を抽出し、
KabuRateLimits.gate(endpoint) で呼び出し側が endpoint 名ベースに acquire できるようにする。
"""

from __future__ import annotations

import asyncio
from typing import Awaitable, Callable, Optional

# R5 rate-limit token bucket sizes (req/sec).
_INFO_RATE_PER_SEC = 10
_ORDER_RATE_PER_SEC = 5
_WALLET_RATE_PER_SEC = 10
# PUT /register は info 系 (10/s) だが、subscribe() は 1 銘柄ごとに register set 全体を
# 再送するため 50 銘柄 universe を立て続けに subscribe すると burst で kabu の
# `4001006 API実行回数エラー` を踏む。register は startup の一回限りで board GET と重ならない
# ので、専用 bucket を 5/s・no-burst (capacity=1) にして 10/s 上限に大きな安全マージンを取る。
_REGISTER_RATE_PER_SEC = 5

_ENDPOINT_CATEGORY: dict[str, str] = {
    "sendorder":      "order",
    "cancelorder":    "order",
    "orders":         "info",
    "positions":      "info",
    "apisoftlimit":   "info",
    "unregister/all": "info",
    "board":          "info",
    "symbol":         "info",
    "wallet/cash":    "wallet",
    "wallet/margin":  "wallet",
    "wallet/future":  "wallet",
    "wallet/option":  "wallet",
    "register":       "register",
    "unregister":     "register",
}


class _TokenBucket:
    """Minimal async-friendly token bucket for R5 rate-limit pre-suppression.

    rate: tokens added per second (== capacity).
    Uses a *time_source* + injectable *sleep* so tests can drive it
    deterministically without sleeping real time.
    """

    def __init__(
        self,
        rate: int,
        *,
        time_source: Callable[[], float],
        sleep: Callable[[float], Awaitable[None]],
        capacity: Optional[int] = None,
    ) -> None:
        self._rate = float(rate)
        # capacity = max burst. Default == rate (preserves order/wallet/info burst
        # behavior). capacity=1 => strict spacing (no burst), used for /register.
        self._capacity = float(capacity) if capacity is not None else float(rate)
        self._tokens = self._capacity
        self._last = time_source()
        self._time = time_source
        self._sleep = sleep
        self._lock = asyncio.Lock()

    async def acquire(self) -> None:
        async with self._lock:
            now = self._time()
            elapsed = now - self._last
            if elapsed > 0:
                self._tokens = min(
                    self._capacity, self._tokens + elapsed * self._rate
                )
                self._last = now
            if self._tokens < 1.0:
                await self._sleep((1.0 - self._tokens) / self._rate)
                self._tokens = 1.0
                self._last = self._time()
            self._tokens -= 1.0


class KabuRateLimits:
    """KabuStation 4 バケット (info / order / wallet / register) のファサード。

    gate(endpoint) でエンドポイント名から対応バケットの acquire を呼び出す。
    `sleep` は `lambda d: adapter._rate_limit_sleep(d)` を渡すことで
    テスト注入パターンを維持する。
    """

    def __init__(
        self,
        *,
        time_source: Callable[[], float],
        sleep: Callable[[float], Awaitable[None]],
    ) -> None:
        self._buckets: dict[str, _TokenBucket] = {
            "info":     _TokenBucket(_INFO_RATE_PER_SEC, time_source=time_source, sleep=sleep),
            "order":    _TokenBucket(_ORDER_RATE_PER_SEC, time_source=time_source, sleep=sleep),
            "wallet":   _TokenBucket(_WALLET_RATE_PER_SEC, time_source=time_source, sleep=sleep),
            "register": _TokenBucket(_REGISTER_RATE_PER_SEC, time_source=time_source, sleep=sleep, capacity=1),
        }

    async def gate(self, endpoint: str) -> None:
        """endpoint 名に対応するバケットで acquire する。"""
        category = _ENDPOINT_CATEGORY.get(endpoint)
        if category is None:
            raise ValueError(f"KabuRateLimits: unknown endpoint {endpoint!r}")
        await self._buckets[category].acquire()
