"""AFK e2e gate (#95 Phase 4): a notebook ``bt.replay()`` cell drives a REAL backtest.

Drives the WHOLE Phase 4 Python path end to end, the production way:

    DataEngineBackend.run_cell(source, index, scenario_json)
      → _build_notebook_bt (load_replay_data + ReplayKernelObserver + stop_event)
      → NotebookSession injects bt → marimo Runner runs the pressed cell
      → `for bar in bt.replay(): bt.submit_market(...)` drives KernelStepper
      → ReplayKernelObserver: apply_replay_event (chart) + RunBuffer (fills/equity)
        + engine.last_portfolio (#65 running snapshot → poll lane → Hakoniwa)
      → on terminal: finalize → run_summary; engine back to IDLE.

and asserts: the cell traded (BUY+SELL fills in last_portfolio), the run finalized
(run_summary returned), the chart streamed exactly once (no prime/skip), AND the cross-thread
stop halts a paced run mid-flight, AND a paced run takes measurably longer than full speed (the
F6 reinterpretation of issue「速度変更が効く」→「bars_per_second 違いで wallclock が変わる」).

A synthetic per-symbol DuckDB is built in a temp dir so the gate runs without the owner's mount
(data faithfulness is #47/#48's job). The strategy is authored as a B2 cell (BUY bar 3 / SELL bar 40).
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import duckdb  # noqa: E402
import pytest  # noqa: E402

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.cell_synthesis import synthesize_json  # noqa: E402

# Reuse the synthetic-DuckDB builder + constants from the imperative AFK gate (same fixture shape).
from test_replay_duckdb_kernel_afk import _N_BARS, _build_synthetic_duckdb  # noqa: E402

pytestmark = pytest.mark.marimo

_SCENARIO = {
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


def _bt_cell(*, bars_per_second=None) -> str:
    """A B2 replay cell: BUY 100 at bar 3, flatten at bar 40 (the kernel golden twin's legs)."""
    bps = "" if bars_per_second is None else f"bars_per_second={bars_per_second}"
    return (
        "i = 0\n"
        f"for bar in bt.replay({bps}):\n"
        "    if i == 3:\n"
        "        bt.submit_market(100)\n"
        "    elif i == 40:\n"
        "        bt.submit_market(-100)\n"
        "    i += 1\n"
    )


def _source(body: str) -> str:
    return synthesize_json(json.dumps([{"body": body, "name": "_", "config": {}}]))


def _backend(root):
    from engine._backend_impl import DataEngineBackend
    from engine.core import DataEngine

    return DataEngineBackend(engine=DataEngine(duckdb_root=str(root)))


def _run_cell(backend, source, scenario) -> dict:
    """Run one press and TEAR DOWN the marimo kernel afterwards.

    marimo's RuntimeContext is thread-local and the NotebookSession keeps it alive for reuse; in
    production there is ONE session for the process, but a test that builds several must close each
    on the SAME thread that built it (else the next build hits "RuntimeContext already initialized").
    Closing after the run is a test-isolation artifact, not the production lifecycle."""
    try:
        return json.loads(backend.run_cell(source, 0, json.dumps(scenario)))
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


def test_replay_cell_run_drives_real_backtest(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    out = _run_cell(backend, _source(_bt_cell()), _SCENARIO)

    assert out["ok"], out
    # The cell drove a real run that FINALIZED a summary (Hakoniwa run_result tile source).
    assert "run_summary" in out and out["run_summary"], out

    # It traded the real engine: BUY + SELL fills landed in the #65 running snapshot.
    pf = backend.engine.last_portfolio
    assert pf is not None
    assert len(pf["orders"]) == 2, f"expected BUY+SELL fills, got {pf.get('orders')}"

    # Chart streamed exactly once per bar (no prime/skip) — the running Hakoniwa chart source.
    assert len(backend.engine._rs.ohlc_points) == _N_BARS

    # The engine returned to IDLE after the run (force_stop), ready for the next press.
    assert backend.engine._replay_state == "IDLE"


def test_pure_compute_cell_does_not_touch_engine(tmp_path) -> None:
    # A 土台 cell that never calls bt.replay()/bt.step() keeps the Phase 2 path: no bt is built
    # (even with a scenario committed), no engine transition, no last_portfolio (#95 P4-1 / ADR D1).
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    out = _run_cell(backend, _source("answer = 6 * 7\nanswer"), _SCENARIO)

    assert out["ok"], out
    assert out["ran"][0]["output"] == "42"
    assert "run_summary" not in out
    assert backend.engine.last_portfolio is None


def test_non_driving_press_in_bt_notebook_leaves_engine_idle(tmp_path) -> None:
    # A notebook with a bt.replay cell, but the PRESSED cell is an independent pure-compute cell
    # that never drives bt. Phase 6 (P6-6) detects bt drive PER PRESSED CELL via AST, so pressing
    # the pure cell builds NO bt at all (carry-over A: a bt.replay sibling no longer drags the
    # engine to LOADED on a pure press) — the engine simply stays IDLE. Either way the invariant
    # under test holds: a non-driving press never strands the engine, so the next real run loads.
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = synthesize_json(json.dumps([
        {"body": "x = 1\nx", "name": "_", "config": {}},        # cell 0: pure compute (pressed)
        {"body": _bt_cell(), "name": "_", "config": {}},        # cell 1: independent bt.replay cell
    ]))
    try:
        out = json.loads(backend.run_cell(src, 0, json.dumps(_SCENARIO)))   # press cell 0
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()

    assert out["ok"], out
    assert "run_summary" not in out                      # cell 0 did not drive a backtest
    assert backend.engine._replay_state == "IDLE", "engine stranded in LOADED after a non-driving press"

    # The leak would have bricked this: a real bt run still loads + runs afterwards.
    out2 = _run_cell(_backend(tmp_path), _source(_bt_cell()), _SCENARIO)
    assert out2["ok"] and "run_summary" in out2, out2


def test_cross_thread_stop_halts_paced_run_mid_flight(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    # Pace the run so it lasts ~2.5s (50 bars / 20 bps), then force-stop after a few bars — the
    # cross-thread ⏹ path (force_stop_replay from another thread → stepper STOPPED).
    holder: dict = {}

    def _run():
        holder["out"] = _run_cell(backend, _source(_bt_cell(bars_per_second=20)), _SCENARIO)

    t = threading.Thread(target=_run)
    t.start()
    time.sleep(0.4)                       # let ~8 bars stream
    backend.engine.force_stop_replay()    # the ⏹ press (main thread)
    t.join(timeout=15)
    assert not t.is_alive(), "run did not stop after force_stop"

    # It halted well before the end: fewer than all bars reached the chart.
    streamed = len(backend.engine._rs.ohlc_points)
    assert 0 < streamed < _N_BARS, f"expected an early stop, streamed {streamed}/{_N_BARS}"


def test_pacing_makes_a_run_measurably_slower(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)

    t0 = time.perf_counter()
    _run_cell(_backend(tmp_path), _source(_bt_cell()), _SCENARIO)
    full_speed = time.perf_counter() - t0

    t0 = time.perf_counter()
    _run_cell(_backend(tmp_path), _source(_bt_cell(bars_per_second=50)), _SCENARIO)
    paced = time.perf_counter() - t0

    # 50 bars at 50 bars/s ≈ 1s of pacing sleep; full speed is ~instant. The rate is honoured.
    assert paced > full_speed + 0.5, f"paced={paced:.3f}s not slower than full={full_speed:.3f}s"


# ----------------------------------------------------------------------------------------
# #100 slice① (findings 0077): run_result follows the SAME poll-symmetric lifecycle as the
# running portfolio snapshot — cleared at run-begin, set at finalize — so a SECOND bt.replay run
# shows the running view (not the prior run's stale full-stats) while it streams.
# ----------------------------------------------------------------------------------------

def test_run_summary_cleared_at_run_begin_so_rerun_shows_running_not_stale(tmp_path) -> None:
    """#100 slice① RED→GREEN: the run_result tile must show the RUNNING view (not run1's full
    stats) while run2 streams.  The C# poll lane reads ``engine.last_run_summary``; the bug was
    that nothing cleared it at run-begin (the only clear lived on the sunset title-bar Run path),
    so run2's running view showed run1's stale summary until run2 finalized.

    ``engine.last_portfolio`` is already cleared at ``on_run_begin`` (the proven #65 snapshot seam);
    ``last_run_summary`` must follow the SAME lifecycle.  Driving the FIRST ``bt.replay()`` step of
    run2 fires ``on_run_begin`` — at that instant, BEFORE any bar streams or any finalize, the
    stale summary must already be gone.
    """
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    # Run 1 to completion (close the marimo session after — thread-local isolation).
    out1 = _run_cell(backend, _source(_bt_cell()), _SCENARIO)
    assert out1["ok"] and out1.get("run_summary"), out1
    assert backend.engine.last_run_summary is not None, (
        "run1 did not publish a run_summary snapshot for the poll lane"
    )

    # Start run 2: build the bt + drive the first replay step → on_run_begin must clear the stale
    # run1 summary BEFORE the first bar (so the poll lane renders the running view, not full-stats).
    bt2, _rb, _sc = backend._build_notebook_bt(json.dumps(_SCENARIO))
    it = bt2.replay()
    try:
        next(it)  # fire on_run_begin (LOADED→RUNNING + clear last_portfolio + clear last_run_summary)
        assert backend.engine.last_run_summary is None, (
            "run2's on_run_begin did not clear the prior run's summary — the run_result tile shows "
            f"run1's stale full-stats while run2 streams (got {backend.engine.last_run_summary!r})"
        )
        # Sanity: the portfolio snapshot is cleared at run-begin too (the proven sibling behavior).
        assert backend.engine.last_portfolio is None
    finally:
        it.close()
        backend.engine.force_stop_replay()


def test_pure_compute_press_does_not_clear_run_summary(tmp_path) -> None:
    """#100 slice①: after a bt run completes, a PURE-COMPUTE press must NOT clear the run_result
    snapshot — the tile keeps the last run's full stats until the NEXT bt run begins.  This is the
    natural consequence of clearing only at ``on_run_begin`` (which fires solely for a bt-driven
    press): a press that builds no bt leaves ``last_run_summary`` untouched, mirroring the existing
    "pure-compute omits the run_summary key" contract on the C# side."""
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    out1 = json.loads(backend.run_cell(_source(_bt_cell()), 0, json.dumps(_SCENARIO)))
    assert out1["ok"] and out1.get("run_summary"), out1
    summary1 = backend.engine.last_run_summary
    assert summary1 is not None
    try:
        # A 土台 cell: no bt drive → no on_run_begin → the run_result snapshot persists.
        out2 = json.loads(backend.run_cell(_source("answer = 42\nanswer"), 0, json.dumps(_SCENARIO)))
        assert out2["ok"], out2
        assert "run_summary" not in out2, out2
        assert backend.engine.last_run_summary is summary1, (
            "a pure-compute press cleared the run_result snapshot — it must persist the last run's "
            "full stats until the next bt run begins"
        )
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
