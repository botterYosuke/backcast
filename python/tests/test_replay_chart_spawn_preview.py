"""Replay chart spawn preview (#129) — DataEngine.populate_replay_preview gate.

Pins the contract for ``DataEngine.populate_replay_preview`` (findings 0104, slices S1/S2/S3):

- S1 PREVIEW-01: IDLE+Replay → ``per_id_ohlc_points[iid]`` populated from DuckDB bars.
- S1 PREVIEW-02: missing DuckDB file → graceful empty (no exception, returns NO_DATA).
- S1 PREVIEW-03: Manual/Auto mode → no-op (Replay-only by D0).
- S2 PREVIEW-04: ``duckdb_bars.get_date_range`` returns per-instrument (min, max) ISO dates.
- S2 PREVIEW-05: empty start/end → falls back to the full DuckDB date range.
- S3 PREVIEW-06: ``replay_state != "IDLE"`` (LOADED / RUNNING) → no-op.
- S3 PREVIEW-07: ``load_replay_data`` fresh reset clears ``per_id_ohlc_points`` (RUN start cleanup).

The conftest hook translates each ``@pytest.mark.scenario("PREVIEW-NN")`` into the canonical
``[E2E PREVIEW-NN PASS|FAIL]`` rollup tag.
"""
from __future__ import annotations

import datetime
import os
import sys

import duckdb
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402
from engine.kernel.duckdb_bars import get_date_range  # noqa: E402


_SYMBOL = "8918"
_IID = "8918.TSE"
_DAY0 = datetime.date(2024, 10, 1)


def _build_daily(root, *, symbol: str = _SYMBOL, n: int = 10, day0: datetime.date = _DAY0) -> None:
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily ("
            "Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, Low BIGINT, "
            "Close BIGINT, Volume BIGINT)"
        )
        rows = []
        for i in range(n):
            day = day0 + datetime.timedelta(days=i)
            base = 1000 + i
            rows.append((day, symbol, base, base + 5, base - 5, base + 2, 100 + i))
        con.executemany("INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", rows)
    finally:
        con.close()


@pytest.mark.scenario("PREVIEW-01")
def test_idle_replay_populates_per_id_ohlc(tmp_path) -> None:
    """S1: spawn-time RPC under IDLE+Replay drops the instrument's bars into ``_rs``."""
    # NB: ``_mode`` is "static" until ``load_replay_data`` flips it to "replay" — but the
    # preview RPC's whole purpose is to render BEFORE that flip (the user has not pressed RUN).
    # The actual D0 guard reads ``mode_manager.current_mode`` (ExecutionMode SoT), which
    # defaults to "Replay" when no mode_manager is attached.
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))
    assert eng.replay_state == "IDLE"

    ok, ec, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")

    assert ok and ec == "" and count == 10
    pts = eng._rs.per_id_ohlc_points[_IID]
    assert len(pts) == 10
    # Bars are well-formed OhlcPoints with monotonically increasing open_time_ms (chart x-axis).
    assert all(p.open > 0 and p.close > 0 for p in pts)
    assert all(pts[i].open_time_ms < pts[i + 1].open_time_ms for i in range(len(pts) - 1))
    # OHLC integrity (chart's autoscale: high >= max(open,close), low <= min(open,close)).
    assert all(p.high >= max(p.open, p.close) and p.low <= min(p.open, p.close) for p in pts)
    # Engine remains in IDLE — preview is cold; it does NOT advance any run lifecycle.
    assert eng.replay_state == "IDLE"


@pytest.mark.scenario("PREVIEW-02")
def test_missing_duckdb_is_graceful(tmp_path) -> None:
    """S1 AC: an instrument whose .duckdb file does not exist must NOT crash the RPC.

    The chart stays empty for that iid (per_id_ohlc_points slot is an empty list, not absent).
    """
    # Build a file for 8918 but ask for a different symbol that has no file.
    _build_daily(tmp_path, n=5)
    eng = DataEngine(duckdb_root=str(tmp_path))
    missing_iid = "7203.TSE"

    ok, ec, count = eng.populate_replay_preview(missing_iid, "2024-10-01", "2024-10-10", "Daily")

    assert not ok and ec == "NO_DATA" and count == 0
    # No exception bubbled up, and we did not leak partial state for the missing symbol.
    assert eng._rs.per_id_ohlc_points.get(missing_iid, []) == []


