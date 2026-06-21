"""#102 Slice 1 gate: per-cell console (stdout/stderr) capture (findings 0079).

Drives ``IncrementalNotebookSession`` over cells that ``print`` to stdout/stderr and asserts:

  * a plain ``print('a')`` is captured as a single stdout segment
  * multiple ``print`` calls in one cell accumulate (in arrival order)
  * ``print(..., file=sys.stderr)`` AND ``sys.stderr.write`` are captured as stderr segments
  * mixed stdout/stderr in the same cell preserves arrival order (interleaved segments)
  * a pressed cell's reactive descendant attributes its print to ITS OWN cell, not the pressed cell
  * a re-press clears the prior press's console (per-cell, only on cells that ran)
  * console + rich output (last-expression value) coexist independently (both populated)

Format contract (findings 0076 D1/D4): each ``ran[i]`` carries
``console = [{"stream": "stdout"|"stderr", "text": str}, ...]``, with adjacent
same-stream writes COLLAPSED into one segment (marimo cell.ts:133 / collapseConsoleOutputs.tsx parity).

  uv run python -m pytest tests/test_notebook_console.py
"""
from __future__ import annotations

import pytest

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.notebook_session import IncrementalNotebookSession  # noqa: E402

pytestmark = pytest.mark.marimo


def _press(cells: list[tuple[str, str]], pressed: str) -> dict:
    """Run ``pressed`` over the given (cell_id, code) list once; return the {"ran","stale",...} dict."""
    s = IncrementalNotebookSession()
    try:
        return s.run_pressed(
            [{"cell_id": cid, "code": code} for cid, code in cells],
            pressed,
        )
    finally:
        s.close()


def _ran_by_id(res: dict, cell_id: str) -> dict:
    """The single ran entry whose cell_id matches; ``KeyError`` when absent (caller wants it present)."""
    for r in res["ran"]:
        if r["cell_id"] == cell_id:
            return r
    raise KeyError(f"no ran entry for {cell_id!r}; ran={res['ran']!r}")


def test_plain_print_is_captured_as_a_single_stdout_segment():
    res = _press([("c", "print('a')")], "c")
    assert res["ok"], res
    r = _ran_by_id(res, "c")
    assert r["console"] == [{"stream": "stdout", "text": "a\n"}], r


def test_multiple_prints_in_one_cell_accumulate_in_order():
    res = _press([("c", "print('a'); print('b'); print('c')")], "c")
    r = _ran_by_id(res, "c")
    # Adjacent-same-stream writes COLLAPSE into one segment (marimo parity), preserving order.
    assert r["console"] == [{"stream": "stdout", "text": "a\nb\nc\n"}], r


def test_stderr_print_and_write_are_captured_as_stderr_segments():
    code = "import sys\nprint('e1', file=sys.stderr)\nsys.stderr.write('e2\\n')"
    res = _press([("c", code)], "c")
    r = _ran_by_id(res, "c")
    # adjacent-same-stream collapse: one merged stderr segment.
    assert r["console"] == [{"stream": "stderr", "text": "e1\ne2\n"}], r


def test_mixed_stdout_stderr_preserves_arrival_order():
    code = (
        "import sys\n"
        "print('o1')\n"
        "print('e1', file=sys.stderr)\n"
        "print('o2')\n"
    )
    res = _press([("c", code)], "c")
    r = _ran_by_id(res, "c")
    # Adjacent same stream collapses; cross-stream switches mark a new segment.
    assert r["console"] == [
        {"stream": "stdout", "text": "o1\n"},
        {"stream": "stderr", "text": "e1\n"},
        {"stream": "stdout", "text": "o2\n"},
    ], r


def test_reactive_descendant_print_is_attributed_to_the_descendant_cell():
    # Pressing `a` reactively runs `b` (autorun, descendants of pressed). Each cell's print
    # must land on ITS OWN cell's console — not the pressed cell's.
    cells = [
        ("a", "x = 1\nprint('from-a')"),
        ("b", "y = x + 1\nprint('from-b')"),
    ]
    res = _press(cells, "a")
    a = _ran_by_id(res, "a")
    b = _ran_by_id(res, "b")
    assert a["console"] == [{"stream": "stdout", "text": "from-a\n"}], a
    assert b["console"] == [{"stream": "stdout", "text": "from-b\n"}], b


def test_press_clears_prior_consoles_on_cells_that_run():
    # The same session presses `a` twice. The second press's stdout for `a` must NOT carry
    # over the first press's stdout (marimo: cells that run get clear_console BEFORE the run).
    s = IncrementalNotebookSession()
    try:
        cells = [{"cell_id": "a", "code": "print('round1')"}]
        r1 = s.run_pressed(cells, "a")
        a1 = next(r for r in r1["ran"] if r["cell_id"] == "a")
        assert a1["console"] == [{"stream": "stdout", "text": "round1\n"}], a1

        cells2 = [{"cell_id": "a", "code": "print('round2')"}]
        r2 = s.run_pressed(cells2, "a")
        a2 = next(r for r in r2["ran"] if r["cell_id"] == "a")
        # Only round2 — no round1 leakage.
        assert a2["console"] == [{"stream": "stdout", "text": "round2\n"}], a2
    finally:
        s.close()


def test_console_and_rich_output_coexist_on_the_same_cell():
    # `print('a')` AND a last-expression value `42` in the same cell — both must surface:
    # console carries the stdout, rich (mimetype/data) carries the value's HTML repr.
    res = _press([("c", "print('a')\n42")], "c")
    r = _ran_by_id(res, "c")
    assert r["console"] == [{"stream": "stdout", "text": "a\n"}], r
    # marimo wraps the bare int repr in <pre> as text/html (same as test_notebook_rich_output.py).
    assert r["mimetype"] == "text/html", r
    assert "42" in r["data"], r
