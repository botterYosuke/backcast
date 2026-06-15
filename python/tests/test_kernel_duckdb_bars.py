"""DuckDB direct-read bar source faithfulness (#47, ADR-0006).

The kernel's market-data source is the owner's J-Quants DuckDB read directly via `duckdb`
(no intermediate parquet catalog, no Nautilus). This pins the data-equivalence acceptance
criteria for the daily / single-instrument tracer (8918.TSE):

  - the read window has the expected 68 bars;
  - every bar's raw OHLCV + ts_event matches the frozen golden fixture (catalog-derived,
    mount-independent — survives the catalog removal in #50);
  - every bar matches the committed catalog parquet exactly, INCLUDING ts_event_ns — the
    #47 linchpin: DuckDB stores Date at midnight, so the reader must synthesize 15:30 JST
    or the timestamp drifts and the frozen golden FAILs (skip-if catalog absent);
  - the 5-digit LocalCode (89180) mixed into the daily file is excluded;
  - the DuckDB read path imports no `nautilus_trader` (import-purity for this path).

Real-data dependency: skipped where the owner's DuckDB root is not mounted (repo convention).
"""
from __future__ import annotations

import json
import os
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

import duckdb
import pytest

from datetime import datetime, time as dt_time
from zoneinfo import ZoneInfo

from engine.kernel.duckdb_bars import daily_db_path, db_path, load_bars
from spike.kernel_golden import scenario
from spike.kernel_golden.subprocess_util import run_python

_FIXTURE = os.path.join(_PYTHON_ROOT, "tests", "fixtures", "duckdb_bars_8918_daily_golden.json")
_DB_PATH = daily_db_path(scenario.DUCKDB_ROOT, scenario.INSTRUMENT)
_DB_PRESENT = scenario.DUCKDB_ROOT_CONFIGURED and _DB_PATH.exists()

# --- Minute (#48) ----------------------------------------------------------------
_MINUTE_INSTRUMENT = "8918.TSE"
_MINUTE_DATE = "2024-04-01"
_MINUTE_FIXTURE = os.path.join(
    _PYTHON_ROOT, "tests", "fixtures", "duckdb_bars_8918_minute_20240401_golden.json"
)
_JST = ZoneInfo("Asia/Tokyo")

pytestmark = pytest.mark.skipif(
    not _DB_PRESENT, reason=f"J-Quants DuckDB not mounted at {scenario.DUCKDB_ROOT}"
)


def _load_window() -> list:
    return load_bars(
        scenario.DUCKDB_ROOT, scenario.INSTRUMENT, start=scenario.START, end=scenario.END
    )


def _load_fixture() -> dict:
    with open(_FIXTURE, encoding="utf-8") as fh:
        return json.load(fh)


def test_window_bar_count() -> None:
    bars = _load_window()
    assert len(bars) == 68, f"expected 68 daily bars, got {len(bars)}"


def test_matches_frozen_fixture() -> None:
    """Durable regression: DuckDB read == frozen 68-bar golden (no catalog needed)."""
    bars = _load_window()
    fixture = _load_fixture()
    assert len(bars) == fixture["bar_count"]
    for i, (b, exp) in enumerate(zip(bars, fixture["bars"])):
        assert b.ts_event_ns == exp["ts_event_ns"], f"bar {i} ts_event_ns drift"
        assert b.open == exp["open"], f"bar {i} open"
        assert b.high == exp["high"], f"bar {i} high"
        assert b.low == exp["low"], f"bar {i} low"
        assert b.close == exp["close"], f"bar {i} close"
        assert b.volume == exp["volume"], f"bar {i} volume"


