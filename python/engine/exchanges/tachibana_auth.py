from __future__ import annotations

import asyncio
import json
import logging
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Awaitable, Callable, Mapping, Optional

import httpx

from .tachibana_codec import decode_response_body
from .tachibana_url import (
    BASE_URL_DEMO,
    BASE_URL_PROD,
    EventUrl,
    MasterUrl,
    PriceUrl,
    RequestUrl,
    build_auth_url,
)

log = logging.getLogger(__name__)

__all__ = [
    "TachibanaError",
    "ApiError",
    "LoginError",
    "UnreadNoticesError",
    "SessionExpiredError",
    "current_p_sd_date",
    "check_response",
    "login",
    "validate_session_on_startup",
]


class TachibanaError(Exception):
    """Base exception for Tachibana API errors."""


class ApiError(TachibanaError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message


class LoginError(TachibanaError):
    """Raised when login fails."""

    def __init__(
        self,
        message: str = "login failed",
        *,
        code: str = "LOGIN_FAILED",
    ) -> None:
        super().__init__(message)
        self.message = message
        self.code = code


class UnreadNoticesError(TachibanaError):
    """Raised when the API reports unread notices (sKinsyouhouMidokuFlg=1)."""

    def __init__(
        self,
        message: str = "unread notices flag set",
        *,
        code: str = "UNREAD_NOTICES",
    ) -> None:
        super().__init__(message)
        self.message = message
        self.code = code


class SessionExpiredError(ApiError):
    """Raised when the session has expired (p_errno=2)."""


_JST = timezone(timedelta(hours=9))


def current_p_sd_date() -> str:
    now = datetime.now(_JST)
    ms = now.microsecond // 1000
    return f"{now.year:04d}.{now.month:02d}.{now.day:02d}-{now.hour:02d}:{now.minute:02d}:{now.second:02d}.{ms:03d}"


def check_response(payload: Mapping[str, Any]) -> None:
    p_errno = payload.get("p_errno", "")
    if p_errno == "2":
        raise SessionExpiredError("2", "session expired")
    if p_errno not in ("", "0"):
        raise ApiError(p_errno, f"p_errno={p_errno}")

    s_result_code = payload.get("sResultCode", "0")
    if s_result_code != "0":
        raise ApiError(s_result_code, f"sResultCode={s_result_code}")

    midoku = payload.get("sKinsyouhouMidokuFlg", "0")
    if midoku == "1":
        raise UnreadNoticesError("unread notices flag set")


# ---------------------------------------------------------------------------
# Phase 8 A1: PNoCounter / TachibanaSession
# ---------------------------------------------------------------------------


class PNoCounter:
    """Per-instance monotonic `p_no` generator."""

    __slots__ = ("_value",)

    def __init__(self) -> None:
        self._value = int(time.time())

    def next(self) -> int:
        self._value += 1
        return self._value

    def peek(self) -> int:
        return self._value

    def restore(self, value: int) -> None:
        if value > self._value:
            self._value = value

    def fast_forward(self, value: int) -> None:
        """Advance the counter past ``value`` so the next ``next()`` exceeds it.

        Unlike ``restore``, this guarantees forward progress even when the
        current ``_value`` is already greater than ``value`` (clock skew between
        the subprocess that consumed p_no and this counter's ``time.time()``
        seed). Required for the session_cache resume path (R4 monotonic).
        """
        self._value = max(self._value, value) + 1


@dataclass(frozen=True, slots=True)
class TachibanaSession:
    """Result of a successful login."""

    url_request: RequestUrl
    url_master: MasterUrl
    url_price: PriceUrl
    url_event: EventUrl
    url_event_ws: str
    zyoutoeki_kazei_c: str
    expires_at_ms: Optional[int] = None


# ---------------------------------------------------------------------------
# Phase 8 A1.3: login / validate_session_on_startup (stub for A1.3b RED tests)
# ---------------------------------------------------------------------------


def _validate_virtual_urls(payload: dict[str, Any]) -> None:
    """REST URLs must be https://, WS must be wss://.

    Virtual URLs are session-secret (they embed the ND= token), so errors
    route through mask_secrets() rather than logging the raw URL.
    """
    from engine.live.logging import mask_secrets

    for key in ("sUrlRequest", "sUrlMaster", "sUrlPrice", "sUrlEvent"):
        url = payload.get(key, "")
        if not isinstance(url, str) or not url.startswith("https://"):
            log.error(
                "tachibana login: %s did not start with https:// (masked=%r)",
                key,
                mask_secrets({key: url}),
            )
            raise LoginError("virtual URL invalid", code="login_failed")
    ws = payload.get("sUrlEventWebSocket", "")
    if not isinstance(ws, str) or not ws.startswith("wss://"):
        log.error(
            "tachibana login: sUrlEventWebSocket did not start with wss:// "
            "(masked=%r)",
            mask_secrets({"sUrlEventWebSocket": ws}),
        )
        raise LoginError("virtual URL invalid", code="login_failed")


def _decode_json(body: bytes) -> dict[str, Any]:
    text = decode_response_body(body)
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        log.error("tachibana login: JSON parse failed: %s", exc)
        raise LoginError("login response parse failed", code="login_failed") from exc
    if not isinstance(data, dict):
        log.error(
            "tachibana login: response is not a JSON object (got %s)",
            type(data).__name__,
        )
        raise LoginError("login response parse failed", code="login_failed")
    return data


async def _safe_get(client: httpx.AsyncClient, url: str) -> bytes:
    """GET url; map any HTTP / network failure to LoginError(transport_error).

    Without raise_for_status() a 502 / 503 / proxy HTML response would flow
    into _decode_json and surface as "JSON parse failed", burying the real
    transport problem.
    """
    try:
        resp = await client.get(url)
        resp.raise_for_status()
    except httpx.HTTPStatusError as exc:
        log.error(
            "tachibana login: HTTP %s from server (body prefix=%r)",
            exc.response.status_code,
            exc.response.content[:200],
        )
        raise LoginError(
            "transport error", code="transport_error"
        ) from exc
    except httpx.HTTPError as exc:
        log.error("tachibana login: transport failure: %s", exc)
        raise LoginError(
            "transport error", code="transport_error"
        ) from exc
    return resp.content


async def login(
    user_id: str,
    password: str,
    *,
    is_demo: bool,
    p_no_counter: PNoCounter,
    http_client: Optional[httpx.AsyncClient] = None,
) -> TachibanaSession:
    """Issue CLMAuthLoginRequest and return a TachibanaSession.

    p_no_counter is required so retries / startup re-login never reuse a
    p_no already accepted by the server (R4 monotonic contract).

    Raises:
        UnreadNoticesError: sKinsyouhouMidokuFlg=='1' (code='unread_notices').
        SessionExpiredError: p_errno=='2' (code='session_expired').
        LoginError: any other auth-time failure. code is one of
            the upstream p_errno / sResultCode string, '-62'
            (service hours), 'transport_error', or 'login_failed'.
    """
    # R10 / INV-T3-SECRET: この auth login は adapter を経由しない経路 (login dialog
    # subprocess の tachibana_login_flow._run_auth) からも直接呼ばれる。sUserId/sPassword
    # は R2 により URL に乗るため、request を投げる前に httpx/httpcore の request ログを
    # 沈黙させる。adapter.__init__ の抑制だけでは dialog 経路を覆えない (#19 / findings 0009)。
    from engine.live.logging import suppress_third_party_http_logs
    suppress_third_party_http_logs()

    base = BASE_URL_DEMO if is_demo else BASE_URL_PROD
    payload: dict[str, Any] = {
        "p_no": str(p_no_counter.next()),
        "p_sd_date": current_p_sd_date(),
        "sCLMID": "CLMAuthLoginRequest",
        "sUserId": user_id,
        "sPassword": password,
    }
    url = build_auth_url(base, payload, sJsonOfmt="5")

    own_client = http_client is None
    # Per-component timeouts: on Windows a scalar timeout does not bound
    # the connect phase when the virtual URL has expired (DNS resolves but
    # TCP SYN never gets a reply), causing a silent hang.
    _DEFAULT_TIMEOUT = httpx.Timeout(connect=10.0, read=30.0, write=10.0, pool=5.0)
    client = http_client or httpx.AsyncClient(timeout=_DEFAULT_TIMEOUT)
    try:
        body = await _safe_get(client, url)
    finally:
        if own_client:
            await client.aclose()

    data = _decode_json(body)
    check_response(data)
    _validate_virtual_urls(data)

    return TachibanaSession(
        url_request=RequestUrl(data["sUrlRequest"]),
        url_master=MasterUrl(data["sUrlMaster"]),
        url_price=PriceUrl(data["sUrlPrice"]),
        url_event=EventUrl(data["sUrlEvent"]),
        url_event_ws=data["sUrlEventWebSocket"],
        zyoutoeki_kazei_c=str(data.get("sZyoutoekiKazeiC", "")),
        expires_at_ms=None,
    )


async def validate_session_on_startup(
    request: Callable[[Mapping[str, Any]], Awaitable[Mapping[str, Any]]],
) -> None:
    """Probe a restored session's liveness with a read-only REQUEST (#35).

    `is_session_valid_for_today()` only checks the cached JST date, which is
    necessary but not sufficient: a same-day session can still be dead (night
    market-close crossing / server invalidation) and reply ``p_errno="2"``.
    Restoring such a corpse would arm the order path on a logged-out session
    (CONTEXT「セッション当日有効 / セッション生存」).

    `request` issues a Tachibana REQUEST (the adapter's bound ``_request``) and
    returns the decoded response dict. We fire the cheapest authenticated read
    (``CLMZanKaiKanougaku`` 買余力 — pure read, no order side effect) and route
    it through ``check_response``, so a dead virtual URL surfaces as
    ``SessionExpiredError`` before any order can be placed.

    Returns None when the session is alive. Raises ``SessionExpiredError``
    (``p_errno="2"``) or ``ApiError`` (other business-level codes).
    """
    resp = await request(
        {"sCLMID": "CLMZanKaiKanougaku", "sIssueCode": "", "sSizyouC": ""}
    )
    check_response(resp)
