"""J-Quants listed-info DuckDB reader (Replay-mode instrument universe source).

The `+Add` picker's Replay supply: `<BACKCAST_JQUANTS_DUCKDB_ROOT>/listed_info.duckdb` carries
per-day point-in-time snapshots of the JPX listed-company master (Date / Code / CompanyName /
MarketCode / Sector*). For a scenario whose `replay_end_date = D`, the universe is the latest
snapshot with `Date <= D` — that gives a historically correct universe (listings/delistings
respected) without us having to roll our own activity intervals.

Per owner decision (2026-06-21): NO MarketCode filter — ETFs / REITs / TOKYO PRO are returned
alongside primary/standard/growth listings; downstream is free to filter. DuckDB unset/missing
returns `None` so the backend RPC can surface a typed error instead of a stale fallback
(the legacy `data/bar/` parquet scan was retired with this module — see _backend_impl.py).
"""
from __future__ import annotations

import logging
from datetime import date
from typing import NamedTuple

import duckdb

from .paths import jquants_listed_info_path


class ListedSymbolsSnapshot(NamedTuple):
    """`as_of_date` is the actual `Date` row returned (latest snapshot <= requested end_date).

    Empty `as_of_date` is the typed "no snapshot exists at-or-before end_date" signal — callers
    that need to distinguish "DB has snapshot with 0 listings" from "no snapshot at this date"
    look at `as_of_date` rather than `len(codes)`.

    `names` is parallel to `codes` (issue #46 — kabu Live picker UX): the listed_info
    `CompanyName` column. An individual entry may be the empty string when the source row had
    a NULL CompanyName; downstream callers fall back to the id for display so the picker
    always has a label.
    """

    codes: list[str]
    as_of_date: str
    names: list[str]


def _validate_iso_date(end_date: str) -> None:
    """Raise ValueError if `end_date` is not a strict YYYY-MM-DD.

    Uses `date.fromisoformat` — the project's canonical idiom (engine.signals._require_iso_date,
    engine.jquants_loader, engine.kernel.duckdb_bars tests). A non-empty string that fails this
    is a programmer error from a misconfigured caller; the backend RPC catches the ValueError
    and surfaces it as a typed result.
    """
    try:
        date.fromisoformat(end_date)
    except ValueError as exc:
        raise ValueError(f"end_date must be ISO date (YYYY-MM-DD), got {end_date!r}") from exc


def read_listed_snapshot(end_date: str | None) -> ListedSymbolsSnapshot | None:
    """Latest `listed_info` snapshot with `Date <= end_date`.

    Returns None when the DuckDB is unavailable (root unset or file missing) so the caller can
    distinguish "configuration error" from "empty universe". When `end_date` is empty/None, the
    overall MAX(Date) snapshot is returned. Raises ValueError on malformed `end_date`.
    """
    path = jquants_listed_info_path()
    if path is None:
        return None

    end_date = (end_date or "").strip()
    if end_date:
        _validate_iso_date(end_date)

    try:
        con = duckdb.connect(str(path), read_only=True)
    except duckdb.Error as exc:
        logging.warning("listed_info: connect failed: %s", exc)
        return None
    try:
        # Single CTE: pick MAX(Date) within bound, then list codes for that exact Date in one
        # round-trip. Empty `end_date` falls back to the overall MAX (no upper bound).
        if end_date:
            cte = "SELECT MAX(Date) AS as_of FROM listed_info WHERE Date <= ?"
            params = [end_date]
        else:
            cte = "SELECT MAX(Date) AS as_of FROM listed_info"
            params = []
        rows = con.execute(
            f"""
            WITH bound AS ({cte})
            SELECT (SELECT as_of FROM bound) AS as_of, li.Code, li.CompanyName
            FROM listed_info li
            WHERE li.Date = (SELECT as_of FROM bound)
            ORDER BY li.Code
            """,
            params,
        ).fetchall()
    finally:
        con.close()

    if not rows:
        # `(SELECT as_of FROM bound)` returned NULL → no snapshot at-or-before `end_date`.
        return ListedSymbolsSnapshot(codes=[], as_of_date="", names=[])
    as_of = rows[0][0]
    codes = [str(r[1]) for r in rows]
    names = [str(r[2]) if r[2] is not None else "" for r in rows]
    return ListedSymbolsSnapshot(codes=codes, as_of_date=str(as_of), names=names)
