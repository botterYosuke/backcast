"""Retirement gate (#76 S6b-β): the #30 Replay transport is gone; only force_stop survives.

ADR-0012's reactive execution model supersedes the play/pause/step/speed/stop affordance — a
reactive drain completes near-instantly (0.3s/50k), so scrubbing a run is obsolete. The footer
transport UI was removed and these seams were retired together:

  * inproc RPC surface (C#-facing): pause_replay / resume_replay / step_replay / set_replay_speed.
  * DataEngine engine methods + the run_event (pause) / step_event / speed multiplier state.
  * KernelRunner control seam: the run_event/step_event/speed_provider params and the pause/step
    gate + dynamic-speed throttle.

What SURVIVES (run-lifecycle teardown — NOT a user transport control):
  * DataEngine.force_stop_replay() (internal): _backend_impl calls it on run completion/abort.
  * KernelRunner's stop_event: a set stop_event breaks the per-bar loop promptly so a long run
    halts instead of streaming every remaining bar (force_stop teardown is load-bearing — a leaked
    mid-run kernel context kills the next run).

These structural assertions pin the retirement so a future change can't silently re-introduce the
transport surface (the contract is "reactive run is fire-and-forget; the only control is stop").
"""
from __future__ import annotations

import inspect
import os
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.core import DataEngine  # noqa: E402
from engine.inproc_server import InprocLiveServer  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402

_RETIRED_ENGINE = (
    "pause_replay", "resume_replay", "stop_replay", "step_replay",
    "set_replay_speed", "replay_speed_multiplier", "step_event", "run_event",
)
_RETIRED_RPC = ("pause_replay", "resume_replay", "step_replay", "set_replay_speed")
_RETIRED_RUNNER_PARAMS = ("run_event", "step_event", "speed_provider")


def test_engine_has_no_transport_methods() -> None:
    eng = DataEngine()
    for name in _RETIRED_ENGINE:
        assert not hasattr(eng, name), f"retired engine transport member still present: {name}"


def test_inproc_server_retires_user_transport_but_keeps_force_stop_teardown() -> None:
    # The inproc façade is the C#-facing production RPC surface. The four USER transport RPCs are
    # retired; force_stop_replay SURVIVES as run-lifecycle teardown (the C# host's Stop() calls it
    # to unblock the launcher's synchronous start_engine before closing the server).
    for name in _RETIRED_RPC:
        assert not hasattr(InprocLiveServer, name), f"retired transport RPC still exposed: {name}"
    assert hasattr(InprocLiveServer, "force_stop_replay"), "force_stop teardown RPC must survive"


def test_kernel_runner_drops_transport_params() -> None:
    params = set(inspect.signature(KernelRunner.__init__).parameters)
    for p in _RETIRED_RUNNER_PARAMS:
        assert p not in params, f"retired KernelRunner param still accepted: {p}"
    # stop_event survives (force_stop teardown).
    assert "stop_event" in params


def test_force_stop_replay_survives_as_internal_teardown() -> None:
    eng = DataEngine()
    ok, err = eng.force_stop_replay()
    assert ok and err is None
    assert eng.replay_stop_event.is_set(), "force_stop must signal the kernel run to halt"


class _Recorder:
    def __init__(self) -> None:
        self.bars: list[Bar] = []

    def push_bar(self, bar) -> None:
        self.bars.append(bar)

    def push_order(self, fill) -> None:  # pragma: no cover - inert strategy
        pass

    def push_portfolio(self, pf) -> None:  # pragma: no cover
        pass

    def on_equity(self, ts_ms: int, equity: float, cash: float) -> None:
        pass

    def push_run_complete(self, run_id, summary) -> None:
        pass


def _bars(n: int) -> list[Bar]:
    return [
        Bar(
            instrument_id="8918.TSE",
            ts_event_ns=1_700_000_000_000_000_000 + i * 1_000_000_000,
            open=100.0 + i, high=101.0 + i, low=99.0 + i, close=100.0 + i, volume=10.0,
        )
        for i in range(n)
    ]


def test_stop_event_breaks_a_running_loop_promptly(monkeypatch) -> None:
    """force_stop teardown still works: a set stop_event halts the per-bar loop before it streams
    every remaining bar (the run-lifecycle invariant that survives the transport retirement)."""
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: _bars(200))
    sink = _Recorder()
    stop_ev = threading.Event()
    stop_ev.set()  # request stop before the first bar
    runner = KernelRunner(
        data_root="/unused",
        instrument_ids=["8918.TSE"],
        start="2024-10-01",
        end="2025-01-10",
        initial_cash=10_000_000,
        strategy=Strategy(),  # inert
        sink=sink,
        stop_event=stop_ev,
    )
    runner.run()
    assert len(sink.bars) == 0, "a pre-set stop_event must break before streaming any bar"


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
