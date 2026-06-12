from __future__ import annotations

from engine.live.adapter import DepthUpdate
from engine.live.event_bus import MarketDataBus
from engine.live.supervised_task import BusConsumerTask
from engine.models import DepthSnapshot, DepthLevel


class DepthCache(BusConsumerTask):
    """最新の板 (DepthUpdate) を instrument ごとに DepthSnapshot として保持する
    bus-fed キャッシュ。LastPriceCache と同じ start/stop/_run ライフサイクル。
    Live のみで動き、GetState が snapshot() を per_instrument[id].depth に注入する。"""

    def __init__(self, bus: MarketDataBus) -> None:
        self._bus = bus
        self._depth: dict[str, DepthSnapshot] = {}

    async def _handle(self, evt: object) -> None:
        if not isinstance(evt, DepthUpdate):
            return
        try:
            snapshot = DepthSnapshot(
                bids=[DepthLevel(price=l.price, size=l.size) for l in evt.bids],
                asks=[DepthLevel(price=l.price, size=l.size) for l in evt.asks],
                timestamp_ms=evt.ts_ns // 1_000_000,
            )
        except Exception:
            # 不正な level (price<=0 / NaN 等で strict DepthLevel が reject)
            # は該当 update だけ skip。1 銘柄の不正 tick で全 depth feed が
            # 永久停止するのを防ぐ（consume loop は止めない）。
            return
        self._depth[evt.instrument_id] = snapshot

    def snapshot(self) -> dict[str, DepthSnapshot]:
        return dict(self._depth)

    def remove(self, instrument_id: str) -> None:
        self._depth.pop(instrument_id, None)
