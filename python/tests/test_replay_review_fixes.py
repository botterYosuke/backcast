"""#49 code-review fixes — RED→GREEN regression tests.

Covers the Medium+ findings from the #49 review:
  #1 granularity normalization on the DuckDB production path
  #4 EventSink declares on_equity (no silent getattr probe)
  #5 a running kernel replay halts promptly on a stop signal
  #3 the per-bar animation throttle is bounded (Minute runs don't sleep for minutes)
  #2 equity is mark-to-market (cash + open-position value), with cash reported separately

A synthetic per-symbol DuckDB is built in a temp dir so the WIRING runs in CI without the
owner's mount (data faithfulness is #47/#48's job, on the real DuckDB).
"""
from __future__ import annotations

import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import duckdb  # noqa: E402


def _build_synthetic_duckdb(root, *, symbol: str = "8918", n: int = 50) -> None:
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily ("
            "Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, Low BIGINT, "
            "Close BIGINT, Volume BIGINT)"
        )
        day0 = datetime.date(2024, 10, 1)
        rows = []
        for i in range(n):
            day = day0 + datetime.timedelta(days=i)
            base = 1000 + i
            rows.append((day, symbol, base, base + 5, base - 5, base + 2, 1000 + i))
        con.executemany("INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", rows)
    finally:
        con.close()


def _write_strategy(tmp_path, *, granularity: str, body_extra: str = "") -> str:
    """Write a kernel-native strategy file with the given SCENARIO granularity."""
    src = f'''
from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy

SCENARIO = {{
    "schema_version": 2,
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": {granularity!r},
    "initial_cash": 10_000_000,
}}


class ReviewFixStrategy(Strategy):
    def __init__(self, *, instrument_id: str = "8918.TSE") -> None:
        super().__init__(strategy_id="review-fix")
        self.instrument_id = instrument_id
        self.n_bars = 0

    def on_start(self) -> None:
        pass

    def on_bar(self, bar: Bar) -> None:
        self.n_bars += 1
{body_extra}
'''
    path = os.path.join(str(tmp_path), "review_fix_strategy.py")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(src)
    return path


# --- #1: granularity normalization ------------------------------------------
def test_duckdb_run_accepts_noncanonical_granularity(tmp_path) -> None:
    """A scenario granularity like 'daily' (lowercase) must run — the legacy catalog
    path normalized via normalize_granularity; the DuckDB path must too."""
    _build_synthetic_duckdb(tmp_path)
    strategy = _write_strategy(tmp_path, granularity="daily")  # lowercase

    from engine.core import DataEngine
    from engine._backend_impl import DataEngineBackend

    eng = DataEngine(duckdb_root=str(tmp_path))
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok, f"load_replay_data failed: {err}"
    result = DataEngineBackend(engine=eng).start_engine(strategy)

    assert result.success, (
        f"lowercase granularity should run, got {result.error_code}: {result.error_message}"
    )


# --- #4: EventSink declares on_equity (no silent getattr probe) --------------
def test_eventsink_declares_on_equity() -> None:
    """The golden EventSink must declare a (no-op) on_equity so the runner can call it
    unconditionally — a getattr probe silently drops the equity curve on a method typo."""
    from engine.kernel.sink import EventSink

    class _Target:
        def push_bar(self, s): pass
        def push_order(self, s): pass
        def push_portfolio(self, s): pass
        def push_run_complete(self, rid, s): pass

    sink = EventSink(_Target())
    assert hasattr(sink, "on_equity"), "EventSink must declare on_equity"
    # no-op: must accept the (ts_event_ms, equity, cash) call shape without raising/emitting
    sink.on_equity(0, 0.0, 0.0)


# --- #5: a running kernel replay halts promptly on a stop signal -------------
def test_kernel_run_breaks_on_stop_event(tmp_path) -> None:
    """force_stop must actually halt mid-run. The kernel loop checks an injected stop_event
    and breaks — a run_event(pause) alone can't signal stop (set==run, clear==pause)."""
    import threading

    _build_synthetic_duckdb(tmp_path, n=50)

    from engine.kernel.runner import KernelRunner
    from engine.kernel.strategy import Strategy
    from engine.kernel.duckdb_bars import Bar

    stop = threading.Event()
    seen = {"bars": 0}
    STOP_AFTER = 5

    class _Spy:
        def push_bar(self, bar):
            seen["bars"] += 1
            if seen["bars"] == STOP_AFTER:
                stop.set()
        def push_order(self, fill): pass
        def push_portfolio(self, pf): pass
        def push_run_complete(self, rid, summary): pass
        def on_equity(self, ts, eq, cash): pass

    class _Noop(Strategy):
        def __init__(self, *, instrument_id="8918.TSE"):
            super().__init__(strategy_id="noop")
        def on_start(self): pass
        def on_bar(self, bar: Bar): pass

    KernelRunner(
        data_root=str(tmp_path),
        instrument_ids=["8918.TSE"],
        granularity="Daily",
        start="2024-10-01",
        end="2025-01-10",
        initial_cash=10_000_000,
        strategy=_Noop(),
        sink=_Spy(),
        stop_event=stop,
    ).run()

    assert seen["bars"] == STOP_AFTER, (
        f"loop must break right after stop_event is set; processed {seen['bars']} of 50 bars"
    )