@pytest.mark.scenario("PREVIEW-03")
def test_live_mode_is_no_op(tmp_path) -> None:
    """S1 D0: outside Replay (LiveManual / LiveAuto) the preview RPC must be a no-op.

    ExecutionMode (mode_manager.current_mode) is the SoT — Manual/Auto draws venue prices,
    not historical bars. The C# trigger is dumb; the Python guard owns the gate.
    """

    class _StubModeMgr:
        def __init__(self, mode: str) -> None:
            self.current_mode = mode

    _build_daily(tmp_path, n=3)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.attach_mode_manager(_StubModeMgr("LiveManual"))

    ok, ec, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")

    assert not ok and ec == "UNSUPPORTED_MODE" and count == 0
    assert _IID not in eng._rs.per_id_ohlc_points

    # Symmetric for LiveAuto.
    eng.attach_mode_manager(_StubModeMgr("LiveAuto"))
    ok2, ec2, _ = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert not ok2 and ec2 == "UNSUPPORTED_MODE"


@pytest.mark.scenario("PREVIEW-04")
def test_get_date_range_returns_min_max(tmp_path) -> None:
    """S2: ``get_date_range`` returns the file's (min_date, max_date) as ISO strings."""
    _build_daily(tmp_path, n=7, day0=datetime.date(2023, 5, 1))

    span = get_date_range(str(tmp_path), _IID, "Daily")

    assert span == ("2023-05-01", "2023-05-07")
    # Missing file → None (no exception; lets caller fall through to "empty preview").
    assert get_date_range(str(tmp_path), "9999.TSE", "Daily") is None


@pytest.mark.scenario("PREVIEW-05")
def test_empty_start_end_falls_back_to_full_range(tmp_path) -> None:
    """S2 D3: empty/None start/end → use the full DuckDB date range per instrument."""
    _build_daily(tmp_path, n=12, day0=datetime.date(2024, 3, 1))
    eng = DataEngine(duckdb_root=str(tmp_path))

    # Both empty strings (the on-the-wire form of "not set") → full range.
    ok, ec, count = eng.populate_replay_preview(_IID, "", "", "Daily")

    assert ok and count == 12
    pts = eng._rs.per_id_ohlc_points[_IID]
    assert len(pts) == 12

    # Malformed start → same fallback (treated as "missing").
    ok2, _, count2 = eng.populate_replay_preview(_IID, "not-a-date", "", "Daily")
    assert ok2 and count2 == 12


@pytest.mark.scenario("PREVIEW-06")
def test_loaded_and_running_states_are_no_op(tmp_path) -> None:
    """S3: any non-IDLE replay_state must reject the preview RPC (RUN owns _rs).

    LOADED is part of the run lifecycle (between load_replay_data and start_engine) — a
    stale preview landing here would corrupt the about-to-stream accumulation.
    """
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.load_replay_data([_IID], "2024-10-01", "2024-10-10", "Daily")
    assert eng.replay_state == "LOADED"

    ok, ec, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert not ok and ec == "RUNNING" and count == 0
    assert _IID not in eng._rs.per_id_ohlc_points

    eng.start()  # LOADED → RUNNING
    ok2, ec2, _ = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert not ok2 and ec2 == "RUNNING"


@pytest.mark.scenario("PREVIEW-07")
def test_run_start_naturally_clears_preview(tmp_path) -> None:
    """S3 / D2: load_replay_data re-creates _rs, structurally clearing the preview dict
    (no manual cleanup needed — the existing reset path owns this contract)."""
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))

    ok, _, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert ok and count > 0
    assert len(eng._rs.per_id_ohlc_points[_IID]) == count

    # The user presses RUN → load_replay_data → _rs reset → preview gone.
    eng.load_replay_data([_IID], "2024-10-01", "2024-10-10", "Daily")
    assert eng._rs.per_id_ohlc_points == {}, "load_replay_data must reset per_id_ohlc_points"


def test_bad_granularity_is_rejected(tmp_path) -> None:
    """A malformed granularity is rejected loudly (BAD_GRANULARITY, no state mutation)."""
    _build_daily(tmp_path, n=3)
    eng = DataEngine(duckdb_root=str(tmp_path))

    ok, ec, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Yearly")

    assert not ok and ec == "BAD_GRANULARITY" and count == 0
    assert _IID not in eng._rs.per_id_ohlc_points


def test_backend_service_wraps_to_plain_dict(tmp_path) -> None:
    """BackendService.populate_replay_preview returns a plain dict the in-proc bridge can ship."""
    from engine.backend_service import BackendService

    _build_daily(tmp_path, n=4)
    eng = DataEngine(duckdb_root=str(tmp_path))
    svc = BackendService(engine=eng)

    resp = svc.populate_replay_preview(_IID, "", "", "Daily")
    assert resp == {"success": True, "error_code": "", "bar_count": 4}

    # Missing file path: graceful failure dict (no exception).
    resp2 = svc.populate_replay_preview("7203.TSE", "", "", "Daily")
    assert resp2["success"] is False and resp2["error_code"] == "NO_DATA"


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
