"""#95 Phase 5 (#98) gates — B3 step persistence + NoScenarioBacktester (findings 0074).

Phase 3 (test_backtester_phase3) pinned: replay parity, step-to-end parity, stop_event seam,
bars_per_second no-op, submit_market context-out, lifecycle states, finalize-before-terminal.

Phase 4 (test_backtester_phase4 / test_notebook_replay_afk) pinned: production observer wiring,
on_run_begin firing, disarmed handle, pacing capture / sleep insertion, cross-thread stop.

Phase 5 (findings 0074) adds the B3 ``bt.step()`` reset/idempotency rules ADR-0016 D3 fully
spelled out but Phases 3/4 intentionally deferred (Phase 4 finding §5):

  - ``NoScenarioBacktester`` fail-closes every cell-facing operation with a guidance message;
    ``_close_open_bar`` / ``arm`` / ``disarm`` stay safe no-ops (Phase 2 finally compatibility +
    NotebookSession.apply_inject uniformity).

The B3 step PERSISTENCE / SCENARIO-RESET semantics — that the host (DataEngineBackend) caches
a step bt across presses, rebuilds it on a different scenario commit, and tears it down at
terminal — are gated by the AFK e2e (test_notebook_step_afk).
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

from engine.strategy_runtime.backtester import NoScenarioBacktester  # noqa: E402


# ----------------------------------------------------------------------------------------
# Q5 — NoScenarioBacktester fail-closed on every cell-facing operation
# ----------------------------------------------------------------------------------------

@pytest.mark.parametrize(
    "method, args",
    [
        ("step", ()),
        ("bar", ()),
        ("portfolio", ()),
        ("submit_market", (100.0,)),
    ],
)
def test_no_scenario_backtester_methods_fail_closed(method: str, args: tuple) -> None:
    bt = NoScenarioBacktester()
    with pytest.raises(RuntimeError) as excinfo:
        getattr(bt, method)(*args)
    msg = str(excinfo.value)
    # The guidance message tells the user how to recover.
    assert "commit the startup panel" in msg, msg
    # The method name is in the error so cell output identifies which call tripped it.
    assert f"bt.{method}(" in msg, msg


def test_no_scenario_backtester_replay_fails_on_call_not_on_iteration() -> None:
    """``replay()`` is a regular function (not a generator-with-yield) — so the error fires at
    call time and ``for bar in bt.replay():`` never enters the loop body (no observation
    against an undefined engine state)."""
    bt = NoScenarioBacktester()
    with pytest.raises(RuntimeError) as excinfo:
        bt.replay()
    assert "commit the startup panel" in str(excinfo.value)
    assert "bt.replay(" in str(excinfo.value)


def test_no_scenario_backtester_close_open_bar_is_noop() -> None:
    """The Phase 2 per-cell-RUN ``finally`` calls ``bt._close_open_bar()`` unconditionally; on
    the placeholder it must NOT raise (no bar is open to close)."""
    bt = NoScenarioBacktester()
    bt._close_open_bar()
    bt._close_open_bar()  # idempotent — calling twice is still a no-op


def test_no_scenario_backtester_arm_disarm_are_noops() -> None:
    """``NotebookSession._apply_inject`` calls ``arm`` / ``disarm`` on the injected bt uniformly
    (so it works for both real ``Backtester`` and the placeholder)."""
    bt = NoScenarioBacktester()
    bt.arm()
    bt.disarm()
    bt.arm()  # idempotent in either direction


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
