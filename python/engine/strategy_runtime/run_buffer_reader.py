"""engine.strategy_runtime.run_buffer_reader — typed JSONL reader for run-buffer files."""
from __future__ import annotations

import json
import logging
from functools import cached_property
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class Fill:
    instrument_id: str
    side: str  # "BUY" | "SELL"
    qty: float
    price: float
    ts_event_ms: int
    commission: Optional[float] = field(default=None)


@dataclass(frozen=True)
class EquityPoint:
    ts_event_ms: int
    equity: float
    # Realized cash at this point, recorded separately from equity (mark-to-market) by the
    # DuckDB/kernel path (#49 review #2). None for the legacy path that wrote equity only.
    cash: float | None = None


class RunBufferReader:
    """Reads fills.jsonl and equity.jsonl from a run directory into typed records.

    Parsing is lazy and cached: files are read on the first property access
    and held for the lifetime of the instance.

    Stale-data risk: if the underlying JSONL files are still being appended
    (e.g., a run is in progress), the cached result reflects only the data
    present at the time of first access.  Callers must instantiate a new
    ``RunBufferReader`` to re-read from disk.
    """

    def __init__(self, run_dir: Path) -> None:
        self._run_dir = Path(run_dir)

    @cached_property
    def fills(self) -> list[Fill]:
        return _parse_fills(self._run_dir / "fills.jsonl")

    @cached_property
    def equity_points(self) -> list[EquityPoint]:
        return _parse_equity_points(self._run_dir / "equity.jsonl")


# ── internal parsers ──────────────────────────────────────────────────────────

def _parse_fills(path: Path) -> list[Fill]:
    fills: list[Fill] = []
    if not path.exists():
        return fills
    with path.open(encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                log.warning("run_buffer_reader: invalid JSON at %s line %d, skipping", path, lineno)
                continue
            fill = _row_to_fill(row, path, lineno)
            if fill is not None:
                fills.append(fill)
    return fills


def _row_to_fill(row: dict, path: Path, lineno: int) -> Optional[Fill]:
    instrument_id = str(row.get("instrument_id", ""))
    if not instrument_id:
        log.debug("run_buffer_reader: empty instrument_id at %s:%d, skipping", path, lineno)
        return None
    side = row.get("side")
    if side not in ("BUY", "SELL"):
        log.debug("run_buffer_reader: invalid side=%r at %s:%d, skipping", side, path, lineno)
        return None
    qty = _to_optional_float(row.get("qty"))
    if qty is None or qty <= 0:
        log.debug("run_buffer_reader: invalid qty=%r at %s:%d, skipping", row.get("qty"), path, lineno)
        return None
    price = _to_optional_float(row.get("price"))
    if price is None or price <= 0:
        log.debug("run_buffer_reader: invalid price=%r at %s:%d, skipping", row.get("price"), path, lineno)
        return None
    try:
        ts_ms = int(row.get("ts_event_ms", 0))
    except (TypeError, ValueError):
        ts_ms = 0

    raw_commission = row.get("commission")
    commission = _to_optional_float(raw_commission)
    if commission is None and raw_commission not in (None, ""):
        log.warning(
            "run_buffer_reader: non-numeric commission=%r at %s:%d, treating as missing",
            raw_commission,
            path,
            lineno,
        )

    return Fill(
        instrument_id=instrument_id,
        side=side,
        qty=qty,
        price=price,
        ts_event_ms=ts_ms,
        commission=commission,
    )


def _parse_equity_points(path: Path) -> list[EquityPoint]:
    points: list[EquityPoint] = []
    if not path.exists():
        return points
    with path.open(encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                log.warning("run_buffer_reader: invalid JSON at %s line %d, skipping", path, lineno)
                continue
            equity = _to_optional_float(row.get("equity"))
            if equity is None:
                log.debug("run_buffer_reader: invalid equity at %s:%d, skipping", path, lineno)
                continue
            try:
                ts_ms = int(row.get("ts_event_ms", 0))
            except (TypeError, ValueError):
                ts_ms = 0
            cash = _to_optional_float(row.get("cash"))  # None on the legacy equity-only path
            points.append(EquityPoint(ts_event_ms=ts_ms, equity=equity, cash=cash))
    return points


def _to_optional_float(value) -> Optional[float]:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
