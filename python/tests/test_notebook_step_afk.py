"""AFK e2e gate (#95 Phase 5 / #98) — B3 ``bt.step()`` persistence & reset (findings 0074).

Drives the WHOLE Phase 5 Python path end to end:

    DataEngineBackend.run_cell(source, index, scenario_json)
      → detect step-only source → _acquire_step_bt (cache hit or build fresh)
      → NotebookSession injects bt → marimo Runner runs the pressed cell
      → bar = bt.step() advances the SAME stepper across presses
      → terminal None → finalize → cached bt torn down → engine back to IDLE
      → next press rebuilds → pointer resets

and asserts:

  - PERSISTENCE: pressing the same step cell twice keeps the SAME bt and the chart accumulates
    bars (cache hit, no rebuild between presses).
  - SCENARIO RESET: a press with a DIFFERENT committed scenario invalidates the cache and
    rebuilds bt — the next press starts from bar 0 again.
  - TERMINAL: pressing the step cell N times where N >= total bars finalizes the run summary
    on the bar that returns None, then a subsequent press on the same scenario REBUILDS (the
    cache was torn down at terminal).
  - NoScenarioBacktester: when no scenario is committed but the source uses ``bt.step``, the
    cell output contains the guidance ``RuntimeError`` (no NameError).

Reuses the synthetic DuckDB builder from the imperative AFK gate (same fixture shape).
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.cell_synthesis import synthesize_json  # noqa: E402

# Reuse the synthetic-DuckDB builder + constants from the imperative AFK gate.
from test_replay_duckdb_kernel_afk import _N_BARS, _build_synthetic_duckdb  # noqa: E402

pytestmark = pytest.mark.marimo

_SCENARIO_A = {
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}

# A DIFFERENT scenario — narrower date range, same instrument. The exact dates don't matter for
# the cache-key reset: any scenario_json that differs from A's should invalidate the cache.
_SCENARIO_B = {
    "instruments": ["8918.TSE"],
    "start": "2024-11-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


def _step_cell() -> str:
    """A B3 step cell: each press advances the pointer by 1 and shows the bar index."""
    return "bar = bt.step()\nbar"


def _source(body: str) -> str:
    return synthesize_json(json.dumps([{"body": body, "name": "_", "config": {}}]))


def _backend(root):
    from engine._backend_impl import DataEngineBackend
    from engine.core import DataEngine

    return DataEngineBackend(engine=DataEngine(duckdb_root=str(root)))


def _press(backend, source, scenario, *, idx=0):
    """One press; does NOT close the notebook session (Phase 5 step REUSES it across presses)."""
    scenario_json = json.dumps(scenario) if scenario is not None else ""
    return json.loads(backend.run_cell(source, idx, scenario_json))


# ----------------------------------------------------------------------------------------
# PERSISTENCE: pressing twice on the same scenario keeps the SAME bt (pointer advances)
# ----------------------------------------------------------------------------------------

def test_step_cell_press_persists_bt_across_presses(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())

    try:
        out1 = _press(backend, src, _SCENARIO_A)
        assert out1["ok"], out1
        bt_after_first = backend._step_bt
        assert bt_after_first is not None, "step bt was not cached after the first press"

        out2 = _press(backend, src, _SCENARIO_A)
        assert out2["ok"], out2
        assert backend._step_bt is bt_after_first, "step bt was rebuilt on the same scenario commit"

        # Two presses advanced the chart by 2 bars (cache hit = pointer accumulates).
        assert len(backend.engine._rs.ohlc_points) == 2
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# SCENARIO RESET: a different committed scenario rebuilds bt (pointer reset)
# ----------------------------------------------------------------------------------------

def test_step_cell_different_scenario_rebuilds_bt(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())

    try:
        _press(backend, src, _SCENARIO_A)
        _press(backend, src, _SCENARIO_A)
        bt_a = backend._step_bt
        assert bt_a is not None

        # Switch to a different committed scenario → cache invalidates → fresh bt.
        out_b = _press(backend, src, _SCENARIO_B)
        assert out_b["ok"], out_b
        assert backend._step_bt is not None
        assert backend._step_bt is not bt_a, "different scenario commit did not rebuild bt"
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# TERMINAL: pressing past END finalizes + tears down; the next press REBUILDS
# ----------------------------------------------------------------------------------------

def test_step_cell_terminal_finalizes_and_rebuilds_on_next_press(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())

    try:
        # Drive to terminal: press N_BARS times to open every bar; the (N+1)-th press returns
        # None from bt.step() — Backtester.step finalizes its run on that press.
        for _ in range(_N_BARS + 1):
            out = _press(backend, src, _SCENARIO_A)
            assert out["ok"], out

        # On the terminal press, run_cell finalized the RunBuffer summary and tore down the cache.
        assert "run_summary" in out
        assert backend._step_bt is None
        assert backend.engine._replay_state == "IDLE"

        # A subsequent press on the SAME scenario rebuilds (the cache was torn down at terminal).
        out_after = _press(backend, src, _SCENARIO_A)
        assert out_after["ok"], out_after
        assert backend._step_bt is not None, "next press after terminal did not rebuild bt"
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# NoScenarioBacktester: bt cell with no committed scenario surfaces the guidance error
# ----------------------------------------------------------------------------------------

def test_step_cell_without_committed_scenario_surfaces_guidance_error(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())

    try:
        out = _press(backend, src, scenario=None)  # no commit
        assert out["ok"], out
        cell_output = out["ran"][0]["output"]
        assert "RuntimeError" in cell_output, cell_output
        assert "commit the startup panel" in cell_output, cell_output
        # ok=False on the cell that raised (Phase 2 per-cell output capture).
        assert out["ran"][0]["ok"] is False
        # The fail-closed placeholder did NOT touch the engine.
        assert backend._step_bt is None
        assert backend.engine.last_portfolio is None
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# Substring false-positive: bt.step in a comment does not strand the engine in LOADED
# ----------------------------------------------------------------------------------------

def test_substring_false_positive_does_not_strand_engine_in_loaded(tmp_path) -> None:
    """findings 0074 §P5-1: ``"bt.step" in source`` matches comments too.  A cell that mentions
    bt.step in a comment but never drives the handle must NOT leave the engine LOADED — else
    the next bt.replay() press would fail ``LoadReplayData is only allowed from IDLE``."""
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    # The cell never touches bt at runtime; only the comment matches the substring.
    src = _source("# TODO: switch to bt.step\nanswer = 42\nanswer")

    try:
        out = _press(backend, src, _SCENARIO_A)
        assert out["ok"], out
        # The cache should be torn down (was_driven=False on a freshly-built cache miss).
        assert backend._step_bt is None, (
            "step bt cache survived a press where the cell never drove — engine is stranded"
        )
        # And the engine is back to IDLE so a subsequent press of a real bt cell can load.
        assert backend.engine._replay_state == "IDLE"
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# Open-bar leak: a cell that raises after bt.step does not bleed submits into next press
# ----------------------------------------------------------------------------------------

def test_open_bar_is_closed_in_finally_when_cell_raises_after_step(tmp_path) -> None:
    """findings 0072 §Q2 (Phase 2 finally contract):  the per-cell-RUN finally must call
    ``bt._close_open_bar()`` so a cell that raises after opening a bar via ``bt.step()`` does
    NOT leave submits queued for the next press's auto-close.  Otherwise the failed press's
    ``bt.submit_market(...)`` would fill on the NEXT press's bar instead of being discarded."""
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    # Cell: open bar 0, submit BUY, raise.  The bar must be CLOSED in finally so the BUY
    # fills at bar 0's close (this press), not bar 1's close (the next press).
    raising_src = _source("bar = bt.step()\nbt.submit_market(100)\nraise ValueError('rollback')")
    inert_src = _source("bar = bt.step()\nbar")

    try:
        # Press 1: cell raises after the submit.  ok=False, but bar 0 must close → fill happens.
        out1 = _press(backend, raising_src, _SCENARIO_A)
        assert out1["ok"], out1
        assert out1["ran"][0]["ok"] is False, out1["ran"][0]
        assert "ValueError" in out1["ran"][0]["output"], out1["ran"][0]
        # The BUY filled THIS press: portfolio shows the position now, not on a later press.
        pf = backend.engine.last_portfolio
        assert pf is not None
        assert pf.get("orders"), "BUY submit did not fill on the raising press's bar close"

        # Press 2: a non-raising step cell on the SAME cached bt → opens bar 1 with no new fills.
        out2 = _press(backend, inert_src, _SCENARIO_A)
        assert out2["ok"], out2
        # The orders list has the SAME number of fills (bar 1's close didn't add a stale submit).
        pf2 = backend.engine.last_portfolio
        assert pf2 is not None
        assert len(pf2.get("orders", [])) == len(pf.get("orders", [])), (
            "the failed press's submits leaked into the next press's bar close"
        )
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# Build-failure recovery: engine is reset to IDLE so the next press is not bricked
# ----------------------------------------------------------------------------------------

