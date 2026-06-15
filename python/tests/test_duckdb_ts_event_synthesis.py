"""Mount-independent regression for the DuckDB ts_event synthesis (#47/#48 linchpin).

`test_kernel_duckdb_bars.py` pins the 15:30-JST daily close (and the minute bar-end)
convention against the owner's real DuckDB, but that whole module is skipped where the
DuckDB root is not mounted (CI / fresh checkout). After #50 (ADR-0006) removed the catalog
pyarrow reader, the equivalence test that compared the synthesis against the catalog was
dropped — so this test pins the same linchpin **without any mount**: it builds a tiny
per-symbol DuckDB in tmp_path and asserts the synthesized `ts_event_ns`.

The convention mirrors `engine/jquants_loader.py` (the catalog-gen source) exactly:
  - Daily: Date(midnight) → 15:30 Asia/Tokyo (TSE close) → UTC ns.
  - Minute: Date + Time('HH:MM' start label) → (h, m, 59, 999999) JST (bar end) → UTC ns.
"""
from __future__ import annotations

from datetime import date, datetime, time as dt_time
from pathlib import Path
from zoneinfo import ZoneInfo

import duckdb

from engine.kernel.duckdb_bars import load_bars

_JST = ZoneInfo("Asia/Tokyo")


def _expected_daily_ns(day: str) -> int:
    dt = datetime.combine(date.fromisoformat(day), dt_time(15, 30), tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def _expected_minute_ns(day: str, time_str: str) -> int:
    h, m = (int(p) for p in time_str.split(":"))
    dt = datetime.combine(date.fromisoformat(day), dt_time(h, m, 59, 999999), tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def _write_daily_db(root: Path, symbol: str, rows: list[tuple]) -> None:
    db = root / "stocks_daily"
    db.mkdir(parents=True, exist_ok=True)
    con = duckdb.connect(str(db / f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily (Code VARCHAR, Date DATE, Open BIGINT, "
            "High BIGINT, Low BIGINT, Close BIGINT, Volume BIGINT)"
        )
        con.executemany(
            "INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)",
            [(symbol, *r) for r in rows],
        )
    finally:
        con.close()


def _write_minute_db(root: Path, symbol: str, rows: list[tuple]) -> None:
    db = root / "stocks_minute"
    db.mkdir(parents=True, exist_ok=True)
    con = duckdb.connect(str(db / f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_minute (Code VARCHAR, Date DATE, Time VARCHAR, "
            "Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume DOUBLE)"
        )
        con.executemany(
            "INSERT INTO stocks_minute VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            [(symbol, *r) for r in rows],
        )
    finally:
        con.close()


def test_daily_ts_event_is_1530_jst_close(tmp_path: Path) -> None:
    _write_daily_db(tmp_path, "9999", [("2024-10-03", 10, 12, 9, 11, 500)])
    bars = load_bars(str(tmp_path), "9999.TSE", granularity="Daily")
    assert len(bars) == 1
    assert bars[0].ts_event_ns == _expected_daily_ns("2024-10-03")
    # raw OHLCV pass-through (cast to float)
    assert (bars[0].open, bars[0].close, bars[0].volume) == (10.0, 11.0, 500.0)


def test_minute_ts_event_is_bar_end_jst(tmp_path: Path) -> None:
    _write_minute_db(tmp_path, "9999", [("2024-10-03", "09:00", 10.0, 12.0, 9.0, 11.0, 7.0)])
    bars = load_bars(str(tmp_path), "9999.TSE", start="2024-10-03", end="2024-10-03",
                     granularity="Minute")
    assert len(bars) == 1
    # 09:00 start label resolves to 09:00:59.999999 JST (bar end), not 09:00:00.
    assert bars[0].ts_event_ns == _expected_minute_ns("2024-10-03", "09:00")
