"""engine.synth.stochastic — 確率過程・周期 PricePath ビルダー（#154 / findings 0120）.

**生成器コア（core.py）を 1 行も変えずに** ``PricePath`` プロトコルだけで実装する拡張ビルダー群＝
拡張性の実証。numpy は使わず **stdlib ``random`` + ``math``** で決定論再現する（engine の numpy-free
規約を尊重・seed 固定で同系列）。

  * ``gbm``  … seed 固定の幾何ブラウン運動（決定論再現可能な乱数価格）。
  * ``sine`` … 正弦波（周期的価格）。
  * ``ramp`` … start→stop の線形ランプ（バー数で正規化）。
  * ``volume_path`` … 出来高を時間変化させる VolumePath（ADV/turnover ランキング用）。
"""
from __future__ import annotations

import math
import random
from typing import Optional

from engine.synth.core import BarPoint, PathReturn, PricePath, VolumePath


def gbm(
    s0: float,
    *,
    mu: float = 0.0,
    sigma: float = 0.2,
    dt: float = 1.0,
    seed: int = 0,
    volume: Optional[float] = None,
) -> PricePath:
    """seed 固定の幾何ブラウン運動。``S_{n+1} = S_n * exp((mu - sigma^2/2)*dt + sigma*sqrt(dt)*Z)``。

    同じ ``seed`` なら**完全に同じ系列**を再現する（``random.Random(seed)`` の gauss を bar 順に消費）。
    各足は flat（open=close）として返す（OHLC を散らしたいなら spread を別途乗せる呼び出し側で）。
    """
    rng = random.Random(seed)
    drift = (mu - 0.5 * sigma * sigma) * dt
    vol = sigma * math.sqrt(dt)
    # bar_index に対して決定論を保つため系列を遅延生成しつつキャッシュする。
    series: list[float] = [float(s0)]

    def _ensure(i: int) -> float:
        while len(series) <= i:
            z = rng.gauss(0.0, 1.0)
            series.append(series[-1] * math.exp(drift + vol * z))
        return series[i]

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        c = _ensure(i)
        return BarPoint(close=c, open=c, high=c, low=c, volume=volume)

    return _path


def sine(
    base: float,
    amplitude: float,
    period: int,
    *,
    phase: float = 0.0,
    volume: Optional[float] = None,
) -> PricePath:
    """正弦波 close = base + amplitude*sin(2π*i/period + phase)（周期的価格）。"""
    if period <= 0:
        raise ValueError("period must be positive")

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        c = base + amplitude * math.sin(2.0 * math.pi * i / period + phase)
        return BarPoint(close=c, open=c, high=c, low=c, volume=volume)

    return _path


def ramp(
    start: float,
    stop: float,
    n: int,
    *,
    volume: Optional[float] = None,
) -> PricePath:
    """start→stop を ``n`` バーで線形補間するランプ（i=0→start, i=n-1→stop）。"""
    if n <= 1:
        raise ValueError("ramp needs n >= 2")
    s = float(start)
    e = float(stop)
    span = n - 1

    def _path(i: int, ts: int, prev_close: Optional[float]) -> PathReturn:
        frac = min(max(i, 0), span) / span
        c = s + (e - s) * frac
        return BarPoint(close=c, open=c, high=c, low=c, volume=volume)

    return _path


def volume_path(base: float, *, slope: float = 0.0, period: int = 0, amplitude: float = 0.0) -> VolumePath:
    """出来高を時間変化させる VolumePath（価格パスと同じ callable パターン）。

    ``base + slope*i`` の線形成分 ＋（``period>0`` なら）``amplitude*sin(2π*i/period)`` の周期成分。
    """

    def _vp(i: int, ts: int, prev_close: Optional[float]) -> float:
        v = base + slope * i
        if period > 0:
            v += amplitude * math.sin(2.0 * math.pi * i / period)
        return float(v)

    return _vp