@pytest.mark.skipif(
    not os.path.isdir(scenario.CATALOG), reason="catalog fixture absent (removed in #50)"
)
def test_matches_catalog_including_ts_event() -> None:
    """Faithfulness vs the #24 catalog known-good, INCLUDING the ts_event linchpin."""
    from engine.kernel.bars import load_bars as load_catalog_bars

    duck = _load_window()
    cat = load_catalog_bars(
        scenario.CATALOG, scenario.INSTRUMENT, start=scenario.START, end=scenario.END
    )
    assert len(duck) == len(cat) == 68
    for i, (d, c) in enumerate(zip(duck, cat)):
        # ts_event_ns equality is the #47 linchpin (15:30 JST reproduction).
        assert d.ts_event_ns == c.ts_event_ns, f"bar {i} ts_event_ns: {d.ts_event_ns} != {c.ts_event_ns}"
        assert (d.open, d.high, d.low, d.close, d.volume) == (
            c.open, c.high, c.low, c.close, c.volume
        ), f"bar {i} OHLCV mismatch"


def test_five_digit_localcode_excluded() -> None:
    """The daily file mixes 4-digit Code and 5-digit LocalCode; only 4-digit is read."""
    con = duckdb.connect(str(_DB_PATH), read_only=True)
    try:
        five_digit = con.execute(
            "SELECT count(*) FROM stocks_daily WHERE length(Code)=5"
        ).fetchone()[0]
    finally:
        con.close()
    assert five_digit > 0, "fixture invariant: the daily file should contain LocalCode rows"

    bars = load_bars(scenario.DUCKDB_ROOT, scenario.INSTRUMENT)  # whole history, no window
    # All returned bars carry the requested 4-digit instrument; none leaked from LocalCode.
    assert all(b.instrument_id == scenario.INSTRUMENT for b in bars)
    con = duckdb.connect(str(_DB_PATH), read_only=True)
    try:
        four_digit = con.execute(
            "SELECT count(*) FROM stocks_daily WHERE Code = ?",
            [scenario.INSTRUMENT.split(".")[0]],
        ).fetchone()[0]
    finally:
        con.close()
    assert len(bars) == four_digit, "read must return exactly the 4-digit Code rows"


def _load_minute_window() -> list:
    return load_bars(
        scenario.DUCKDB_ROOT,
        _MINUTE_INSTRUMENT,
        start=_MINUTE_DATE,
        end=_MINUTE_DATE,
        granularity="Minute",
    )


