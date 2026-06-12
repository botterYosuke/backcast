"""BackendEventStream — threadsafe な fan-out バス（Phase 9 §3.12 / Step 0）。

責務:
- publish(event): 現在 subscribe 中の全 subscriber に event を配る
- subscribe(): blocking iterator (_Subscription) を返す（以降の publish を受信、過去は replay しない）
- close(): 全 subscriber に終端 sentinel を流し、以降の publish を RuntimeError で拒否

threading モデル:
- Rust inproc transport の受信 thread が publish し、Python 側の consumer thread が
  blocking iterator でイベントを取り出す。したがって asyncio ではなく
  queue.Queue + threading.Lock で実装する（market-data 用 MarketDataBus とは別物）。

payload は BackendEvent 固定。topic / filtering は持たない。
"""
from __future__ import annotations

import queue
import threading
from typing import Iterator

from engine.live.backend_events import BackendEvent

_SENTINEL = object()


class _Subscription:
    """1 subscriber 分の blocking iterator。queue から取り出し、sentinel で終端する。"""

    def __init__(self, bus: "BackendEventStream") -> None:
        self._bus = bus
        self._queue: queue.Queue[object] = queue.Queue()
        self._closed = False

    def _put(self, item: object) -> None:
        self._queue.put(item)

    def __iter__(self) -> Iterator[BackendEvent]:
        return self

    def __next__(self) -> BackendEvent:
        item = self._queue.get()
        if item is _SENTINEL:
            raise StopIteration
        return item  # type: ignore[return-value]

    def close(self) -> None:
        """subscriber を bus から外し、ブロック中の __next__ を解放する。"""
        if self._closed:
            return
        self._closed = True
        self._bus._remove(self)
        self._queue.put(_SENTINEL)


class BackendEventStream:
    def __init__(self) -> None:
        self._subscribers: list[_Subscription] = []
        self._closed = False
        self._lock = threading.Lock()

    def subscriber_count(self) -> int:
        """Number of active subscribers (read-only; for diagnostics / tests)."""
        with self._lock:
            return len(self._subscribers)

    def subscribe(self) -> _Subscription:
        sub = _Subscription(self)
        with self._lock:
            if self._closed:
                # close 済みなら即終端する subscription を返す
                sub._put(_SENTINEL)
            else:
                self._subscribers.append(sub)
        return sub

    def publish(self, event: BackendEvent) -> None:
        with self._lock:
            if self._closed:
                raise RuntimeError("BackendEventStream is closed")
            subs = list(self._subscribers)
        for sub in subs:
            sub._put(event)

    def _remove(self, sub: _Subscription) -> None:
        with self._lock:
            if sub in self._subscribers:
                self._subscribers.remove(sub)

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            subs = list(self._subscribers)
            self._subscribers.clear()
        for sub in subs:
            sub._put(_SENTINEL)
