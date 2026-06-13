"""max_position_size pre-trade rail: 約定後建玉の **時価評価額**で判定する（#25 review findings 2/3/4）。

旧実装は `abs(取得原価) + abs(注文金額)` で判定し、以下を取りこぼした:
  - finding 2: 取得原価で判定するので値上がり後に上限を回避できる。
  - finding 3: flat を跨ぐ反対売買で「現建玉 + 注文額」を二重計上し、最終建玉より過大評価して拒否する。
新実装は `abs(net_signed_qty + signed_order_qty) × reference_price` で判定する。
返済（エクスポージャ減少）は cap で止めない（finding 4）。

`evaluate_pre_trade`（合成エントリポイント）を直接叩く純粋ユニットテスト。
"""
from __future__ import annotations

import os
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine.live.pre_trade_gate import evaluate_pre_trade
from engine.live.safety_rails import (
    KIND_MAX_POSITION_SIZE,
    SafetyLimits,
    SafetyRails,
)

IID = "8918.TSE"


def _rails(cap: int) -> SafetyRails:
    return SafetyRails(SafetyLimits(max_position_size_jpy=cap))


def test_position_cap_uses_market_value_not_cost_basis():
    """finding 2: 100株@取得10（原価1000）保有・現在値20・上限1500 で 1株 BUY。

    約定後建玉は 101株×20=2020 > 1500 なので DENY。旧実装は原価1000 + 注文20 = 1020 ≤ 1500 で
    誤って通過した。
    """
    v = evaluate_pre_trade(
        instrument_id=IID,
        is_buy=True,
        qty=1.0,
        order_notional_jpy=20.0 * 1.0,
        reference_price=20.0,
        net_signed_qty=100.0,
        rails=_rails(1500),
    )
    assert v is not None and v.kind == KIND_MAX_POSITION_SIZE, v


def test_position_cap_does_not_overestimate_cross_flat_sell():
    """finding 3: long 100株@10 から 150株 SELL → 最終 short 50株（建玉 500円）。

    最終建玉 abs(100-150)×10=500 ≤ 上限1000 なので通過すべき。旧実装は現建玉1000 + 注文1500 = 2500 で
    過大拒否した。
    """
    v = evaluate_pre_trade(
        instrument_id=IID,
        is_buy=False,
        qty=150.0,
        order_notional_jpy=10.0 * 150.0,
        reference_price=10.0,
        net_signed_qty=100.0,
        rails=_rails(1000),
    )
    assert v is None, v


def test_position_cap_skips_reducing_order():
    """finding 4: 建玉を減らす反対売買（決済）は cap で止めない。

    long 300株@9 から 100株 SELL（最終 200株=1800円）。上限を最終建玉より低い 1500 に置いても、
    エクスポージャを **減らす** 注文なので cap は不課（決済を弾くと保守の向きが逆になる）。
    """
    v = evaluate_pre_trade(
        instrument_id=IID,
        is_buy=False,
        qty=100.0,
        order_notional_jpy=9.0 * 100.0,
        reference_price=9.0,
        net_signed_qty=300.0,
        rails=_rails(1500),
    )
    assert v is None, v


def test_position_cap_denies_increasing_order_over_market_value():
    """建て増しで約定後時価が上限を超えたら DENY（cap が依然として効く回帰防止）。

    long 100株@10・現在値10・上限1500 で 100株 BUY → 最終 200株×10=2000 > 1500 → DENY。
    """
    v = evaluate_pre_trade(
        instrument_id=IID,
        is_buy=True,
        qty=100.0,
        order_notional_jpy=10.0 * 100.0,
        reference_price=10.0,
        net_signed_qty=100.0,
        rails=_rails(1500),
    )
    assert v is not None and v.kind == KIND_MAX_POSITION_SIZE, v


if __name__ == "__main__":
    failures = []
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
            except AssertionError as exc:
                failures.append(f"{name}: {exc}")
    if failures:
        print("[PRE-TRADE POSITION CAP FAIL]")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("[PRE-TRADE POSITION CAP PASS] market-value position cap; findings 2/3/4")
