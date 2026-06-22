"""Tachibana login form state — pure presenter logic, no tkinter dependency."""
from __future__ import annotations
import os
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class FormInit:
    env_hint: str
    allow_prod: bool
    is_debug_build: bool
    # v4r9 公開鍵認証 (ADR-0023): パスワードは廃止。認証ID + 秘密鍵 PEM パスを供給する。
    dev_auth_id: Optional[str]
    dev_private_key_path: Optional[str]
    dev_demo: Optional[bool]
    initial_mode: str  # "demo" or "prod"


AUTH_FAILED = "AUTH_FAILED"
NETWORK_ERROR = "NETWORK_ERROR"
SERVICE_OUT_OF_HOURS = "SERVICE_OUT_OF_HOURS"
USER_CANCELLED = "USER_CANCELLED"
EMPTY_FIELDS = "EMPTY_FIELDS"


def build_form_init(
    env_hint: str,
    env_dict: Optional[dict] = None,
    is_debug_build: bool = True,
) -> FormInit:
    if env_dict is None:
        env_dict = dict(os.environ)

    allow_prod = env_dict.get("TACHIBANA_ALLOW_PROD") == "1"

    # env_hint が "prod" かつ allow_prod のとき prod、それ以外は demo
    if env_hint == "prod" and allow_prod:
        initial_mode = "prod"
    else:
        initial_mode = "demo"

    if is_debug_build:
        # 本番=無印 / デモ=_DEMO サフィックス（tachibana_credentials の規約と一致）。dialog の
        # 初期モードに合わせて prefill する（demo の認証情報を prod に出さない）。
        suffix = "_DEMO" if initial_mode == "demo" else ""
        dev_auth_id = env_dict.get(f"DEV_TACHIBANA_AUTH_ID{suffix}") or None
        dev_private_key_path = (
            env_dict.get(f"DEV_TACHIBANA_PRIVATE_KEY_PATH{suffix}") or None
        )
        dev_demo_str = env_dict.get("DEV_TACHIBANA_DEMO", "true").lower()
        dev_demo: Optional[bool] = dev_demo_str not in ("false", "0", "no", "off", "")
    else:
        dev_auth_id = None
        dev_private_key_path = None
        dev_demo = None

    return FormInit(
        env_hint=env_hint,
        allow_prod=allow_prod,
        is_debug_build=is_debug_build,
        dev_auth_id=dev_auth_id,
        dev_private_key_path=dev_private_key_path,
        dev_demo=dev_demo,
        initial_mode=initial_mode,
    )


def validate_submission(auth_id: str, private_key_path: str, mode: str) -> Optional[str]:
    """Return error code string if invalid, None if OK.

    v4r9: 認証ID と秘密鍵ファイルパスがどちらも非空であることを要求する
    （パスワードは送らない）。ファイル存在チェックはログイン実行側で行う。
    """
    if not auth_id.strip() or not private_key_path.strip():
        return EMPTY_FIELDS
    return None
