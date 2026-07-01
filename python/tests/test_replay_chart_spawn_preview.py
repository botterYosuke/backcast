"""Replay chart spawn preview (#129) — DataEngine.populate_replay_preview gate.

Pins the contract for ``DataEngine.populate_replay_preview`` (findings 0104, slices S1/S2/S3):

- S1 PREVIEW-01: IDLE+Replay → ``per_id_ohlc_points[iid]`` populated from DuckDB bars.
- S1 PREVIEW-02: missing DuckDB file → graceful empty (no exception, returns NO_DATA).
- S1 PREVIEW-03: Manual/Auto mode → no-op (Replay-only by D0).
- S2 PREVIEW-04: ``duckdb_bars.get_date_range`` returns per-instrument (min, max) ISO dates.
- S2 PREVIEW-05: cold preview is decoupled from scenario dates (empty start/end included).
- #188 PREVIEW-12/13: cold preview draws only the recent bounded tail for large histories.
- S3 PREVIEW-06: ``replay_state != "IDLE"`` (LOADED / RUNNING) → no-op.
- S3 PREVIEW-07: ``load_replay_data`` fresh reset clears ``per_id_ohlc_points`` (RUN start cleanup).
- #156 reopen PREVIEW-11: a VALID scenario window that sits ENTIRELY OUTSIDE (after) the catalog
  still draws available recent history — the regression #169 introduced (today-relative seed window
  past a frozen historical snapshot → empty chart). Cold preview is decoupled from the scenario
  window.

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
from engine.kernel.duckdb_bars import get_date_range, load_bars  # noqa: E402


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


@pytest.mark.scenario("PREVIEW-12")
def test_load_bars_limit_returns_tail_in_ascending_order(tmp_path) -> None:
    """#188: DuckDB-side preview limiting fetches the recent tail, not the whole file."""
    _build_daily(tmp_path, n=7, day0=datetime.date(2024, 4, 1))

    bars = load_bars(str(tmp_path), _IID, granularity="Daily", limit=3)

    assert len(bars) == 3
    assert [b.close for b in bars] == [1006.0, 1007.0, 1008.0]
    assert all(bars[i].ts_event_ns < bars[i + 1].ts_event_ns for i in range(len(bars) - 1))


@pytest.mark.scenario("PREVIEW-05")
def test_empty_start_end_falls_back_to_full_range(tmp_path) -> None:
    """S2: the cold preview draws the full DuckDB date range per instrument.

    #156 reopen: this is now UNCONDITIONAL (the idle chart always shows the instrument's whole
    history). Empty/None/malformed start/end are one case of that — they still yield the full
    range, exactly as before. PREVIEW-11 covers the case the old code got wrong: a *valid* window
    that misses the catalog.
    """
    _build_daily(tmp_path, n=12, day0=datetime.date(2024, 3, 1))
    eng = DataEngine(duckdb_root=str(tmp_path))

    # Both empty strings (the on-the-wire form of "not set") → full range.
    ok, ec, count = eng.populate_replay_preview(_IID, "", "", "Daily")

    assert ok and count == 12
    pts = eng._rs.per_id_ohlc_points[_IID]
    assert len(pts) == 12

    # Malformed start → same full range (treated as "missing").
    ok2, _, count2 = eng.populate_replay_preview(_IID, "not-a-date", "", "Daily")
    assert ok2 and count2 == 12


@pytest.mark.scenario("PREVIEW-13")
def test_preview_caps_initial_history_to_recent_tail(tmp_path) -> None:
    """#188: spawn preview draws only the recent bounded tail for large histories."""
    _build_daily(tmp_path, n=1205, day0=datetime.date(2020, 1, 1))
    eng = DataEngine(duckdb_root=str(tmp_path))

    ok, ec, count = eng.populate_replay_preview(_IID, "", "", "Daily")

    assert ok and ec == "" and count == 1000
    pts = eng.get_current_state().per_instrument[_IID].ohlc_points
    assert len(pts) == 1000
    assert pts[0].close == 1000 + 205 + 2
    assert pts[-1].close == 1000 + 1204 + 2


