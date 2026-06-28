"""Tachibana login form state — pure presenter logic, no tkinter dependency."""
from __future__ import annotations
import os
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class FormInit:
    env_hint: str
    initial_mode: str  # "demo" or "prod"
    auth_id_prefill: str  # ADR-0033: demo only, debug build only; else ""
    key_path_prefill: str  # ADR-0033: demo only, debug build only; else ""


AUTH_FAILED = "AUTH_FAILED"
NETWORK_ERROR = "NETWORK_ERROR"
SERVICE_OUT_OF_HOURS = "SERVICE_OUT_OF_HOURS"
USER_CANCELLED = "USER_CANCELLED"
EMPTY_FIELDS = "EMPTY_FIELDS"


def _demo_prefill() -> tuple[str, str]:
    """Resolve demo credentials for prefill (ADR-0033 D1/D3/D4).

    Returns ``(auth_id, key_path)`` from ``DEV_TACHIBANA_AUTH_ID_DEMO`` /
    ``DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO``. Returns ``("", "")`` in release
    builds (``IS_DEBUG_BUILD`` False) or when the keys are absent. The env read
    lives here in the pure presenter so the login surface stays env-free
    (PRODGATE-08); never reads prod keys (D2).
    """
    from engine.live._build_mode import IS_DEBUG_BUILD

    if not IS_DEBUG_BUILD:
        return ("", "")
    # engine.paths autoloads .env into os.environ on import (setdefault). Import
    # for the side-effect so a first-call-before-orchestrator still sees DEV_*.
    import engine.paths  # noqa: F401

    # Reuse the credential-resolver's env-key names (single source of truth) so
    # prefill and the credentials_source="env" path can never drift apart.
    from engine.exchanges.tachibana_credentials import env_keys_for

    keys = env_keys_for(is_demo=True)
    auth_id = (os.environ.get(keys.dev_auth_id) or "").strip()
    key_path = (os.environ.get(keys.dev_private_key_path) or "").strip()
    return (auth_id, key_path)


def build_form_init(env_hint: str) -> FormInit:
    """Presenter state for the tachibana login dialog (v4r9 公開鍵認証 / ADR-0023).

    ADR-0027: prod 解禁の env ゲート (TACHIBANA_ALLOW_PROD) は廃止。初期モードは
    env_hint だけで決める (prod / それ以外は demo)。

    ADR-0033: demo モードの認証ID・秘密鍵パスは debug ビルドのとき .env から prefill
    する (D1)。prod モードは prefill せず常にユーザー入力 (D2)。#181/ADR-0040: Unity モーダルは
    モード切替のたびに venue_login_form_init→build_form_init(mode) を呼び直して prefill を
    再導出する (demo→値 / prod→空)。
    """
    initial_mode = "prod" if env_hint == "prod" else "demo"
    auth_id_prefill, key_path_prefill = _demo_prefill() if initial_mode == "demo" else ("", "")
    return FormInit(
        env_hint=env_hint,
        initial_mode=initial_mode,
        auth_id_prefill=auth_id_prefill,
        key_path_prefill=key_path_prefill,
    )


def validate_submission(auth_id: str, private_key_path: str, mode: str) -> Optional[str]:
    """Return error code string if invalid, None if OK.

    v4r9: 認証ID と秘密鍵ファイルパスがどちらも非空であることを要求する
    （パスワードは送らない）。ファイル存在チェックはログイン実行側で行う。
    """
    if not auth_id.strip() or not private_key_path.strip():
        return EMPTY_FIELDS
    return None
