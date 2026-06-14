"""Step 1 (kernel additive) — #25 Kernel Live Foundation の building blocks を pin する。

FSM 遷移本体（SUBMITTED→ACCEPTED→PARTIAL→FILLED）は LiveBroker（Step 3）で検証する。本書は
additive 改修が ① Replay 観測挙動を壊さない ② Live 用 building block が期待どおり、を固定する:
- OrderEngine.precheck() は通過時 INITIALIZED のまま / 違反で DENIED / dup で ValueError
- OrderEngine.submit() は従来どおり通過で ACCEPTED（Replay 互換）
- RiskEngine(regulation_provider=) が pre-trade で発火（fail-closed 含む）
- Portfolio.seed_position + 実現損益の明示積算（flat 往復は cash-delta と一致）
- strategy_loader.load(base_cls=) が kernel twin を nautilus 非ロードで読む

standalone でも pytest でも実行可。
"""
from __future__ import annotations

import sys

from engine.kernel.orders import (
    Order,
    OrderEngine,
    OrderFilled,
    OrderSide,
    OrderStatus,
)
from engine.kernel.portfolio import Portfolio
from engine.kernel.risk import RiskEngine
from engine.live.safety_rails import SafetyLimits, SafetyRails


def _engine(rails=None, regulation_provider=None, venue="TSE"):
    return OrderEngine(
        risk_engine=RiskEngine(rails, regulation_provider=regulation_provider), venue=venue
    )


def _order(cid="O-1", side=OrderSide.BUY, qty=100.0, instrument="8918.TSE"):
    return Order(
        client_order_id=cid, strategy_id="s", instrument_id=instrument, side=side, quantity=qty
    )


# ── precheck / submit ────────────────────────────────────────────────────────


def test_precheck_pass_leaves_initialized():
    eng = _engine()
    order = _order()
    violation = eng.precheck(order, net_signed_qty=0.0, reference_price=0.0)
    assert violation is None
    # precheck は次の遷移を caller に委ねる（Live は SUBMITTED へ、Replay は submit() が ACCEPTED へ）。
    assert order.status is OrderStatus.INITIALIZED


def test_submit_pass_sets_accepted_replay_compat():
    eng = _engine()
    order = _order()
    violation = eng.submit(order, net_signed_qty=0.0, reference_price=0.0)
    assert violation is None
    assert order.status is OrderStatus.ACCEPTED


def test_precheck_violation_denies():
    rails = SafetyRails(SafetyLimits(allowed_instruments=("7203.TSE",)))
    eng = _engine(rails=rails)
    order = _order(instrument="8918.TSE")  # not allowlisted
    violation = eng.precheck(order, net_signed_qty=0.0, reference_price=0.0)
    assert violation is not None
    assert order.status is OrderStatus.DENIED
    assert order.denied_reason


def test_duplicate_client_order_id_raises():
    eng = _engine()
    eng.precheck(_order(cid="DUP"), net_signed_qty=0.0, reference_price=0.0)
    try:
        eng.precheck(_order(cid="DUP"), net_signed_qty=0.0, reference_price=0.0)
    except ValueError as exc:
        assert "DUP" in str(exc)
    else:  # pragma: no cover
        raise AssertionError("expected ValueError on duplicate client_order_id")


# ── regulation provider (Live pre-trade parity) ──────────────────────────────


def test_regulation_provider_denies_regulated_buy():
    # 規制中銘柄への建て増し（BUY）は deny。provider 経由で評価される（D6）。
    eng = _engine(regulation_provider=lambda: {"8918.TSE"})
    order = _order(side=OrderSide.BUY)
    violation = eng.precheck(order, net_signed_qty=0.0, reference_price=0.0)
    assert violation is not None
    assert order.status is OrderStatus.DENIED


def test_regulation_provider_failure_is_fail_closed():
    def _boom():
        raise RuntimeError("regulation feed down")

    eng = _engine(regulation_provider=_boom)
    order = _order(side=OrderSide.BUY)
    violation = eng.precheck(order, net_signed_qty=0.0, reference_price=0.0)
    assert violation is not None  # fail-closed


