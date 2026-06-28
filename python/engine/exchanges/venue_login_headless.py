"""Headless venue authentication (#181 / ADR-0040).

The venue-login GUI now lives in Unity (uGUI modal); Python only authenticates,
*headless*, with the form values the modal collected. This module is the GUI-free
heart of the retired tkinter ``*_login_flow.py`` dialogs — the same auth calls
(``tachibana_auth.login`` + ``save_session`` / ``kabusapi_auth.fetch_token``) and the
same exception→error_code→Japanese-message mapping, with no ``tkinter`` import.

``LiveLoopManager.submit_venue_login`` (the modal's submit RPC) calls these on the
live loop. On failure they raise :class:`LoginSubmitFailure`, which carries the
error_code plus the Japanese ``status_text`` and ``allow_retry`` the modal renders.
No secret value is logged or returned except the kabu bearer token (in-memory).
"""
from __future__ import annotations

import logging
from datetime import datetime
from typing import Any
from zoneinfo import ZoneInfo

from engine.exchanges.tachibana_login_form_state import (
    AUTH_FAILED,
    NETWORK_ERROR,
    SERVICE_OUT_OF_HOURS,
)
from engine.exchanges.kabusapi_login_form_state import auth_failure_view

log = logging.getLogger(__name__)


class LoginSubmitFailure(Exception):
    """A headless auth attempt failed in a way the modal renders and retries.

    ``error_code`` is the stable code (gated/asserted); ``status_text`` is the
    Japanese line shown in red inside the modal; ``allow_retry`` tells the modal
    whether to re-enable OK (False = fix the venue/app first, use 再確認).
    """

    def __init__(self, error_code: str, status_text: str, allow_retry: bool) -> None:
        super().__init__(error_code)
        self.error_code = error_code
        self.status_text = status_text
        self.allow_retry = allow_retry


# --- Tachibana ---------------------------------------------------------------

def _map_tachibana_exception(exc: BaseException) -> str:
    """Map tachibana_auth exceptions to login error_code strings (was tachibana_login_flow)."""
    try:
        from engine.exchanges.tachibana_auth import (
            ApiError,
            LoginError,
            ServiceOutOfHoursError,
            SessionExpiredError,
            UnreadNoticesError,
        )
    except Exception:  # pragma: no cover - defensive
        return AUTH_FAILED
    import httpx as _httpx

    if isinstance(exc, ServiceOutOfHoursError):
        return SERVICE_OUT_OF_HOURS
    if isinstance(exc, SessionExpiredError):
        return AUTH_FAILED
    if isinstance(exc, UnreadNoticesError):
        return AUTH_FAILED
    if isinstance(exc, ApiError) and getattr(exc, "code", None) == "-62":
        return SERVICE_OUT_OF_HOURS
    if isinstance(exc, LoginError):
        code = getattr(exc, "code", "") or ""
        if code in ("-62",):
            return SERVICE_OUT_OF_HOURS
        if code in ("transport_error",):
            return NETWORK_ERROR
        return AUTH_FAILED
    if isinstance(exc, (_httpx.ConnectError, _httpx.ReadError, _httpx.TimeoutException)):
        return NETWORK_ERROR
    return AUTH_FAILED


def _tachibana_status_text(exc: BaseException, error_code: str) -> str:
    """Compose the Japanese banner from raise_for_login_error (F-Banner1)."""
    try:
        from engine.exchanges.tachibana_login_messages import raise_for_login_error
        raise_for_login_error(exc)
    except Exception as banner_exc:
        return getattr(banner_exc, "message", f"ログイン失敗: {error_code}")
    return f"ログイン失敗: {error_code}"