def _oracle_minute_ts_ns(day: str, time_str: str) -> int:
    """Independent ts oracle = catalog-gen convention (jquants_loader.py:24): (h,m,59,999999) JST."""
    year, month, dom = (int(p) for p in day.split("-"))
    hour, minute = (int(p) for p in time_str.split(":"))
    from datetime import date as _date

    dt = datetime.combine(_date(year, month, dom), dt_time(hour, minute, 59, 999999), tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def test_minute_window_bar_count() -> None:
    bars = _load_minute_window()
    assert len(bars) == 119, f"expected 119 minute bars on {_MINUTE_DATE}, got {len(bars)}"


def test_minute_matches_frozen_fixture() -> None:
    """Durable data-equivalence: minute reader == frozen 119-bar golden (raw OHLCV + ts)."""
    bars = _load_minute_window()
    with open(_MINUTE_FIXTURE, encoding="utf-8") as fh:
        fixture = json.load(fh)
    assert len(bars) == fixture["bar_count"] == 119
    for i, (b, exp) in enumerate(zip(bars, fixture["bars"])):
        assert b.instrument_id == _MINUTE_INSTRUMENT, f"minute bar {i} instrument_id"
        assert b.ts_event_ns == exp["ts_event_ns"], f"minute bar {i} ts_event_ns drift"
        assert b.open == exp["open"], f"minute bar {i} open"
        assert b.high == exp["high"], f"minute bar {i} high"
        assert b.low == exp["low"], f"minute bar {i} low"
        assert b.close == exp["close"], f"minute bar {i} close"
        assert b.volume == exp["volume"], f"minute bar {i} volume"


def test_minute_ts_is_bar_end_convention() -> None:
    """Linchpin (daily 15:30 analogue): minute ts = (h,m,59,999999) JST bar-end, NOT '09:00' start."""
    from datetime import date as _date

    bars = _load_minute_window()
    # First labelled minute is 09:00 → ts must be 09:00:59.999999 JST (bar end).
    assert bars[0].ts_event_ns == _oracle_minute_ts_ns(_MINUTE_DATE, "09:00")
    # And NOT the bar-start instant 09:00:00 — guards against dropping the 59.999999s synthesis.
    bar_start_ns = int(
        datetime.combine(_date(2024, 4, 1), dt_time(9, 0, 0), tzinfo=_JST).timestamp()
        * 1_000_000_000
    )
    assert bars[0].ts_event_ns != bar_start_ns, "ts must be bar-end, not bar-start (00s)"
    # Last labelled minute is 15:00 → 15:00:59.999999 JST.
    assert bars[-1].ts_event_ns == _oracle_minute_ts_ns(_MINUTE_DATE, "15:00")


def test_minute_code_fallback_uses_localcode() -> None:
    """Minute files hold ONLY the 5-digit LocalCode; the reader must fall back to it (#48)."""
    minute_path = db_path(scenario.DUCKDB_ROOT, _MINUTE_INSTRUMENT, "Minute")
    con = duckdb.connect(str(minute_path), read_only=True)
    try:
        four_digit = con.execute(
            "SELECT count(*) FROM stocks_minute WHERE Code = ?",
            [_MINUTE_INSTRUMENT.split(".")[0]],
        ).fetchone()[0]
        five_digit = con.execute(
            "SELECT count(*) FROM stocks_minute WHERE length(Code) = 5"
        ).fetchone()[0]
    finally:
        con.close()
    assert four_digit == 0, "fixture invariant: minute file has no 4-digit Code"
    assert five_digit > 0, "fixture invariant: minute file has 5-digit LocalCode rows"
    bars = _load_minute_window()
    assert bars, "5-digit LocalCode fallback must return rows (else AC#1 fails)"
    assert all(b.instrument_id == _MINUTE_INSTRUMENT for b in bars)


def test_duckdb_read_path_is_nautilus_free() -> None:
    """Importing duckdb_bars and reading bars must not load the Nautilus Rust core."""
    source = (
        "import sys;"
        "sys.path.insert(0, %r);" % _PYTHON_ROOT
        + "from engine.kernel.duckdb_bars import load_bars, load_universe_bars;"
        "from spike.kernel_golden import scenario;"
        "load_bars(scenario.DUCKDB_ROOT, scenario.INSTRUMENT, start=scenario.START, end=scenario.END);"
        # #48: minute + universe paths must be nautilus-free too (AC#3).
        "load_bars(scenario.DUCKDB_ROOT, '8918.TSE', start='2024-04-01', end='2024-04-01', granularity='Minute');"
        "load_universe_bars(scenario.DUCKDB_ROOT, ['1301.TSE', '1305.TSE'], start='2024-01-04', end='2024-01-04', granularity='Minute');"
        "from spike.kernel_golden.purity import leaked_nautilus_modules;"
        "leaked = leaked_nautilus_modules(sys.modules);"
        "print('LEAKED:' + ','.join(leaked)) if leaked else print('PURE')"
    )
    proc = run_python(["-c", source], timeout=60)
    assert proc.returncode == 0, f"purity subprocess failed:\n{proc.stderr}"
    assert proc.stdout.strip() == "PURE", f"nautilus leaked into DuckDB path:\n{proc.stdout}"


if __name__ == "__main__":
    if not _DB_PRESENT:
        print(f"[KERNEL DUCKDB BARS SKIP] DuckDB not mounted at {scenario.DUCKDB_ROOT}")
        sys.exit(0)
    failures = []
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
            except AssertionError as exc:
                failures.append(f"{name}: {exc}")
            except Exception as exc:  # noqa: BLE001 — surface skips/errors in standalone run
                failures.append(f"{name}: {type(exc).__name__}: {exc}")
    if failures:
        print("[KERNEL DUCKDB BARS FAIL]")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("[KERNEL DUCKDB BARS PASS] DuckDB direct-read == frozen golden == catalog (ts incl.)")