def test_no_regulation_provider_default_skips():
    eng = _engine()  # regulation_provider=None
    order = _order(side=OrderSide.BUY)
    assert eng.precheck(order, net_signed_qty=0.0, reference_price=0.0) is None


# ── Portfolio: seed + realized accrual ────────────────────────────────────────


def _fill(side, qty, px, instrument="8918.TSE", cid="O"):
    return OrderFilled(
        client_order_id=cid,
        venue_order_id="V",
        strategy_id="s",
        instrument_id=instrument,
        side=side,
        last_qty=qty,
        last_px=px,
        ts_event_ns=0,
    )


def test_realized_pnl_flat_roundtrip_matches_cash_delta():
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(OrderSide.BUY, 100, 8.0))
    pf.apply_fill(_fill(OrderSide.SELL, 100, 10.0))
    # closing long: 100 * (10 - 8) = 200
    assert pf.realized_pnl == 200.0
    assert pf.realized_pnl == pf.cash - 1_000_000.0  # flat → equals cash delta
    assert pf.open_positions() == []


def test_realized_pnl_only_on_reducing_portion():
    pf = Portfolio(initial_cash=1_000_000.0)
    pf.apply_fill(_fill(OrderSide.BUY, 100, 8.0))
    pf.apply_fill(_fill(OrderSide.BUY, 100, 12.0))  # increase, no realize
    assert pf.realized_pnl == 0.0
    pf.apply_fill(_fill(OrderSide.SELL, 100, 11.0))  # avg_px=10 → 100*(11-10)=100
    assert pf.realized_pnl == 100.0


def test_seed_position_then_sell_realizes_against_seed_avg():
    pf = Portfolio(initial_cash=500_000.0)
    pf.seed_position("8918.TSE", 200, 9.0)  # venue 既存建玉
    assert pf.net_signed_qty("8918.TSE") == 200
    pf.apply_fill(_fill(OrderSide.SELL, 200, 9.5))  # 200*(9.5-9.0)=100
    assert pf.realized_pnl == 100.0


def test_seed_zero_quantity_ignored():
    pf = Portfolio(initial_cash=0.0)
    pf.seed_position("8918.TSE", 0, 9.0)
    assert pf.open_positions() == []


# ── strategy_loader base_cls (kernel twin, nautilus-free) ─────────────────────

KERNEL_TWIN = "spike/fixtures/strategies/kernel_spike_buy_sell.py"


def test_loader_loads_kernel_twin_without_nautilus():
    from engine.kernel.strategy import Strategy as KernelStrategy
    from engine.strategy_runtime.strategy_loader import load

    _module, scenario, strategy_cls = load(KERNEL_TWIN, base_cls=KernelStrategy)
    assert issubclass(strategy_cls, KernelStrategy)
    assert strategy_cls.__name__ == "KernelSpikeBuySell"
    assert "instruments" in scenario


def test_base_strategy_accepts_common_construction_contract():
    # 専用 __init__ を持たない最小 kernel 戦略でも、controller の `strategy_cls(instrument_id=..., **params)`
    # 生成契約で構築できる（基底が instrument_id/**params を受ける・finding 2）。strategy_id は Live では
    # controller が nautilus_strategy_id を inject するので、構築時は未指定でよい。
    from engine.kernel.strategy import Strategy

    class _Minimal(Strategy):
        pass

    s = _Minimal(instrument_id="7203.TSE", holding_minutes="42")
    assert s.instrument_id == "7203.TSE"
    assert s.params == {"holding_minutes": "42"}
    assert s.id == ""


def test_loader_wrong_base_finds_zero():
    # nautilus Strategy base で kernel twin を読むと subclass 0 件 → StrategyLoadError。
    from engine.strategy_runtime.strategy_loader import StrategyLoadError, load

    try:
        from nautilus_trader.trading.strategy import Strategy as NautilusStrategy
    except ImportError:
        return  # nautilus 不在環境ではスキップ（kernel 経路の本質ではない）
    try:
        load(KERNEL_TWIN, base_cls=NautilusStrategy)
    except StrategyLoadError as exc:
        assert "subclass found" in str(exc)
    else:  # pragma: no cover
        raise AssertionError("expected StrategyLoadError (0 nautilus subclasses)")


if __name__ == "__main__":  # standalone 実行
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
