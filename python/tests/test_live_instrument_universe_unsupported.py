"""Sidebar `+Add` picker — LIVE-mode universe for a venue that cannot enumerate instruments.

Two evolutions of the same picker seam are pinned here, in order:

(A) findings 0103 (2026-06-25) — error-code separation. kabu logs in, but its adapter declares
    ``enumerates_instruments = False`` (kabuStation MVP returns [] from fetch_instruments — issue
    #253). The engine MUST distinguish "logged-in but can't enumerate" from "not logged in" so the
    C# picker can render a distinct placeholder ("Venue has no instrument list" vs "Venue not
    connected"). Without this split the menu badge ("Connected: KABU") and the picker
    ("Venue not connected") would contradict each other — the bug owners reported on 2026-06-25.

(B) issue #46 (2026-06-25) — kabu Live *browse* supply from listed_info. Splitting the error code
    fixes the contradiction but leaves logged-in kabu with a zero-candidate picker (the user can't
    actually pick instruments to add). So when kabu is logged in AND ``listed_info.duckdb``
    (ADR-0006) is mounted, ``_list_instruments_live`` falls back to the listed_info snapshot
    (~4400 TSE codes emitted as ``<code>.TSE`` — kabu's ``_parse_instrument_id`` accepts that
    exact suffix, so browse→add→subscribe runs id-conversion-free). DuckDB missing →
    ``LOCAL_UNIVERSE_UNAVAILABLE`` (the Replay picker's typed config error), NOT
    ``LIVE_UNIVERSE_UNSUPPORTED`` — distinguishing "venue can't enumerate" from "owner's
    listed_info isn't mounted". Issue #253's anti-prune defense survives: the adapter's
    ``enumerates_instruments`` stays False so the persisted store is never served as the
    authoritative live universe (this fallback is a picker browse-only seam).

delete-the-production-logic litmus:
  - flip the REAL ``KabuStationAdapter.enumerates_instruments`` to True → ``test_kabu_adapter_declares_
    no_instrument_enumeration`` fails (the root-cause fact is gone).
  - delete the ``enumerates_instruments`` short-circuit in ``_list_instruments_live`` → logged-in
    kabu falls through to the fetch path → ``fetch_instruments failed: ...`` ≠ either expected
    code → multiple tests RED.
  - delete the ``runner.venue_id == "KABU"`` fallback branch (issue #46) →
    ``test_logged_in_kabu_with_listed_info_returns_ready`` falls back to UNSUPPORTED and FAILs
    (the picker would still render zero candidates, regression of the user-visible report).
  - widen the fallback to all non-enumerating venues (drop the kabu venue_id guard) →
    ``test_non_kabu_non_enumerating_venue_still_unsupported`` would silently serve TSE codes to a
    non-TSE venue and FAIL — proving the kabu-only scoping is load-bearing.
  - the login guard ``if runner is None or not runner.is_logged_in()`` has BOTH halves pinned: the
    ``runner is None`` half by ``test_no_session_returns_not_logged_in`` and the
    ``not runner.is_logged_in()`` half by ``test_logged_out_runner_returns_not_logged_in`` —
    dropping either disjunct flips one test RED.
"""
from __future__ import annotations

import os
import sys

import pytest

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine._backend_impl import DataEngineBackend
from engine.exchanges.kabusapi import KabuStationAdapter
from engine.jquants_listed_info import ListedSymbolsSnapshot


# --- minimal fakes: drive the real `_list_instruments_live` dispatch without a venue/network ---
class _FakeAdapter:
    def __init__(self, venue_id: str, enumerates: bool):
        self.venue_id = venue_id
        self.enumerates_instruments = enumerates
        self.is_logged_in = True


class _FakeRunner:
    def __init__(self, adapter: _FakeAdapter, logged_in: bool = True):
        self._adapter = adapter
        self._logged_in = logged_in

    def is_logged_in(self) -> bool:
        return self._logged_in

    @property
    def adapter(self) -> _FakeAdapter:
        return self._adapter

    @property
    def venue_id(self) -> str:
        return self._adapter.venue_id


class _FakeSession:
    def __init__(self, runner):
        self.runner = runner
        self.instruments_scheduler = None


class _FakeLiveMgr:
    def __init__(self, session):
        self._session = session
        self._instruments_timeout_s = 5.0


def _backend_with_session(session):
    # The live path reads only `self._live_mgr._session`, so __new__ (skip the heavy __init__)
    # plus an injected live manager is enough to exercise the real dispatch.
    be = DataEngineBackend.__new__(DataEngineBackend)
    be._live_mgr = _FakeLiveMgr(session)
    return be


def test_kabu_adapter_declares_no_instrument_enumeration():
    """The root-cause fact: kabu cannot enumerate its instrument master (issue #253)."""
    assert KabuStationAdapter.enumerates_instruments is False


