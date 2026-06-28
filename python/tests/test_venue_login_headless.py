"""#181 / ADR-0040 — Unity uGUI modal-driven headless venue login (Python half).

The login GUI now lives in Unity; Python only does headless auth. These tests pin the
new RPC surface on ``LiveLoopManager``:

- ``submit_venue_login`` — validate → headless auth → finalize → CONNECTED, and the
  failure rendering (error_code / Japanese status_text / allow_retry) the modal shows.
- ``venue_login_form_init`` — prefill the modal (ADR-0033 demo/verify prefill).
- ``venue_login_probe_station`` — kabu 本体起動確認.

This file replaces the retired ``test_inproc_prompt_login.py`` (tkinter dispatcher).
The headless auth functions are patched so no real venue is contacted; the orchestrator
wiring (loop marshal, venue_sm convergence, adapter login) is exercised for real with a
``MockVenueAdapter``.
"""
from __future__ import annotations

import pytest

from engine.live import live_orchestrator
from engine.live.live_orchestrator import LiveLoopManager
from engine.exchanges import venue_login_headless, kabusapi_login_form_state


def _make_mgr(venue_id: str):
    """A real LiveLoopManager bound to a shared MockVenueAdapter (no real venue)."""
    from engine.core import DataEngine
    from engine.live.mock_adapter import MockVenueAdapter
    from engine.live.state_machine import VenueStateMachine
    from engine.mode_manager import ModeManager

    shared_mock = MockVenueAdapter()
    data_engine = DataEngine()
    venue_sm = VenueStateMachine()
    data_engine.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=data_engine)
    data_engine.attach_mode_manager(mode_manager)
    mgr = LiveLoopManager(
        engine=data_engine,
        mode_manager=mode_manager,
        venue_sm=venue_sm,
        live_adapter_factory=lambda env_hint=None: shared_mock,
        live_venue_id=venue_id,
        engine_controller=None,
        publish_backend_event_callback=lambda ev: None,
    )
    return mgr, venue_sm, shared_mock


def _teardown_mgr(mgr) -> None:
    try:
        mgr.venue_logout()
    except Exception:
        pass
    try:
        mgr.stop_live_loop(timeout=3.0)
    except Exception:
        pass


# --- submit_venue_login: success → CONNECTED ---------------------------------

def test_submit_kabu_reaches_connected(monkeypatch):
    # kabu: validate + probe pass → headless fetch_token → prompt_result creds →
    # adapter.login → venue_sm CONNECTED. The token never crosses back to the modal.
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)

    async def _fake_auth(api_password, mode):
        assert api_password == "pw123" and mode == "verify"
        return "TOK"

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _fake_auth)
    mgr, venue_sm, mock = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "pw123")
        assert res["success"], res
        assert res["error_code"] == ""
        assert venue_sm.current == "CONNECTED"
        assert mock.is_logged_in
    finally:
        _teardown_mgr(mgr)


def test_submit_tachibana_reaches_connected(monkeypatch):
    # tachibana: validate → headless login + save_session (faked) → session_cache
    # creds → adapter.login → CONNECTED. No token returned (session_cache on disk).
    captured = {}

    async def _fake_auth(auth_id, key_path, mode):
        captured["args"] = (auth_id, key_path, mode)  # returns None (saves session)

    monkeypatch.setattr(venue_login_headless, "authenticate_tachibana", _fake_auth)
    mgr, venue_sm, mock = _make_mgr("TACHIBANA")
    try:
        fields = '{"auth_id": "myid", "key_path": "C:/k.pem"}'
        res = mgr.submit_venue_login("TACHIBANA", "demo", fields, "")
        assert res["success"], res
        assert captured["args"] == ("myid", "C:/k.pem", "demo")
        assert venue_sm.current == "CONNECTED"
        assert mock.is_logged_in
    finally:
        _teardown_mgr(mgr)


# --- submit_venue_login: validation (no auth, no loop) ------------------------

def test_submit_kabu_empty_password_is_empty_fields(monkeypatch):
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "")
        assert res["success"] is False
        assert res["error_code"] == "EMPTY_FIELDS"
        assert venue_sm.current == "DISCONNECTED"  # never started AUTHENTICATING
    finally:
        _teardown_mgr(mgr)


def test_submit_tachibana_empty_fields_is_empty_fields():
    mgr, _, _ = _make_mgr("TACHIBANA")
    try:
        res = mgr.submit_venue_login("TACHIBANA", "demo", '{"auth_id": "", "key_path": ""}', "")
        assert (res["success"], res["error_code"]) == (False, "EMPTY_FIELDS")
    finally:
        _teardown_mgr(mgr)


def test_submit_kabu_station_not_running(monkeypatch):
    # Station down (probe False) → KABU_STATION_NOT_RUNNING with allow_retry False
    # (re-check the app first), surfaced before any auth.
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: False)
    mgr, _, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "pw123")
        assert res["success"] is False
        assert res["error_code"] == "KABU_STATION_NOT_RUNNING"
        assert res["allow_retry"] is False
        assert "kabuStation" in res["status_text"]
    finally:
        _teardown_mgr(mgr)