def test_step_bt_build_failure_resets_engine_to_idle(tmp_path) -> None:
    """findings 0074 §P5-1 / Phase 4 parity: if ``_acquire_step_bt`` fails AFTER
    ``load_replay_data`` took the engine IDLE→LOADED but BEFORE the cache assignment, the
    engine must be reset to IDLE so the next press isn't bricked with ``LoadReplayData is only
    allowed from IDLE``.  The replay path has this guarantee; the step path also needs it."""
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())
    # Inject a failure between load_replay_data succeeding and the bt being cached: monkey-patch
    # the from_scenario factory so it raises AFTER the engine has transitioned.
    from engine.strategy_runtime import backtester as backtester_mod

    real_from_scenario = backtester_mod.Backtester.from_scenario

    def _raising_from_scenario(scenario, **kw):
        # Stand the engine LOADED first (mirrors what _build_notebook_bt would have done) then
        # raise to simulate a post-load build failure.
        raise RuntimeError("simulated factory failure")

    try:
        backtester_mod.Backtester.from_scenario = staticmethod(_raising_from_scenario)
        out = _press(backend, src, _SCENARIO_A)
        # The error surfaced cleanly (not a NameError, not a silent hang).
        assert out["ok"] is False, out
        assert "simulated factory failure" in (out.get("error") or "")
        # Critical: engine reset to IDLE so the next press can load again.
        assert backend._step_bt is None
        assert backend.engine._replay_state == "IDLE", (
            "engine stranded in LOADED after step bt build failure — next press will brick"
        )

        # Restore + verify the next press succeeds (no LoadReplayData failure).
        backtester_mod.Backtester.from_scenario = staticmethod(real_from_scenario)
        out2 = _press(backend, src, _SCENARIO_A)
        assert out2["ok"], out2
        assert backend._step_bt is not None
    finally:
        backtester_mod.Backtester.from_scenario = staticmethod(real_from_scenario)
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# Source change: dropping bt.step from the source tears down the cache
# ----------------------------------------------------------------------------------------

