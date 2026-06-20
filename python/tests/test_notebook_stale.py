"""Phase 6 (#95) gate: faithful per-cell staleness over the incremental marimo kernel.

findings 0075 P6-1. Drives ``IncrementalNotebookSession`` (the production seam that replaces
rebuild-on-change) with cells keyed by STABLE id (the C# window/region id). Asserts the marimo
graph/stale model the spike proved, now through the production class:

  STALE-ON-EDIT   restaging new/edited cells marks them (and downstream) stale WITHOUT running.
  PRESS-RUNS      a press runs the pressed cell + stale ancestors + reactive descendants, and
                  clears stale on exactly the cells that ran.
  INDEPENDENT     an upstream-independent cell never runs and stays stale until its own press.
  PERSIST         globals survive an edit (no kernel rebuild) — the live-state landmine dissolves.
  DELETE          removing a cell purges its defs from globals and stales its orphaned children.

  uv run python -m pytest tests/test_notebook_stale.py
"""
from __future__ import annotations

import pytest

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.notebook_session import IncrementalNotebookSession  # noqa: E402

pytestmark = pytest.mark.marimo

# a defines threshold; b reads it (descendant); c is independent.
_A = {"cell_id": "a", "code": "import marimo as mo\nthreshold = 10"}
_B = {"cell_id": "b", "code": "downstream = threshold * 2\ndownstream"}
_C = {"cell_id": "c", "code": "independent = 42\nindependent"}


def _ids(ran_result: dict) -> set:
    return {r["cell_id"] for r in ran_result["ran"]}


def test_restage_marks_all_stale_without_running():
    s = IncrementalNotebookSession()
    try:
        res = s.restage([_A, _B, _C])
        assert res["error"] is None
        assert set(res["stale"]) == {"a", "b", "c"}
        assert s._host is not None and "threshold" not in s._host.k.globals  # nothing executed
    finally:
        s.close()


def test_press_runs_stale_ancestors_not_independent():
    s = IncrementalNotebookSession()
    try:
        res = s.run_pressed([_A, _B, _C], "b")  # press downstream b
        assert res["ok"], res
        assert _ids(res) == {"a", "b"}  # b + its stale ancestor a; NOT independent c
        out = {r["cell_id"]: r["output"] for r in res["ran"]}
        assert out["b"] == "20"  # threshold(10) * 2
        assert set(res["stale"]) == {"c"}  # a,b cleared; c still needs its own press
        g = s._host.k.globals
        assert g["threshold"] == 10 and g["downstream"] == 20
    finally:
        s.close()


def test_independent_press_clears_only_itself():
    s = IncrementalNotebookSession()
    try:
        s.restage([_A, _B, _C])
        res = s.run_pressed([_A, _B, _C], "c")
        assert _ids(res) == {"c"}
        assert set(res["stale"]) == {"a", "b"}  # c gone; a,b untouched
        assert s._host.k.globals["independent"] == 42
    finally:
        s.close()


def test_edit_propagates_stale_downstream_and_globals_persist():
    s = IncrementalNotebookSession()
    try:
        s.run_pressed([_A, _B, _C], "b")  # a=10, downstream=20
        s.run_pressed([_A, _B, _C], "c")  # independent=42
        assert s.stale() == []
        # Edit a: threshold 10 -> 20. b must go stale; globals keep their prior values (no rebuild).
        a2 = {"cell_id": "a", "code": "import marimo as mo\nthreshold = 20"}
        res = s.restage([a2, _B, _C])
        assert "b" in res["stale"], res
        g = s._host.k.globals
        assert g["independent"] == 42  # independent press's value survived the edit
        assert g["downstream"] == 20  # not re-run yet (stale, not executed)
        # Press b: a (edited, stale) reruns then b -> downstream reflects threshold=20.
        res = s.run_pressed([a2, _B, _C], "b")
        assert {"a", "b"} <= _ids(res)
        assert s._host.k.globals["downstream"] == 40
        assert s.stale() == ["c"] or "b" not in s.stale()
    finally:
        s.close()


def test_delete_cell_purges_defs_and_stales_children():
    # d defines x; e reads x (child). Deleting d purges x and stales e.
    d = {"cell_id": "d", "code": "x = 5"}
    e = {"cell_id": "e", "code": "y = x + 1\ny"}
    s = IncrementalNotebookSession()
    try:
        s.run_pressed([d, e], "e")  # x=5, y=6
        assert s._host.k.globals["x"] == 5 and s._host.k.globals["y"] == 6
        assert s.stale() == []
        # Delete d (notebook now only has e). x is purged; e becomes stale (its parent vanished).
        res = s.restage([e])
        assert "x" not in s._host.k.globals
        assert "d" not in s._order
        assert "e" in res["stale"]
    finally:
        s.close()


def test_unchanged_restage_is_idempotent():
    s = IncrementalNotebookSession()
    try:
        s.run_pressed([_A, _B], "b")
        assert s.stale() == []
        res = s.restage([_A, _B])  # identical -> no new staleness
        assert res["stale"] == []
    finally:
        s.close()
