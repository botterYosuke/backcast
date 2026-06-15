"""Unit gate (#30): DataEngine Replay-transport control methods.

Covers the kernel-path transport seam that the InprocLiveServer forwarders + C# footer drive:
set_replay_speed (now stored, not a no-op), step_replay routed to the KernelRunner step_event
on the DuckDB path (PAUSED-only), and step_event hygiene (no stale pulse across run/resume).
Pure-Python: no DuckDB mount — the DuckDB path is simulated via white-box state.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402


def _kernel_paused(engine: DataEngine) -> None:
    """Put the engine in the kernel (DuckDB) path, PAUSED — as it is mid-run after pause."""
    engine._replay_duckdb_root = "/synthetic/root"  # marks the kernel path active
    engine._replay_state = "PAUSED"
    engine._run_event.clear()


def test_set_replay_speed_stores_multiplier() -> None:
    engine = DataEngine()
    assert engine.replay_speed_multiplier == 1  # default 1x

    ok, err = engine.set_replay_speed(10)
    assert ok and err is None
    assert engine.replay_speed_multiplier == 10  # actually stored (was a no-op pre-#30)


def test_set_replay_speed_rejects_zero_and_negative() -> None:
    engine = DataEngine()
    for bad in (0, -1):
        ok, err = engine.set_replay_speed(bad)
        assert not ok and "greater than 0" in err
    assert engine.replay_speed_multiplier == 1  # unchanged by a rejected call


def test_set_replay_speed_rejects_non_int_without_raising() -> None:
    engine = DataEngine()
    ok, err = engine.set_replay_speed("fast")  # host boundary: must not raise TypeError
    assert not ok and "positive integer" in err
    assert engine.replay_speed_multiplier == 1


def test_step_replay_kernel_path_pulses_step_event_when_paused() -> None:
    engine = DataEngine()
    _kernel_paused(engine)
    assert not engine.step_event.is_set()

    ok, err = engine.step_replay()
    assert ok and err is None
    assert engine.step_event.is_set(), "kernel-path step must pulse step_event (one bar)"


def test_step_replay_kernel_path_rejected_when_not_paused() -> None:
    engine = DataEngine()
    engine._replay_duckdb_root = "/synthetic/root"
    engine._replay_state = "RUNNING"  # not paused

    ok, err = engine.step_replay()
    assert not ok and "paused run" in err
    assert not engine.step_event.is_set(), "no pulse may be left while RUNNING (stale-step bug)"


def test_resume_clears_unconsumed_step_pulse() -> None:
    engine = DataEngine()
    _kernel_paused(engine)
    engine.step_replay()              # pulse set (runner not running in this unit test)
    assert engine.step_event.is_set()

    ok, _ = engine.resume_replay()    # PAUSED → RUNNING
    assert ok
    assert not engine.step_event.is_set(), "resume must drop a stale step pulse (#30 hygiene)"


def test_start_engine_resets_speed_and_clears_step_pulse() -> None:
    engine = DataEngine()
    # Simulate a prior run's residue.
    engine._replay_speed_multiplier = 50
    engine._step_event.set()
    # LOADED → start_engine.
    engine._replay_state = "LOADED"

    ok, _ = engine.start_engine()
    assert ok
    assert engine.replay_speed_multiplier == 1, "fresh run starts at 1x (footer default)"
    assert not engine.step_event.is_set(), "fresh run carries no stale step pulse"


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
