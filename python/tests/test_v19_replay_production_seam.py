"""v19 through the production Replay seam (#73 backend gate).

Drives the exact path the Unity app uses to run a Replay — not the direct KernelRunner of
test_v19_replay_core.py, but the production state-machine seam:

    DataEngine(duckdb_root) → load_replay_data → DataEngineBackend.start_engine(strategy_file)
      → KernelRunner + ReplayKernelObserver → apply_replay_event + RunBuffer
      → get_portfolio (orders/fills) + reducer ohlc_points (chart)

so that the in-app HITL (#73 visual confirm) is de-risked: the panels the owner watches
(orders / positions / run_result / chart) are fed by these same projections. Skipped when
the owner's DuckDB minute mount is absent (repo convention).
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

_HERE = os.path.dirname(os.path.abspath(__file__))
_V19 = os.path.abspath(os.path.join(_HERE, "..", "strategies", "v19", "v19_morning.py"))


def _minute_root() -> str | None:
    from engine.paths import jquants_duckdb_root

    root = jquants_duckdb_root()
    if root is None:
        return None
    if not os.path.exists(os.path.join(str(root), "stocks_minute", "6920.duckdb")):
        return None
    return str(root)


@pytest.mark.skipif(
    _minute_root() is None,
    reason="J-Quants DuckDB minute mount absent (BACKCAST_JQUANTS_DUCKDB_ROOT)",
)
def test_v19_runs_through_production_replay_seam(monkeypatch) -> None:
    from engine.core import DataEngine
    from engine._backend_impl import DataEngineBackend
    from engine.kernel.strategy import Strategy as KernelStrategy
    from engine.strategy_runtime.strategy_loader import load as load_strategy

    # #95 Phase 4 (F6): the imperative Replay path no longer has a per-bar throttle (the
    # _REPLAY_BAR_INTERVAL_SEC constant was removed), so it already runs at full speed — no
    # monkeypatch needed. This stays a WIRING gate; faithfulness is covered by test_v19_replay_core.
    root = _minute_root()
    # The app loads the chart/window state from the sidecar scenario, then start_engine
    # re-reads the same sidecar — mirror that (same single source of truth).
    _module, scn, _cls = load_strategy(_V19, base_cls=KernelStrategy)

    eng = DataEngine(duckdb_root=root)
    ok, err = eng.load_replay_data(
        list(scn["instruments"]), scn["start"], scn["end"], scn["granularity"]
    )
    assert ok, f"load_replay_data failed: {err}"

    backend = DataEngineBackend(engine=eng)
    result = backend.start_engine(_V19)
    assert result.success, f"start_engine failed: {result.error_code} {result.error_message}"

    # Orders/fills surfaced through the unchanged get_portfolio projection (orders panel).
    pf = eng.last_portfolio
    assert pf is not None
    assert len(pf["orders"]) >= 2, f"expected v19 round-trip fills, got {pf['orders']}"

    # Bars streamed bar-by-bar into the reducer (the chart panel the owner watches), not
    # injected in one post-run batch — at least the primary instrument accumulated candles.
    primary = scn["instruments"][0]
    assert eng._rs.per_id_ohlc_points.get(primary), "no streamed chart points for primary"
