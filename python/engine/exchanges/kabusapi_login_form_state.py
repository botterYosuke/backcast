"""kabuStation login form state — pure presenter logic, no tkinter dependency."""
from __future__ import annotations
import socket
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class FormInit:
    env_hint: str
    station_port: int


KABU_STATION_NOT_RUNNING = "KABU_STATION_NOT_RUNNING"
# ポートは listen しているが本体がブローカーへ未ログイン (kabu code 4001007/4001017)。
# 「アプリ起動済み」≠「口座ログイン済み」。kabu は早朝に本体を強制ログアウトする (findings 0106)。
KABU_STATION_NOT_LOGGED_IN = "KABU_STATION_NOT_LOGGED_IN"
KABU_API_DISABLED = "KABU_API_DISABLED"
# login ダイアログの唯一の auth 呼び出しは /token (fetch_token)。その HTTP 401 実体は kabu
# code 4001013「kabuステーションはログイン済みだが API パスワードが不正」(ptal/error.html) で、
# トークン失効ではない (発行時点で失効すべき既存トークンは無い)。よって「期限切れ」ではなく
# 「パスワード不正」として提示する (findings 0106 / D1)。
KABU_AUTH_REJECTED = "KABU_AUTH_REJECTED"
AUTH_FAILED = "AUTH_FAILED"
USER_CANCELLED = "USER_CANCELLED"
EMPTY_FIELDS = "EMPTY_FIELDS"


def build_form_init(env_hint: str) -> FormInit:
    """Presenter state for the kabu login dialog.

    ADR-0027: prod 解禁の env ゲート (KABU_ALLOW_PROD) と debug ビルドの DEV_*
    prefill は廃止。ポートは env_hint だけで決め (prod=18080 / verify=18081)、
    API パスワードは常にユーザーが入力する (空欄で開く / D2・D3)。
    """
    station_port = 18080 if env_hint == "prod" else 18081
    return FormInit(env_hint=env_hint, station_port=station_port)


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
