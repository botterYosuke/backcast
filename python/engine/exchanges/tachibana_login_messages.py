from __future__ import annotations

import logging
from typing import TYPE_CHECKING, Any, Mapping, NoReturn

from .tachibana_auth import ApiError, LoginError, SessionExpiredError, UnreadNoticesError

if TYPE_CHECKING:
    from .tachibana_auth import TachibanaError

log = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# User-facing banner text (F-Banner1 / architecture.md §6)
# ---------------------------------------------------------------------------
# Python composes the entire LoginError.message; Rust UI prints it verbatim.
# Server-side p_err / sResultText is intentionally NOT propagated to the user
# (it leaks Tachibana-internal English / inconsistent wording that breaks the
# Japanese banner contract) and is only logged for triage.

_MSG_LOGIN_FAILED = "ログインに失敗しました。認証 ID / 秘密鍵 / 公開鍵登録を確認してください"
_MSG_SERVICE_OUT_OF_HOURS = (
    "立花サーバーが現在サービス時間外です（デモ環境は平日 8:00–18:00 JST）。"
    "時間内に再ログインしてください"
)
_MSG_SESSION_EXPIRED_STARTUP = (
    "立花のセッションが切れました（夜間閉局）。再ログインしてください"
)
_MSG_TRANSPORT_ERROR = (
    "立花サーバとの通信に失敗しました。ネットワーク / プロキシ設定を確認してください"
)
_MSG_LOGIN_PARSE_FAILED = "立花ログイン応答の形式が不正です。サポートに連絡してください"
_MSG_VIRTUAL_URL_INVALID = (
    "立花ログイン応答の URL が想定と異なります。サポートに連絡してください"
)
_MSG_UNREAD_NOTICES = "未読の重要通知があります。e-shiten Web で確認後に再ログインしてください"

# p_errno codes indicating "server is currently outside service hours"
# rather than a credential problem. -62 = システムサービス時間外（旧）、
# 9 = システム・サービス停止中（利用時間外, v4r9〜 / ServiceOutOfHoursError）。
_SERVICE_OUT_OF_HOURS_CODES = frozenset({"-62", "9"})


def raise_for_login_error(exc: Exception) -> NoReturn:
    """Convert raw Tachibana exceptions to login-banner-shaped exceptions.

    F-Banner1: Python composes the user-facing message; server-side
    p_err / sResultText is logged but never reaches the UI.
    """
    if isinstance(exc, SessionExpiredError):
        raise SessionExpiredError("session_expired", _MSG_SESSION_EXPIRED_STARTUP) from exc
    if isinstance(exc, UnreadNoticesError):
        raise UnreadNoticesError(_MSG_UNREAD_NOTICES, code="unread_notices") from exc
    if isinstance(exc, ApiError):
        log.error(
            "tachibana login: API error code=%r server_message=%r",
            exc.code,
            exc.message,
        )
        if exc.code in _SERVICE_OUT_OF_HOURS_CODES:
            raise LoginError(_MSG_SERVICE_OUT_OF_HOURS, code=exc.code) from exc
        raise LoginError(_MSG_LOGIN_FAILED, code=exc.code) from exc

    if isinstance(exc, LoginError):
        # Already a LoginError, but might need banner mapping if it's a raw one
        # from tachibana_auth.py helpers.
        if exc.code == "transport_error":
            raise LoginError(_MSG_TRANSPORT_ERROR, code="transport_error") from exc
        if exc.code == "login_parse_failed":
            raise LoginError(_MSG_LOGIN_PARSE_FAILED, code="login_failed") from exc
        if exc.code == "virtual_url_invalid":
            raise LoginError(_MSG_VIRTUAL_URL_INVALID, code="login_failed") from exc

    # Fallback for unexpected exceptions
    raise exc
