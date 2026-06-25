"""Sidebar `+Add` picker — LIVE-mode universe for a venue that cannot enumerate instruments.

User report (2026-06-25): after logging into **kabu** the menu bar badge reads "Connected: KABU",
but the sidebar [+ Add] picker shows **"Venue not connected"** — a direct contradiction.

Root cause (two halves):
  - ENGINE (this gate): kabu's adapter declares ``enumerates_instruments = False`` (kabuStation MVP
    returns [] from fetch_instruments — issue #253). So ``list_instruments("live", "")`` for a
    *logged-in* kabu session returns the typed code ``LIVE_UNIVERSE_UNSUPPORTED`` — NOT
    ``LIVE_VENUE_NOT_LOGGED_IN``. The engine ALREADY distinguishes the two; these tests lock that
    contract so a refactor can't silently merge them.
  - C# (separate gate — UniverseSidebarE2ERunner): ``BackendAvailableInstrumentsProvider.MapError``
    used to collapse BOTH codes into ``AvailableInstrumentsResult.NotConnected`` → the picker
    rendered "Venue not connected" for a connected-but-unenumerable venue. Fixed to a distinct
    ``Unsupported`` status with the message "Venue has no instrument list" (findings 0103).

delete-the-production-logic litmus:
  - flip kabu's ``enumerates_instruments`` to True → the UNSUPPORTED test fails (it would fall through
    to the store/fetch path instead of the typed short-circuit).
  - drop the ``is_logged_in()`` guard in ``_list_instruments_live`` → the not-logged-in test fails
    (it would no longer return LIVE_VENUE_NOT_LOGGED_IN). The two assertions together prove the codes
    are genuinely distinct (non-vacuous).
"""
from __future__ import annotations

import os
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine._backend_impl import DataEngineBackend
from engine.exchanges.kabusapi import KabuStationAdapter


# --- minimal fakes: drive the real `_list_instruments_live` dispatch without a venue/network ---
class _FakeAdapter:
    def __init__(self, venue_id: str, enumerates: bool):
        self.venue_id = venue_id
        self.enumerates_instruments = enumerates
        self.is_logged_in = True


class _FakeRunner:
    def __init__(self, adapter: _FakeAdapter):
        self._adapter = adapter

    def is_logged_in(self) -> bool:
        return True

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


def test_logged_in_kabu_returns_universe_unsupported_not_not_logged_in():
    """REPRODUCES the report at the engine seam: a CONNECTED kabu session yields a universe that is
    *unsupported* — NOT the not-logged-in code. The C# side must therefore not render
    "Venue not connected" (which is reserved for LIVE_VENUE_NOT_LOGGED_IN).
    """
    adapter = _FakeAdapter("KABU", enumerates=False)
    session = _FakeSession(_FakeRunner(adapter))
    res = _backend_with_session(session).list_instruments("live", "")
    assert res.success is False
    assert res.error_message == "LIVE_UNIVERSE_UNSUPPORTED"
    assert res.error_message != "LIVE_VENUE_NOT_LOGGED_IN"
    assert res.instrument_ids == []


def test_no_session_returns_not_logged_in():
    """The genuinely-disconnected case stays distinct: no live session → LIVE_VENUE_NOT_LOGGED_IN.
    Pairing this with the test above proves the two states are NOT collapsed (non-vacuous)."""
    res = _backend_with_session(None).list_instruments("live", "")
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
