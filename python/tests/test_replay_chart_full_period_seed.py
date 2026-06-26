"""Replay full-period cold load (#156 / findings 0119 D-5) — engine seed gate.

The new Mesh-based ChartView (S1) virtualizes the visible window from the FULL per-instrument
series. For the chart to have a full series to virtualize, the engine must — on Replay
``load_replay_data`` — cold-seed ``per_id_ohlc_points`` with every bar in scenario ``start..end``
for every instrument (not just the streamed primary that the reducer accumulates one-bar-at-a-time
under the Live-tuned ``max_history_len=1000`` cap).

Live mode is unaffected: the reducer ring buffer stays at 1000 to bound LiveAuto memory.

The conftest hook translates each ``@pytest.mark.scenario("REPLAY-FULL-NN")`` into the canonical
``[E2E REPLAY-FULL-NN PASS|FAIL]`` rollup tag.
"""
from __future__ import annotations

import datetime
import os
import sys

import duckdb
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402


_SYMBOL_A = "8918"
_SYMBOL_B = "7203"
_IID_A = "8918.TSE"
_IID_B = "7203.TSE"
_DAY0 = datetime.date(2022, 1, 4)


def _build_daily(
    root,
    *,
    symbol: str,
    n: int,
    day0: datetime.date = _DAY0,
) -> None:
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE IF NOT EXISTS stocks_daily ("
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


@pytest.mark.scenario("REPLAY-FULL-01")
def test_replay_load_cold_seeds_full_period(tmp_path) -> None:
    """REPLAY-FULL-01: load_replay_data must cold-seed per_id_ohlc_points with EVERY bar in
    the scenario window for EVERY instrument — even though that exceeds the Live-tuned
    ``max_history_len = 1000`` cap. Without this, the new virtualized ChartView only ever
    sees the streaming reducer's tail (≤ 1000 bars), so a "full period" Daily request that
    spans years renders truncated.
    """
    n_a = 1500  # > Live cap (1000) — the whole point of this gate
    n_b = 1500
    _build_daily(tmp_path, symbol=_SYMBOL_A, n=n_a)
    _build_daily(tmp_path, symbol=_SYMBOL_B, n=n_b)
    eng = DataEngine(duckdb_root=str(tmp_path))

    start = _DAY0.isoformat()
    end = (_DAY0 + datetime.timedelta(days=max(n_a, n_b) - 1)).isoformat()
    ok, err = eng.load_replay_data([_IID_A, _IID_B], start, end, "Daily")

    assert ok and err is None
    assert eng.replay_state == "LOADED"
    # Every requested instrument got cold-seeded with its full bar count.
    pts_a = eng._rs.per_id_ohlc_points.get(_IID_A, [])
    pts_b = eng._rs.per_id_ohlc_points.get(_IID_B, [])
    assert len(pts_a) == n_a, f"expected {n_a} bars for {_IID_A}, got {len(pts_a)}"
    assert len(pts_b) == n_b, f"expected {n_b} bars for {_IID_B}, got {len(pts_b)}"
    # Bars are well-formed and ts-ascending (chart x-axis invariant).
    for iid, pts in ((_IID_A, pts_a), (_IID_B, pts_b)):
        assert all(p.open > 0 and p.close > 0 for p in pts)
        assert all(pts[i].open_time_ms < pts[i + 1].open_time_ms for i in range(len(pts) - 1))
        assert all(p.volume > 0 for p in pts), f"cold seed dropped volume for {iid}"

    # The state JSON projection (what the C# decoder polls) MUST surface the full series.
    state = eng.get_current_state()
    assert _IID_A in state.per_instrument and _IID_B in state.per_instrument
    assert len(state.per_instrument[_IID_A].ohlc_points) == n_a
    assert len(state.per_instrument[_IID_B].ohlc_points) == n_b


@pytest.mark.scenario("REPLAY-FULL-02")
def test_live_keeps_1000_cap() -> None:
    """REPLAY-FULL-02: Live's reducer ring buffer cap stays at 1000.

    Findings 0119 D-5 explicitly puts kabu historical backfill out of scope; Live keeps its
    bounded buffer to cap LiveAuto memory across the whole subscription set. We assert the
    default ctor (the Live construction path) leaves ``max_history_len = 1000`` AND that the
    Live reducer state created at init reflects that cap.
    """
    eng = DataEngine()

    # The internal cap source stays the Live-tuned 1000.
    assert eng._max_history_len == 1000
    # The initial (Live / static) reducer state propagates that cap — so the streaming reducer
    # apply_event's len > max_history_len → pop(0) ring-buffer is unchanged for Live.
    assert eng._rs.max_history_len == 1000


@pytest.mark.scenario("REPLAY-FULL-03")
def test_scenario_change_reseeds_full_period(tmp_path) -> None:
    """REPLAY-FULL-03: a new scenario commit (force_stop_replay → load_replay_data with a
    different start/end) re-seeds per_id_ohlc_points with the NEW scenario's full window,
    not the prior one's. The prior cold-load must be discarded — same lifecycle invariant the
    findings 0104 preview gate (PREVIEW-07) protects, just at the load_replay_data layer.
    """
    _build_daily(tmp_path, symbol=_SYMBOL_A, n=1200)
    eng = DataEngine(duckdb_root=str(tmp_path))

    # First scenario: a 30-day slice from day 0.
    start1 = _DAY0.isoformat()
    end1 = (_DAY0 + datetime.timedelta(days=29)).isoformat()
    ok1, _ = eng.load_replay_data([_IID_A], start1, end1, "Daily")
    assert ok1
    pts1 = eng._rs.per_id_ohlc_points.get(_IID_A, [])
    assert len(pts1) == 30, f"first scenario seed: expected 30 bars, got {len(pts1)}"

    # User aborts / re-commits with a wider scenario.
    eng.force_stop_replay()
    assert eng.replay_state == "IDLE"
    start2 = _DAY0.isoformat()
    end2 = (_DAY0 + datetime.timedelta(days=899)).isoformat()
    ok2, _ = eng.load_replay_data([_IID_A], start2, end2, "Daily")
    assert ok2
    pts2 = eng._rs.per_id_ohlc_points.get(_IID_A, [])
    # Re-seeded with the NEW scenario's full window — prior 30-bar list is gone.
    assert len(pts2) == 900, f"second scenario re-seed: expected 900 bars, got {len(pts2)}"
    # ts-ascending preserved across the re-seed.
    assert all(pts2[i].open_time_ms < pts2[i + 1].open_time_ms for i in range(len(pts2) - 1))


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
