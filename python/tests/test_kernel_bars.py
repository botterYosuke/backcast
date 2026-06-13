"""Kernel bar reader parity (#24): the nautilus-free reader must equal the oracle.

The Backcast Execution Kernel cannot use the Nautilus ParquetDataCatalog (importing
it loads the Rust core — findings 0008 §1.3). It reads the same catalog parquet
directly via pyarrow. For golden parity the OHLCV sequence the kernel feeds the
strategy must be byte-identical to what Nautilus' `catalog_data_loader.load_bars_for_scenario`
feeds its strategy.

This test reads the SAME parquet with both readers (in standalone CPython, where
Nautilus is available) and asserts identical bar sequences — an independent
cross-check of the decoder, not a self-referential golden.

Decode invariant: OHLC are stored as fixed_size_binary[8] = little-endian int64,
Nautilus standard precision (PRECISION_BYTES=8) raw = value × 1e9.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

CATALOG = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "spike",
    "fixtures",
    "jquants-catalog",
)
SCENARIO = {
    "schema_version": 2,
    "instruments": ["8918.TSE"],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


def test_kernel_reader_matches_nautilus_catalog_loader() -> None:
    from engine.kernel.bars import load_bars
    from engine.strategy_runtime.catalog_data_loader import (
        load_bars_for_scenario,
        merge_bars_by_ts,
    )

    kernel_bars = load_bars(
        CATALOG, "8918.TSE", start=SCENARIO["start"], end=SCENARIO["end"]
    )
    oracle_bars = merge_bars_by_ts(load_bars_for_scenario(CATALOG, SCENARIO))

    assert kernel_bars, "kernel reader returned no bars"
    assert len(kernel_bars) == len(oracle_bars), (
        f"bar count differs: kernel={len(kernel_bars)} oracle={len(oracle_bars)}"
    )
    for i, (k, o) in enumerate(zip(kernel_bars, oracle_bars)):
        assert k.ts_event_ns == o.ts_event, f"bar {i} ts_event: {k.ts_event_ns} != {o.ts_event}"
        assert k.open == float(o.open.as_double()), f"bar {i} open: {k.open} != {o.open}"
        assert k.high == float(o.high.as_double()), f"bar {i} high mismatch"
        assert k.low == float(o.low.as_double()), f"bar {i} low mismatch"
        assert k.close == float(o.close.as_double()), f"bar {i} close mismatch"
        assert k.volume == float(o.volume.as_double()), f"bar {i} volume mismatch"


if __name__ == "__main__":
    try:
        test_kernel_reader_matches_nautilus_catalog_loader()
    except AssertionError as exc:
        print(f"[KERNEL BARS FAIL] {exc}")
        sys.exit(1)
    print("[KERNEL BARS PASS] pyarrow reader matches Nautilus catalog loader OHLCV")
