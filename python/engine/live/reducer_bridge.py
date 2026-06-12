"""LiveReducerBridge — live KlineUpdate → replay reducer event 変換 + apply (Phase 8 Step 2e).

責務:
- 純関数で live `KlineUpdate` (pydantic, ts_ns) を replay `KlineUpdate` (dataclass,
  timestamp_ms) と `ReplayTimeUpdated` に変換する。
- `MarketDataBus` を購読し、KlineUpdate が来たら `data_engine.apply_replay_event`
  に `ReplayTimeUpdated -> KlineUpdate` の順で流す（§4.3 順序不変条件）。
- DepthUpdate / TradesUpdate は reducer の関心外なので無視する。

設計判断:
- instrument_id / volume を reducer KlineUpdate に引き渡し、per-instrument 経路
  （per_id_close / per_id_ohlc_points）に乗せる。global の OHLC 集約には積まれない。
- bridge は data_engine の Protocol を持たず duck typing。テスト用 stub を許容。
- 起動順序: bus.subscribe() を bridge.start() の中で同期的に行ってから task spawn。
  外部 publish より先に subscribe が必ず完了している（§7 ADR）。
"""
from __future__ import annotations

from typing import Callable, Optional, Protocol

from engine.live.adapter import KlineUpdate as LiveKlineUpdate
from engine.live.event_bus import MarketDataBus
from engine.live.supervised_task import BusConsumerTask
from engine.reducer import (
    KlineUpdate as ReducerKlineUpdate,
    ReplayEvent,
    ReplayTimeUpdated,
)


def _ns_to_ms(ts_ns: int) -> int:
    return ts_ns // 1_000_000


def live_kline_to_reducer_kline(live: LiveKlineUpdate) -> ReducerKlineUpdate:
    ts_ms = _ns_to_ms(live.ts_ns)
    return ReducerKlineUpdate(
        timestamp_ms=ts_ms,
        open_time_ms=ts_ms,
        open=live.open,
        high=live.high,
        low=live.low,
        close=live.close,
        instrument_id=live.instrument_id,
        volume=live.volume,
    )


def live_kline_to_replay_time_updated(live: LiveKlineUpdate) -> ReplayTimeUpdated:
    return ReplayTimeUpdated(timestamp_ms=_ns_to_ms(live.ts_ns))


class _DataEngineLike(Protocol):
    def apply_replay_event(self, event: ReplayEvent) -> None: ...


class LiveReducerBridge(BusConsumerTask):
    """bus → reducer/DataEngine の薄い橋。

    - `start()` で bus.subscribe() を取得し、消費 task を spawn する。
    - `stop()` で task を cancel→await。bus 側が先に close された場合も
      iterator が綺麗に終端するため、追加処理は不要。
    """

    def __init__(
        self,
        bus: MarketDataBus,
        data_engine: _DataEngineLike,
        mode_provider: Optional[Callable[[], str]] = None,
    ) -> None:
        self._bus = bus
        self._data_engine = data_engine
        self._mode_provider = mode_provider

    async def _handle(self, evt: object) -> None:
        # Replay モード中は live 由来イベントを reducer に流さない（混線防止）
        if self._mode_provider is not None and self._mode_provider() == "Replay":
            return
        if isinstance(evt, LiveKlineUpdate):
            self._data_engine.apply_replay_event(
                live_kline_to_replay_time_updated(evt)
            )
            self._data_engine.apply_replay_event(
                live_kline_to_reducer_kline(evt)
            )
        # DepthUpdate / TradesUpdate は無視
