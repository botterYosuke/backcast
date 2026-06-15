"""Kernel teardown gate (#24, AC#2): the kernel run is Rust-core-free and exits clean.

ADR-0004 案 C's whole premise: with no Nautilus Rust core in the process, the multi-CRT/
FLS teardown crash (s0-result §1.1–§1.4) cannot occur — so teardown is clean. The
structural, headlessly-automatable proof is:
  1. a fresh kernel subprocess exits 0 (no segfault / stack-overflow on teardown),
  2. it loaded NO `nautilus_trader` / `nautilus_pyo3` module (--assert-pure),
  3. its output still matches the committed golden (it actually ran the tracer).

The remaining leg — that this same kernel exits clean *inside Unity-Mono batchmode* — is a
manual gate (findings 0008 §6; can't be driven from headless CPython). This test pins the
structural guarantee that makes the Mono leg hold.
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
from spike.kernel_golden.verify_golden import first_difference

# The kernel now sources bars from the J-Quants DuckDB (ADR-0006 / #47); skip where the
# owner's data root is not mounted (real-data dependency; repo skip-if-absent convention).
_DB_PRESENT = daily_db_path(scenario.DUCKDB_ROOT, scenario.INSTRUMENT).exists()


# Generous vs the kernel's ~seconds runtime: a longer wall time means a real teardown
# HANG (FLS/thread-join deadlock — a documented failure mode for this multi-CRT teardown
# family), not a slow run. A hang is exactly the teardown regression this gate must catch,
# so we must NOT let subprocess.run block forever.
_TEARDOWN_TIMEOUT_S = 120


def _run_kernel_subprocess() -> subprocess.CompletedProcess:
    from spike.kernel_golden.subprocess_util import run_python

    return run_python(
        ["-m", "spike.kernel_golden.run_kernel", "--assert-pure"],
        timeout=_TEARDOWN_TIMEOUT_S,
    )


@pytest.mark.skipif(
    not _DB_PRESENT, reason=f"J-Quants DuckDB not mounted at {scenario.DUCKDB_ROOT}"
)
def test_kernel_subprocess_exits_clean_and_rust_core_free() -> None:
    try:
        proc = _run_kernel_subprocess()
    except subprocess.TimeoutExpired as exc:
        raise AssertionError(
            f"kernel subprocess did not exit within {_TEARDOWN_TIMEOUT_S}s — a teardown "
            "HANG is the regression this gate exists to catch."
        ) from exc
    # returncode 2 = nautilus leaked (--assert-pure); nonzero/negative = crash on teardown.
    assert proc.returncode == 0, (
        f"kernel subprocess did not exit cleanly (rc={proc.returncode}); "
        f"negative = native crash, 2 = nautilus loaded.\nstderr={proc.stderr!r}"
    )
    with open(GOLDEN_PATH, encoding="utf-8") as fh:
        golden = json.load(fh)
    contract = json.loads(proc.stdout)["contract"]
    diff = first_difference(golden["contract"], contract)
    assert diff is None, f"kernel ran but drifted from golden: {diff}"


if __name__ == "__main__":
    if not _DB_PRESENT:
        print(f"[KERNEL TEARDOWN SKIP] DuckDB not mounted at {scenario.DUCKDB_ROOT}")
        sys.exit(0)
    try:
        test_kernel_subprocess_exits_clean_and_rust_core_free()
    except AssertionError as exc:
        print(f"[KERNEL TEARDOWN FAIL] {exc}")
        sys.exit(1)
    print("[KERNEL TEARDOWN PASS] kernel subprocess exits 0, Rust-core-free, golden match")
