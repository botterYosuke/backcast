"""engine.kernel.risk — RiskEngine wrapping the existing pre/post-trade rails (#24).

Component #5 of the Backcast Execution Kernel. Centralises BOTH rail evaluations on the
kernel order path using the exact same import-pure logic the live path uses:
`evaluate_pre_trade` (engine.live.pre_trade_gate) and `evaluate_post_trade`
(engine.live.post_trade_gate). Nautilus-free.

`rails=None` means no independent rails configured: pre-trade skips allowlist/cap checks
(matching the live caller) and post-trade is a no-op.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Iterable, Optional, Sequence

from engine.live.post_trade_gate import evaluate_post_trade
from engine.live.pre_trade_gate import evaluate_pre_trade
from engine.live.safety_rails import RailViolation, SafetyRails


@dataclass(frozen=True)
class _EquitySnapshot:
    """Duck-types post_trade_gate's snapshot so `equity_from_snapshot` returns `equity`.

    The kernel computes mark-to-market equity itself (cash + position market value), so
    it is passed pre-folded as `cash` with no positions — `equity_from_snapshot` then
    returns it unchanged and `evaluate_post_trade` (post_trade_gate) does the rail check.
    """

    cash: float
    positions: Sequence[object] = ()


class RiskEngine:
    def __init__(
        self,
        rails: Optional[SafetyRails],
        regulation_provider: Optional[Callable[[], Iterable[str]]] = None,
    ) -> None:
        self._rails = rails
        # 信用規制 pre-trade フィルタ（() -> 規制中 instrument_id 集合）。None = 規制フィルタ無し
        # （Replay の既定。golden への影響なし）。Live は controller が注入する（D6）。
        self._regulation_provider = regulation_provider

    @property
    def rails(self) -> Optional[SafetyRails]:
        return self._rails

    def check_pre_trade(
        self,
        *,
        instrument_id: str,
        is_buy: bool,
        qty: float,
        net_signed_qty: float,
        current_position_value_jpy: float,
        order_notional_jpy: float = 0.0,
    ) -> RailViolation | None:
        return evaluate_pre_trade(
            instrument_id=instrument_id,
            is_buy=is_buy,
            qty=qty,
            order_notional_jpy=order_notional_jpy,
            current_position_value_jpy=current_position_value_jpy,
            net_signed_qty=net_signed_qty,
            rails=self._rails,
            regulation_provider=self._regulation_provider,
        )

    def check_post_trade(
        self, *, equity: float, baseline_equity: float
    ) -> RailViolation | None:
        """Evaluate the post-trade rail against mark-to-market `equity`. No-op without rails.

        `equity` is the caller's mark-to-market equity (cash + position market value), so
        opening a position is not a loss — only an adverse price move is.
        """
        if self._rails is None:
            return None
        return evaluate_post_trade(
            snapshot=_EquitySnapshot(cash=equity),
            rails=self._rails,
            baseline_equity=baseline_equity,
        )
