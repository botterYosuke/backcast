"""Tachibana login form state — pure presenter logic, no tkinter dependency."""
from __future__ import annotations
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class FormInit:
    env_hint: str
    initial_mode: str  # "demo" or "prod"


AUTH_FAILED = "AUTH_FAILED"
NETWORK_ERROR = "NETWORK_ERROR"
SERVICE_OUT_OF_HOURS = "SERVICE_OUT_OF_HOURS"
USER_CANCELLED = "USER_CANCELLED"
EMPTY_FIELDS = "EMPTY_FIELDS"


def build_form_init(env_hint: str) -> FormInit:
    """Presenter state for the tachibana login dialog (v4r9 公開鍵認証 / ADR-0023).

    ADR-0027: prod 解禁の env ゲート (TACHIBANA_ALLOW_PROD) と debug ビルドの DEV_*
    prefill は廃止。初期モードは env_hint だけで決め (prod / それ以外は demo)、認証ID と
    秘密鍵パスは常にユーザーが入力する (空欄で開く / D2・D3)。
    """
    initial_mode = "prod" if env_hint == "prod" else "demo"
    return FormInit(env_hint=env_hint, initial_mode=initial_mode)


def validate_submission(auth_id: str, private_key_path: str, mode: str) -> Optional[str]:
    """Return error code string if invalid, None if OK.

    v4r9: 認証ID と秘密鍵ファイルパスがどちらも非空であることを要求する
    （パスワードは送らない）。ファイル存在チェックはログイン実行側で行う。
    """
    if not auth_id.strip() or not private_key_path.strip():
        return EMPTY_FIELDS
    return None
