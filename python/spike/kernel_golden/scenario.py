"""spike.kernel_golden.scenario — shared tracer scenario constants (#24, nautilus-free).

The single source of truth for the golden tracer: catalog, instrument, window, cash,
and the Nautilus oracle strategy file. Both the oracle and kernel subprocesses read
these so they run the identical scenario (findings 0008 §2).
"""
from __future__ import annotations

import os
import sys

PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if PYTHON_ROOT not in sys.path:
    sys.path.insert(0, PYTHON_ROOT)

from engine import paths

CATALOG = os.path.join(PYTHON_ROOT, "spike", "fixtures", "jquants-catalog")
# J-Quants DuckDB root (ADR-0006): the kernel reads <root>/stocks_daily/<code>.duckdb
# directly. Per-machine path read from .env (BACKCAST_JQUANTS_DUCKDB_ROOT) via engine.paths —
# never hardcoded (paths differ per machine; .env.example). Empty when unset, so the real-data
# tests skip-if-absent. CATALOG is retained only as the data-equivalence comparison source
# (#47) and the oracle source until #50.
_DUCKDB_ROOT = paths.jquants_duckdb_root()
# Explicit "is the root configured?" flag. When unset, DUCKDB_ROOT is "" and db_path() would
# yield a RELATIVE path (e.g. stocks_daily/8918.duckdb) that could false-match a same-named
# file under cwd — so real-data tests must gate skip on this flag, not on .exists() alone.
DUCKDB_ROOT_CONFIGURED = _DUCKDB_ROOT is not None
DUCKDB_ROOT = str(_DUCKDB_ROOT) if _DUCKDB_ROOT is not None else ""
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