# --- #3: per-bar animation throttle is bounded ------------------------------
def test_throttle_total_is_bounded_for_many_bars(tmp_path, monkeypatch) -> None:
    """The 10ms/bar animation throttle must NOT scale to bar_count (a year of Minute bars
    would sleep for minutes). Total throttle sleep is budgeted regardless of bar count."""
    N = 1000  # all within the wide window below → 1000 streamed bars
    _build_synthetic_duckdb(tmp_path, n=N)

    slept = {"total": 0.0}
    import time as _time
    real_sleep = _time.sleep

    def _spy_sleep(secs):
        slept["total"] += secs
        # don't actually sleep the budgeted time in the test
    monkeypatch.setattr(_time, "sleep", _spy_sleep)

    from engine.kernel.runner import KernelRunner
    from engine.kernel.strategy import Strategy
    from engine.kernel.duckdb_bars import Bar

    class _Sink:
        def push_bar(self, b): pass
        def push_order(self, f): pass
        def push_portfolio(self, p): pass
        def push_run_complete(self, r, s): pass
        def on_equity(self, t, e, c): pass

    class _Noop(Strategy):
        def __init__(self, *, instrument_id="8918.TSE"):
            super().__init__(strategy_id="noop")
        def on_start(self): pass
        def on_bar(self, bar: Bar): pass

    KernelRunner(
        data_root=str(tmp_path),
        instrument_ids=["8918.TSE"],
        granularity="Daily",
        start="2024-10-01",
        end="2030-01-01",  # wide → all N bars stream
        initial_cash=10_000_000,
        strategy=_Noop(),
        sink=_Sink(),
        bar_interval_sec=0.01,  # unbounded would be N*0.01 = 10s
    ).run()

    assert slept["total"] <= 3.0, (
        f"throttle must be budget-bounded; slept {slept['total']:.2f}s for {N} bars "
        f"(unbounded would be {N * 0.01:.0f}s)"
    )


# --- #2: equity is mark-to-market; cash reported separately ------------------
def test_get_portfolio_equity_is_mark_to_market(tmp_path) -> None:
    """A strategy ending with an OPEN position: get_portfolio.equity must be
    cash + position×latest-close (mark-to-market), NOT just realized cash."""
    _build_synthetic_duckdb(tmp_path, n=50)
    # BUY 100 at bar 3 and never sell → 100 shares held at run end.
    body = "        if self.n_bars == 3:\n            self.submit_market(self.instrument_id, OrderSide.BUY, 100)"
    strategy = _write_strategy(tmp_path, granularity="Daily", body_extra=body)

    from engine.core import DataEngine
    from engine._backend_impl import DataEngineBackend

    eng = DataEngine(duckdb_root=str(tmp_path))
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok, err
    backend = DataEngineBackend(engine=eng)
    result = backend.start_engine(strategy)
    assert result.success, f"{result.error_code}: {result.error_message}"

    pf = backend.get_portfolio()
    # synthetic: bar i has close = (1000+i)+2; BUY at bar 3 (i=2, close=1004), last bar i=49 close=1051.
    buy_px, last_close, qty = 1004, 1051, 100
    expected_cash = 10_000_000 - qty * buy_px           # realized cash after the buy
    expected_equity = expected_cash + qty * last_close   # mark-to-market

    assert pf.equity > pf.cash, (
        f"equity must exceed cash while holding a position (MTM), got equity={pf.equity} cash={pf.cash}"
    )
    assert abs(pf.cash - expected_cash) < 1.0, f"cash={pf.cash} expected≈{expected_cash}"
    assert abs(pf.equity - expected_equity) < 1.0, f"equity={pf.equity} expected≈{expected_equity}"
