"""CPython golden gate (#24, AC#1): kernel == committed golden == live Nautilus oracle.

Runs the oracle and the kernel each in a SEPARATE subprocess (findings 0008 §4) and
asserts:
  1. oracle contract == committed golden  (golden-staleness guard: the committed golden
     still matches the real Nautilus path),
  2. oracle provenance == committed provenance (nautilus build / precision / hashes),
  3. kernel contract == committed golden    (the pure-Python kernel reproduces the oracle).

This is the standalone-CPython leg; the Mono leg lives in test_kernel_teardown_mono.py.
Loads Nautilus (oracle subprocess) so it only runs where the Rust core is available.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

import pytest

from engine.kernel.duckdb_bars import daily_db_path
from spike.kernel_golden import scenario
from spike.kernel_golden.capture_golden import GOLDEN_PATH
from spike.kernel_golden.subprocess_util import run_python
from spike.kernel_golden.verify_golden import first_difference

# The kernel leg now sources bars from the J-Quants DuckDB (ADR-0006 / #47); skip where the
# owner's data root is not mounted. The oracle leg keeps using the committed catalog.
_DB_PRESENT = scenario.DUCKDB_ROOT_CONFIGURED and daily_db_path(scenario.DUCKDB_ROOT, scenario.INSTRUMENT).exists()


def _run(module: str) -> subprocess.CompletedProcess:
    # 180s: oracle leg imports Nautilus (~10-20s); a longer wait would mean a hang.
    return run_python(["-m", module], timeout=180)


def _golden() -> dict:
    with open(GOLDEN_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def test_oracle_subprocess_matches_committed_golden() -> None:
    golden = _golden()
    proc = _run("spike.kernel_golden.run_oracle")
    assert proc.returncode == 0, f"oracle subprocess failed:\n{proc.stderr}"
    out = json.loads(proc.stdout)
    diff = first_difference(golden["contract"], out["contract"])
    assert diff is None, f"committed golden is stale vs the live Nautilus oracle: {diff}"
    assert out["provenance"] == golden["provenance"], (
        "oracle provenance drifted (nautilus build / precision / hashes) — re-capture and review"
    )


@pytest.mark.skipif(
    not _DB_PRESENT, reason=f"J-Quants DuckDB not mounted at {scenario.DUCKDB_ROOT}"
)
def test_kernel_subprocess_matches_committed_golden() -> None:
    golden = _golden()
    proc = _run("spike.kernel_golden.run_kernel")
    assert proc.returncode == 0, f"kernel subprocess failed:\n{proc.stderr}"
    contract = json.loads(proc.stdout)["contract"]
    diff = first_difference(golden["contract"], contract)
    assert diff is None, f"kernel drifted from golden: {diff}"


if __name__ == "__main__":
    failures = []
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
            except AssertionError as exc:
                failures.append(f"{name}: {exc}")
    if failures:
        print("[KERNEL GOLDEN CPYTHON FAIL]")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("[KERNEL GOLDEN CPYTHON PASS] kernel == golden == live Nautilus oracle (subprocess-isolated)")