def test_step_bt_torn_down_when_source_no_longer_uses_bt_step(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)

    try:
        _press(backend, _source(_step_cell()), _SCENARIO_A)
        assert backend._step_bt is not None

        # Source no longer drives bt (purely 土台) → the cache is dropped.
        out = _press(backend, _source("answer = 42\nanswer"), _SCENARIO_A)
        assert out["ok"], out
        assert backend._step_bt is None
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# P6-6 (findings 0075): mixed bt.replay + bt.step notebook — pressing the STEP cell persists
# ----------------------------------------------------------------------------------------

def test_mixed_replay_and_step_notebook_pressing_step_persists(tmp_path) -> None:
    """findings 0074 §範囲外 / 0075 P6-6: a notebook holding BOTH a bt.replay cell and a bt.step
    cell.  Pressing the STEP cell must use the persistent step cache (pointer advances).  The old
    whole-source substring match saw ``bt.replay`` anywhere and built a FRESH bt every press,
    resetting the step pointer; pressed-cell AST detection chooses the path from the pressed cell."""
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = synthesize_json(json.dumps([
        {"body": "for _bar in bt.replay():\n    pass", "name": "_", "config": {}},  # 0: replay (independent, not pressed)
        {"body": "bar = bt.step()\nbar", "name": "_", "config": {}},                # 1: step (pressed)
    ]))
    try:
        out1 = _press(backend, src, _SCENARIO_A, idx=1)
        assert out1["ok"], out1
        bt1 = backend._step_bt
        assert bt1 is not None, "pressing the step cell built no step cache (replay sibling bypassed it)"

        out2 = _press(backend, src, _SCENARIO_A, idx=1)
        assert out2["ok"], out2
        assert backend._step_bt is bt1, "step cache was rebuilt — the replay sibling reset the pointer"
        # Pointer persisted across presses → the chart accumulated 2 bars (cache hit, not rebuild).
        assert len(backend.engine._rs.ohlc_points) == 2
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# #100 slice② (findings 0077): pressing the REPLAY cell mid-step-session ENDS the step session
# cleanly (owner decision (c): mode switch) — it must NOT silently finalize a partial step run.
# ----------------------------------------------------------------------------------------

