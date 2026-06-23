"""v19 marimo cell through the production per-cell RUN seam — artifact self-resolution gate.

The Unity app runs ``v19_morning_cell.py`` via the **marimo per-cell RUN button**:

    NotebookRunController.RunCell → HostNotebookCellExecutor → DataEngineBackend.run_cell(
        source_text, pressed_index, scenario_json, strategy_path)
      → _build_notebook_bt → Backtester.replay() over the committed scenario's DuckDB bars

The strategy's ``_artifacts`` cell self-loads its scorer + universe from the cell-adjacent
``artifacts`` dir via ``Path(__file__).parent / "artifacts"`` (NO sidecar scorer key).  Unlike the
imperative loader (``strategy_loader.py`` sets ``module.__file__ = original_path``), the marimo
per-cell path historically passed NO on-disk anchor, so ``__file__`` was cwd-derived and the
artifacts were not found → ``FileNotFoundError`` → the cell never traded (silent empty Replay).

This gate drives the REAL cell over the owner's REAL minute mount, deliberately WITHOUT
``V19_ARTIFACTS_DIR`` (production sets none), and asserts the run self-loads its artifacts and
trades.  RED before the ``strategy_path`` → ``__file__`` plumbing; GREEN after.

Skipped when the owner's DuckDB minute mount is absent (repo convention).
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

_HERE = os.path.dirname(os.path.abspath(__file__))
_V19_DIR = os.path.abspath(os.path.join(_HERE, "..", "strategies", "v19"))
_CELL_PY = os.path.join(_V19_DIR, "v19_morning_cell.py")
_CELL_JSON = os.path.join(_V19_DIR, "v19_morning_cell.json")


def _minute_root() -> str | None:
    from engine.paths import jquants_duckdb_root

    root = jquants_duckdb_root()
    if root is None:
        return None
    if not os.path.exists(os.path.join(str(root), "stocks_minute", "6920.duckdb")):
        return None
    return str(root)


def _pressed_replay_index(source: str) -> int:
    from engine.strategy_runtime.cell_synthesis import load_app_from_text

    bodies = list(load_app_from_text(source)._cell_manager.codes())
    idx = next((i for i, b in enumerate(bodies) if "bt.replay" in b), None)
    assert idx is not None, "v19_morning_cell has no bt.replay cell"
    return idx


@pytest.mark.skipif(
    _minute_root() is None,
    reason="J-Quants DuckDB minute mount absent (BACKCAST_JQUANTS_DUCKDB_ROOT)",
)
def test_v19_cell_self_loads_artifacts_and_trades_via_run_cell(monkeypatch) -> None:
    from engine._backend_impl import DataEngineBackend
    from engine.core import DataEngine

    root = _minute_root()
    # Production faithfulness: the app sets NO V19_ARTIFACTS_DIR — the cell must self-locate its
    # artifacts from its own on-disk dir.  Clearing it here proves the __file__ plumbing, not an env.
    monkeypatch.delenv("V19_ARTIFACTS_DIR", raising=False)

    source = open(_CELL_PY, encoding="utf-8").read()
    scenario = json.loads(open(_CELL_JSON, encoding="utf-8").read())["scenario"]
    # Trim to a single committed trading day for gate speed (entry 10:00 / exit 14:55 same JST day).
    scenario = dict(scenario, start="2025-01-06", end="2025-01-06")
    pressed = _pressed_replay_index(source)

    backend = DataEngineBackend(engine=DataEngine(duckdb_root=root))
    try:
        # The C# RunCell path hands run_cell the document's canonical .py path (#78 provider) so the
        # marimo cell globals get the right __file__ — exactly what HostNotebookCellExecutor passes.
        out = json.loads(
            backend.run_cell(source, pressed, json.dumps(scenario), strategy_path=_CELL_PY)
        )
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()

    # No cell silently FileNotFounds on its artifacts (the RED symptom).
    for r in out.get("ran", []):
        o = r.get("output") or ""
        assert "FileNotFoundError" not in o and "No such file" not in o, f"artifact load failed: {o}"

    assert out["ok"], out
    assert out.get("run_summary"), f"run never finalized a summary: {out}"

    # It actually traded the real engine over real bars (top-k entry at 10:00, flatten at 14:55).
    pf = backend.engine.last_portfolio
    assert pf is not None, "no running snapshot — the cell did not drive the engine"
    assert len(pf.get("orders", [])) > 0, f"expected fills, got {pf.get('orders')}"
    assert backend.engine._replay_state == "IDLE", "engine not back to IDLE after run"