@pytest.mark.scenario("PREVIEW-11")
def test_valid_window_outside_catalog_still_draws_full_history(tmp_path) -> None:
    """#156 reopen — the exact regression #169 shipped, as a deterministic real-DuckDB gate.

    The chart was empty on the owner's screen because the cold preview honoured the scenario's
    *valid* today-relative seed window ([today-3mo, today], seeded by #169's New). The J-Quants
    DuckDB is a frozen historical export that never reaches "today", so that window sat ENTIRELY
    AFTER the data → ``load_bars`` returned 0 bars → empty chart. No prior gate exercised a valid
    window that misses the catalog (PREVIEW-01/07 pass windows that equal the data; PREVIEW-05
    passes empty/invalid windows), so the bug shipped green.

    Contract now: the cold preview is DECOUPLED from the scenario window and always draws the full
    catalog history. RED on the old code (count == 0, empty slot), GREEN after.
    """
    # Catalog: 10 daily bars in late 2024. The scenario window is in 2026 — valid ISO dates, but
    # entirely past the end of the data (mirrors #169's SeedDefaults(today) = [today-3mo, today]).
    _build_daily(tmp_path, n=10, day0=datetime.date(2024, 10, 1))
    eng = DataEngine(duckdb_root=str(tmp_path))

    future_start, future_end = "2026-03-27", "2026-06-27"
    ok, ec, count = eng.populate_replay_preview(_IID, future_start, future_end, "Daily")

    assert ok and ec == "" and count == 10, (
        "cold preview must draw the FULL catalog history, not the out-of-range scenario window "
        "(#156 reopen / #169 regression)"
    )
    pts = eng._rs.per_id_ohlc_points[_IID]
    assert len(pts) == 10

    # And it must reach the polled projection the chart decoder reads (full boundary, not just _rs).
    pi_map = eng.get_current_state().per_instrument
    assert _IID in pi_map and len(pi_map[_IID].ohlc_points) == 10, (
        "full history must surface through get_current_state().per_instrument (chart renders empty "
        "otherwise — the owner's symptom)"
    )


@pytest.mark.scenario("PREVIEW-06")
def test_loaded_and_running_states_are_no_op(tmp_path) -> None:
    """S3: any non-IDLE replay_state must reject the preview RPC (RUN owns _rs).

    LOADED is part of the run lifecycle (between load_replay_data and start_engine) — a
    stale preview landing here would corrupt the about-to-stream accumulation.

    #156 / findings 0119 D-5: ``load_replay_data`` itself now cold-seeds the full scenario
    window into ``per_id_ohlc_points`` (so the virtualized ChartView has the full series).
    The non-IDLE preview RPC must still be a no-op — it returns ``RUNNING`` without
    overwriting the cold-loaded series.
    """
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.load_replay_data([_IID], "2024-10-01", "2024-10-10", "Daily")
    assert eng.replay_state == "LOADED"
    # D-5: load_replay_data cold-seeded the full window. Capture it so we can assert the
    # rejected preview RPC did not mutate it.
    seeded_before = list(eng._rs.per_id_ohlc_points.get(_IID, []))
    assert len(seeded_before) == 10

    ok, ec, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert not ok and ec == "RUNNING" and count == 0
    # The cold seed is untouched (no-op preview RPC must not overwrite the loaded state).
    assert eng._rs.per_id_ohlc_points.get(_IID, []) == seeded_before

    eng.start()  # LOADED → RUNNING
    ok2, ec2, _ = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert not ok2 and ec2 == "RUNNING"


@pytest.mark.scenario("PREVIEW-07")
def test_run_start_naturally_clears_preview(tmp_path) -> None:
    """S3 / D2: load_replay_data re-creates _rs, structurally replacing the preview dict
    (no manual cleanup needed — the existing reset path owns this contract).

    #156 / findings 0119 D-5: ``load_replay_data`` now REPLACES the preview with its own
    full-window cold seed (per scenario `start..end`, every instrument). The protective
    property the preview-clear was protecting still holds — the prior preview's content does
    not leak into the run — but the new state has the scenario's cold seed, not an empty
    dict. The preview seed path is *extended* into a full-scenario cold load, not removed.
    """
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))

    # Pre-load preview into a wider range so we can detect any leak after the narrower load.
    ok, _, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert ok and count == 10
    assert len(eng._rs.per_id_ohlc_points[_IID]) == 10

    # The user presses RUN → load_replay_data → _rs reset → preview replaced by the cold seed
    # for the *new* scenario window (4-day slice here vs the 10-bar preview above).
    eng.load_replay_data([_IID], "2024-10-01", "2024-10-04", "Daily")
    pts = eng._rs.per_id_ohlc_points.get(_IID, [])
    assert len(pts) == 4, (
        "load_replay_data must replace the prior preview with the new scenario's cold seed "
        "(findings 0119 D-5)"
    )


