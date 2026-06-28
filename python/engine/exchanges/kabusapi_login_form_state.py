"""kabuStation login form state — pure presenter logic, no tkinter dependency."""
from __future__ import annotations
import os
import socket
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class FormInit:
    env_hint: str
    station_port: int
    api_password_prefill: str  # ADR-0033: verify only, debug build only; else ""


KABU_STATION_NOT_RUNNING = "KABU_STATION_NOT_RUNNING"
# ポートは listen しているが本体がブローカーへ未ログイン (kabu code 4001007/4001017)。
# 「アプリ起動済み」≠「口座ログイン済み」。kabu は早朝に本体を強制ログアウトする (findings 0109)。
KABU_STATION_NOT_LOGGED_IN = "KABU_STATION_NOT_LOGGED_IN"
KABU_API_DISABLED = "KABU_API_DISABLED"
# login ダイアログの唯一の auth 呼び出しは /token (fetch_token)。その HTTP 401 実体は kabu
# code 4001013「kabuステーションはログイン済みだが API パスワードが不正」(ptal/error.html) で、
# トークン失効ではない (発行時点で失効すべき既存トークンは無い)。よって「期限切れ」ではなく
# 「パスワード不正」として提示する (findings 0109 / D1)。
KABU_AUTH_REJECTED = "KABU_AUTH_REJECTED"
AUTH_FAILED = "AUTH_FAILED"
USER_CANCELLED = "USER_CANCELLED"
EMPTY_FIELDS = "EMPTY_FIELDS"


def _verify_prefill() -> str:
    """Resolve the verify API password for prefill (ADR-0033 D1/D3/D4).

    Returns ``DEV_KABU_API_PASSWORD`` in debug builds, else ``""`` (release build
    or key absent). Never reads the prod key ``PROD_KABU_API_PASSWORD`` (D2). The
    env read lives here in the pure presenter so the login surface stays env-free
    (PRODGATE-08).
    """
    from engine.live._build_mode import IS_DEBUG_BUILD

    if not IS_DEBUG_BUILD:
        return ""
    # engine.paths autoloads .env into os.environ on import (setdefault).
    import engine.paths  # noqa: F401

    return (os.environ.get("DEV_KABU_API_PASSWORD") or "").strip()


def build_form_init(env_hint: str) -> FormInit:
    """Presenter state for the kabu login dialog.

    ADR-0027: prod 解禁の env ゲート (KABU_ALLOW_PROD) は廃止。ポートは env_hint
    だけで決める (prod=18080 / verify=18081)。

    ADR-0033: verify モードの API パスワードは debug ビルドのとき .env から prefill
    する (D1)。prod モードは prefill せず常にユーザー入力 (D2)。#181/ADR-0040: Unity モーダルは
    モード切替のたびに venue_login_form_init→build_form_init(mode) を呼び直して prefill を再導出する。
    """
    station_port = 18080 if env_hint == "prod" else 18081
    api_password_prefill = "" if env_hint == "prod" else _verify_prefill()
    return FormInit(
        env_hint=env_hint,
        station_port=station_port,
        api_password_prefill=api_password_prefill,
    )


def probe_station(host: str = "127.0.0.1", port: int = 18081) -> bool:
    """Return True if kabuStation is listening on the given port."""
    try:
        with socket.create_connection((host, port), timeout=0.5):
            return True
    except (ConnectionRefusedError, OSError, TimeoutError):
        return False


def validate_submission(api_password: str) -> Optional[str]:
    if not api_password.strip():
        return EMPTY_FIELDS
    return None


@dataclass(frozen=True)
class AuthFailureView:
    """auth 失敗時の UI 提示。allow_retry=False なら OK を disabled のまま据え置き、
    recheck_btn で本体設定変更後の再確認を促す。"""
    status_text: str
    allow_retry: bool


def auth_failure_view(error_code: str) -> AuthFailureView:
    if error_code == KABU_API_DISABLED:
        return AuthFailureView(
            status_text="kabuStation 本体の API 設定を有効化し『再確認』を押してください",
            allow_retry=False,
        )
    if error_code == KABU_STATION_NOT_LOGGED_IN:
        return AuthFailureView(
            status_text="kabuステーション本体が口座にログインしていません。本体でログインしてから再試行してください",
            allow_retry=True,
        )
    if error_code == KABU_AUTH_REJECTED:
        return AuthFailureView(
            status_text="API パスワードが正しくありません。確認して再試行してください",
            allow_retry=True,
        )
    if error_code == KABU_STATION_NOT_RUNNING:
        return AuthFailureView(
            status_text=f"{KABU_STATION_NOT_RUNNING}: kabuStation を起動し『再確認』を押してください",
            allow_retry=False,
        )
    return AuthFailureView(
        status_text=f"ログイン失敗: {error_code}",
        allow_retry=True,
    )
