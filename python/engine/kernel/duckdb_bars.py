"""engine.kernel.duckdb_bars — nautilus-free DuckDB direct-read bar source (#47, #48).

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。kernel の bar 源を
catalog parquet リーダー（`engine.kernel.bars`）から **J-Quants DuckDB の直読み**へ置換する。
owner 所有の銘柄別 DuckDB（`<root>/<table>/<code>.duckdb`）を `duckdb` で直接クエリし、中間
parquet catalog を持たず・**nautilus を一切 import しない**経路で kernel へ bar を渡す
（import-purity は `run_kernel --assert-pure` / Mono teardown gate が担保）。

#47（日足・単一銘柄, findings 0017）に #48 で **分足**と**複数銘柄 universe**を同経路で追加:

- 粒度（granularity）= `"Daily"` / `"Minute"`。table 名 == サブディレクトリ名（`stocks_daily` /
  `stocks_minute`）。ファイルは両粒度とも 4 桁 symbol で命名（`8918.duckdb`）。
- ts_event 規約（findings 0017 §2 / 0018 の linchpin・catalog 生成元 `engine/jquants_loader.py`）:
  - 日足: `Date`(midnight) → **15:30 Asia/Tokyo**（東証クローズ, jquants_loader.py:14）→ UTC ns。
  - 分足: `Date`(DATE) + `Time`(VARCHAR `'09:00'`=bar 開始ラベル) → **(h, m, 59, 999999) JST**
    （bar 終了, jquants_loader.py:24）→ UTC ns。DuckDB の `Time` は bar 開始ラベルなので reader が
    59.999999 秒を合成して bar end へ寄せる。
- 値 = 生(raw) OHLCV を float へ cast（日足は BIGINT・分足は DOUBLE）。`Adjustment*` 列は不使用。
- 銘柄行選択（#48 grill・両対応フォールバック）: 要求 instrument の symbol に対し **4 桁 Code を優先**
  （日足は 4 桁 `8918` を採用＝凍結 golden 維持）。4 桁が無ければ **5 桁 LocalCode**
  （`length(Code)=5 AND substr(Code,1,4)=symbol`・分足は `89180` のみ存在）へフォールバック。
  日足の 4 桁/5 桁混在ファイルでは 4 桁が勝つので二重計上しない。5 桁候補が複数あるのに 4 桁が無い
  曖昧ケースは fail-closed（`ValueError`）。
- 複数銘柄 universe: 各銘柄を読み `merge_bars_by_ts` で **時刻順に stable マージ**（同一 ts は入力＝
  universe 順を保持）して kernel へ単一ストリームで供給する。
"""
from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from datetime import date as date_cls, datetime, time as dt_time
from pathlib import Path
from zoneinfo import ZoneInfo

import duckdb

# Close-of-period conventions, identical to engine/jquants_loader.py (the catalog build
# source). Reproduced so DuckDB-sourced ts_event matches the frozen golden / catalog-gen
# convention bit-for-bit (the #47 daily linchpin and its #48 minute analogue).
_JST = ZoneInfo("Asia/Tokyo")
_DAILY_CLOSE = dt_time(15, 30)  # TSE close (jquants_loader.py:14)


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


