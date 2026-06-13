"""spike.kernel_golden.run_oracle — run the Nautilus oracle, print normalized contract (#24).

Subprocess entry point that runs the preserved Nautilus comparison oracle
(spike_buy_sell.py through NautilusBacktestRunner) and prints the normalized contract
plus build provenance. Loads the Rust core — runs only in standalone CPython, never in
the Mono process (findings 0008 §4).

Output: a single JSON object {"contract": {...}, "provenance": {...}} on stdout.
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from spike.kernel_golden import scenario
from spike.kernel_golden.normalize import (
    CaptureSink,
    canonical_json,
    normalize,
    sha256_file,
    sha256_text,
)


def run() -> dict:
    from engine.nautilus_backtest_runner import NautilusBacktestRunner

    sink = CaptureSink()
    result = NautilusBacktestRunner(
        catalog_path=scenario.CATALOG,
        strategy_file=scenario.ORACLE_STRATEGY_FILE,
        instruments=[scenario.INSTRUMENT],
        start_date=scenario.START,
        end_date=scenario.END,
        granularity="Daily",
        initial_cash=scenario.INITIAL_CASH,
        rust_sink=sink,
    ).run()
    if not result.get("success"):
        raise RuntimeError(f"oracle run failed: {result.get('error')}")
    return normalize(sink.events, initial_cash=scenario.INITIAL_CASH)


def _provenance() -> dict:
    from nautilus_trader import __version__ as nautilus_version
    from nautilus_trader.core import nautilus_pyo3

    catalog_parquets = sorted(
        os.path.join(scenario.CATALOG_PARQUET_DIR, f)
        for f in os.listdir(scenario.CATALOG_PARQUET_DIR)
        if f.endswith(".parquet")
    )
    catalog_hash = sha256_text("".join(sha256_file(p) for p in catalog_parquets))
    scenario_blob = canonical_json(
        {
            "instrument": scenario.INSTRUMENT,
            "start": scenario.START,
            "end": scenario.END,
            "initial_cash": scenario.INITIAL_CASH,
            "granularity": "Daily",
        }
    )
    return {
        "nautilus_version": str(nautilus_version),
        "precision_bytes": int(nautilus_pyo3.PRECISION_BYTES),
        "strategy_sha256": sha256_file(scenario.ORACLE_STRATEGY_FILE),
        "catalog_sha256": catalog_hash,
        "scenario_sha256": sha256_text(scenario_blob),
    }


def main() -> int:
    out = {"contract": run()["contract"], "provenance": _provenance()}
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
