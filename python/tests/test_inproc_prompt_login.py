"""#122 regression — venue login runs IN-PROCESS (no subprocess).

Original bug (VENUE_LOGIN_FAILED): the embedded marimo server installs
``WindowsSelectorEventLoopPolicy`` globally, so ``_ensure_live_loop`` produced a
SelectorEventLoop — which on Windows cannot spawn subprocesses. The login dialog
was launched via ``create_subprocess_exec`` on that loop, so it raised
``NotImplementedError`` and the orchestrator swallowed it into the catch-all
``VENUE_LOGIN_FAILED`` (token / API password / kabuStation login state were all
irrelevant). After #122 the dialog runs on a dedicated in-process thread, so
login no longer depends on the loop being subprocess-capable.

These tests pin the death-angle the bug slipped through: every *other* login
test drives ``credentials_source="env"`` and never touches the dialog seam, and
no test exercised the (selector-loop × prompt-path) intersection.

Forward guard, not a mechanical RED: these tests monkeypatch the NEW in-process
``run_dialog`` seam, so they cannot be made RED by simply running them against the
old subprocess code (a monkeypatch never reaches across a subprocess boundary).
They lock the post-#122 behaviour going forward. The genuine RED *was* observed
during #122 development: on a Windows selector loop the (selector-loop × prompt)
path raised NotImplementedError before the in-process seam landed.
"""
from __future__ import annotations

import asyncio
import sys
import threading
import time

import pytest

from engine.live import live_orchestrator
from engine.live.live_orchestrator import LiveLoopManager
from engine.exchanges import kabusapi_login_flow, tachibana_login_flow
from engine.exchanges._login_dialog import apply_cancel_timeout


def _run_on_selector_loop(coro):
    """Run *coro* on a fresh SelectorEventLoop — the exact loop type marimo's
    ``WindowsSelectorEventLoopPolicy`` forces (no Windows subprocess support)."""
    loop = asyncio.SelectorEventLoop()
    try:
        return loop.run_until_complete(coro)
    finally:
        loop.close()


def _bare_manager() -> LiveLoopManager:
    # _handle_prompt_login touches no instance state, so skip the heavy ctor.
    return LiveLoopManager.__new__(LiveLoopManager)


def test_kabu_prompt_login_returns_token_in_memory_on_selector_loop(monkeypatch):
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        kabusapi_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": True, "error_code": "", "token": "TEST_KABU_TOKEN"},
    )
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, ec) == (True, "")
    assert token == "TEST_KABU_TOKEN"  # in-memory, never via a cred-path file


def test_tachibana_prompt_login_succeeds_on_selector_loop(monkeypatch):
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        tachibana_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": True, "error_code": ""},
    )
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("TACHIBANA", "demo")
    )
    assert (success, ec) == (True, "")
    assert token is None  # tachibana uses session_cache on disk, no token return


def test_kabu_prompt_login_auth_failed_returns_error_code(monkeypatch):
    # A dialog reporting a failed auth must surface its error_code verbatim
    # (not be remapped) — the orchestrator owns the run_dialog result contract.
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        kabusapi_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": False, "error_code": "AUTH_FAILED", "token": None},
    )
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, ec, token) == (False, "AUTH_FAILED", None)


def test_kabu_prompt_login_user_cancelled_returns_error_code(monkeypatch):
    # A user-cancelled dialog degrades to its USER_CANCELLED code, not a crash.
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        kabusapi_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": False, "error_code": "USER_CANCELLED", "token": None},
    )
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, ec, token) == (False, "USER_CANCELLED", None)


def test_prompt_login_dialog_crash_degrades_to_error_code(monkeypatch):
    # Tk()/mainloop failure must NOT take down the host — it degrades to an
    # error_code (the only mitigation for losing subprocess crash isolation).
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)

    def _boom(env_hint, cancel_event=None):
        raise RuntimeError("Tk mainloop exploded")

    monkeypatch.setattr(kabusapi_login_flow, "run_dialog", _boom)
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, ec, token) == (False, "VENUE_LOGIN_FAILED", None)  # degraded, host survived


def test_prompt_login_no_display_degrades_to_error_code(monkeypatch):
    # Headless host (no Tk display): the in-proc dispatcher owns the
    # NO_DISPLAY_AVAILABLE verdict (relocated from login_dialog_runner).
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: False, raising=False)
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, token) == (False, None)
    assert ec == "NO_DISPLAY_AVAILABLE"


