"""Sidebar `+Add` picker — Replay-mode instrument-list supply (the "銘柄一覧が出てこない" gate).

User report (2026-06-22): clicking the sidebar **[+ Add]** button shows no instrument list.

The picker's Replay candidate list comes from the in-proc backend RPC
`DataEngineBackend.list_instruments("local", scenario_end)` →
`engine.jquants_listed_info.read_listed_snapshot()` →
`<BACKCAST_JQUANTS_DUCKDB_ROOT>/listed_info.duckdb` (point-in-time `MAX(Date) <= end_date`).
The C# `BackendAvailableInstrumentsProvider` maps the result to the picker: `success=True`
with ids → the candidate rows; `LOCAL_UNIVERSE_UNAVAILABLE` → an **error placeholder** (no list).

Root cause of the report: when `listed_info.duckdb` is missing (root unset, volume unmounted,
or file absent — the owner's `/Volumes/StockData/jp/listed_info.duckdb` was not present), the
supply returns `LOCAL_UNIVERSE_UNAVAILABLE`, so the picker renders the error placeholder and the
user sees "no instrument list". The supply LOGIC is correct — the list is empty only because the
DuckDB is unavailable.

This whole production supply path had NO Python test (the C# `UniverseSidebarE2ERunner` exercises
only a `StubProvider`, never the real DuckDB). These tests are SELF-CONTAINED — each builds a
synthetic `listed_info.duckdb` and points the env var at it, so the gate is deterministic in CI
regardless of the owner's mounted volume.

delete-the-production-logic litmus:
  - break `_list_instruments_local` / `read_listed_snapshot` → the DB-present tests fail.
  - drop the `LOCAL_UNIVERSE_UNAVAILABLE` typed error → the DB-missing test fails (it would no
    longer prove WHY the picker is empty).
"""
from __future__ import annotations

import os
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

import duckdb
import pytest

from engine._backend_impl import DataEngineBackend
from engine.jquants_listed_info import read_listed_snapshot

_ENV = "BACKCAST_JQUANTS_DUCKDB_ROOT"


def _build_listed_info(root, rows):
    """Write a synthetic `<root>/listed_info.duckdb` with the given (Date, Code) rows."""
    db = root / "listed_info.duckdb"
    con = duckdb.connect(str(db))
    try:
        con.execute(
            "CREATE TABLE listed_info "
            "(Date DATE, Code VARCHAR, CompanyName VARCHAR, MarketCode VARCHAR)"
        )
        con.executemany(
            "INSERT INTO listed_info VALUES (?, ?, ?, ?)",
            [(d, c, f"name-{c}", "0111") for d, c in rows],
        )
    finally:
        con.close()
    return db


# The local supply path (list_instruments("local", ...) → _list_instruments_local →
# _read_local_snapshot → read_listed_snapshot) reads NO instance state, so a backend built
# with __new__ (skipping the heavy __init__) is sufficient to exercise the real dispatch.
def _backend():
    return DataEngineBackend.__new__(DataEngineBackend)


@pytest.fixture
def listed_db(tmp_path, monkeypatch):
    """A synthetic listed_info universe pointed-at by the env var. Returns the root dir."""
    _build_listed_info(
        tmp_path,
        rows=[
            ("2024-01-01", "7203"),
            ("2024-01-01", "1301"),
            ("2024-12-01", "7203"),
            ("2024-12-01", "1301"),
            ("2024-12-01", "9984"),
            # a FUTURE snapshot — must be excluded for an end_date in 2024.
            ("2025-06-01", "6758"),
        ],
    )
    monkeypatch.setenv(_ENV, str(tmp_path))
    return tmp_path


def test_picker_supply_returns_universe_when_duckdb_present(listed_db):
    """The behavior the user WANTS: [+ Add] in Replay → the listed universe at scenario.end.

    This is the GREEN guard — when the DuckDB is reachable the picker is supplied with the
    candidate ids, so the list is NOT empty.
    """
    res = _backend().list_instruments("local", "2024-12-31")
    assert res.success, f"supply failed even with DuckDB present: {res.error_message}"
    # point-in-time snapshot is the latest Date <= end_date (2024-12-01), code.TSE ids, sorted.
    assert res.instrument_ids == ["1301.TSE", "7203.TSE", "9984.TSE"]
    assert [i.id for i in res.instruments] == res.instrument_ids