@pytest.mark.scenario("PREVIEW-08")
def test_preview_surfaces_through_get_current_state_projection(tmp_path) -> None:
    """#129 layer 3 (真因) — the faithful gate.

    The earlier PREVIEW-01..07 asserted ``eng._rs.per_id_ohlc_points[iid]`` *directly*. That
    never builds the poll projection, so it missed the real ship-breaking bug:
    ``_build_trading_state_locked`` iterated ``per_id_close`` only, dropping any preview-only iid
    (present in per_id_ohlc_points, absent from per_id_close because RUN was never pressed). The
    poll JSON ships ``get_current_state().per_instrument``, so the chart decoder saw HasSeries=false
    and rendered nothing — the empty chart the owner saw on the real screen.

    This gate drives the *projection* (get_current_state), which is what production actually polls.
    RED before the union fix (per_instrument is empty), GREEN after.
    """
    _build_daily(tmp_path, n=10)
    eng = DataEngine(duckdb_root=str(tmp_path))

    ok, _, count = eng.populate_replay_preview(_IID, "2024-10-01", "2024-10-10", "Daily")
    assert ok and count == 10
    # Preview only wrote per_id_ohlc_points; per_id_close stays empty (no streamed close yet).
    assert _IID in eng._rs.per_id_ohlc_points
    assert _IID not in eng._rs.per_id_close

    state = eng.get_current_state()
    # The projection MUST surface the preview iid (this is the line that was broken).
    assert _IID in state.per_instrument, (
        "preview iid dropped from per_instrument projection — chart renders empty (#129 layer 3)"
    )
    pi = state.per_instrument[_IID]
    assert len(pi.ohlc_points) == 10, "preview bars must reach the polled projection, not just _rs"
    # price falls back to the last candle's close when no streamed close exists yet.
    assert pi.price == eng._rs.per_id_ohlc_points[_IID][-1].close


@pytest.mark.scenario("PREVIEW-09")
def test_projection_union_does_not_disturb_streamed_instruments(tmp_path) -> None:
    """The union projection must not change behaviour for normal streamed instruments.

    When per_id_close IS present (the streaming case), price stays the streamed last close and
    the iid is not duplicated. A second preview-only iid coexists in the same projection.
    """
    from engine.models import OhlcPoint

    eng = DataEngine(duckdb_root=str(tmp_path))
    # Simulate a streamed instrument: both dicts populated together (reducer.py invariant).
    streamed = "9999.TSE"
    eng._rs.per_id_close[streamed] = 1234.0
    eng._rs.per_id_ohlc_points[streamed] = [
        OhlcPoint(timestamp_ms=1, open_time_ms=1, open=1230, high=1240, low=1220, close=1234)
    ]
    # And a preview-only instrument: ohlc points but no close.
    eng._rs.per_id_ohlc_points[_IID] = [
        OhlcPoint(timestamp_ms=2, open_time_ms=2, open=1000, high=1010, low=990, close=1005)
    ]

    pi_map = eng.get_current_state().per_instrument
    assert set(pi_map) == {streamed, _IID}
    # Streamed iid keeps its streamed close as price (union changed nothing for it).
    assert pi_map[streamed].price == 1234.0
    # Preview-only iid derives price from its last candle close.
    assert pi_map[_IID].price == 1005.0


@pytest.mark.scenario("PREVIEW-10")
def test_projection_surfaces_quote_only_instrument_with_empty_series(tmp_path) -> None:
    """Characterize the close-only direction the union now iterates.

    A TradeUpdate writes ``per_id_close[iid]`` (reducer.py:76) but NOT ``per_id_ohlc_points``
    (that fires only for KlineUpdate, reducer.py:102-103). So a quote/trade-only instrument has a
    close with no candles — the asymmetric case opposite to a preview iid. The union must still
    surface it (price = streamed close, empty series), exactly as the old per_id_close-only loop did.
    Pins the one behaviour the union touches that PREVIEW-08/09 don't cover, so a future refactor of
    the dict.fromkeys union can't silently drop or phantom it.
    """
    eng = DataEngine(duckdb_root=str(tmp_path))
    quote_only = "7777.TSE"
    eng._rs.per_id_close[quote_only] = 500.0  # TradeUpdate set a close; no KlineUpdate → no candles
    assert quote_only not in eng._rs.per_id_ohlc_points

    pi_map = eng.get_current_state().per_instrument
    assert quote_only in pi_map, "close-only iid must still surface (union must not regress streaming)"
    assert pi_map[quote_only].price == 500.0, "price comes from the streamed close, not None"
    assert pi_map[quote_only].ohlc_points == [], "no candles → empty series, no phantom OHLC"


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
