"""engine.kernel.duckdb_bars — nautilus-free DuckDB direct-read bar source (#47).

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。kernel の bar 源を
catalog parquet リーダー（`engine.kernel.bars`）から **J-Quants DuckDB の直読み**へ置換する。
owner 所有の銘柄別 DuckDB（`<root>/stocks_daily/<code>.duckdb`・table `stocks_daily`）を
`duckdb` で直接クエリし、中間 parquet catalog を持たず・**nautilus を一切 import しない**経路で
kernel へ bar を渡す（import-purity は `run_kernel --assert-pure` / Mono teardown gate が担保）。

凍結 #24 golden との faithfulness（findings 0017）:
  - 値 = 生(raw) OHLCV（`Open/High/Low/Close/Volume` BIGINT）を float へ cast（catalog decode が
    8.0 等の float を golden に焼いているため一致させる）。`Adjustment*` 列は当面 NULL → 不使用。
  - 銘柄ID = `<code>.TSE`（master の市場は全て東証）。日足ファイルは 4 桁 Code（`8918`）と
    5 桁 LocalCode（`89180`）が混在するため、**要求 instrument と一致する 4 桁 Code 行のみ採用**し
    5 桁行は捨てる。
  - ts_event = `Date` の 15:30 Asia/Tokyo（= 東証クローズ）を UTC ns へ変換。DuckDB の `Date` は
    深夜 0:00 で格納されるため、**15:30 JST を合成しないと ts_event がズレて golden が FAIL する**
    （#47 の linchpin）。この規約は catalog 生成元 `engine/jquants_loader.py:14`
    `datetime.combine(d, time(15,30), Asia/Tokyo)` と bit 単位で同一。
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, time as dt_time
from pathlib import Path
from zoneinfo import ZoneInfo

import duckdb

# Daily-bar close convention, identical to engine/jquants_loader.py:14 (the catalog build
# source). 15:30 JST = TSE close; reproduced so DuckDB-sourced ts_event matches the frozen
# #24 golden's fill timestamps bit-for-bit.
_JST = ZoneInfo("Asia/Tokyo")
_DAILY_CLOSE = dt_time(15, 30)

_OHLCV_COLUMNS = "Date, Open, High, Low, Close, Volume"


@dataclass(frozen=True)
class Bar:
    """A single OHLCV bar. ts_event_ns is the market event time in nanoseconds (UTC)."""

    instrument_id: str
    ts_event_ns: int
    open: float
    high: float
    low: float
    close: float
    volume: float


def _symbol_of(instrument_id: str) -> str:
    """'8918.TSE' → '8918' (the 4-digit J-Quants Code)."""
    symbol = instrument_id.split(".")[0]
    if not symbol:
        raise ValueError(f"instrument_id has empty symbol: {instrument_id!r}")
    return symbol


def daily_db_path(data_root: str | Path, instrument_id: str) -> Path:
    """Per-symbol daily DuckDB file: <root>/stocks_daily/<code>.duckdb."""
    return Path(data_root) / "stocks_daily" / f"{_symbol_of(instrument_id)}.duckdb"


def _date_to_ts_event_ns(value: datetime) -> int:
    """DuckDB daily Date (midnight) → 15:30 Asia/Tokyo close → UTC nanoseconds."""
    dt = datetime.combine(value.date(), _DAILY_CLOSE, tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def load_bars(
    data_root: str | Path,
    instrument_id: str,
    *,
    start: str | None = None,
    end: str | None = None,
) -> list[Bar]:
    """Load Daily bars for one instrument from its J-Quants DuckDB, ts_event-ascending.

    `start` / `end` are inclusive 'YYYY-MM-DD' dates; the daily `Date` is stored at
    midnight, so an inclusive `Date <= end` keeps the end-date bar (mirrors the catalog
    oracle window). Only the 4-digit Code matching `instrument_id` is selected — the
    5-digit LocalCode rows in the same file are excluded (ADR-0006).
    """
    symbol = _symbol_of(instrument_id)
    db_path = daily_db_path(data_root, instrument_id)
    if not db_path.exists():
        raise FileNotFoundError(f"DuckDB daily file not found: {db_path}")

    clauses = ["Code = ?"]
    params: list[str] = [symbol]
    if start is not None:
        clauses.append("Date >= ?")
        params.append(start)
    if end is not None:
        clauses.append("Date <= ?")
        params.append(end)
    sql = (
        f"SELECT {_OHLCV_COLUMNS} FROM stocks_daily "
        f"WHERE {' AND '.join(clauses)} ORDER BY Date"
    )

    con = duckdb.connect(str(db_path), read_only=True)
    try:
        rows = con.execute(sql, params).fetchall()
    finally:
        con.close()

    return [
        Bar(
            instrument_id=instrument_id,
            ts_event_ns=_date_to_ts_event_ns(date_value),
            open=float(open_),
            high=float(high),
            low=float(low),
            close=float(close),
            volume=float(volume),
        )
        for (date_value, open_, high, low, close, volume) in rows
    ]