def test_picker_supply_unavailable_when_duckdb_missing(tmp_path, monkeypatch):
    """REPRODUCES THE REPORT: no listed_info.duckdb → typed LOCAL_UNIVERSE_UNAVAILABLE.

    `BackendAvailableInstrumentsProvider.MapError` turns this into the picker's error
    placeholder ("...listed_info.duckdb not configured"), which is exactly the user-visible
    "銘柄一覧が出てこない" — the list is empty because the universe source is unreachable, not
    because of a logic bug. tmp_path has NO listed_info.duckdb.
    """
    monkeypatch.setenv(_ENV, str(tmp_path))  # root set, but the .duckdb file is absent
    res = _backend().list_instruments("local", "2024-12-31")
    assert res.success is False
    assert res.error_message == "LOCAL_UNIVERSE_UNAVAILABLE"
    assert res.instrument_ids == []


def test_picker_supply_unavailable_when_root_unset(monkeypatch):
    """Root env var entirely unset → also LOCAL_UNIVERSE_UNAVAILABLE (the owner's live case:

    BACKCAST_JQUANTS_DUCKDB_ROOT pointed at an unmounted volume, so the file did not exist).
    """
    monkeypatch.delenv(_ENV, raising=False)
    res = _backend().list_instruments("local", "2024-12-31")
    assert res.success is False
    assert res.error_message == "LOCAL_UNIVERSE_UNAVAILABLE"


def test_picker_falls_back_to_latest_when_end_predates_all_snapshots(tmp_path, monkeypatch):
    """Owner request (2026-06-22, findings 0084): when scenario.end predates EVERY listed_info

    snapshot, the picker FALLS BACK to the latest universe instead of an empty list. The owner's DB
    only has recent snapshots (real: 2025-12-03..05); an early/stale end must still show instruments.
    Non-vacuous: an in-range end still scopes point-in-time (proves the fallback is gated on empty,
    not always-latest). delete-the-fallback litmus: drop the `if not snapshot.codes` retry → the
    early-end assert returns [] and FAILs.
    """
    _build_listed_info(
        tmp_path,
        rows=[("2025-12-03", "7203"), ("2025-12-03", "1301"),
              ("2025-12-05", "7203"), ("2025-12-05", "1301"), ("2025-12-05", "9984")],
    )
    monkeypatch.setenv(_ENV, str(tmp_path))
    be = _backend()

    # an end before ALL snapshots → fall back to the latest (2025-12-05) universe, NOT empty.
    early = be.list_instruments("local", "2024-12-31")
    assert early.success and early.instrument_ids == ["1301.TSE", "7203.TSE", "9984.TSE"]

    # a date the DB DOES cover still scopes point-in-time (2025-12-03 has only 1301/7203, no 9984).
    scoped = be.list_instruments("local", "2025-12-03")
    assert scoped.success and scoped.instrument_ids == ["1301.TSE", "7203.TSE"]


def test_picker_empty_end_serves_latest_universe(listed_db):
    """Empty scenario.end → the latest universe (overall MAX snapshot), so the picker shows the

    current instruments instead of "Set scenario.end first" (the C# provider now routes ""→backend
    rather than short-circuiting to EndUnset).
    """
    res = _backend().list_instruments("local", "")
    assert res.success
    assert res.instrument_ids == ["6758.TSE"]  # listed_db's latest snapshot is 2025-06-01 (6758)


def test_picker_supply_point_in_time_excludes_future_listings(listed_db):
    """The universe is the latest snapshot <= end_date — a future-dated row must not leak in."""
    res = _backend().list_instruments("local", "2024-12-31")
    assert res.success
    assert "6758.TSE" not in res.instrument_ids  # 2025-06-01 row is after scenario.end

    # Move end_date past the future snapshot → now (and only now) 6758 appears.
    later = _backend().list_instruments("local", "2025-12-31")
    assert later.success
    assert later.instrument_ids == ["6758.TSE"]  # 2025-06-01 is the latest snapshot now


def test_reader_empty_distinct_from_unavailable_but_rpc_falls_back(listed_db):
    """The low-level reader still returns an EMPTY snapshot (NOT the unavailable None) for an end

    before any snapshot — so "reachable-but-empty" stays distinguishable from "unreachable-DuckDB"
    at the reader level (the fallback lives in the RPC, not the shared reader). The picker RPC then
    layers the fallback: list_instruments serves the latest universe instead of the empty snapshot.
    """
    snap = read_listed_snapshot("2020-01-01")
    assert snap is not None  # DB reachable → not the "unavailable" None
    assert snap.codes == [] and snap.as_of_date == ""

    # RPC fallback: the picker shows the latest universe rather than the reader's empty snapshot.
    res = _backend().list_instruments("local", "2020-01-01")
    assert res.success is True
    assert res.instrument_ids == ["6758.TSE"]  # listed_db's latest snapshot (2025-06-01)


def test_picker_supply_rejects_malformed_end_date(listed_db):
    """A non-ISO end_date surfaces as a typed error (not a crash) so the picker can show it."""
    res = _backend().list_instruments("local", "31-12-2024")
    assert res.success is False
    assert "ISO date" in res.error_message