@pytest.mark.scenario("KABU-LIVE-46")
def test_logged_in_kabu_with_listed_info_returns_ready(monkeypatch):
    """ISSUE #46 (2026-06-25): logged-in kabu + mounted listed_info.duckdb → the picker is served
    the listed_info universe (TSE codes as ``<code>.TSE``), NOT the placeholder error code. This
    is the user-visible AC — "candidates appear in the search box after kabu login".

    We monkeypatch ``read_listed_snapshot`` so the test runs without DuckDB / a real listed_info
    file (CI / contributors without the J-Quants mount). The id format ``<code>.TSE`` is the
    contract kabu's ``_parse_instrument_id`` consumes (``code.TSE → (Symbol=code, Exchange=1)``),
    so browse→add→subscribe runs id-conversion-free. CompanyName is plumbed end-to-end so the
    picker can filter on Japanese name (issue #46 follow-up — review finding A5).
    """
    import engine._backend_impl as impl
    monkeypatch.setattr(
        impl,
        "read_listed_snapshot",
        lambda end_date: ListedSymbolsSnapshot(
            codes=["1301", "7203", "9984"],
            as_of_date="2025-12-04",
            names=["極洋", "トヨタ自動車", "ソフトバンクグループ"],
        ),
    )
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is True
    assert res.instrument_ids == ["1301.TSE", "7203.TSE", "9984.TSE"]
    # market field carries the venue suffix (mirrors _list_instruments_local).
    assert all(inst.market == "TSE" for inst in res.instruments)
    # CompanyName flows through to InstrumentInfo.name so the picker can search by name (the
    # missing-name case falls back to the id; see test_logged_in_kabu_listed_info_missing_names).
    names = {inst.id: inst.name for inst in res.instruments}
    assert names == {"1301.TSE": "極洋", "7203.TSE": "トヨタ自動車", "9984.TSE": "ソフトバンクグループ"}
    # Non-vacuity: the picker MUST NOT see either the old "unsupported" code (which would re-
    # render the empty placeholder and reproduce the user's report) NOR the not-logged-in code
    # (which would re-introduce findings 0103's "Connected: KABU" vs "Venue not connected" UI
    # contradiction even for the success path).
    assert res.error_message == ""
    assert res.error_message != "LIVE_UNIVERSE_UNSUPPORTED"
    assert res.error_message != "LIVE_VENUE_NOT_LOGGED_IN"


@pytest.mark.scenario("KABU-LIVE-46")
def test_logged_in_kabu_listed_info_missing_names_falls_back_to_id(monkeypatch):
    """CompanyName missing per row (legacy schema / NULL cells) → InstrumentInfo.name falls back
    to the id so the picker always has a non-empty display label."""
    import engine._backend_impl as impl
    monkeypatch.setattr(
        impl,
        "read_listed_snapshot",
        lambda end_date: ListedSymbolsSnapshot(
            codes=["7203", "9984"],
            as_of_date="2025-12-04",
            names=["", ""],  # both rows NULL in listed_info
        ),
    )
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is True
    names = {inst.id: inst.name for inst in res.instruments}
    # id-as-name fallback so the picker label is never empty (UI invariant).
    assert names == {"7203.TSE": "7203.TSE", "9984.TSE": "9984.TSE"}


@pytest.mark.scenario("KABU-LIVE-46")
def test_logged_in_kabu_without_listed_info_returns_local_universe_unavailable(monkeypatch):
    """ISSUE #46: logged-in kabu but listed_info.duckdb is NOT mounted (BACKCAST_JQUANTS_DUCKDB_ROOT
    unset or file missing → ``read_listed_snapshot`` returns None) → typed config error
    ``LOCAL_UNIVERSE_UNAVAILABLE`` (the Replay picker's code). The UI distinguishes this from
    ``LIVE_UNIVERSE_UNSUPPORTED`` ("venue can't enumerate") — they map to different user remedies
    (mount the DuckDB vs nothing the owner can do for this venue).
    """
    import engine._backend_impl as impl
    monkeypatch.setattr(impl, "read_listed_snapshot", lambda end_date: None)
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LOCAL_UNIVERSE_UNAVAILABLE"
    # MUST NOT collapse into the venue-capability code — that would mis-label a fixable config
    # problem as an unfixable venue limitation. Nor into LIVE_VENUE_NOT_LOGGED_IN — see the
    # findings 0103 invariant test_logged_in_kabu_with_listed_info_returns_ready also pins.
    assert res.error_message != "LIVE_UNIVERSE_UNSUPPORTED"
    assert res.error_message != "LIVE_VENUE_NOT_LOGGED_IN"


