"""engine.live.post_trade_gate — Post-trade rail 合成の単一エントリポイント (#223).

AccountSnapshot を受け取り、SafetyRails の max_daily_loss を評価して
RailViolation | None を返す純粋関数。

呼び出し側（orchestrator）が担う責務（gate に含まない）:
  - baseline_equity の初回確立と保存（baseline 未確立時は gate を呼ばない）
  - 違反後の rails / baseline 解放（二重発火防止）
  - fail_run スレッド起動と SafetyRailViolation + LiveStrategyEvent の push
"""

from __future__ import annotations

from engine.live.safety_rails import RailViolation, SafetyRails


def equity_from_snapshot(snapshot) -> float:
    """口座スナップショットの mark-to-market equity（cash + Σ unrealized_pnl）。

    保守的近似: realized は cash に反映済み前提。建玉の含み損益のみ加算。
    snapshot は AccountSnapshot 互換の duck-type（cash / positions 属性を持てばよい）。
    """
    equity = float(getattr(snapshot, "cash", 0.0) or 0.0)
    for p in getattr(snapshot, "positions", ()) or ():
        equity += float(getattr(p, "unrealized_pnl", 0.0) or 0.0)
    return equity


def evaluate_post_trade(
    *,
    snapshot,
    rails: SafetyRails,
    baseline_equity: float,
) -> RailViolation | None:
    """Post-trade rail 評価（#223 単一エントリポイント）。

    Parameters
    ----------
    snapshot        : AccountSnapshot 互換オブジェクト（cash / positions 属性）
    rails           : SafetyRails（max_daily_loss_jpy config を保持）
    baseline_equity : run 開始時点の equity（orchestrator が初回確立して渡す）

    Returns
    -------
    RailViolation | None — max_daily_loss 違反があれば RailViolation、なければ None。

    Notes
    -----
    baseline 未確立時（baseline が None の場合）は **呼び出し側が gate を呼ばない**設計。
    違反後の rails/baseline 解放・スレッド起動・fail_run も orchestrator が担当する。
    """
    equity = equity_from_snapshot(snapshot)
    return rails.check_post_trade(daily_pnl_jpy=equity - baseline_equity)
