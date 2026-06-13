"""engine.live.nautilus_risk_config — Nautilus-coupled rail config builder (#24).

Split out of `safety_rails.py` so that the pure rail logic (`SafetyRails`,
`evaluate_pre_trade`, `evaluate_post_trade`) stays **import-free of Nautilus**.

`safety_rails.py` must never `import nautilus_trader` (importing it loads the Rust
core `nautilus_trader.core.nautilus_pyo3`, which re-introduces the multi-CRT/FLS
teardown crash the Backcast Execution Kernel exists to remove — ADR-0004 案 C /
findings 0008 §1.1). The native rails (`max_order_value` / `max_orders_per_minute`)
are only meaningful on the Nautilus live path, so the one helper that builds a
`LiveRiskEngineConfig` lives here and is imported lazily from
`NautilusLiveEngineController._do_attach()` only.

Tested for import purity by `tests/test_gate_import_purity.py`.
"""
from __future__ import annotations

from nautilus_trader.live.config import LiveRiskEngineConfig

from engine.live.safety_rails import SafetyLimits


def build_live_risk_engine_config(
    limits: SafetyLimits, instrument_ids: list[str]
) -> LiveRiskEngineConfig:
    """Build the Nautilus native-rail config from `SafetyLimits`.

    - `max_order_value_jpy` → `max_notional_per_order`（instrument ごとに同額をマップ）。
      0 のときは何も入れない（= そのチェック無効、Nautilus 側 default 動作）。
    - `max_orders_per_minute` → `max_order_submit_rate = "N/00:01:00"`。0 のときは
      Nautilus default（`"100/00:00:01"`）に委ねる（フィールドを設定しない）。
    """
    kwargs: dict = {}
    if limits.max_order_value_jpy > 0:
        kwargs["max_notional_per_order"] = {
            iid: int(limits.max_order_value_jpy) for iid in instrument_ids
        }
    if limits.max_orders_per_minute > 0:
        kwargs["max_order_submit_rate"] = f"{limits.max_orders_per_minute}/00:01:00"
    return LiveRiskEngineConfig(**kwargs)