def test_mixed_notebook_replay_press_ends_step_session_cleanly(tmp_path) -> None:
    """#100 slice② RED→GREEN: in a mixed replay+step notebook, pressing the REPLAY cell while a
    step session is live must cleanly END the step session, not silently finalize a PARTIAL step
    run as if it completed.

    Bug (before fix): all bt share ``engine.replay_stop_event``.  A step session holds the engine
    RUNNING; pressing replay → ``load_replay_data`` fails the IDLE guard → ``except`` →
    ``force_stop_replay()`` SETS the shared event.  Phase 6's ``notebook_uses_step`` gate PRESERVES
    the step cache, so the next step press is a cache HIT (``on_run_begin`` does NOT re-fire, so the
    event stays SET) → ``KernelStepper.open_next_bar`` sees ``is_set()`` → STOPPED → ``bt.step()``
    returns ``None`` → ``run_cell`` finalizes the partial run as a "complete" summary.  The user
    thinks the step session ran to the end; a sibling cell killed it.

    Fix (c): a replay-driving press tears down any live step bt FIRST (IDLE the engine + reset
    providers), so replay loads & runs cleanly AND the step session is explicitly ended.  A
    subsequent step press REBUILDS a fresh bt (pointer reset) — a real bar, not a silent finalize.
    """
    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = synthesize_json(json.dumps([
        {"body": "for _bar in bt.replay():\n    pass", "name": "_", "config": {}},  # 0: replay
        {"body": "bar = bt.step()\nbar", "name": "_", "config": {}},                # 1: step
    ]))
    try:
        # Advance the step session a few bars (< total) — pointer mid-stream, engine RUNNING.
        for _ in range(3):
            out_step = _press(backend, src, _SCENARIO_A, idx=1)
            assert out_step["ok"], out_step
        assert backend._step_bt is not None
        assert len(backend.engine._rs.ohlc_points) == 3, "step did not advance 3 bars"

        # Press the REPLAY cell mid-step-session. (c): END the step session, then run replay.
        out_replay = _press(backend, src, _SCENARIO_A, idx=0)
        assert out_replay["ok"], out_replay
        # Replay actually loaded from IDLE and ran to completion (the step teardown freed the engine).
        assert "run_summary" in out_replay and out_replay["run_summary"], out_replay
        # The step session was ended by the replay press (cache dropped, not left poisoned).
        assert backend._step_bt is None, "replay press did not end the live step session"

        # Re-press the step cell: a FRESH step session (pointer reset) returns a real bar — NOT a
        # silent finalize of the partial run the shared stop_event would have killed.
        out_restep = _press(backend, src, _SCENARIO_A, idx=1)
        assert out_restep["ok"], out_restep
        assert "run_summary" not in out_restep, (
            "the re-step press silently finalized a partial run — the shared stop_event poisoned "
            f"the step session (got run_summary={out_restep.get('run_summary')!r})"
        )
        assert backend._step_bt is not None, "re-pressing step did not rebuild a fresh session"
        step_output = out_restep["ran"][0]["output"]
        assert step_output and step_output != "None", (
            f"step returned None right after a sibling replay press (silent terminal): {step_output!r}"
        )
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


# ----------------------------------------------------------------------------------------
# carry-over C (findings 0075): step-bt cache key is JSON-key-order insensitive
# ----------------------------------------------------------------------------------------

def test_scenario_cache_key_is_order_insensitive(tmp_path) -> None:
    """carry-over C: the cache key normalises JSON key order, so the same committed scenario
    serialised with a different key order is a cache HIT — not a silent rebuild that would reset
    the step pointer."""
    import collections

    _build_synthetic_duckdb(tmp_path)
    backend = _backend(tmp_path)
    src = _source(_step_cell())
    a = json.dumps(_SCENARIO_A)
    reordered = json.dumps(collections.OrderedDict(reversed(list(_SCENARIO_A.items()))))
    assert a != reordered, "test setup: the two serialisations must differ as raw strings"

    try:
        out1 = json.loads(backend.run_cell(src, 0, a))
        assert out1["ok"], out1
        bt1 = backend._step_bt
        assert bt1 is not None

        out2 = json.loads(backend.run_cell(src, 0, reordered))
        assert out2["ok"], out2
        assert backend._step_bt is bt1, "reordered-key scenario rebuilt bt — cache key not normalised"
    finally:
        if backend._notebook_session is not None:
            backend._notebook_session.close()


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
