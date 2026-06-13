"""Step 2 (#25) — KlineUpdate closed/partial 識別子の plumbing を pin する。

driver の「partial を on_bar に渡さない」は Step 3/4（driver 実装後）で検証する。本書は供給側:
- aggregator の bucket-rollover 確定バーは is_closed=True
- build_now() の進行中スナップショットは is_closed=False
- KlineUpdate の既定（venue 直送 / 既存呼び出し）は is_closed=True（後方互換）
"""
from __future__ import annotations

from engine.live.adapter import KlineUpdate, TradesUpdate
from engine.live.aggregator import TickBarAggregator

IID = "8918.TSE"
SEC = 1_000_000_000


def _tick(ts_ns: int, price: float, size: float = 1.0) -> TradesUpdate:
    return TradesUpdate(
        kind="trades", instrument_id=IID, ts_ns=ts_ns, price=price, size=size, aggressor_side="buy"
    )


def test_rollover_bar_is_closed_true():
    agg = TickBarAggregator(instrument_id=IID, interval_ns=SEC)
    assert agg.on_tick(_tick(0, 10.0)) is None  # opens bucket 0
    closed = agg.on_tick(_tick(SEC, 11.0))  # bucket 1 → bucket 0 closes
    assert closed is not None
    assert closed.is_closed is True
    assert closed.close == 10.0


def test_build_now_snapshot_is_closed_false():
    agg = TickBarAggregator(instrument_id=IID, interval_ns=SEC)
    agg.on_tick(_tick(0, 10.0))
    partial = agg.build_now()
    assert partial is not None
    assert partial.is_closed is False


def test_kline_default_is_closed_true():
    # venue 直送 / 既存呼び出しは is_closed 既定 True（確定バー扱い）。
    k = KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=0, open=1, high=1, low=1, close=1, volume=1
    )
    assert k.is_closed is True


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
