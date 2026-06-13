"""spike.kernel_golden.scenario — shared tracer scenario constants (#24, nautilus-free).

The single source of truth for the golden tracer: catalog, instrument, window, cash,
and the Nautilus oracle strategy file. Both the oracle and kernel subprocesses read
these so they run the identical scenario (findings 0008 §2).
"""
from __future__ import annotations

import os

PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

CATALOG = os.path.join(PYTHON_ROOT, "spike", "fixtures", "jquants-catalog")
INSTRUMENT = "8918.TSE"
START = "2024-10-01"
END = "2025-01-10"
INITIAL_CASH = 10_000_000

ORACLE_STRATEGY_FILE = os.path.join(
    PYTHON_ROOT, "spike", "fixtures", "strategies", "spike_buy_sell.py"
)
# The on-disk parquet feeding both readers — hashed into golden provenance.
CATALOG_PARQUET_DIR = os.path.join(
    CATALOG, "data", "bar", f"{INSTRUMENT}-1-DAY-LAST-EXTERNAL"
)
