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
KABU_API_DISABLED = "KABU_API_DISABLED"
KABU_TOKEN_EXPIRED = "KABU_TOKEN_EXPIRED"
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
    if error_code == KABU_TOKEN_EXPIRED:
        return AuthFailureView(
            status_text="トークン期限切れ。API パスワードを確認して再試行してください",
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
