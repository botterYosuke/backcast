"""Regression gate (#58): a no-trade day (OHLCV=0 bar) must NOT crash a Replay run.

J-Quants DuckDB records a full market halt (e.g. 2020-10-01, the TSE system outage that
suspended trading in every name all day) as a single row with Open=High=Low=Close=Volume=0.
When that raw bar streams to the reducer, ``OhlcPoint``'s ``open/high/low/close > 0``
invariant rejects it, the kernel run halts mid-stream, and ``start_engine`` returns
``RUN_FAILED`` (4 validation errors for OhlcPoint). See the issue body and findings 0023 §6.

Behavioural invariant (approach-agnostic — holds whether the fix SKIPs the bar at the
loader or CARRY-FORWARDs the prior close at the reducer):

  - the run completes (``result.success`` — no RUN_FAILED / ValidationError halt);
  - no zero-priced candle leaks into ``ohlc_points`` (a no-trade day must never surface
    open/high/low/close == 0);
  - the surrounding real bars still stream and the strategy still trades.

The gate drives the production path end to end on a synthetic per-symbol DuckDB whose only
abnormal row is one all-zero bar in the middle of the window (the 2020-10-01 analogue), so
it runs in CI without the owner's mount. Modelled on test_replay_duckdb_kernel_afk.py.

Authored RED under ``xfail(strict=True)`` (it reproduced the live RUN_FAILED); now enforcing.
GREEN as of the #58 fix: no-trade days (OHLCV all zero) are dropped at the loader
(engine.kernel.duckdb_bars.is_no_trade_bar / load_bars) and skipped at priming
(core._prime_provider_locked), so the run completes from any entry point.
"""
from __future__ import annotations

import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import duckdb  # noqa: E402
import pytest  # noqa: E402

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_STRATEGY = os.path.join(
    _PYTHON_ROOT, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"
)
_N_BARS = 50  # > SELL_AT_BAR (40) so both legs of the golden twin fill
_HALT_INDEX = 10  # the no-trade day sits well before the BUY (bar 3 fills earlier; SELL at 40)


def _build_duckdb_with_halt_day(root, *, symbol: str = "8918", n: int = _N_BARS) -> None:
    """Write <root>/stocks_daily/<symbol>.duckdb with `n` ascending daily bars, one of which
    (index _HALT_INDEX) is an all-zero no-trade day — the J-Quants market-halt encoding."""
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
            if i == _HALT_INDEX:
                rows.append((day, symbol, 0, 0, 0, 0, 0))  # full market halt: OHLCV all zero
            else:
                base = 1000 + i
                rows.append((day, symbol, base, base + 5, base - 5, base + 2, 1000 + i))
        con.executemany("INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", rows)
    finally:
        con.close()


def _run_e2e(root):
    from engine.core import DataEngine
    from engine._backend_impl import DataEngineBackend

    eng = DataEngine(duckdb_root=str(root))
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok, f"load_replay_data failed: {err}"
    result = DataEngineBackend(engine=eng).start_engine(_STRATEGY)
    return result, eng


def test_no_trade_day_does_not_crash_replay_run(tmp_path) -> None:
    _build_duckdb_with_halt_day(tmp_path)
    result, eng = _run_e2e(tmp_path)

    # 1) the run completes — a no-trade day must not halt the kernel with RUN_FAILED.
    assert result.success, (
        f"no-trade day crashed the run: {result.error_code} {result.error_message}"
    )

    # 2) no zero-priced candle leaks (skip OR carry-forward — both must avoid a 0 OHLC bar).
    for pt in eng._rs.ohlc_points:
        assert min(pt.open, pt.high, pt.low, pt.close) > 0, (
            f"a zero-priced candle leaked into the chart: {pt}"
        )

    # 3) the surrounding real bars still streamed and the strategy still traded both legs.
    assert len(eng._rs.ohlc_points) >= _N_BARS - 1
    pf = eng.last_portfolio
    assert pf is not None and len(pf["orders"]) == 2, (
        f"expected BUY+SELL fills around the halt day, got {pf and pf.get('orders')}"
    )


