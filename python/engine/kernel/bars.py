"""engine.kernel.bars — nautilus-free bar source for the Backcast Execution Kernel (#24).

Reads the project's ParquetDataCatalog bar files directly via pyarrow, decoding the
fixed_size_binary[8] OHLCV columns without importing Nautilus (which would load the
Rust core — findings 0008 §1.3). The output sequence is byte-identical to what
Nautilus' `catalog_data_loader.load_bars_for_scenario` + `merge_bars_by_ts` feed a
strategy, so a kernel run can be golden-compared to the Nautilus oracle.

On-disk layout (findings 0008 §2):
    <catalog>/data/bar/<instrument_id>-1-DAY-LAST-EXTERNAL/*.parquet
with schema {open,high,low,close,volume: fixed_size_binary[8], ts_event,ts_init: uint64}
and metadata {price_precision, size_precision}.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pyarrow.parquet as pq

# Nautilus standard precision (PRECISION_BYTES=8): a Price/Quantity raw int64 equals
# value × 10**9. The display precision (price_precision metadata) is separate; the raw
# scale is fixed. A 16-byte high-precision build would use 10**18 — guarded at the
# golden layer via the PRECISION_BYTES=8 provenance pin (findings 0008 §4).
_FIXED_SCALAR = 1_000_000_000

_OHLCV = ("open", "high", "low", "close", "volume")


@dataclass(frozen=True)
class Bar:
    """A single OHLCV bar. ts_event_ns is the market event time in nanoseconds."""

    instrument_id: str
    ts_event_ns: int
    open: float
    high: float
    low: float
    close: float
    volume: float


def _decode_raw(value: bytes) -> float:
    """fixed_size_binary[8] little-endian int64 → float (raw / 1e9)."""
    return int.from_bytes(value, "little") / _FIXED_SCALAR


def _date_to_ns(date_str: str) -> int:
    """'YYYY-MM-DD' → UTC midnight in nanoseconds (inclusive lower bound)."""
    dt = datetime.fromisoformat(date_str).replace(tzinfo=timezone.utc)
    return int(dt.timestamp() * 1_000_000_000)


def _date_to_exclusive_end_ns(date_str: str) -> int:
    """'YYYY-MM-DD' → UTC midnight of the next day in nanoseconds (exclusive upper bound)."""
    dt = (datetime.fromisoformat(date_str) + timedelta(days=1)).replace(tzinfo=timezone.utc)
    return int(dt.timestamp() * 1_000_000_000)


def _bar_dir(catalog_root: str | Path, instrument_id: str) -> Path:
    return Path(catalog_root) / "data" / "bar" / f"{instrument_id}-1-DAY-LAST-EXTERNAL"


def load_bars(
    catalog_root: str | Path,
    instrument_id: str,
    *,
    start: str | None = None,
    end: str | None = None,
) -> list[Bar]:
    """Load Daily bars for one instrument, ts_event-ascending.

    `start` / `end` are inclusive 'YYYY-MM-DD' dates; filtering mirrors the Nautilus
    oracle exactly: keep bars with `start_ns <= ts_event < (end + 1 day)_ns`.
    """
    start_ns = _date_to_ns(start) if start else None
    end_ns = _date_to_exclusive_end_ns(end) if end else None

    bars: list[Bar] = []
    for parquet_path in sorted(_bar_dir(catalog_root, instrument_id).glob("*.parquet")):
        table = pq.read_table(parquet_path)
        cols = {name: table.column(name).to_pylist() for name in (*_OHLCV, "ts_event")}
        for i, ts in enumerate(cols["ts_event"]):
            if start_ns is not None and ts < start_ns:
                continue
            if end_ns is not None and ts >= end_ns:
                continue
            bars.append(
                Bar(
                    instrument_id=instrument_id,
                    ts_event_ns=int(ts),
                    open=_decode_raw(cols["open"][i]),
                    high=_decode_raw(cols["high"][i]),
                    low=_decode_raw(cols["low"][i]),
                    close=_decode_raw(cols["close"][i]),
                    volume=_decode_raw(cols["volume"][i]),
                )
            )

    bars.sort(key=lambda b: b.ts_event_ns)
    return bars
