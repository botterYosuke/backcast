"""Phase 2 (#95) 土台 gate: per-cell RUN over the marimo Runner interactive path.

ADR-0016 D2 / findings 0070 F1 / findings 0071. Drives ``NotebookSession`` (the production
seam) over sources built by the production synthesiser (``cell_synthesis.synthesize_json``), so
the gate exercises real synthesised marimo ``.py`` — the same round-trip the C# editor produces.

  OUTPUT      a pressed cell's last-expression value (repr) and an explicit ``mo.output`` are
              both captured as the cell's output text.
  DOWNSTREAM  pressing an upstream cell reactively recomputes its DAG descendants.
  INDEPENDENT an upstream-independent cell does NOT recompute (it is structurally absent from
              the ran set — compute_cells_to_run("autorun") returns pressed + descendants only).
  LIFECYCLE   editing the source rebuilds the kernel (picks up the edit); an unchanged source
              reuses it (globals persist). Out-of-range / non-marimo source fail soft.
  DRIFT       the marimo private symbols the seam binds to still exist with the expected shape
              (ADR-0012 §3 — a marimo upgrade that renames them fails HERE, not silently in prod).

  uv run python -m pytest tests/test_notebook_interactive_run.py
"""
from __future__ import annotations

import json

import pytest

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.cell_synthesis import synthesize_json  # noqa: E402
from engine.strategy_runtime.notebook_session import NotebookSession  # noqa: E402

pytestmark = pytest.mark.marimo


def _src(*bodies: str) -> str:
    """Synthesise a marimo ``.py`` from ordered cell bodies (production synthesis path)."""
    return synthesize_json(json.dumps([{"body": b, "name": "_", "config": {}} for b in bodies]))


# cell 0 defines mo + threshold; 1 reads threshold (descendant); 2 is independent; 3 reads mo.
_BODY_STATE = "import marimo as mo\nthreshold = 10"
_BODY_DOWNSTREAM = "downstream = threshold * 2\ndownstream"
_BODY_INDEPENDENT = "independent = 42\nindependent"
_BODY_MO_OUTPUT = "mo.output.replace('explicit-hi')"


def _ran_by_index(result: dict) -> dict:
    assert result["ok"], result
    return {r["index"]: r for r in result["ran"]}


def test_pressed_cell_last_expr_output_captured():
    src = _src(_BODY_STATE, _BODY_DOWNSTREAM)
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src, 1))  # press the downstream cell
        assert 1 in ran, ran
        assert ran[1]["output"] == "20", ran[1]  # threshold(10) * 2, clean repr (not HTML-wrapped)
        assert ran[1]["ok"]
    finally:
        s.close()


class _FakeBt:
    """A stand-in bt handle that records its armed state each time the cell drives it (#95 P4-1)."""

    def __init__(self) -> None:
        self.armed = True
        self.drive_states: list[bool] = []

    def arm(self) -> None:
        self.armed = True

    def disarm(self) -> None:
        self.armed = False

    def replay(self):
        self.drive_states.append(self.armed)
        if self.armed:
            yield 1
            yield 2


def test_injected_bt_disarmed_during_coldrun_armed_during_press():
    # The host injects bt as a free ref. While the kernel is (re)built (cold-run) the handle is
    # DISARMED — a bt.replay() cell yields nothing and starts no backtest; the explicit press ARMS
    # it (#95 P4-1 / findings 0073). A keystroke must never silently drive a run.
    bt = _FakeBt()
    src = _src("seen = list(bt.replay())\nlen(seen)")
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src, 0, inject={"bt": bt}))
        assert bt.drive_states == [False, True], bt.drive_states  # cold-run disarmed, press armed
        assert ran[0]["output"] == "2"  # the armed press streamed 2 items
    finally:
        s.close()


def test_explicit_mo_output_captured():
    src = _src(_BODY_STATE, _BODY_MO_OUTPUT)
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src, 1))
        assert 1 in ran
        assert "explicit-hi" in ran[1]["output"], ran[1]  # mo.output published text reaches the window
    finally:
        s.close()


def test_downstream_recomputes_and_independent_does_not():
    src = _src(_BODY_STATE, _BODY_DOWNSTREAM, _BODY_INDEPENDENT, _BODY_MO_OUTPUT)
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src, 0))  # press the state cell (root)
        # DOWNSTREAM: cells reading threshold / mo recompute.
        assert 1 in ran, f"downstream (reads threshold) did not recompute: {ran}"
        assert 3 in ran, f"downstream (reads mo) did not recompute: {ran}"
        assert ran[1]["output"] == "20"
        # INDEPENDENT: the cell that reads neither threshold nor mo is structurally absent.
        assert 2 not in ran, f"upstream-independent cell recomputed (must not): {ran}"
    finally:
        s.close()


