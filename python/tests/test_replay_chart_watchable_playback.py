"""Replay watchable playback — per-instrument chart follows the streamed cursor (#182).

``bt.replay(bars_per_second=N)``'s sole reason to exist is *watchable* playback
(``backtester.py`` ``replay()`` docstring): during a RUN the chart must advance one bar at a
time, in pace with streaming — not paint the whole series at once. The bug (#182): the
engine cold-seeds ``per_id_ohlc_points`` with the FULL scenario window at LOAD (#156 /
findings 0119 D-5) and the reducer dedupes streamed bars by ts, so ``per_id_ohlc_points``
stays at the full count for the entire run. Because the C# ``ChartView`` draws
``per_instrument[id].ohlc_points`` (NOT the streamed top-level ``ohlc_points``), the chart
shows every bar from the first poll and ``bars_per_second`` only slows the Python loop.

The fix clips the per-instrument series the poll JSON projects to the *streamed replay
cursor* (``rs.timestamp_ms`` — the latest streamed primary bar's time) while a run is in
flight or has streamed at least one bar, and leaves the FULL cold-seeded series untouched
before the run (LOADED / preview fit-all — #156 must not regress).

Production path mirrored here (the issue's own repro): synthetic DuckDB → ``load_replay_data``
(cold-seed) → ``start_engine`` (LOADED→RUNNING) → ``apply_replay_event(KlineUpdate)`` one bar
at a time (exactly what ``ReplayKernelObserver.push_bar`` does) → ``get_current_state()`` (the
projection the C# decoder polls).

conftest translates each ``@pytest.mark.scenario("REPLAY-PLAY-NN")`` into the canonical
``[E2E REPLAY-PLAY-NN PASS|FAIL]`` rollup tag.
"""
from __future__ import annotations

import datetime
import os
import sys

import duckdb
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402
from engine.reducer import KlineUpdate  # noqa: E402

_SYMBOL_A = "8918"
_SYMBOL_B = "7203"
_IID_A = "8918.TSE"  # primary (instrument_ids[0])
_IID_B = "7203.TSE"
_DAY0 = datetime.date(2022, 1, 4)


