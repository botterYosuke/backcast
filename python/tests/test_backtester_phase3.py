"""#95 Phase 3 gates — the ``bt`` handle + KernelStepper extract (findings 0071).

The #24 golden / replay byte-identity after the extract is gated by the existing suites
(test_kernel_subprocess_matches_committed_golden, test_kernel_runner_production_seam,
test_v19_*). This file pins the NEW Phase 3 surface:

  (β) replay parity   — `for bar in bt.replay()` reproduces the imperative KernelRunner's
                        sink buffer byte-for-byte (done-gate 2),
  (γ) step-to-end parity — `while bt.step()` reproduces the same buffer (done-gate 3,
                        pins findings 0070 F4(A): step pushed to the end == replay),
  + stop_event seam preserve (4), bars_per_second no-op smoke (6), submit_market context-out
    fail-closed (7), bt.bar()/bt.portfolio() lifecycle rule over the 4 states (8), and the
    finalize()-before-terminal guard.

Pure-Python: bars are injected, so no DuckDB mount is needed.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.stepper import KernelStepper, StepEvent  # noqa: E402
from engine.kernel.strategy import Strategy as KernelStrategy  # noqa: E402
from engine.strategy_runtime.backtester import Backtester  # noqa: E402

_IID = "8918.TSE"
_CASH = 10_000_000.0
_SID = "S"


def _bars(closes: list[float]) -> list[Bar]:
    return [
        Bar(
            instrument_id=_IID,
            ts_event_ns=1_700_000_000_000_000_000 + i * 1_000_000_000,
            open=c, high=c + 1.0, low=c - 1.0, close=c, volume=10.0,
        )
        for i, c in enumerate(closes)
    ]


# A close path that forces a BUY (rising into a flat book) then a full SELL (falling while long):
# 100 → 101 (buy) → 102 (hold) → 101 (sell) → 100 (flat).
_CLOSES = [100.0, 101.0, 102.0, 101.0, 100.0]


class _Recorder:
    """RustBacktestSink-shaped push target: records the exact JSON the EventSink emits."""

    def __init__(self) -> None:
        self.events: list[tuple] = []

    def push_bar(self, payload: str) -> None:
        self.events.append(("bar", payload))

    def push_order(self, payload: str) -> None:
        self.events.append(("order", payload))

    def push_portfolio(self, payload: str) -> None:
        self.events.append(("portfolio", payload))

    def push_run_complete(self, run_id: str, payload: str) -> None:
        self.events.append(("complete", run_id, payload))


class _PrevCloseStrat(KernelStrategy):
    """Imperative twin: buy 100 on a rising bar when flat, flatten on a falling bar when long."""

    def on_start(self) -> None:
        self._prev: float | None = None

    def on_bar(self, bar: Bar) -> None:
        pf = self.portfolio_snapshot()
        if self._prev is not None:
            if bar.close > self._prev and pf.position == 0:
                self.submit_market(self.instrument_id, OrderSide.BUY, 100)
            elif bar.close < self._prev and pf.position > 0:
                self.submit_market(self.instrument_id, OrderSide.SELL, pf.position)
        self._prev = bar.close


def _run_imperative(monkeypatch, bars: list[Bar]) -> list[tuple]:
    rec = _Recorder()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    KernelRunner(
        data_root="/unused",
        instrument_ids=[_IID],
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=_CASH,
        strategy=_PrevCloseStrat(strategy_id=_SID, instrument_id=_IID),
        push_target=rec,
    ).run()
    return rec.events


def _make_bt(bars: list[Bar], rec: _Recorder, **kw) -> Backtester:
    stepper = KernelStepper(
        bars=list(bars),
        instrument_ids=[_IID],
        initial_cash=_CASH,
        strategy=None,        # the cell body is the strategy
        strategy_id=_SID,     # same id → same client_order_ids as the imperative twin
        push_target=rec,
        **kw,
    )
    return Backtester(stepper)


def _bt_body_replay(bt: Backtester, *, bars_per_second=None) -> None:
    """The same logic as _PrevCloseStrat, authored as a B2 replay cell."""
    prev: float | None = None
    for bar in bt.replay(bars_per_second=bars_per_second):
        pf = bt.portfolio()
        if prev is not None:
            if bar.close > prev and pf.position == 0:
                bt.submit_market(100)
            elif bar.close < prev and pf.position > 0:
                bt.submit_market(-pf.position)
        prev = bar.close


def _bt_body_step(bt: Backtester) -> None:
    """The same logic, authored as a B3 step-to-end loop."""
    prev: float | None = None
    while (bar := bt.step()) is not None:
        pf = bt.portfolio()
        if prev is not None:
            if bar.close > prev and pf.position == 0:
                bt.submit_market(100)
            elif bar.close < prev and pf.position > 0:
                bt.submit_market(-pf.position)
        prev = bar.close


# --------------------------------------------------------------------------------------
# (β) replay parity — done-gate 2
# --------------------------------------------------------------------------------------

def test_backtester_replay_byte_identical_to_kernel_runner(monkeypatch) -> None:
    bars = _bars(_CLOSES)
    imperative = _run_imperative(monkeypatch, bars)

    rec = _Recorder()
    _bt_body_replay(_make_bt(bars, rec))

    assert rec.events == imperative
    # sanity: the run actually traded (otherwise "byte-identical" could be a no-trade artifact)
    assert any(kind == "order" for kind, *_ in rec.events)


# --------------------------------------------------------------------------------------
# (γ) step-to-end parity — done-gate 3 (findings 0070 F4(A))
# --------------------------------------------------------------------------------------

def test_backtester_step_to_end_byte_identical_to_kernel_runner(monkeypatch) -> None:
    bars = _bars(_CLOSES)
    imperative = _run_imperative(monkeypatch, bars)

    rec = _Recorder()
    _bt_body_step(_make_bt(bars, rec))

    assert rec.events == imperative


def test_replay_and_step_produce_the_same_buffer() -> None:
    bars = _bars(_CLOSES)
    rec_replay = _Recorder()
    _bt_body_replay(_make_bt(bars, rec_replay))
    rec_step = _Recorder()
    _bt_body_step(_make_bt(bars, rec_step))
    assert rec_replay.events == rec_step.events


# --------------------------------------------------------------------------------------
# (4) stop_event seam preserve
# --------------------------------------------------------------------------------------

def test_pre_set_stop_event_streams_no_bars() -> None:
    import threading

    stop = threading.Event()
    stop.set()
    rec = _Recorder()
    stepper = _make_bt(_bars(_CLOSES), rec, stop_event=stop)._stepper

    handle = stepper.open_next_bar()
    assert handle.event is StepEvent.STOPPED
    assert handle.reason == "stopped"
    # No bar pushed; finalize is done and reports the early halt.
    assert all(kind != "bar" for kind, *_ in rec.events)
    result = stepper.finalize()
    assert result.bars == 0
    assert result.stopped_reason == "stopped"


def test_pre_set_stop_event_via_bt_handle() -> None:
    import threading

    stop = threading.Event()
    stop.set()
    rec = _Recorder()
    bt = _make_bt(_bars(_CLOSES), rec, stop_event=stop)
    assert bt.step() is None
    assert list(bt.replay()) == []


# --------------------------------------------------------------------------------------
# (6) bars_per_second no-op smoke — Phase 3 accepts the arg, inserts no sleep
# --------------------------------------------------------------------------------------

def test_bars_per_second_accepted_no_sleep() -> None:
    # Phase 3 (Q5 A1): bt.replay(bars_per_second=N) accepts + stores the arg but wires NO
    # pacing — the stepper's throttle (bar_interval_sec) stays 0, so the sleep path is never
    # active. Per-bar sleep(1/N) is Phase 4.
    bars = _bars(_CLOSES)
    bt = _make_bt(bars, _Recorder())
    streamed = list(bt.replay(bars_per_second=10))

    assert len(streamed) == len(bars)         # every bar streamed
    assert bt._bars_per_second == 10          # arg stored for Phase 4
    assert bt._stepper._bar_interval_sec == 0.0  # no pacing wired into the engine


# --------------------------------------------------------------------------------------
# (7) submit_market context-out fail-closed
# --------------------------------------------------------------------------------------

def test_submit_market_before_first_step_raises() -> None:
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    with pytest.raises(ValueError):
        bt.submit_market(100)


def test_submit_market_after_close_raises() -> None:
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    assert bt.step() is not None      # open bar 0
    bt._close_open_bar()              # Phase 2 hook closes it
    with pytest.raises(ValueError):
        bt.submit_market(100)         # no bar open after the explicit close


def test_submit_market_after_end_raises() -> None:
    bt = _make_bt(_bars([100.0]), _Recorder())
    assert bt.step() is not None      # bar 0
    assert bt.step() is None          # END (closes bar 0, finalizes)
    with pytest.raises(ValueError):
        bt.submit_market(100)


# --------------------------------------------------------------------------------------
# (8) bt.bar() / bt.portfolio() lifecycle rule — the 4 states (Q3-b)
# --------------------------------------------------------------------------------------

def test_lifecycle_states_bar_and_portfolio() -> None:
    bars = _bars(_CLOSES)
    bt = _make_bt(bars, _Recorder())

    # 未開始: no bar; aggregate snapshot (flat, empty positions, initial cash).
    assert bt.bar() is None
    pf0 = bt.portfolio()
    assert pf0.position == 0.0
    assert dict(pf0.positions) == {}
    assert pf0.cash == _CASH

    # BAR_OPEN: current bar; primary instrument = the open bar's instrument.
    bar0 = bt.step()
    assert bar0 is bars[0]
    assert bt.bar() is bars[0]
    assert bt.portfolio().position == 0.0  # nothing filled yet

    # buy on the next (rising) bar, then read the book on the bar after it.
    bar1 = bt.step()                       # closes bar 0, opens bar 1 (101 > 100)
    assert bar1 is bars[1]
    bt.submit_market(100)                  # BUY 100 @ bar1 close
    bar2 = bt.step()                       # closes bar 1 → fill; opens bar 2
    assert bt.bar() is bars[2]
    assert bt.portfolio().position == 100.0

    # close後 / 次step前: explicit close keeps the last bar as bt.bar(); submit now fails.
    bt._close_open_bar()
    assert bt.bar() is bars[2]
    assert bt.portfolio().position == 100.0
    with pytest.raises(ValueError):
        bt.submit_market(100)

    # drive to END; the last bar remains observable.
    while bt.step() is not None:
        pass
    assert bt.bar() is bars[-1]
    assert isinstance(bt.portfolio().position, float)


# --------------------------------------------------------------------------------------
# finalize() guard — only valid after END / STOPPED
# --------------------------------------------------------------------------------------

def test_finalize_before_terminal_raises() -> None:
    stepper = _make_bt(_bars(_CLOSES), _Recorder())._stepper
    stepper.open_next_bar()  # BAR_OPEN — run is live, not terminal
    with pytest.raises(RuntimeError):
        stepper.finalize()


def test_zero_qty_submit_is_noop_inside_open_bar() -> None:
    bt = _make_bt(_bars(_CLOSES), _Recorder())
    bt.step()              # open bar 0
    bt.submit_market(0)    # flat = no order (signed_qty_to_side → None)
    bt.submit_market(-0.0)
    # advance to settle: no fills should have been produced.
    rec_orders = [e for e in bt._stepper._ctx.pending]
    assert rec_orders == []


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
