"""#95 Phase 4 gates — B2 ``bt.replay()`` pacing, production-observer parity, mid-run stop.

Phase 4 turns the Phase-3 seams into real behaviour (findings 0073):

  (P4-2) pacing      — ``bt.replay(bars_per_second=N)`` CAPTURES ``1/N`` into the stepper's
                       throttle at the START of the stream (immutable for the run, F6); ``None``
                       → full speed; a non-positive rate fails closed; the per-bar sleep fires.
  (P4-6) production parity — a ``bt.replay()`` run driven through the *production*
                       ``ReplayKernelObserver`` (engine chart + ``last_portfolio`` snapshots +
                       RunBuffer fills/equity) is byte-identical to the imperative ``KernelRunner``
                       driving the same observer — the production binding ADR-0016 Consequences asks
                       for (the Phase-3 β/γ tests only cover the EventSink push_target level).
  (stop) mid-run     — setting the stop_event DURING the stream halts it early with a finalize
                       (the cross-thread ⏹ path, simulated single-threaded here).

Pure-Python: bars are injected, so no DuckDB mount is needed. The Phase-3 helpers are reused.
"""
from __future__ import annotations

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.stepper import KernelStepper  # noqa: E402
from engine.strategy_runtime.backtester import Backtester  # noqa: E402

from test_backtester_phase3 import (  # noqa: E402  reuse the Phase-3 fixtures
    _CASH,
    _CLOSES,
    _IID,
    _SID,
    _PrevCloseStrat,
    _Recorder,
    _bars,
    _bt_body_replay,
    _make_bt,
)


# ======================================================================================
# (P4-2) pacing — start-captured 1/N, full-speed default, fail-closed, real per-bar sleep
# ======================================================================================

def test_pacing_captures_rate_at_stream_start() -> None:
    # The rate is captured the moment the generator starts (first next()), not stored for later
    # — speed is not a live register (F6). 4 bars/s → 0.25s per bar.
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    gen = bt.replay(bars_per_second=4)
    next(gen)
    assert bt._stepper._bar_interval_sec == pytest.approx(0.25)


def test_pacing_full_speed_when_unspecified() -> None:
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    list(bt.replay())  # no bars_per_second → full speed
    assert bt._stepper._bar_interval_sec == 0.0


def test_pacing_non_positive_rate_fails_closed() -> None:
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    with pytest.raises(ValueError):
        list(bt.replay(bars_per_second=0))
    with pytest.raises(ValueError):
        list(bt.replay(bars_per_second=-2))


def test_pacing_inserts_one_sleep_per_closed_bar(monkeypatch) -> None:
    # The stepper's throttle (close_current_bar tail) sleeps 1/N once per CLOSED bar. The first
    # open_next_bar closes nothing, so N bars → N sleeps of 1/rate. Patching the real clock keeps
    # the test instant while proving the sleep actually fires at the captured interval.
    slept: list[float] = []
    monkeypatch.setattr(time, "sleep", lambda s: slept.append(s))
    bars = _bars(_CLOSES)
    bt = _make_bt(bars, _Recorder())
    list(bt.replay(bars_per_second=2))
    assert slept == [0.5] * len(bars)


def test_full_speed_inserts_no_sleep(monkeypatch) -> None:
    slept: list[float] = []
    monkeypatch.setattr(time, "sleep", lambda s: slept.append(s))
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    list(bt.replay())  # full speed
    assert slept == []


# ======================================================================================
# (P4-6) production-observer parity — bt.replay() == imperative KernelRunner through the
#        SAME ReplayKernelObserver (engine chart + last_portfolio + RunBuffer)
# ======================================================================================

class _FakeEngine:
    """Captures everything the ReplayKernelObserver pushes to the engine: per-bar chart klines
    (apply_replay_event) and the atomic last_portfolio snapshots."""

    def __init__(self) -> None:
        self.klines: list[tuple] = []
        self.snapshots: list[dict] = []
        self._last_portfolio: dict | None = None

    def apply_replay_event(self, kline) -> None:
        self.klines.append(
            (kline.timestamp_ms, kline.open, kline.high, kline.low, kline.close,
             kline.instrument_id, kline.volume)
        )

    @property
    def last_portfolio(self):
        return self._last_portfolio

    @last_portfolio.setter
    def last_portfolio(self, value) -> None:
        self._last_portfolio = value
        if value is not None:
            self.snapshots.append(value)


class _FakeRunBuffer:
    """Captures the RunBuffer fill/equity writes (→ compute_portfolio → get_portfolio)."""

    def __init__(self) -> None:
        self.fills: list[dict] = []
        self.equities: list[dict] = []

    def write_fill(self, fill: dict) -> None:
        self.fills.append(fill)

    def write_equity(self, equity: dict) -> None:
        self.equities.append(equity)


def _drive_via_observer_imperative(monkeypatch, bars):
    from engine.strategy_runtime.replay_kernel_observer import ReplayKernelObserver

    eng, rb = _FakeEngine(), _FakeRunBuffer()
    observer = ReplayKernelObserver(engine=eng, run_buffer=rb)
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    KernelRunner(
        data_root="/unused",
        instrument_ids=[_IID],
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=_CASH,
        strategy=_PrevCloseStrat(strategy_id=_SID, instrument_id=_IID),
        sink=observer,
    ).run()
    return eng, rb


def _drive_via_observer_bt(bars):
    from engine.strategy_runtime.replay_kernel_observer import ReplayKernelObserver

    eng, rb = _FakeEngine(), _FakeRunBuffer()
    observer = ReplayKernelObserver(engine=eng, run_buffer=rb)
    stepper = KernelStepper(
        bars=list(bars),
        instrument_ids=[_IID],
        initial_cash=_CASH,
        strategy=None,       # the cell body is the strategy
        strategy_id=_SID,    # same id → same client_order_ids as the imperative twin
        sink=observer,
    )
    bt = Backtester(stepper)
    _bt_body_replay(bt)
    return eng, rb


def test_bt_replay_byte_identical_to_imperative_through_production_observer(monkeypatch) -> None:
    bars = _bars(_CLOSES)
    eng_a, rb_a = _drive_via_observer_imperative(monkeypatch, bars)
    eng_b, rb_b = _drive_via_observer_bt(bars)

    assert eng_a.klines == eng_b.klines           # per-bar chart stream identical
    assert eng_a.snapshots == eng_b.snapshots     # every running last_portfolio snapshot identical
    assert rb_a.fills == rb_b.fills               # RunBuffer fills identical
    assert rb_a.equities == rb_b.equities         # RunBuffer per-bar equity identical
    # And the run actually traded (guards against a no-op "parity" that proves nothing).
    assert len(rb_b.fills) == 2                   # BUY + SELL


# ======================================================================================
# (stop) mid-run stop — setting the event DURING the stream halts early with a finalize
# ======================================================================================

def test_stop_event_set_mid_stream_halts_early() -> None:
    import threading

    stop = threading.Event()
    bars = _bars([100.0, 101.0, 102.0, 103.0, 104.0, 105.0])
    bt = _make_bt(bars, _Recorder(), stop_event=stop)

    streamed = []
    for bar in bt.replay():
        streamed.append(bar)
        if len(streamed) == 2:
            stop.set()  # simulates the cross-thread ⏹ press

    # The stream stops well before the end: after the 2nd bar settles, the next open_next_bar sees
    # the set event and returns STOPPED. We saw fewer than all bars.
    assert 0 < len(streamed) < len(bars)
    assert bt._stepper.finalize().stopped_reason == "stopped"