def _build_daily(root, *, symbol: str, n: int, day0: datetime.date = _DAY0) -> None:
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        # CREATE OR REPLACE (not IF NOT EXISTS): re-seeding the same symbol in one test must
        # give a fresh n-bar table, not append onto a prior load's rows.
        con.execute(
            "CREATE OR REPLACE TABLE stocks_daily ("
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


def _loaded_engine(tmp_path, *, n: int, ids=(_IID_A,)) -> tuple[DataEngine, str, str]:
    for iid in ids:
        _build_daily(tmp_path, symbol=iid.split(".")[0], n=n)
    eng = DataEngine(duckdb_root=str(tmp_path))
    start = _DAY0.isoformat()
    end = (_DAY0 + datetime.timedelta(days=n - 1)).isoformat()
    ok, err = eng.load_replay_data(list(ids), start, end, "Daily")
    assert ok and err is None, err
    assert eng.replay_state == "LOADED"
    return eng, start, end


def _stream(eng: DataEngine, iid: str, pt) -> None:
    """Mirror ReplayKernelObserver.push_bar: feed one cold-seeded bar back as a streamed event."""
    eng.apply_replay_event(
        KlineUpdate(
            timestamp_ms=pt.timestamp_ms,
            open_time_ms=pt.open_time_ms,
            open=pt.open,
            high=pt.high,
            low=pt.low,
            close=pt.close,
            instrument_id=iid,
            volume=pt.volume,
        )
    )


def _drawn(eng: DataEngine, iid: str) -> int:
    """How many OHLC bars the C# ChartView would draw for ``iid`` this poll."""
    return len(eng.get_current_state().per_instrument[iid].ohlc_points)


@pytest.mark.scenario("REPLAY-PLAY-01")
def test_running_chart_follows_streamed_cursor(tmp_path) -> None:
    """REPLAY-PLAY-01 (AC1): during RUNNING the drawn per-instrument series grows one bar at a
    time in step with streaming — it does NOT paint the full cold-seeded series up front."""
    n = 8
    eng, _, _ = _loaded_engine(tmp_path, n=n)
    seed = list(eng._rs.per_id_ohlc_points[_IID_A])  # the bars the kernel will stream
    assert len(seed) == n

    ok, err = eng.start_engine()  # LOADED → RUNNING
    assert ok and err is None, err

    # Before the first bar streams the chart is empty (warming up) — NOT a flash of all n.
    assert _drawn(eng, _IID_A) == 0

    for k in range(1, n + 1):
        _stream(eng, _IID_A, seed[k - 1])
        drawn = _drawn(eng, _IID_A)
        assert drawn == k, f"after streaming {k} bars the chart drew {drawn} (expected {k})"


@pytest.mark.scenario("REPLAY-PLAY-02")
def test_loaded_preview_shows_full_series(tmp_path) -> None:
    """REPLAY-PLAY-02 (AC2 / #156 no-regression): before RUN (LOADED) the chart shows the FULL
    cold-seeded series so fit-all preview is preserved."""
    n = 30
    eng, _, _ = _loaded_engine(tmp_path, n=n)
    # No start_engine, no streaming: pure LOADED preview.
    assert eng.replay_state == "LOADED"
    assert _drawn(eng, _IID_A) == n


@pytest.mark.scenario("REPLAY-PLAY-03")
def test_complete_run_and_observation_break(tmp_path) -> None:
    """REPLAY-PLAY-03 (AC3 + AC4): a complete run ends with every bar drawn; an observation-only
    cell that breaks early (no submit_market) leaves ONLY the streamed-so-far bars drawn — and
    that count persists after the run finalizes to IDLE (force_stop_replay), it does NOT snap
    back to the full cold-seeded series."""
    n = 10

    # --- complete run: all bars stream, terminal IDLE shows all n (AC3) ---
    eng, _, _ = _loaded_engine(tmp_path, n=n)
    seed = list(eng._rs.per_id_ohlc_points[_IID_A])
    eng.start_engine()
    for pt in seed:
        _stream(eng, _IID_A, pt)
    eng.force_stop_replay()  # RUNNING → IDLE (the host's run-end teardown)
    assert eng.replay_state == "IDLE"
    assert _drawn(eng, _IID_A) == n

    # --- observation-only break at bar 4: stays 4 after IDLE (AC4) ---
    eng2, _, _ = _loaded_engine(tmp_path, n=n)
    seed2 = list(eng2._rs.per_id_ohlc_points[_IID_A])
    eng2.start_engine()
    broke_at = 4
    for pt in seed2[:broke_at]:
        _stream(eng2, _IID_A, pt)
    assert _drawn(eng2, _IID_A) == broke_at
    eng2.force_stop_replay()
    assert eng2.replay_state == "IDLE"
    assert _drawn(eng2, _IID_A) == broke_at, "broken run must not snap back to the full series"


@pytest.mark.scenario("REPLAY-PLAY-04")
def test_multi_instrument_clips_to_primary_cursor(tmp_path) -> None:
    """REPLAY-PLAY-04: with several instruments, every per-instrument chart follows the same
    replay clock (the primary's streamed timestamp), so a non-primary instrument is clipped to
    the bars whose time is at or before the current replay cursor."""
    n = 6
    eng, _, _ = _loaded_engine(tmp_path, n=n, ids=(_IID_A, _IID_B))
    seed_a = list(eng._rs.per_id_ohlc_points[_IID_A])
    seed_b = list(eng._rs.per_id_ohlc_points[_IID_B])
    assert len(seed_a) == n and len(seed_b) == n

    eng.start_engine()
    # The kernel streams same-ts bars for all instruments in one tick group. Stream the first k
    # ticks (both instruments) and assert both charts follow the cursor.
    for k in range(1, n + 1):
        _stream(eng, _IID_A, seed_a[k - 1])
        _stream(eng, _IID_B, seed_b[k - 1])
        assert _drawn(eng, _IID_A) == k
        assert _drawn(eng, _IID_B) == k


@pytest.mark.scenario("REPLAY-PLAY-05")
def test_live_static_mode_never_clips(tmp_path) -> None:
    """REPLAY-PLAY-05: clipping is a Replay concept — in Live/static mode the per-instrument series
    is NEVER clipped to the streamed cursor, even if a bar's time exceeds the latest streamed
    timestamp. This pins the ``_mode == "replay"`` guard: without it, a static-mode engine with a
    populated top-level ``ohlc_points`` (streamed=True) would clip Live charts to rs.timestamp_ms
    and silently drop bars. Built white-box because the reducer's Live invariant normally keeps
    timestamp_ms ≥ every per-id open_time_ms — the guard is the explicit floor under that."""
    from engine.core import ReducerState  # noqa: PLC0415
    from engine.models import OhlcPoint  # noqa: PLC0415

    eng = DataEngine()  # default ctor → Live/static mode, replay_state IDLE
    assert eng._mode == "static" and eng.replay_state == "IDLE"
    iid = _IID_A
    # A static-mode reducer whose top-level series is non-empty (streamed=True) but whose per-id
    # bar opens AFTER the latest streamed timestamp — the exact shape the mode guard must not clip.
    pts = [
        OhlcPoint(timestamp_ms=100, open_time_ms=100, open=1, high=2, low=1, close=1.5, volume=1),
        OhlcPoint(timestamp_ms=200, open_time_ms=200, open=1, high=2, low=1, close=1.5, volume=1),
    ]
    eng._rs = ReducerState(
        timestamp_ms=100,  # cursor < the second bar's open_time_ms (200)
        price=1.5,
        ohlc_points=[pts[0]],  # non-empty → streamed=True if the mode guard were absent
        per_id_ohlc_points={iid: list(pts)},
        per_id_close={iid: 1.5},
    )
    drawn = len(eng.get_current_state().per_instrument[iid].ohlc_points)
    assert drawn == 2, f"Live/static mode must not clip — drew {drawn}, expected 2 (mode guard regressed)"


@pytest.mark.scenario("REPLAY-PLAY-06")
def test_post_run_preview_shows_full_period(tmp_path) -> None:
    """REPLAY-PLAY-06 (#156 no-regression in the post-run IDLE state): after a run completes the
    host leaves the engine IDLE with _mode='replay' and a non-empty streamed top-level series
    (force_stop_replay does NOT reset _rs). A FRESH ``populate_replay_preview`` then re-seeds the
    chart with the instrument's FULL catalog (decoupled from the run window, #156 reopen) — and
    that full-period preview must NOT be clipped to the stale run cursor. Regression guard for the
    clip leaking into a re-triggered preview (host fires it on scenario Commit / chart spawn /
    layout restore while IDLE)."""
    catalog_n = 30   # the instrument's full catalog
    run_n = 10       # the run only streams the first 10 (a sub-window)
    _build_daily(tmp_path, symbol=_SYMBOL_A, n=catalog_n)

    eng = DataEngine(duckdb_root=str(tmp_path))
    start = _DAY0.isoformat()
    end = (_DAY0 + datetime.timedelta(days=run_n - 1)).isoformat()
    ok, err = eng.load_replay_data([_IID_A], start, end, "Daily")
    assert ok and err is None, err
    seed = list(eng._rs.per_id_ohlc_points[_IID_A])
    assert len(seed) == run_n  # cold-seed honours the run window

    eng.start_engine()
    for pt in seed:
        _stream(eng, _IID_A, pt)
    eng.force_stop_replay()  # run end → IDLE, _mode stays "replay", streamed top-level kept
    assert eng.replay_state == "IDLE"
    assert _drawn(eng, _IID_A) == run_n  # the run's own chart still shows its streamed bars (AC3)

    # Host re-requests a preview while IDLE (universe edit / chart spawn / layout restore). This
    # re-seeds the FULL catalog — it must come back full, not clipped to the prior run cursor.
    ok, code, count = eng.populate_replay_preview(_IID_A, start, end, "Daily")
    assert ok, f"preview failed: {code}"
    assert count == catalog_n
    drawn = _drawn(eng, _IID_A)
    assert drawn == catalog_n, (
        f"post-run preview must show the FULL period ({catalog_n}), got {drawn} — the watchable "
        f"clip leaked into a fresh preview and dropped the tail (#156 regression)"
    )


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