def test_submit_unknown_venue_and_invalid_env():
    mgr, _, _ = _make_mgr("KABU")
    try:
        assert mgr.submit_venue_login("MOCK", "verify", "{}", "x")["error_code"] == "UNKNOWN_VENUE"
        assert mgr.submit_venue_login("KABU", "bogus", "{}", "x")["error_code"] == "INVALID_ENV"
    finally:
        _teardown_mgr(mgr)


# --- submit_venue_login: failure rendering (status_text / allow_retry) --------

def test_submit_kabu_auth_failure_surfaces_status_text(monkeypatch):
    # A LoginSubmitFailure from headless auth is rendered verbatim by the modal:
    # error_code + Japanese status_text + allow_retry. The modal stays open to retry,
    # so the session must reset to DISCONNECTED (not stick at AUTHENTICATING).
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)

    async def _fail(api_password, mode):
        raise venue_login_headless.LoginSubmitFailure(
            "KABU_AUTH_REJECTED", "API パスワードが正しくありません。確認して再試行してください", True
        )

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _fail)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "wrong")
        assert res["success"] is False
        assert res["error_code"] == "KABU_AUTH_REJECTED"
        assert "API パスワード" in res["status_text"]
        assert res["allow_retry"] is True
        assert venue_sm.current == "DISCONNECTED"  # reset so the modal can retry
    finally:
        _teardown_mgr(mgr)


def test_submit_tachibana_auth_failure_surfaces_status_text(monkeypatch):
    async def _fail(auth_id, key_path, mode):
        raise venue_login_headless.LoginSubmitFailure(
            "SERVICE_OUT_OF_HOURS", "立花サーバーが現在サービス時間外です", True
        )

    monkeypatch.setattr(venue_login_headless, "authenticate_tachibana", _fail)
    mgr, venue_sm, _ = _make_mgr("TACHIBANA")
    try:
        res = mgr.submit_venue_login("TACHIBANA", "demo", '{"auth_id":"a","key_path":"b"}', "")
        assert (res["success"], res["error_code"]) == (False, "SERVICE_OUT_OF_HOURS")
        assert "サービス時間外" in res["status_text"]
        assert venue_sm.current == "DISCONNECTED"
    finally:
        _teardown_mgr(mgr)


# --- PRODGATE-01: prod reaches headless auth with no allow flag (ADR-0027) ----

@pytest.mark.scenario("PRODGATE-01")
def test_submit_prod_reaches_auth_without_allow_flag(monkeypatch):
    """ADR-0027 D1/D2: prod は env フラグ無しでも headless 認証へ到達する（front-stop 廃止）。

    旧挙動 (PROD_NOT_ALLOWED front-stop) を再導入すれば prod が auth に届かず RED。
    """
    monkeypatch.delenv("KABU_ALLOW_PROD", raising=False)
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)

    reached = {"prod": False}

    async def _fake_auth(api_password, mode):
        reached["prod"] = (mode == "prod")
        return "PROD_TOKEN"

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _fake_auth)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "prod", "{}", "pw")
        assert res["success"], res
        assert reached["prod"] is True  # prod was not front-stopped
        assert venue_sm.current == "CONNECTED"
    finally:
        _teardown_mgr(mgr)


# --- venue_login_form_init (prefill) -----------------------------------------

def test_form_init_kabu_returns_port_and_mode():
    mgr, _, _ = _make_mgr("KABU")
    try:
        verify = mgr.venue_login_form_init("KABU", "verify")
        prod = mgr.venue_login_form_init("KABU", "prod")
        assert verify["venue"] == "KABU" and verify["station_port"] == 18081
        assert prod["station_port"] == 18080 and prod["initial_mode"] == "prod"
        # prod never prefills the password (ADR-0033 D2).
        assert prod["api_password_prefill"] == ""
    finally:
        _teardown_mgr(mgr)


def test_form_init_tachibana_returns_mode():
    mgr, _, _ = _make_mgr("TACHIBANA")
    try:
        prod = mgr.venue_login_form_init("TACHIBANA", "prod")
        assert prod["venue"] == "TACHIBANA" and prod["initial_mode"] == "prod"
        # prod never prefills credentials (ADR-0033 D2).
        assert prod["auth_id_prefill"] == "" and prod["key_path_prefill"] == ""
    finally:
        _teardown_mgr(mgr)


# --- venue_login_probe_station ------------------------------------------------

def test_probe_station_kabu_reports_running_and_port(monkeypatch):
    seen = {}

    def _probe(host="127.0.0.1", port=18081):
        seen["port"] = port
        return port == 18081  # verify up, prod down

    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", _probe)
    mgr, _, _ = _make_mgr("KABU")
    try:
        verify = mgr.venue_login_probe_station("KABU", "verify")
        assert verify == {"venue": "KABU", "running": True, "port": 18081}
        prod = mgr.venue_login_probe_station("KABU", "prod")
        assert prod["running"] is False and prod["port"] == 18080
    finally:
        _teardown_mgr(mgr)


