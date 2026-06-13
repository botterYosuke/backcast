"""MarketDataBus — asyncio Queue ベースの fan-out（Phase 8 §3.3）。

責務:
- publish(event): 現在 subscribe 中の全 Queue に event を配る
- subscribe(): `_Subscription`（async-iterable）を返す（以降の publish を受信、過去は replay しない）
- close(): 全 subscriber に終端 sentinel を流し、以降の publish を RuntimeError で拒否

topic / filtering は持たない（live_runner 側の責務）。
"""
from __future__ import annotations

import asyncio
from typing import AsyncIterator

from engine.live.adapter import LiveEvent


class _Subscription:
    """単一 subscriber。`async for` で消費でき、`close()` で購読解除する。

    キュー登録は `subscribe()` 時に **同期的**に行う（D8: consumer task 起動前に subscribe を確立して
    取りこぼしを防ぐ）。consumer を一度も iterate せず破棄する経路（attach rollback 等）では生成器の
    `finally` が走らず queue が `_subscribers` に残り続け、publish が無限に積む leak になる。`close()` は
    iterate の有無に関わらず queue を確実に外す（#25 review finding 7）。
    """

    def __init__(self, bus: "MarketDataBus", q: "asyncio.Queue[LiveEvent | None]") -> None:
        self._bus = bus
        self._q = q

    def __aiter__(self) -> AsyncIterator[LiveEvent]:
        return self._consume()

    async def _consume(self) -> AsyncIterator[LiveEvent]:
        try:
            while True:
                item = await self._q.get()
                if item is None:
                    return
                yield item
        finally:
            self._bus._drop(self._q)

    def close(self) -> None:
        """購読解除（冪等）。consumer 未開始でも queue を `_subscribers` から外す。"""
        self._bus._drop(self._q)


class MarketDataBus:
    def __init__(self) -> None:
        self._subscribers: list[asyncio.Queue[LiveEvent | None]] = []
        self._closed = False

    def subscribe(self) -> _Subscription:
        q: asyncio.Queue[LiveEvent | None] = asyncio.Queue()
        if self._closed:
            # close 済みなら即終端する subscription を返す（登録しない）。
            q.put_nowait(None)
        else:
            self._subscribers.append(q)
        return _Subscription(self, q)

    def _drop(self, q: "asyncio.Queue[LiveEvent | None]") -> None:
        if q in self._subscribers:
            self._subscribers.remove(q)

    async def publish(self, event: LiveEvent) -> None:
        if self._closed:
            raise RuntimeError("MarketDataBus is closed")
        for q in self._subscribers:
            await q.put(event)

    async def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        for q in self._subscribers:
            await q.put(None)
        self._subscribers.clear()