async def authenticate_tachibana(auth_id: str, key_path: str, mode: str) -> None:
    """Log in to Tachibana and persist the session URLs to disk (session_cache).

    Mirrors the retired dialog's ``_run_auth`` + ``_on_auth_done`` ("ok" branch):
    load the PEM, ``tachibana_auth.login``, then ``save_session``. Returns nothing —
    the adapter's later ``credentials_source="session_cache"`` login re-reads the
    persisted session. Raises :class:`LoginSubmitFailure` on any failure.
    """
    from engine.exchanges.tachibana_auth import PNoCounter, login as _auth_login
    from engine.exchanges.tachibana_pubkey import load_private_key_from_file

    p_no_counter = PNoCounter()
    try:
        # PEM → RSA key (utf-8-sig BOM-safe; missing-file → PubkeyCryptoError, same
        # helper the credentials path uses).
        private_key = load_private_key_from_file(key_path)
        session = await _auth_login(
            auth_id,
            private_key,
            is_demo=(mode == "demo"),
            p_no_counter=p_no_counter,
        )
    except BaseException as exc:  # noqa: BLE001 - surfaced via LoginSubmitFailure
        log.error("tachibana headless login failed: %r", exc)
        ec = _map_tachibana_exception(exc)
        raise LoginSubmitFailure(ec, _tachibana_status_text(exc, ec), allow_retry=True) from exc

    try:
        from engine.exchanges.tachibana_file_store import save_session
        save_session(
            {
                "url_request": str(session.url_request),
                "url_master": str(session.url_master),
                "url_price": str(session.url_price),
                "url_event": str(session.url_event),
                "url_event_ws": session.url_event_ws,
                "zyoutoeki_kazei_c": session.zyoutoeki_kazei_c,
                "last_p_no": p_no_counter.peek(),
                "issued_jst_date": datetime.now(ZoneInfo("Asia/Tokyo")).date().isoformat(),
            }
        )
    except Exception as exc:
        log.exception("venue_login_headless: tachibana save_session failed")
        raise LoginSubmitFailure(
            AUTH_FAILED, f"ログイン失敗: {AUTH_FAILED}", allow_retry=True
        ) from exc


# --- kabuStation -------------------------------------------------------------

def _map_kabu_exception(exc: BaseException) -> str:
    """Map kabusapi_auth exceptions to login error_code strings (was kabusapi_login_flow)."""
    from engine.exchanges.kabusapi_login_form_state import (
        AUTH_FAILED as KABU_AUTH_FAILED,
        KABU_API_DISABLED,
        KABU_AUTH_REJECTED,
        KABU_STATION_NOT_LOGGED_IN,
        KABU_STATION_NOT_RUNNING,
    )
    try:
        from engine.exchanges.kabusapi_auth import (
            KabuApiError,
            KabuConnectionError,
            KabuTokenExpiredError,
        )
    except Exception:  # pragma: no cover
        return KABU_AUTH_FAILED

    if isinstance(exc, KabuTokenExpiredError):
        # The login flow's only auth call is /token; its HTTP 401 splits by body code
        # (findings 0109): STATION_LOGGED_OUT_CODES → app running but not logged into
        # the account; else → logged in but the API password is wrong.
        from engine.exchanges.kabusapi_auth import STATION_LOGGED_OUT_CODES

        body_code = getattr(exc, "body_code", None)
        if body_code in STATION_LOGGED_OUT_CODES:
            return KABU_STATION_NOT_LOGGED_IN
        return KABU_AUTH_REJECTED
    if isinstance(exc, KabuConnectionError):
        return KABU_STATION_NOT_RUNNING
    if isinstance(exc, KabuApiError):
        code = getattr(exc, "code", None)
        if code in (4001003, "4001003"):
            return KABU_API_DISABLED
        return KABU_AUTH_FAILED
    return KABU_AUTH_FAILED


async def authenticate_kabu(api_password: str, mode: str) -> str:
    """Fetch the kabuStation bearer token (in-memory; never written to a file).

    Mirrors the retired dialog's ``_run_auth``. Returns the token on success; raises
    :class:`LoginSubmitFailure` on failure (status_text/allow_retry from auth_failure_view).
    """
    from engine.exchanges.kabusapi_auth import fetch_token

    env_arg: Any = "prod" if mode == "prod" else "verify"
    try:
        token = await fetch_token(api_password, env=env_arg)
    except BaseException as exc:  # noqa: BLE001 - surfaced via LoginSubmitFailure
        log.error("kabu headless login failed: %r", exc)
        ec = _map_kabu_exception(exc)
        view = auth_failure_view(ec)
        raise LoginSubmitFailure(ec, view.status_text, view.allow_retry) from exc

    if not isinstance(token, str) or not token:
        raise LoginSubmitFailure(
            "LOGIN_INVALID_RESPONSE",
            "ログイン応答が不正です（トークンが空）。再試行してください",
            allow_retry=True,
        )
    return token
