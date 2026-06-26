"""SYNTH-GBM-01 / SYNTH-SINE-01 — 確率過程・周期ビルダー（#154 · findings 0120）.

生成器コア（engine/synth/core.py）を**無改変**のまま PricePath プロトコルだけで実装した拡張ビルダー
（gbm / sine / ramp / volume_path）を gate する。
"""
from __future__ import annotations

import os
import sys

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

from engine.synth import gbm, ramp, sine, synth_bars, volume_path  # noqa: E402


@pytest.mark.scenario("SYNTH-GBM-01")
def test_gbm_seed_is_deterministic_and_reproducible() -> None:
    """gbm(seed 固定) は決定論的に再現可能な価格系列を生成する（numpy 非依存・stdlib random）。

    litmus: seed を無視（毎回新乱数）にすると同 seed で系列が変わり RED。
    """
    args = (["X.TSE"], "2025-01-06", "2025-01-31", "Daily")
    a = synth_bars(*args, path=gbm(100.0, mu=0.05, sigma=0.2, seed=42))["X.TSE"]
    b = synth_bars(*args, path=gbm(100.0, mu=0.05, sigma=0.2, seed=42))["X.TSE"]
    c = synth_bars(*args, path=gbm(100.0, mu=0.05, sigma=0.2, seed=7))["X.TSE"]

    assert [x.close for x in a] == [x.close for x in b], "same seed must reproduce the SAME series"
    assert [x.close for x in a] != [x.close for x in c], "different seed must differ"
    assert a[0].close == pytest.approx(100.0)  # S0
    assert all(x.close > 0 for x in a)  # GBM stays positive
    # 1 bar 進めると系列が伸びるだけ（prefix 安定 = 決定論）。
    short = synth_bars(
        ["X.TSE"], "2025-01-06", "2025-01-07", "Daily", path=gbm(100.0, mu=0.05, sigma=0.2, seed=42)
    )["X.TSE"]
    assert [x.close for x in short] == [x.close for x in a[: len(short)]]


@pytest.mark.scenario("SYNTH-SINE-01")
def test_sine_ramp_and_volume_path_on_unchanged_core() -> None:
    """sine / ramp の追加ビルダー + volume を時間変化させる VolumePath が **コア無改変** で動く。

    litmus: volume パスを無視（定数）にすると出来高の時間変化が消え RED。
    """
    import math

    grid = (["X.TSE"], "2025-01-06", "2025-01-13", "Daily")  # 平日 6 本
    sn = synth_bars(*grid, path=sine(100.0, 10.0, period=4))["X.TSE"]
    assert sn[0].close == pytest.approx(100.0)  # sin(0)=0
    assert sn[1].close == pytest.approx(100.0 + 10.0 * math.sin(2 * math.pi / 4))  # peak

    rp = synth_bars(*grid, path=ramp(10.0, 20.0, n=6))["X.TSE"]
    assert rp[0].close == pytest.approx(10.0) and rp[-1].close == pytest.approx(20.0)
    assert rp[1].close > rp[0].close  # monotone up

    # volume を価格パスと同じ callable で時間変化させる（ADV ランキング用）。
    vp = synth_bars(*grid, path=ramp(10.0, 20.0, n=6), volume=volume_path(1000.0, slope=100.0))["X.TSE"]
    assert [b.volume for b in vp] == [1000.0 + 100.0 * i for i in range(6)]
    # BarPoint.volume 明示はパスより優先（builder volume が勝つ）。
    vp2 = synth_bars(*grid, path=ramp(10.0, 20.0, n=6, volume=5.0), volume=volume_path(1000.0, slope=100.0))["X.TSE"]
    assert all(b.volume == 5.0 for b in vp2)


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
