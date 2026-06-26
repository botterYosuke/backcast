"""engine.synth — 合成マーケットデータ生成器（#151-154 / findings 0120）.

テスト・spike から関数呼び出しで任意期間・任意 OHLCV の合成バーを生成し、Replay と Auto の両経路へ
同一シナリオを流す単一供給源。価格パターンは ``PricePath`` callable Protocol で、新パターンは
ビルダー（builders / stochastic）を足すだけで拡張できる（コア core.py は不変）。
"""
from __future__ import annotations

from engine.synth.builders import constant, explicit, from_fn, trend
from engine.synth.core import (
    DEFAULT_VOLUME,
    BarPoint,
    PricePath,
    VolumePath,
    calendar_grid,
    linear_grid,
    synth_bars,
    ts_to_jst_minute,
)
from engine.synth.feed import SyntheticFeed, auto_trades, universe_bars
from engine.synth.stochastic import gbm, ramp, sine, volume_path

__all__ = [
    "BarPoint",
    "PricePath",
    "VolumePath",
    "DEFAULT_VOLUME",
    "synth_bars",
    "calendar_grid",
    "linear_grid",
    "ts_to_jst_minute",
    # deterministic builders (#151)
    "explicit",
    "constant",
    "trend",
    "from_fn",
    # stochastic / periodic builders (#154)
    "gbm",
    "sine",
    "ramp",
    "volume_path",
    # feed (#152)
    "universe_bars",
    "auto_trades",
    "SyntheticFeed",
]
