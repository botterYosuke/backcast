"""Unit gate (#30): KernelRunner's Replay-transport seams — step + dynamic speed.

Issue #30 adds interactive transport (play/pause/step/speed/stop) to the ONE per-bar
execution loop, extending the #49 injection seams (run_event/stop_event/bar_interval_sec):

  * step_event (threading.Event): while PAUSED (run_event cleared), one pulse advances
    EXACTLY ONE bar then re-blocks. Default None → golden path byte-identical.
  * speed_provider (callable → int multiplier): the per-bar throttle interval is read EACH
    bar as bar_interval_sec / multiplier, so a speed change takes effect mid-run. Default
    None → multiplier 1. The #49-review-#3 total-budget cap (_ANIM_BUDGET_SEC) is removed:
    #30 hands rate ownership to the user (findings 0023 §4(D)).

Pure-Python: load_universe_bars is monkeypatched so no DuckDB mount is needed.
"""
from __future__ import annotations

import os
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402


def _bars(n: int) -> list[Bar]:
    return [
        Bar(
            instrument_id="8918.TSE",
            ts_event_ns=1_700_000_000_000_000_000 + i * 1_000_000_000,
            open=100.0 + i, high=101.0 + i, low=99.0 + i, close=100.0 + i, volume=10.0,
        )
        for i in range(n)
    ]


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


def _make_runner(monkeypatch, *, sink, bars, **kw) -> KernelRunner:
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    return KernelRunner(
        data_root="/unused",
        instrument_ids=["8918.TSE"],
        start="2024-10-01",
        end="2025-01-10",
        initial_cash=10_000_000,
        strategy=Strategy(),  # inert: no orders
        sink=sink,
        **kw,
    )


def test_step_event_advances_exactly_one_bar_then_reblocks(monkeypatch) -> None:
    """PAUSED (run_event cleared): each step_event pulse advances EXACTLY one bar, then the
    loop re-blocks (run_event still clear). The AC's '1 バーだけ前進' is structural."""
    sink = _Recorder()
    run_ev = threading.Event()      # cleared → paused before bar 0
    step_ev = threading.Event()
    runner = _make_runner(
        monkeypatch, sink=sink, bars=_bars(5), run_event=run_ev, step_event=step_ev
    )

    done = threading.Event()
    threading.Thread(target=lambda: (runner.run(), done.set()), daemon=True).start()

    # Paused before bar 0: nothing streams.
    assert not done.wait(0.2)
    assert len(sink.bars) == 0

    # One pulse → exactly one bar, then re-block.
    step_ev.set()
    _wait_for(lambda: len(sink.bars) == 1)
    assert not done.wait(0.2)
    assert len(sink.bars) == 1, "a single step must advance exactly one bar (not 0, not 2+)"

    # A second pulse → exactly two.
    step_ev.set()
    _wait_for(lambda: len(sink.bars) == 2)
    assert not done.wait(0.2)
    assert len(sink.bars) == 2

    # Resume → the rest stream to completion.
    run_ev.set()
    assert done.wait(2.0)
    assert len(sink.bars) == 5


def test_stop_while_paused_breaks_promptly(monkeypatch) -> None:
    """force_stop while PAUSED: the paused gate polls stop_event and breaks (no extra bar)."""
    sink = _Recorder()
    run_ev = threading.Event()  # cleared → paused
    stop_ev = threading.Event()
    step_ev = threading.Event()
    runner = _make_runner(
        monkeypatch, sink=sink, bars=_bars(5),
        run_event=run_ev, stop_event=stop_ev, step_event=step_ev,
    )

    done = threading.Event()
    threading.Thread(target=lambda: (runner.run(), done.set()), daemon=True).start()

    assert not done.wait(0.2)
    assert len(sink.bars) == 0
    stop_ev.set()  # stop while paused
    assert done.wait(2.0), "paused gate must wake on stop_event and break"
    assert len(sink.bars) == 0, "stop while paused must not advance a bar"


def test_speed_provider_scales_per_bar_interval_dynamically(monkeypatch) -> None:
    """The per-bar throttle is read EACH bar as bar_interval_sec / multiplier, so a speed
    change takes effect mid-run (not frozen at construction). No total-budget cap."""
    sink = _Recorder()
    bars = _bars(6)

    mult = {"v": 1}
    runner = _make_runner(
        monkeypatch, sink=sink, bars=bars,
        bar_interval_sec=0.10,
        speed_provider=lambda: mult["v"],
    )

    slept: list[float] = []
    import time as _time
    # run() does `import time as _time` locally → binds the stdlib module; patch its sleep.
    monkeypatch.setattr(_time, "sleep", lambda s: slept.append(s))

    # Speed up DURING the 3rd bar via the push_bar side-channel; that bar's own post-bar
    # throttle already reads the new multiplier (per-bar read, not frozen at construction).
    orig_push = sink.push_bar
    def _push(bar):
        orig_push(bar)
        if len(sink.bars) == 3:
            mult["v"] = 10
    sink.push_bar = _push  # type: ignore[assignment]

    runner.run()

    assert len(slept) == 6
    # Bars 1–2 at 1x → 0.10s; bars 3–6 at 10x → 0.01s (mid-run change took effect).
    assert all(abs(s - 0.10) < 1e-9 for s in slept[:2]), slept
    assert all(abs(s - 0.01) < 1e-9 for s in slept[2:]), slept
    # Uncapped: total == 2*0.10 + 4*0.01 (the #49 _ANIM_BUDGET_SEC=2.0 cap is gone).
    assert abs(sum(slept) - (2 * 0.10 + 4 * 0.01)) < 1e-9


def test_no_step_no_speed_is_byte_identical(monkeypatch) -> None:
    """Golden safety: step_event=None, speed_provider=None, bar_interval_sec=0 → every bar
    streams with no sleep (the golden EventSink path is untouched)."""
    sink = _Recorder()
    import time as _time
    slept: list[float] = []
    monkeypatch.setattr(_time, "sleep", lambda s: slept.append(s))

    _make_runner(monkeypatch, sink=sink, bars=_bars(4)).run()

    assert len(sink.bars) == 4
    assert slept == [], "default (no throttle) path must not sleep"


def _wait_for(pred, timeout: float = 2.0) -> None:
    import time as _t
    deadline = _t.monotonic() + timeout
    while _t.monotonic() < deadline:
        if pred():
            return
        _t.sleep(0.01)


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
