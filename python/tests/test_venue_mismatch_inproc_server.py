"""Engine-side gate for runtime-rebindable single live venue (ADR-0021, findings 0085).

Originally the live venue was LOCKED at server build (D26 "1 backend = 1 venue"): a venue_login
for any venue other than the one the server was constructed with returned VENUE_MISMATCH. That made
the menubar's "Connect Tachibana (Demo)" unreachable on the default MOCK-configured server (the
reported bug). ADR-0021 lifts the lock: while DISCONNECTED, a venue_login REBINDS the adapter factory
to the requested venue. Only a login for a *different* venue while a session is already live is still
rejected with VENUE_MISMATCH (defense-in-depth — the UI greys out Connect while connected, so this is
normally unreachable).

RED→GREEN litmus: removing the rebind in live_orchestrator.venue_login makes the disconnected
cross-venue login fall back to VENUE_MISMATCH → test_disconnected_login_to_other_venue_rebinds goes RED.
Removing the connected-state guard makes the while-connected login rebind instead of reject →
test_login_to_different_venue_while_connected_is_rejected goes RED.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def _make_mock_configured_server(tmp_path, monkeypatch):
    """A server built for "MOCK" whose adapter factory yields a connectable mock for ANY venue.

    Patching build_live_adapter_factory to return a mock for any venue lets the rebind path connect
    a non-MOCK venue deterministically without real credentials (the production builder would return
    a TachibanaAdapter that needs a real session).
    """
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer
    from engine.live.mock_adapter import MockVenueAdapter
    import engine.live.live_adapter_factory as laf

    mock = MockVenueAdapter()
    mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    monkeypatch.setattr(laf, "build_live_adapter_factory", lambda venue: (lambda env_hint=None: mock))

    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.set_rust_event_sink(lambda *a, **k: None)
    return InprocLiveServer(eng, "MOCK"), mock


def test_disconnected_login_to_other_venue_rebinds(tmp_path, monkeypatch) -> None:
    # The reported bug: on the default MOCK server, the menu's "Connect Tachibana (Demo)" must work.
    server, _ = _make_mock_configured_server(tmp_path, monkeypatch)
    try:
        res = server.venue_login("TACHIBANA", "env", "demo")
        # The lock is gone: a disconnected cross-venue login is NOT rejected with VENUE_MISMATCH.
        assert res["error_code"] != "VENUE_MISMATCH", res
        assert res["success"], res
    finally:
        server.close()


def test_login_to_different_venue_while_connected_is_rejected(tmp_path, monkeypatch) -> None:
    # Defense-in-depth: once a session is live, a different-venue login is rejected (no hot-swap).
    server, _ = _make_mock_configured_server(tmp_path, monkeypatch)
    try:
        assert server.venue_login("MOCK", "env", "")["success"]
        res = server.venue_login("TACHIBANA", "env", "demo")
        assert not res["success"], res
        assert res["error_code"] == "VENUE_MISMATCH", res
    finally:
        server.close()


def test_login_to_configured_venue_succeeds(tmp_path, monkeypatch) -> None:
    # The unchanged happy path: connecting the venue the server was built for works (no rebind).
    server, _ = _make_mock_configured_server(tmp_path, monkeypatch)
    try:
        assert server.venue_login("MOCK", "env", "")["success"]
    finally:
        server.close()


def test_same_venue_relogin_while_connected_is_idempotent(tmp_path, monkeypatch) -> None:
    # A SECOND venue_login for the venue already connected is a no-op success that does NOT re-login
    # the live session — the idempotent-CONNECTED short-circuit in live_orchestrator.venue_login
    # returns early. The tight gate is adapter.login_call_count: with the short-circuit it stays 1;
    # remove the short-circuit and the 2nd login falls through to _attempt → adapter.login again
    # (count == 2) → RED. (Observable success+state alone is too weak — the fall-through also returns
    # success; the login_call_count is what proves the session was not torn down / re-logged.)
    server, mock = _make_mock_configured_server(tmp_path, monkeypatch)
    try:
        first = server.venue_login("MOCK", "env", "")
        assert first["success"], first
        assert first["venue_state"] in ("CONNECTED", "SUBSCRIBED"), first
        assert mock.login_call_count == 1, mock.login_call_count
        second = server.venue_login("MOCK", "env", "")
        assert second["success"], second
        assert second["venue_state"] in ("CONNECTED", "SUBSCRIBED"), second
        # the existing session was reused, not re-logged-in
        assert mock.login_call_count == 1, mock.login_call_count
    finally:
        server.close()


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