@pytest.mark.skipif(sys.platform != "win32", reason="ProactorEventLoop is Windows-only")
def test_ensure_live_loop_is_proactor_under_marimo_selector_policy():
    # Defense-in-depth: #122 removes the login subprocess, but _ensure_live_loop
    # still restores Windows' default ProactorEventLoop that marimo's selector
    # policy clobbered — so any future async subprocess on the live loop is safe.
    prev = asyncio.get_event_loop_policy()
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())
    try:
        mgr = _bare_manager()
        mgr._live_loop = None
        mgr._live_thread = None
        loop = mgr._ensure_live_loop()
        try:
            assert isinstance(loop, asyncio.ProactorEventLoop)
        finally:
            loop.call_soon_threadsafe(loop.stop)
            mgr._live_thread.join(timeout=5)
            loop.close()
    finally:
        asyncio.set_event_loop_policy(prev)


def test_prompt_login_hang_dialog_times_out_to_login_timeout(monkeypatch):
    # A dialog that never returns must not hang the live loop: the inner
    # asyncio.wait_for fires LOGIN_TIMEOUT (the subprocess path used proc.kill()).
    monkeypatch.setenv("LIVE_LOGIN_TIMEOUT_S", "0.4")
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    release = threading.Event()

    def _hang(env_hint, cancel_event=None):
        release.wait(timeout=5)  # simulate a dialog blocked on human input
        return {"success": False, "error_code": "USER_CANCELLED", "token": None}

    monkeypatch.setattr(kabusapi_login_flow, "run_dialog", _hang)
    try:
        t0 = time.monotonic()
        success, ec, token = _run_on_selector_loop(
            _bare_manager()._handle_prompt_login("KABU", "verify")
        )
        elapsed = time.monotonic() - t0
        assert (success, ec, token) == (False, "LOGIN_TIMEOUT", None)
        assert elapsed < 3.0  # returned on the inner timeout, did not block on the hang
    finally:
        release.set()


def test_venue_login_prompt_reaches_connected(monkeypatch):
    # End-to-end: venue_login("KABU","prompt",...) drives _handle_prompt_login via
    # run_coroutine_threadsafe on the real live loop, injects the token into the
    # adapter, and converges venue_sm to CONNECTED. Every other login test uses
    # credentials_source="env" and skips this dialog path.
    from engine.core import DataEngine
    from engine.live.mock_adapter import MockVenueAdapter
    from engine.live.state_machine import VenueStateMachine
    from engine.mode_manager import ModeManager

    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        kabusapi_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": True, "error_code": "", "token": "TOK"},
    )
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
        live_venue_id="KABU",
        engine_controller=None,
        publish_backend_event_callback=lambda ev: None,
    )
    try:
        res = mgr.venue_login("KABU", "prompt", "verify")
        assert res.success, res.error_code
        assert venue_sm.current == "CONNECTED"
        assert shared_mock.is_logged_in
    finally:
        try:
            mgr.venue_logout()
        except Exception:
            pass
        try:
            mgr.stop_live_loop(timeout=3.0)
        except Exception:
            pass


# --- M5: dispatcher pure-validation branches (no display, no real dialog) -----

def test_dispatcher_unknown_venue():
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("MOCK", "verify")
    )
    assert (success, ec, token) == (False, "UNKNOWN_VENUE", None)


def test_dispatcher_invalid_env():
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "bogus")
    )
    assert (success, ec, token) == (False, "INVALID_ENV", None)


def test_dispatcher_prod_not_allowed(monkeypatch):
    monkeypatch.delenv("KABU_ALLOW_PROD", raising=False)
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "prod")
    )
    assert (success, ec, token) == (False, "PROD_NOT_ALLOWED", None)


def test_dispatcher_kabu_missing_token_is_invalid_response(monkeypatch):
    # #122-new validation: a kabu dialog reporting success but no token is a
    # LOGIN_INVALID_RESPONSE, never a silent CONNECTED with a null bearer.
    monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True, raising=False)
    monkeypatch.setattr(
        kabusapi_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": True, "error_code": "", "token": ""},
    )
    success, ec, token = _run_on_selector_loop(
        _bare_manager()._handle_prompt_login("KABU", "verify")
    )
    assert (success, ec, token) == (False, "LOGIN_INVALID_RESPONSE", None)


# --- M4: the cancel-close decision (the close path _poll_cancel applies) -------
# The real root.destroy() wiring is display-bound (owner HITL, findings 0093 §HITL);
# the *decision* — cancel signalled ⇒ record LOGIN_TIMEOUT and clear any token — is
# factored into apply_cancel_timeout so it is deterministically unit-testable.

def test_apply_cancel_timeout_kabu_clears_token():
    # kabu result carries a token key → must be cleared on a cancelled-timeout close.
    result = {"success": True, "error_code": "", "token": "LEAK"}
    apply_cancel_timeout(result)
    assert result == {"success": False, "error_code": "LOGIN_TIMEOUT", "token": None}


def test_apply_cancel_timeout_tachibana_has_no_token_key():
    # tachibana result has no token key → helper must not invent one.
    result = {"success": True, "error_code": ""}
    apply_cancel_timeout(result)
    assert result == {"success": False, "error_code": "LOGIN_TIMEOUT"}
