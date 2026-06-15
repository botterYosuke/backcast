"""engine.live.engine_controller — `LiveEngineController` の実体 (Phase 10)。

`LiveStrategyHost` は `LiveEngineController` Protocol
（`attach` / `detach` / `cancel_inflight_orders`）だけに依存する。

本番実装は #50 (ADR-0006) で nautilus を退役し `engine.kernel.live.controller.KernelLiveEngineController`
（pure-Python kernel・Rust core 非ロード）へ移行した。本ファイルはテスト専用 placeholder のみを残す。

- **`NoopLiveEngineController`（テスト専用 placeholder）**: live engine には繋がない。
  attach/detach/cancel を記録するのみで、戦略を **インスタンス化だけ** して
  （`engine_runner` が backtest でやるのと同じ contract 確認）engine には載せない。
  gRPC RPC 配線・state machine・RunRegistry・イベント transport の疎通を、Nautilus を
  起動せずに検証するためのもの。注文経路に繋がっていないため実発注は発生しない。
  テスト（_backend_impl 単体テスト等）が明示的に注入する。
"""

from __future__ import annotations

import logging
from typing import Any, Callable, Optional


log = logging.getLogger(__name__)


class NoopLiveEngineController:
    """Nautilus engine に繋がない placeholder controller（テスト専用 / gRPC plumbing 疎通用）。

    本番経路は `engine.kernel.live` の KernelLiveEngineController。これはテスト注入用 placeholder で、
    `attach` は戦略コンストラクタの contract（kwargs を受けるか）だけ確認し、engine には
    載せない。最後の attach 引数を記録してテスト/デバッグ可能にする。
    """

    def __init__(self) -> None:
        self.attached: dict[str, dict] = {}

    def attach(
        self,
        *,
        strategy_cls: Any,
        scenario: dict,
        instrument_id: str,
        venue: str,
        params: dict[str, str],
        nautilus_strategy_id: str,
        session: Any,
        safety_rails: Any = None,
    ) -> None:
        # 実 engine には繋がない（テスト専用 placeholder）。引数を記録するのみ。
        self.attached[nautilus_strategy_id] = {
            "strategy_cls": getattr(strategy_cls, "__name__", str(strategy_cls)),
            "instrument_id": instrument_id,
            "venue": venue,
            "params": dict(params),
        }
        log.warning(
            "LiveAuto attach via NoopLiveEngineController (TEST PLACEHOLDER): strategy %s "
            "(%s on %s) is NOT connected to a Nautilus engine; no live orders will be placed. "
            "Production uses engine.kernel.live KernelLiveEngineController.",
            nautilus_strategy_id,
            getattr(strategy_cls, "__name__", strategy_cls),
            instrument_id,
        )

    def detach(self, *, nautilus_strategy_id: str) -> None:
        self.attached.pop(nautilus_strategy_id, None)

    def cancel_inflight_orders(self, *, nautilus_strategy_id: str) -> None:
        # placeholder には in-flight order が無い（engine 未接続）。no-op。
        log.debug(
            "cancel_inflight_orders noop (placeholder controller): %s",
            nautilus_strategy_id,
        )

