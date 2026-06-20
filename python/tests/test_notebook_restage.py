"""Phase 6 (#95) gate: edit-time stale projection RPC — ``notebook_restage`` 3-layer (findings 0075).

The incremental session already proves faithful staleness (test_notebook_stale.py). This gate
covers the PRODUCTION RPC the C# editor calls on every edit/blur: synthesise the live source,
diff-register it WITHOUT running, and project the stale cell ids back to cell-ORDER INDICES
(windows are addressed by index). Asserts:

  ALL-STALE     first restage of a fresh notebook marks every cell stale -> [0, 1, 2].
  EDIT-INDEX    after pressing to clear, editing cell 0 stales 0 and its downstream 1 (NOT 2).
  ERROR         an unloadable source returns {"stale": [], "error": "...not a loadable..."}.
  THREE-LAYER   InprocLiveServer.notebook_restage delegates through backend_service -> backend.

  uv run python -m pytest tests/test_notebook_restage.py
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.cell_synthesis import synthesize_json  # noqa: E402

pytestmark = pytest.mark.marimo

# c0 defines threshold; c1 reads it (downstream); c2 is independent.
_C0 = "import marimo as mo\nthreshold = 10"
_C1 = "downstream = threshold * 2\ndownstream"
_C2 = "independent = 42\nindependent"


def _nb(bodies: "list[str]") -> str:
    return synthesize_json(json.dumps([{"body": b, "name": "_", "config": {}} for b in bodies]))


def _backend(root):
    from engine._backend_impl import DataEngineBackend
    from engine.core import DataEngine

    return DataEngineBackend(engine=DataEngine(duckdb_root=str(root)))


def test_restage_marks_every_cell_stale_by_index(tmp_path):
    backend = _backend(tmp_path)
    try:
        out = json.loads(backend.notebook_restage(_nb([_C0, _C1, _C2])))
        assert out["error"] is None
        assert out["stale"] == [0, 1, 2]  # sorted cell-order indices, all stale on first stage
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


def test_restage_after_press_stales_edited_cell_and_downstream_only(tmp_path):
    backend = _backend(tmp_path)
    src = _nb([_C0, _C1, _C2])
    try:
        # Press to clear stale (pure compute, no scenario). Press c1 runs c0 + c1; press c2 runs c2.
        assert json.loads(backend.run_cell(src, 1, ""))["ok"]
        assert json.loads(backend.run_cell(src, 2, ""))["ok"]
        assert json.loads(backend.notebook_restage(src))["stale"] == []  # unchanged -> nothing stale

        # Edit cell 0 (threshold 10 -> 20): index 0 + downstream index 1 go stale; index 2 does not.
        edited = _nb(["import marimo as mo\nthreshold = 20", _C1, _C2])
        out = json.loads(backend.notebook_restage(edited))
        assert out["error"] is None
        assert set(out["stale"]) == {0, 1}
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


def test_restage_unloadable_source_returns_error(tmp_path):
    backend = _backend(tmp_path)
    try:
        out = json.loads(backend.notebook_restage("this is not ( a marimo notebook"))
        assert out["stale"] == []
        assert "not a loadable marimo notebook" in out["error"]
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


def test_restage_delegates_through_all_three_layers(tmp_path):
    # InprocLiveServer (top) -> BackendService -> DataEngineBackend.
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer

    server = InprocLiveServer(DataEngine(duckdb_root=str(tmp_path)))
    try:
        out = json.loads(server.notebook_restage(_nb([_C0, _C1, _C2])))
        assert out["error"] is None
        assert out["stale"] == [0, 1, 2]
    finally:
        sess = server._svc._srv._notebook_session
        if sess is not None:
            sess.close()
