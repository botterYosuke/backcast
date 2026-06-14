"""engine.live.pre_trade_gate — Pre-trade rail 合成の単一エントリポイント (#199)。

手動発注 (ManualOrderFacade) と auto 発注 (NautilusVenueExecClient) が
それぞれインラインで再構築していた rail 合成（どの rail を・どの順で・違反時に
何を返すか）を 1 箇所に集約し、非対称を解消する。

合成順序（auto 経路の "規制銘柄は他 rail を待たず submit しない" に沿う）:
  1. 信用規制チェック（order_increases_exposure なら評価、provider 例外は fail-closed）
  2. SafetyRails.check_pre_trade（allowlist + 建玉上限）

呼び出し側が担う責務（gate に含まない）:
  - 余力チェック (check_buying_power): LIMIT 手動のみ caller が先行する。
  - PAUSE gate: auto の caller (NautilusVenueExecClient) が先行する。
  - deny/publish 副作用 (generate_order_denied / OrderFacadeError): caller が担う。
"""

from __future__ import annotations

import logging
from typing import Callable, Iterable, Optional

from engine.live.safety_rails import (
    KIND_REGULATION,
    RailViolation,
    SafetyRails,
    check_regulation,
    order_increases_exposure,
)

log = logging.getLogger(__name__)


def evaluate_pre_trade(
    *,
    instrument_id: str,
    is_buy: bool,
    qty: float,
    order_notional_jpy: float,
    reference_price: float | None,
    net_signed_qty: float,
    rails: Optional[SafetyRails],
    regulation_provider: Optional[Callable[[], Iterable[str]]] = None,
) -> RailViolation | None:
    """Pre-trade rail 合成評価（#199 単一エントリポイント）。

    Parameters
    ----------
    instrument_id      : 発注銘柄 ID
    is_buy             : True = BUY、False = SELL
    qty                : 発注数量
    order_notional_jpy : 発注金額（MARKET は 0 = price 不明）
    reference_price    : 約定建玉の時価評価に使う参照価格（直近値）。`None` = 参照価格未取得で
                         建玉上限を評価できない（kernel 経路は上位で NO_REFERENCE_PRICE deny 済み）。
    net_signed_qty     : 符号付き建玉数（long>0 / short<0 / flat=0）
    rails              : SafetyRails（allowlist + 建玉上限 config）、None = rails 無効
    regulation_provider: () -> Iterable[str]（規制銘柄集合、None = 規制フィルタ無し）
                         例外を投げたら fail-closed（規制状態不明 → deny）。

    建玉上限（max_position_size）は **約定後の符号付き建玉の時価評価額**
    `abs(net_signed_qty + signed_order_qty) × reference_price` で判定する（#25 review findings 2/3）。
    取得原価ベースの旧判定は値上がり後の上限回避（finding 2）と flat 跨ぎ反対売買の過大拒否
    （finding 3）を起こすため、ここで参照価格から算出して `SafetyRails.check_pre_trade` に渡す。

    Returns
    -------
    RailViolation | None — 違反があれば最初に引っかかった違反、なければ None。
    """
    increases = order_increases_exposure(net_signed_qty, is_buy=is_buy, order_qty=qty)
    if increases:
        reg = _check_regulation_fail_closed(instrument_id, regulation_provider)
        if reg is not None:
            return reg

    if rails is not None:
        signed_order_qty = qty if is_buy else -qty
        projected_position_value_jpy = (
            abs(net_signed_qty + signed_order_qty) * reference_price
            if reference_price is not None
            else None
        )
        return rails.check_pre_trade(
            instrument_id=instrument_id,
            order_notional_jpy=order_notional_jpy,
            projected_position_value_jpy=projected_position_value_jpy,
            increases_exposure=increases,
        )
    log.debug("evaluate_pre_trade: rails=None, allowlist/position_cap checks skipped for %s", instrument_id)
    return None


def _check_regulation_fail_closed(
    instrument_id: str,
    regulation_provider: Optional[Callable[[], Iterable[str]]],
) -> RailViolation | None:
    """信用規制 pre-trade 判定（fail-closed）。provider 未注入なら規制フィルタ無し。

    provider が例外を投げたら fail-closed（規制状態が不明な間は発注しない）。
    """
    if regulation_provider is None:
        return None
    try:
        regulated = regulation_provider()
        return check_regulation(instrument_id=instrument_id, regulated_instruments=regulated)
    except Exception as exc:  # noqa: BLE001 — 判定不能は fail-closed で deny
        log.warning("regulation_provider failed; denying order fail-closed: %s", exc)
        return RailViolation(
            KIND_REGULATION,
            f"{instrument_id} regulation status unavailable; order suppressed (fail-closed)",
        )