def _date_to_ts_event_ns(value: datetime | date_cls) -> int:
    """Daily Date (midnight) → 15:30 Asia/Tokyo close → UTC nanoseconds."""
    day = value.date() if isinstance(value, datetime) else value
    dt = datetime.combine(day, _DAILY_CLOSE, tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def _minute_to_ts_event_ns(value: datetime | date_cls, time_str: str) -> int:
    """Minute Date + Time('HH:MM' start label) → (h, m, 59, 999999) JST end → UTC ns.

    Mirrors engine/jquants_loader.py:24 — the bar's labelled start minute resolved to its
    last microsecond (bar end), the minute analogue of the daily 15:30 close convention.
    """
    day = value.date() if isinstance(value, datetime) else value
    hour, minute = (int(part) for part in time_str.split(":"))
    dt = datetime.combine(day, dt_time(hour, minute, 59, 999999), tzinfo=_JST)
    return int(dt.timestamp() * 1_000_000_000)


def _daily_row_to_bar(instrument_id: str, row: tuple) -> Bar:
    date_value, open_, high, low, close, volume = row
    return Bar(
        instrument_id=instrument_id,
        ts_event_ns=_date_to_ts_event_ns(date_value),
        open=float(open_),
        high=float(high),
        low=float(low),
        close=float(close),
        volume=float(volume),
    )


def _minute_row_to_bar(instrument_id: str, row: tuple) -> Bar:
    date_value, time_str, open_, high, low, close, volume = row
    return Bar(
        instrument_id=instrument_id,
        ts_event_ns=_minute_to_ts_event_ns(date_value, time_str),
        open=float(open_),
        high=float(high),
        low=float(low),
        close=float(close),
        volume=float(volume),
    )


@dataclass(frozen=True)
class _Granularity:
    """Per-granularity read config. `name` doubles as the table name and the subdirectory."""

    name: str
    columns: str  # SELECT list, order matched by the row→Bar builder
    order_by: str
    row_to_bar: Callable[[str, tuple], Bar]


_GRANULARITIES: dict[str, _Granularity] = {
    "Daily": _Granularity(
        name="stocks_daily",
        columns="Date, Open, High, Low, Close, Volume",
        order_by="Date",
        row_to_bar=_daily_row_to_bar,
    ),
    "Minute": _Granularity(
        name="stocks_minute",
        columns="Date, Time, Open, High, Low, Close, Volume",
        order_by="Date, Time",
        row_to_bar=_minute_row_to_bar,
    ),
}


def _granularity(granularity: str) -> _Granularity:
    try:
        return _GRANULARITIES[granularity]
    except KeyError:
        raise ValueError(
            f"unknown granularity {granularity!r}; expected one of {sorted(_GRANULARITIES)}"
        ) from None


def db_path(
    data_root: str | Path, instrument_id: str, granularity: str = "Daily"
) -> Path:
    """Per-symbol DuckDB file: <root>/<table>/<code>.duckdb (table per granularity).

    Rejects an empty/unset data_root loudly (#48 review): an empty root would silently
    resolve to a RELATIVE path (e.g. stocks_daily/8918.duckdb) that could match an unrelated
    file under cwd. The per-machine root comes from .env (BACKCAST_JQUANTS_DUCKDB_ROOT) and is
    always absolute when set, so an empty value means "not configured" — fail rather than guess.
    """
    root = str(data_root).strip()
    if not root or root == ".":
        raise ValueError(
            "DuckDB data_root is not configured; set BACKCAST_JQUANTS_DUCKDB_ROOT in .env"
        )
    g = _granularity(granularity)
    return Path(root) / g.name / f"{_symbol_of(instrument_id)}.duckdb"


def daily_db_path(data_root: str | Path, instrument_id: str) -> Path:
    """Back-compat alias for the daily file path (#47 callers)."""
    return db_path(data_root, instrument_id, "Daily")


def _resolve_code(con: duckdb.DuckDBPyConnection, table: str, symbol: str) -> str | None:
    """Pick the single Code series for `symbol`: prefer 4-digit, else 5-digit LocalCode.

    Returns the chosen Code string, or None when the file has no rows for the symbol.
    Raises when no 4-digit Code exists and the 5-digit LocalCode is ambiguous (fail-closed).
    """
    rows = con.execute(
        f"SELECT DISTINCT Code FROM {table} "
        f"WHERE Code = ? OR (length(Code) = 5 AND substr(Code, 1, 4) = ?)",
        [symbol, symbol],
    ).fetchall()
    codes = {row[0] for row in rows}
    if symbol in codes:
        return symbol  # 4-digit Code preferred (daily golden convention, #47)
    five_digit = sorted(code for code in codes if len(code) == 5)
    if len(five_digit) == 1:
        return five_digit[0]  # LocalCode fallback (minute has only this, #48)
    if not five_digit:
        return None
    raise ValueError(
        f"ambiguous LocalCode for symbol {symbol!r} in {table}: {five_digit}"
    )


def load_bars(
    data_root: str | Path,
    instrument_id: str,
    *,
    start: str | None = None,
    end: str | None = None,
    granularity: str = "Daily",
) -> list[Bar]:
    """Load bars for one instrument from its J-Quants DuckDB, ts_event-ascending.

    `start` / `end` are inclusive 'YYYY-MM-DD' dates compared against the `Date` column.
    The instrument's row series is selected by `_resolve_code` (4-digit Code preferred,
    5-digit LocalCode fallback — #48 grill). Returns [] when the file exists but holds no
    rows for the symbol; raises FileNotFoundError when the file is missing.
    """
    g = _granularity(granularity)
    symbol = _symbol_of(instrument_id)
    path = db_path(data_root, instrument_id, granularity)
    if not path.exists():
        raise FileNotFoundError(f"DuckDB {granularity} file not found: {path}")

    con = duckdb.connect(str(path), read_only=True)
    try:
        chosen = _resolve_code(con, g.name, symbol)
        if chosen is None:
            return []
        clauses = ["Code = ?"]
        params: list[str] = [chosen]
        if start is not None:
            clauses.append("Date >= ?")
            params.append(start)
        if end is not None:
            clauses.append("Date <= ?")
            params.append(end)
        sql = (
            f"SELECT {g.columns} FROM {g.name} "
            f"WHERE {' AND '.join(clauses)} ORDER BY {g.order_by}"
        )
        rows = con.execute(sql, params).fetchall()
    finally:
        con.close()

    return [g.row_to_bar(instrument_id, row) for row in rows]


def merge_bars_by_ts(bar_lists: Iterable[Sequence[Bar]]) -> list[Bar]:
    """Stable-merge per-instrument ts-ascending bar lists into one ts-ascending stream.

    Same ts_event_ns: the input (universe) order is preserved — Python's `sorted` is stable
    and the inputs are concatenated in universe order, so instrument A (earlier in the
    universe) precedes instrument B at an identical ts (#48 AC#2).
    """
    combined = [bar for bars in bar_lists for bar in bars]
    return sorted(combined, key=lambda bar: bar.ts_event_ns)


def load_universe_bars(
    data_root: str | Path,
    instrument_ids: Sequence[str],
    *,
    start: str | None = None,
    end: str | None = None,
    granularity: str = "Daily",
) -> list[Bar]:
    """Load every instrument in `instrument_ids` (in order) and time-order-merge them."""
    return merge_bars_by_ts(
        load_bars(data_root, instrument_id, start=start, end=end, granularity=granularity)
        for instrument_id in instrument_ids
    )
