"""AFK e2e gate (#49 AC④): production Replay runs through DuckDB→kernel, nautilus-free.

Drives the production-equivalent path end to end:

    DataEngine(duckdb_root) → load_replay_data → DataEngineBackend.start_engine
      → KernelRunner(DuckDB direct-read + ReplayKernelObserver)
      → apply_replay_event (reducer/GetState) + RunBuffer (fills/equity → get_portfolio)

and asserts:
  - the run succeeds and the kernel-native strategy traded (fills + equity recorded);
  - the chart accumulated **exactly once** (ohlc_points count == streamed bar count — the
    no-prime/no-skip invariant, findings 0019);
  - in a CLEAN interpreter, the whole path loads NO `nautilus_trader` (AC④ purity).

A synthetic per-symbol DuckDB is built in a temp dir so the WIRING gate runs in CI without
the owner's mount (data faithfulness is #47/#48's job, on the real DuckDB). The strategy is
the kernel-native golden twin `kernel_spike_buy_sell.py` (BUY bar 3 / SELL bar 40).
"""
from __future__ import annotations

import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import duckdb  # noqa: E402

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_STRATEGY = os.path.join(
    _PYTHON_ROOT, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"
)
_N_BARS = 50  # > SELL_AT_BAR (40) so both legs fill


def _build_synthetic_duckdb(root, *, symbol: str = "8918", n: int = _N_BARS) -> None:
    """Write <root>/stocks_daily/<symbol>.duckdb with `n` ascending daily bars (Code=symbol).

    Schema mirrors the real J-Quants daily file's read columns (Date, Code, OHLCV). Dates
    start 2024-10-01 (inside the strategy SCENARIO window) and ascend by one calendar day.
    """
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily ("
            "Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, Low BIGINT, "
            "Close BIGINT, Volume BIGINT)"
        )
        day0 = datetime.date(2024, 10, 1)
        rows = []
        for i in range(n):
            day = day0 + datetime.timedelta(days=i)
            base = 1000 + i
            rows.append((day, symbol, base, base + 5, base - 5, base + 2, 1000 + i))
        con.executemany(
            "INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", rows
        )
    finally:
        con.close()


def _run_e2e(root):
    """Build engine+backend on `root`, run the full path, return (result, engine)."""
    from engine.core import DataEngine
    from engine._backend_impl import DataEngineBackend

    eng = DataEngine(duckdb_root=str(root))
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok, f"load_replay_data failed: {err}"
    backend = DataEngineBackend(engine=eng)
    result = backend.start_engine(_STRATEGY)
    return result, eng


def test_duckdb_kernel_replay_runs_and_streams_exactly_once(tmp_path) -> None:
    _build_synthetic_duckdb(tmp_path)
    result, eng = _run_e2e(tmp_path)

    assert result.success, f"start_engine failed: {result.error_code} {result.error_message}"

    # The kernel-native strategy traded: fills + equity landed in the RunBuffer-derived
    # portfolio, surfaced by the unchanged get_portfolio seam.
    pf = eng.last_portfolio
    assert pf is not None
    assert len(pf["orders"]) == 2, f"expected BUY+SELL fills, got {pf['orders']}"

    # Exactly-once: every streamed bar reached the reducer once (no prime, no skip).
    assert len(eng._rs.ohlc_points) == _N_BARS
    assert len(eng._rs.per_id_ohlc_points["8918.TSE"]) == _N_BARS


def test_duckdb_kernel_replay_loads_no_nautilus(tmp_path) -> None:
    """AC④ purity: the whole DuckDB→kernel run loads no nautilus in a clean interpreter."""
    from spike.kernel_golden.subprocess_util import run_python

    # Self-contained (the tests/ dir is not an importable package): build a synthetic DuckDB,
    # run the full path, and report any leaked nautilus module — all in a clean interpreter.
    child = f"""
import datetime, os, sys, tempfile
sys.path.insert(0, {_PYTHON_ROOT!r})
import duckdb

root = tempfile.mkdtemp()
d = os.path.join(root, "stocks_daily"); os.makedirs(d)
con = duckdb.connect(os.path.join(d, "8918.duckdb"))
con.execute("CREATE TABLE stocks_daily (Date DATE, Code VARCHAR, Open BIGINT, "
            "High BIGINT, Low BIGINT, Close BIGINT, Volume BIGINT)")
day0 = datetime.date(2024, 10, 1)
con.executemany("INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", [
    (day0 + datetime.timedelta(days=i), "8918", 1000 + i, 1005 + i, 995 + i, 1002 + i, 1000 + i)
    for i in range({_N_BARS})
])
con.close()

from engine.core import DataEngine
from engine._backend_impl import DataEngineBackend
from spike.kernel_golden.purity import leaked_nautilus_modules

eng = DataEngine(duckdb_root=root)
ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
if not ok:
    print("LOADFAIL:" + str(err)); sys.exit(2)
result = DataEngineBackend(engine=eng).start_engine({_STRATEGY!r})
if not result.success:
    print("RUNFAIL:" + str(result.error_code) + " " + str(result.error_message)); sys.exit(2)
leaked = leaked_nautilus_modules(sys.modules)
if leaked:
    print("LEAKED:" + ",".join(leaked[:5])); sys.exit(1)
print("PURE")
"""
    proc = run_python(["-c", child], timeout=120)
    assert proc.returncode == 0, (
        "DuckDB→kernel Replay loaded nautilus or failed in a clean interpreter (AC④).\n"
        f"stdout={proc.stdout!r}\nstderr={proc.stderr!r}"
    )
    assert "PURE" in proc.stdout


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
