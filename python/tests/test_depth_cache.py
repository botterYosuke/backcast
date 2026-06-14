"""INV-S9b-DEPTHCACHE — 実 MarketDataBus 駆動で DepthCache の注入シーム耐性を固定する (#27)。

DepthCache は BusConsumerTask (failure_policy="terminate") の subclass。_handle が外へ
raise すると consumer task が死に、以降の板が一切更新されなくなる。kabu/立花の実 depth が
DepthUpdate に正規化されて bus へ流れる本番経路で、不正な段 (price<=0 / NaN — adapter.DepthLevel
は制約なしで構築できるが models.DepthLevel は gt=0/ge=0 で reject) が来ても feed を止めない
ことを証明する (AC: kabu 欠損段/空板・立花 空size/欠損段でも feed が停止しない)。

remove() は subscribe 解除/銘柄除外で板が消えること (AC: DepthCache.remove と整合) を担保する。
"""
from __future__ import annotations

import asyncio

from engine.live.adapter import DepthLevel, DepthUpdate
from engine.live.depth_cache import DepthCache
from engine.live.event_bus import MarketDataBus

IID = "8918.TSE"
TS_NS = 1_700_000_000_000_000_000  # // 1e6 = 1_700_000_000_000 ms


def _update(instrument_id: str, bids, asks, ts_ns: int = TS_NS) -> DepthUpdate:
    return DepthUpdate(
        kind="depth",
        instrument_id=instrument_id,
        ts_ns=ts_ns,
        bids=tuple(DepthLevel(price=p, size=s) for p, s in bids),
        asks=tuple(DepthLevel(price=p, size=s) for p, s in asks),
    )


async def _drive(cache: DepthCache, bus: MarketDataBus, *updates: DepthUpdate) -> None:
    """publish して consumer task が drain し終えるまで待つ。"""
    for u in updates:
        await bus.publish(u)
    # consumer は queue.get → _handle を await で回す。複数回 yield して drain させる。
    for _ in range(10):
        await asyncio.sleep(0)


async def test_normal_depth_is_cached_in_wire_order() -> None:
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        await _drive(
            cache, bus,
            _update(IID, bids=[(2480.0, 100.0), (2479.0, 50.0)], asks=[(2481.0, 200.0)]),
        )
        snap = cache.snapshot()
        assert IID in snap
        d = snap[IID]
        # wire 順を忠実保持 (defensive sort しない — #26 §2.3)。
        assert [(lvl.price, lvl.size) for lvl in d.bids] == [(2480.0, 100.0), (2479.0, 50.0)]
        assert [(lvl.price, lvl.size) for lvl in d.asks] == [(2481.0, 200.0)]
        assert d.timestamp_ms == TS_NS // 1_000_000
    finally:
        await cache.stop()


async def test_bad_level_skips_update_without_killing_feed() -> None:
    """price<=0 の段 (adapter.DepthLevel は許容・models.DepthLevel は gt=0 で reject) を含む
    update は当該 update のみ skip し、consumer loop は死なず、後続の正常 update が着弾する。"""
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        # 1) 正常 → cache される。
        await _drive(cache, bus, _update(IID, bids=[(10.0, 5.0)], asks=[(11.0, 5.0)]))
        assert cache.snapshot()[IID].bids[0].price == 10.0

        # 2) 不正段 (price=0.0) → models.DepthLevel が reject → 当該 update skip。
        #    既存の正常スナップショットは上書きされず保持される。
        await _drive(cache, bus, _update(IID, bids=[(0.0, 5.0)], asks=[(11.0, 5.0)]))
        assert cache.snapshot()[IID].bids[0].price == 10.0  # 据え置き
        assert cache.last_error is None  # consumer loop は生存

        # 3) 後続の正常 update は引き続き反映される (feed は停止していない)。
        await _drive(cache, bus, _update(IID, bids=[(12.0, 5.0)], asks=[(13.0, 5.0)]))
        assert cache.snapshot()[IID].bids[0].price == 12.0
        assert cache.last_error is None
    finally:
        await cache.stop()


async def test_bad_first_update_does_not_block_subsequent_good_one() -> None:
    """初回が不正でも (前値が無くても) feed は止まらず、次の正常 update が新規 cache される。"""
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        await _drive(cache, bus, _update(IID, bids=[(-1.0, 5.0)], asks=[]))  # price<0 → skip
        assert IID not in cache.snapshot()
        assert cache.last_error is None
        await _drive(cache, bus, _update(IID, bids=[(9.0, 5.0)], asks=[]))
        assert cache.snapshot()[IID].bids[0].price == 9.0
    finally:
        await cache.stop()


async def test_empty_board_is_cached_not_dropped() -> None:
    """両側空の板も cache する (「板が無い」≠「両側空の板」— #26 §2.3 / contract)。"""
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        await _drive(cache, bus, _update(IID, bids=[], asks=[]))
        snap = cache.snapshot()
        assert IID in snap
        assert snap[IID].bids == []
        assert snap[IID].asks == []
        assert snap[IID].timestamp_ms == TS_NS // 1_000_000
    finally:
        await cache.stop()


async def test_remove_makes_board_disappear() -> None:
    """subscribe 解除/銘柄除外で板が消える (orchestrator が unsubscribe で呼ぶ)。"""
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        await _drive(cache, bus, _update(IID, bids=[(10.0, 5.0)], asks=[(11.0, 5.0)]))
        assert IID in cache.snapshot()
        cache.remove(IID)
        assert IID not in cache.snapshot()
    finally:
        await cache.stop()


async def test_remove_absent_instrument_is_noop() -> None:
    bus = MarketDataBus()
    cache = DepthCache(bus)
    await cache.start()
    try:
        cache.remove("9999.TSE")  # 未登録 → 例外なし
        assert cache.snapshot() == {}
    finally:
        await cache.stop()


def test_orchestrator_unsubscribe_removes_depth_board() -> None:
    """LiveLoopManager.unsubscribe_market_data が depth_cache.remove を呼ぶ配線 (D20)。

    銘柄除外で板が消える経路 (live_orchestrator.py の depth_cache.remove(instrument_id)) を
    重い __init__ を経ずに duck-typed self で叩いて回帰固定する。remove() 自体の挙動は
    test_remove_makes_board_disappear が、ここは「orchestrator が remove を呼ぶ」を担保。"""
    import threading
    from types import SimpleNamespace

    from engine.live.live_orchestrator import LiveLoopManager

    class _SpyCache:
        def __init__(self) -> None:
            self.removed: list[str] = []

        def remove(self, instrument_id: str) -> None:
            self.removed.append(instrument_id)

    class _Runner:
        async def unsubscribe(self, instrument_id: str) -> None:
            return None

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()
    try:
        price_cache, depth_cache = _SpyCache(), _SpyCache()
        forgotten: list[str] = []
        fake = SimpleNamespace(
            _session=SimpleNamespace(
                runner=_Runner(), price_cache=price_cache, depth_cache=depth_cache
            ),
            _ensure_live_loop=lambda: loop,
            _live_timeout_s=5.0,
            _engine=SimpleNamespace(forget_instrument=forgotten.append),
        )
        ack = LiveLoopManager.unsubscribe_market_data(fake, IID)
        assert ack.success is True
        assert depth_cache.removed == [IID]  # 板が消える配線
        assert price_cache.removed == [IID]  # price も同経路 (D20)
        assert forgotten == [IID]
    finally:
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)
