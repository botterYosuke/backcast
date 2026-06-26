"""engine.synth.builders — 決定論 PricePath ビルダー（#151 / findings 0120）.

各ビルダーは ``PricePath`` を返す関数。``synth_bars`` のコアは ``PricePath`` Protocol しか知らないので、
ここに足すだけで新パターンが使える（コア無改変＝拡張性の floor）。

  * ``explicit`` … 終値配列をそのまま（midstream の Daily 終値列など）。open/spread/volume 制御可。
  * ``constant`` … 定数価格（flat）。
  * ``trend``    … 線形ドリフト。``gap_pct`` で寄り（open=prev_close*(1+gap)）に gap を、``volume`` で出来高を制御。
  * ``from_fn``  … 任意関数（escape hatch）。fn(i, ts, prev_close) -> BarPoint | float。
"""
from __future__ import annotations

from typing import Callable, Optional, Sequence, Union

from engine.synth.core import BarPoint, PathReturn, PricePath


def explicit(
    closes: Sequence[float],
    *,
    open: str = "close",
    spread: float = 0.0,
    volume: Optional[float] = None,
) -> PricePath:
    """終値配列をそのまま流す PricePath。

    ``open="close"``（既定）→ 各足の open=close（flat な寄り・既存テストの慣習）。
    ``open="prev"``       → open=前足終値（gap 既定＝コアの BarPoint 既定に委譲）。
    ``spread``            → high=close+spread / low=close-spread。
    ``volume``            → 出来高（None なら synth_bars 既定）。
    """
    arr = [float(c) for c in closes]

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        if i >= len(arr):
            raise IndexError(f"explicit() has {len(arr)} closes but bar index {i} requested")
        c = arr[i]
        o = c if open == "close" else None  # None → コアが prev_close を既定に使う
        hi = c + spread if spread else None
        lo = c - spread if spread else None
        return BarPoint(close=c, open=o, high=hi, low=lo, volume=volume)

    return _path


def constant(price: float, *, volume: Optional[float] = None) -> PricePath:
    """定数価格（flat）。open=close=high=low=price。"""
    p = float(price)

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        return BarPoint(close=p, open=p, high=p, low=p, volume=volume)

    return _path


def trend(
    start: float,
    step: float,
    *,
    gap_pct: float = 0.0,
    volume: Optional[float] = None,
    spread: float = 0.0,
) -> PricePath:
    """線形ドリフト close = start + i*step。

    ``gap_pct`` … 各足の寄り gap。open = prev_close*(1+gap_pct)（最初の足は close）。gap_pct=0 → 寄りは
                  前足終値（gap 0）。``open / prev_close - 1 == gap_pct`` を満たす。
    ``volume``  … 出来高（定数）。
    ``spread``  … high/low 幅。
    """
    s = float(start)
    st = float(step)
    g = float(gap_pct)

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        c = s + i * st
        if prev_close is None:
            o: Optional[float] = c
        else:
            o = prev_close * (1.0 + g)
        hi = max(o, c) + spread if spread else None
        lo = min(o, c) - spread if spread else None
        return BarPoint(close=c, open=o, high=hi, low=lo, volume=volume)

    return _path


def from_fn(fn: Callable[[int, int, Optional[float]], Union[BarPoint, float, int]]) -> PricePath:
    """任意関数を PricePath にする escape hatch。fn(bar_index, ts_event_ns, prev_close) -> BarPoint | float。"""

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        return fn(i, ts, prev_close)

    return _path