def test_probe_station_non_kabu_is_always_running():
    mgr, _, _ = _make_mgr("TACHIBANA")
    try:
        assert mgr.venue_login_probe_station("TACHIBANA", "demo")["running"] is True
    finally:
        _teardown_mgr(mgr)


# --- import purity: the orchestrator no longer touches tkinter (findings 0130) -

def test_orchestrator_has_no_tkinter_dialog_seam():
    # The retired in-proc dialog seam (#122) is gone: no _handle_prompt_login /
    # _try_create_tk, and no tkinter import in the live_orchestrator module.
    assert not hasattr(LiveLoopManager, "_handle_prompt_login")
    assert not hasattr(live_orchestrator, "_try_create_tk")
    import inspect
    src = inspect.getsource(live_orchestrator)
    assert "import tkinter" not in src and "from tkinter" not in src, (
        "live_orchestrator must not import tkinter (#181/ADR-0040)"
    )


# --- submit_venue_login: failure paths never strand AUTHENTICATING (review fixes) ---

def test_submit_timeout_resets_to_disconnected(monkeypatch):
    # An auth that blocks past _live_login_timeout_s → LOGIN_TIMEOUT, and venue_sm must
    # reset to DISCONNECTED (a stuck AUTHENTICATING would lock out every future login via
    # ALREADY_AUTHENTICATING). Re-introducing a stuck-state would flip this RED.
    import asyncio as _asyncio
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)
    monkeypatch.setattr(live_orchestrator, "_live_login_timeout_s", lambda: 0.1)

    async def _hang(api_password, mode):
        await _asyncio.sleep(5)
        return "TOK"

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _hang)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "pw")
        assert res["success"] is False
        assert res["error_code"] == "LOGIN_TIMEOUT"
        assert venue_sm.current == "DISCONNECTED"  # not stuck at AUTHENTICATING
    finally:
        _teardown_mgr(mgr)


def test_submit_finalize_raise_resets_not_stuck(monkeypatch):
    # If the shared _finalize_login raises (it dereferences the adapter / live loop OUTSIDE
    # its own try), submit must catch → reset → fail, NEVER leave venue_sm at AUTHENTICATING.
    # Litmus: drop the finalize try-guard in submit_venue_login and this raises/stuck-RED.
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)

    async def _ok(api_password, mode):
        return "TOK"

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _ok)
    mgr, venue_sm, _ = _make_mgr("KABU")

    def _boom(creds):
        raise RuntimeError("finalize blew up")

    monkeypatch.setattr(mgr, "_finalize_login", _boom)
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "pw")
        assert res["success"] is False
        assert res["error_code"] == "VENUE_LOGIN_FAILED"
        assert venue_sm.current == "DISCONNECTED"
    finally:
        _teardown_mgr(mgr)


def test_submit_kabu_empty_token_is_invalid_response(monkeypatch):
    # A success-but-blank token from fetch_token must surface LOGIN_INVALID_RESPONSE (not a
    # silent CONNECTED with a null bearer). Was covered by the deleted test_inproc_prompt_login.
    from engine.exchanges import kabusapi_auth
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)

    async def _empty(api_password, env="verify"):
        return ""

    monkeypatch.setattr(kabusapi_auth, "fetch_token", _empty)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        res = mgr.submit_venue_login("KABU", "verify", "{}", "pw")
        assert res["success"] is False
        assert res["error_code"] == "LOGIN_INVALID_RESPONSE"
        assert venue_sm.current == "DISCONNECTED"
    finally:
        _teardown_mgr(mgr)


def test_submit_idempotent_when_already_connected(monkeypatch):
    # The C# host calls set_execution_mode(LiveManual) AFTER a successful submit; if that
    # fails the modal stays open and the user may resubmit while Python is already CONNECTED.
    # The resubmit must be an idempotent no-op success, NOT a re-auth / live-session rebuild.
    monkeypatch.setattr(kabusapi_login_form_state, "probe_station", lambda **k: True)
    calls = {"n": 0}

    async def _ok(api_password, mode):
        calls["n"] += 1
        return "TOK"

    monkeypatch.setattr(venue_login_headless, "authenticate_kabu", _ok)
    mgr, venue_sm, _ = _make_mgr("KABU")
    try:
        first = mgr.submit_venue_login("KABU", "verify", "{}", "pw")
        assert first["success"] and venue_sm.current == "CONNECTED"
        second = mgr.submit_venue_login("KABU", "verify", "{}", "pw")
        assert second["success"] and second["error_code"] == ""
        assert venue_sm.current == "CONNECTED"
        assert calls["n"] == 1  # auth ran once; the retry short-circuited
    finally:
        _teardown_mgr(mgr)
