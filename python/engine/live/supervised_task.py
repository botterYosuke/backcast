"""supervised_task.py — asyncio タスクライフサイクルの共通基底 (issue #184)。

SupervisedTask  : start/stop/last_error の最小 lifecycle。
BusConsumerTask : MarketDataBus 購読 + async for ループ向け (terminate policy)。
IntervalPollTask: sleep → _tick() ループ向け (record-and-continue policy)。
"""
from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from typing import AsyncIterator, ClassVar, Literal, Optional


class SupervisedTask(ABC):
    """start/stop/last_error の共通 lifecycle 管理。

    subclass は _run() を実装する。
    start() は冪等（task が生きていれば no-op）。
    die 済み task は start() で再起動を許可する。
    """

    _task: Optional[asyncio.Task] = None
    _last_error: Optional[BaseException] = None

    async def start(self) -> None:
        if self._task is not None and not self._task.done():
            return
        self._last_error = None
        self._task = asyncio.create_task(self._run())

    @abstractmethod
    async def _run(self) -> None: ...

    async def stop(self) -> None:
        if self._task is None:
            return
        self._task.cancel()
        try:
            await self._task
        except asyncio.CancelledError:
            pass
        self._task = None

    @property
    def last_error(self) -> Optional[BaseException]:
        return self._last_error


class BusConsumerTask(SupervisedTask):
    """MarketDataBus を購読して async for ループで消費する基底 (terminate policy)。

    使い方:
    - subclass は __init__ で self._bus: MarketDataBus を設定する。
    - subclass は _handle(evt) を実装する。
    - per-event の try/except は _handle() 内部で行い、継続したい場合は return する
      (外に raise すると BaseException が _last_error に入り task が死ぬ)。

    start() を override して self._bus.subscribe() を task spawn 前に呼ぶ
    (§7 起動順序 ADR: subscribe は同期完了させてから task spawn)。
    """

    failure_policy: ClassVar[Literal["terminate"]] = "terminate"
    _iter: Optional[AsyncIterator] = None

    async def start(self) -> None:
        if self._task is not None and not self._task.done():
            return
        self._last_error = None
        self._iter = self._bus.subscribe()  # type: ignore[attr-defined]
        self._task = asyncio.create_task(self._run())

    async def _run(self) -> None:
        assert self._iter is not None
        try:
            async for evt in self._iter:
                await self._handle(evt)
        except asyncio.CancelledError:
            return
        except BaseException as exc:
            self._last_error = exc
            return

    @abstractmethod
    async def _handle(self, evt: object) -> None: ...


class IntervalPollTask(SupervisedTask):
    """sleep → _tick() ループで定期ポーリングする基底 (record-and-continue policy)。

    使い方:
    - subclass は __init__ で self._interval_s = <float> を設定する。
    - subclass は _tick() を実装する。
    - _tick() が例外を propagate しても基底が _last_error に記録してループを継続する（CancelledError のみ外に出す）。

    初期ロードが必要な subclass（account_sync / instruments_scheduler）は
    _run() を override して最初の await の前に初期 fetch を行う。
    """

    failure_policy: ClassVar[Literal["record-and-continue"]] = "record-and-continue"
    _interval_s: float = 30.0  # subclass __init__ で self._interval_s = ... として上書き

    async def _run(self) -> None:
        while True:
            try:
                await asyncio.sleep(self._interval_s)
            except asyncio.CancelledError:
                return
            try:
                await self._tick()
            except asyncio.CancelledError:
                return
            except Exception as exc:
                self._last_error = exc

    async def _tick(self) -> None:
        raise NotImplementedError
