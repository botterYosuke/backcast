"""engine.strategy_runtime.position_sizing — confidence 配分のポジションサイジング。

issue #123 (D)。旧 `_stocktrading` の AllowableRisk × confidence 配分に対応する
**pure-Python**（nautilus 非依存）のサイジング計算。SignalDrivenDayTradeStrategy が
寄り建て時に消費する。

配分の定義:
    weight_i      = confidence_i / Σ confidence            （実 signal 件数で正規化）
    notional_i    = BaseTransaction × weight_i             （建玉に充てる想定元本）
    risk_amount_i = BaseTransaction × AllowableRisk × weight_i  （許容ロス＝JPY）
    qty_i         = floor(notional_i / price_i / unit) × unit    （単元株に丸め）
    loss_margin_i = risk_amount_i / qty_i                  （1 株あたりのロスカット幅）

Σ weight_i = 1 なので Σ risk_amount_i = BaseTransaction × AllowableRisk（件数に依らず
予算に一致）。confidence が高い銘柄ほど notional/qty/risk_amount が大きくなる。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

from engine.signals import Signal

DEFAULT_UNIT = 100  # 単元株（TSE は原則 100 株）


@dataclass(frozen=True)
class Allocation:
    """1 銘柄のリスク配分結果（price 非依存の部分）。"""

    weight: float
    notional: float
    risk_amount: float


def allocate_confidence(
    signals: Iterable[Signal],
    *,
    base_transaction: float,
    allowable_risk: float,
) -> dict[str, Allocation]:
    """signals の confidence 比でリスク予算を配分する。

    Σ confidence が 0（または signals 空）のときは空 dict を返す。
    """
    sigs = list(signals)
    total = sum(s.confidence for s in sigs)
    if total <= 0.0:
        return {}
    out: dict[str, Allocation] = {}
    for s in sigs:
        w = s.confidence / total
        out[s.symbol] = Allocation(
            weight=w,
            notional=base_transaction * w,
            risk_amount=base_transaction * allowable_risk * w,
        )
    return out


def qty_for_notional(notional: float, price: float, *, unit: int = DEFAULT_UNIT) -> int:
    """想定元本 notional を price で割り、単元株 unit に切り捨てた株数を返す。

    price<=0 や notional<=0 のときは 0。
    """
    if price <= 0.0 or notional <= 0.0:
        return 0
    raw_units = int((notional / price) // unit)
    return raw_units * unit


def loss_margin_for(risk_amount: float, qty: int) -> float | None:
    """許容ロス(risk_amount) を株数で割った 1 株あたりロスカット幅。qty<=0 は None。"""
    if qty <= 0:
        return None
    return risk_amount / qty