def test_independent_cell_runs_alone_when_pressed():
    src = _src(_BODY_STATE, _BODY_DOWNSTREAM, _BODY_INDEPENDENT)
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src, 2))  # press the independent cell
        assert set(ran) == {2}, f"pressing the independent cell ran others: {ran}"
        assert ran[2]["output"] == "42"
    finally:
        s.close()


def test_edit_rebuilds_and_unchanged_source_reuses():
    src1 = _src(_BODY_STATE, _BODY_DOWNSTREAM)
    s = NotebookSession()
    try:
        ran = _ran_by_index(s.run_pressed(src1, 1))
        assert ran[1]["output"] == "20"
        host_after_first = s._host  # reuse: same source → same kernel instance
        ran = _ran_by_index(s.run_pressed(src1, 1))
        assert s._host is host_after_first, "unchanged source should REUSE the kernel (globals persist)"
        assert ran[1]["output"] == "20"

        # Edit the upstream value → the session must rebuild and pick up the edit.
        src2 = _src("import marimo as mo\nthreshold = 20", _BODY_DOWNSTREAM)
        ran = _ran_by_index(s.run_pressed(src2, 1))
        assert s._host is not host_after_first, "edited source should REBUILD the kernel"
        assert ran[1]["output"] == "40", ran[1]
    finally:
        s.close()


def test_out_of_range_index_fails_soft():
    src = _src(_BODY_STATE, _BODY_DOWNSTREAM)
    s = NotebookSession()
    try:
        res = s.run_pressed(src, 5)
        assert res["ok"] is False
        assert res["ran"] == []
        assert "out of range" in res["error"]
    finally:
        s.close()


def test_non_marimo_source_fails_soft():
    s = NotebookSession()
    try:
        res = s.run_pressed("this is (((not python", 0)
        assert res["ok"] is False, res
        assert res["error"]
        assert s._host is None, "a failed rebuild must leave no leaked kernel"
    finally:
        s.close()


def test_failing_cell_reports_error_text_without_aborting_the_run():
    src = _src(_BODY_STATE, "boom = threshold + undefined_name")
    s = NotebookSession()
    try:
        res = s.run_pressed(src, 1)
        ran = _ran_by_index(res)
        assert 1 in ran
        assert ran[1]["ok"] is False
        assert "NameError" in ran[1]["output"] or "undefined_name" in ran[1]["output"], ran[1]
    finally:
        s.close()


def test_second_thread_is_rejected_fail_closed():
    """The marimo kernel is thread-local: a session driven from a SECOND thread must fail closed
    (clear error) rather than silently corrupt the thread-local context."""
    import threading

    src = _src(_BODY_STATE, _BODY_DOWNSTREAM)
    s = NotebookSession()
    try:
        assert s.run_pressed(src, 1)["ok"]  # establishes the owner thread

        captured = {}

        def _from_other_thread():
            captured["res"] = s.run_pressed(src, 1)

        t = threading.Thread(target=_from_other_thread)
        t.start()
        t.join()
        assert captured["res"]["ok"] is False
        assert "second thread" in captured["res"]["error"], captured["res"]
    finally:
        s.close()


def test_marimo_private_api_drift_gate():
    """ADR-0012 §3: pin the marimo private symbols the interactive-run seam binds to, so a
    marimo upgrade that renames any of them fails THIS test instead of silently breaking prod."""
    import inspect

    from marimo._output.formatting import try_format
    from marimo._runtime.cell_output_list import CellOutputList
    from marimo._runtime.commands import ExecuteCellCommand
    from marimo._runtime.runner.cell_runner import RunResult, Runner
    from marimo._runtime.runner.hooks import create_default_hooks
    from marimo._runtime.runtime import Kernel

    # Runner interactive path.
    assert callable(getattr(Runner, "run_all", None))
    assert callable(getattr(Runner, "compute_cells_to_run", None))
    sig = inspect.signature(Runner.__init__)
    for p in ("roots", "graph", "glbls", "debugger", "hooks", "execution_mode",
              "excluded_cells", "execution_context"):
        assert p in sig.parameters, f"Runner.__init__ lost parameter {p!r}"

    # Output capture surface.
    assert "accumulated_output" in {f for f in getattr(RunResult, "__dataclass_fields__", {})}
    assert callable(getattr(RunResult, "success", None))
    assert callable(getattr(CellOutputList, "stack", None))
    assert callable(try_format)
    assert callable(create_default_hooks)
    assert "code" in inspect.signature(ExecuteCellCommand).parameters

    # Kernel cold-run + context surface.
    for attr in ("run", "_install_execution_context"):
        assert callable(getattr(Kernel, attr, None)), f"Kernel lost {attr!r}"