def test_loader_drops_all_zero_day_but_keeps_partial_corruption_loud(tmp_path) -> None:
    """The loader drops ONLY the canonical no-trade signature (OHLCV all zero). A partially
    corrupt row (close==0 but volume>0) is KEPT so it still surfaces downstream — fail-loud."""
    from engine.kernel.duckdb_bars import load_bars

    d = os.path.join(str(tmp_path), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, "8918.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily (Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, "
            "Low BIGINT, Close BIGINT, Volume BIGINT)"
        )
        con.executemany(
            "INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)",
            [
                (datetime.date(2024, 10, 1), "8918", 1000, 1005, 995, 1002, 1000),  # normal
                (datetime.date(2024, 10, 2), "8918", 0, 0, 0, 0, 0),  # no-trade → dropped
                (datetime.date(2024, 10, 3), "8918", 0, 0, 0, 0, 500),  # corrupt → KEPT (loud)
                (datetime.date(2024, 10, 4), "8918", 1010, 1015, 1005, 1012, 1100),  # normal
            ],
        )
    finally:
        con.close()

    bars = load_bars(str(tmp_path), "8918.TSE", granularity="Daily")
    closes = [b.close for b in bars]
    # The all-zero day is gone; the partial-corruption (close=0, volume>0) row remains.
    assert closes == [1002.0, 0.0, 1012.0], closes


def test_minute_no_trade_day_also_dropped(tmp_path) -> None:
    """The drop is granularity-agnostic: a no-trade Minute bar is dropped too."""
    from engine.kernel.duckdb_bars import load_bars

    d = os.path.join(str(tmp_path), "stocks_minute")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, "8918.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_minute (Date DATE, Time VARCHAR, Code VARCHAR, Open DOUBLE, "
            "High DOUBLE, Low DOUBLE, Close DOUBLE, Volume DOUBLE)"
        )
        con.executemany(
            "INSERT INTO stocks_minute VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            [
                (datetime.date(2024, 4, 1), "09:00", "8918", 1000.0, 1005.0, 995.0, 1002.0, 10.0),
                (datetime.date(2024, 4, 1), "09:01", "8918", 0.0, 0.0, 0.0, 0.0, 0.0),  # dropped
            ],
        )
    finally:
        con.close()

    bars = load_bars(str(tmp_path), "8918.TSE", granularity="Minute")
    assert [b.close for b in bars] == [1002.0]


def test_priming_skips_leading_no_trade_day() -> None:
    """The ctor-injected provider seam (core.py:137) also guards against a no-trade first bar:
    priming must skip leading OHLCV-all-zero ticks and prime from the first real bar (#58)."""
    from engine.core import DataEngine
    from engine.replay import BaseReplayProvider

    class _Provider(BaseReplayProvider):
        # First tick is a no-trade day (all zero); priming must skip to the real second tick.
        def __init__(self) -> None:
            self._ticks = [
                (1.0, 0.0, 0.0, 0.0, 0.0),  # no-trade first day
                (2.0, 100.0, 105.0, 95.0, 102.0),  # first real bar
            ]
            self._i = 0

        def get_next_tick(self):
            if self._i >= len(self._ticks):
                return None
            tick = self._ticks[self._i]
            self._i += 1
            return tick

        def is_exhausted(self) -> bool:
            return self._i >= len(self._ticks)

    eng = DataEngine(replay_provider=_Provider())  # must NOT raise ValidationError
    # Primed from the first REAL bar (close 102 at ts 2s), not the skipped no-trade day.
    assert eng._rs.price == 102.0
    assert len(eng._rs.ohlc_points) == 1
    assert eng._rs.ohlc_points[0].close == 102.0
    assert eng._rs.timestamp_ms == 2000


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