@pytest.mark.scenario("KABU-LIVE-46")
def test_logged_in_kabu_empty_listed_info_returns_local_universe_unavailable(monkeypatch):
    """DuckDB MOUNTED but listed_info table has zero rows (incorrect ingest range / freshly
    created file) → LOCAL_UNIVERSE_UNAVAILABLE, NOT a Ready-with-zero-ids that maps to the
    generic "No instruments" placeholder (review finding B1). An empty-but-reachable snapshot
    is owner-actionable (re-run J-Quants ingest), so we route it through the same typed config
    error as the unmounted case — both surface "configure your local catalog" in the picker."""
    import engine._backend_impl as impl
    monkeypatch.setattr(
        impl,
        "read_listed_snapshot",
        lambda end_date: ListedSymbolsSnapshot(codes=[], as_of_date="", names=[]),
    )
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LOCAL_UNIVERSE_UNAVAILABLE"


@pytest.mark.scenario("KABU-LIVE-46")
def test_logged_in_kabu_listed_info_raises_returns_local_universe_unavailable(monkeypatch):
    """DuckDB present but read raises (corrupted file, schema mismatch, locked) → kabu fallback
    narrows ANY non-typed error from `_read_local_snapshot`'s broad-except path to the typed
    LOCAL_UNIVERSE_UNAVAILABLE (review finding A3) so the C# `MapError` default branch never
    renders raw exception text / absolute file paths as a picker placeholder."""
    import engine._backend_impl as impl

    def _boom(end_date):
        raise RuntimeError("IO Error: Could not read /abs/path/listed_info.duckdb")

    monkeypatch.setattr(impl, "read_listed_snapshot", _boom)
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    # The raw exception message must NOT leak — only the typed code surfaces to the UI.
    assert res.error_message == "LOCAL_UNIVERSE_UNAVAILABLE"
    assert "IO Error" not in res.error_message
    assert "/abs/path" not in res.error_message


@pytest.mark.scenario("KABU-LIVE-46")
def test_non_kabu_non_enumerating_venue_still_unsupported(monkeypatch):
    """ISSUE #46 scoping: the listed_info fallback is GATED on ``venue_id == "KABU"`` because
    listed_info is JPX/TSE-only. A hypothetical future non-enumerating venue (e.g. a US broker
    adapter) MUST keep returning ``LIVE_UNIVERSE_UNSUPPORTED`` — silently serving TSE codes to
    a non-TSE venue would route orders to the wrong exchange (kabu's id parser also rejects
    non-".TSE" suffixes, so subscribe would fail downstream).

    Drives the fallback's else-branch. We monkeypatch ``read_listed_snapshot`` to return a
    populated snapshot so dropping the venue_id guard would actually exercise the silently-
    serves-TSE-codes failure mode the comment describes — without the monkeypatch the test
    would still fail on CI (no DuckDB → LOCAL_UNIVERSE_UNAVAILABLE), but the failure cause
    wouldn't match the stated litmus (review finding A4)."""
    import engine._backend_impl as impl
    monkeypatch.setattr(
        impl,
        "read_listed_snapshot",
        lambda end_date: ListedSymbolsSnapshot(
            codes=["1301", "7203"], as_of_date="2025-12-04", names=["極洋", "トヨタ自動車"]
        ),
    )
    adapter = _FakeAdapter("OTHER", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LIVE_UNIVERSE_UNSUPPORTED"
    assert res.instrument_ids == []


def test_no_session_returns_not_logged_in():
    """The genuinely-disconnected case stays distinct: no live session → LIVE_VENUE_NOT_LOGGED_IN.
    Pairing this with the test above proves the two states are NOT collapsed (non-vacuous)."""
    res = _backend_with_session(None).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LIVE_VENUE_NOT_LOGGED_IN"


def test_logged_out_runner_returns_not_logged_in():
    """The OTHER half of the login guard: a session whose runner reports is_logged_in()==False (e.g.
    venue dropped the session) → LIVE_VENUE_NOT_LOGGED_IN, never reaching the enumerates_instruments
    check. Pairs with test_no_session (which covers the `runner is None` disjunct) so BOTH halves of
    `if runner is None or not runner.is_logged_in()` are pinned — dropping either fails one test."""
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter, logged_in=False))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LIVE_VENUE_NOT_LOGGED_IN"


def test_enumerating_venue_does_not_short_circuit_to_unsupported(monkeypatch):
    """Non-vacuity control: the UNSUPPORTED short-circuit is gated SPECIFICALLY on
    ``enumerates_instruments``, not on "any logged-in venue". A venue that CAN enumerate
    (enumerates_instruments=True) must proceed PAST the early adapter guard into the fetch path.

    Without this, flipping the guard to unconditionally return UNSUPPORTED would still pass the kabu
    test. We force the store read to empty (deterministic regardless of the owner's mounted stores)
    so the path reaches the fetch, where the fake runner has no ``fetch_instruments_blocking`` →
    "fetch_instruments failed: ..." — proving the early guard was bypassed for an enumerating venue.
    """
    import engine._backend_impl as impl
    monkeypatch.setattr(impl.instruments_store, "read_instruments", lambda venue: [])
    adapter = _FakeAdapter("TACHIBANA", enumerates=True)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    # Did NOT stop at the adapter-guard short-circuit; it tried to actually fetch.
    assert res.error_message.startswith("fetch_instruments failed")
