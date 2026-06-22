"""Tachibana v4r9 credential resolution (issue #92 / ADR-0023).

v4r9 公開鍵認証に必要な **認証ID (`sAuthId`) と RSA 秘密鍵** を解決する。供給は 2 系統
（解決優先順）:

1. **本番級 = Fernet** — `secure_config.enc`（`{auth_id, private_key(PEM)}` を Fernet で暗号化）
   ＋ 復号鍵 env `API_DECRYPT_KEY`。暗号化ファイルと復号鍵を分離し、片方漏れても認証情報を
   復元させない（立花サンプル `e_api_login_pubkey.py` 互換）。release ビルドでも有効。
2. **開発 = 平文ファイル + env** — `DEV_TACHIBANA_AUTH_ID`（値）＋ `DEV_TACHIBANA_PRIVATE_KEY_PATH`
   （PEM ファイルのパス）。既存 `DEV_TACHIBANA_*` debug-env 流儀。**`is_debug_build` のときだけ**
   読む（release は env を無視＝R10 / S1）。

返すのは `(auth_id, private_key_obj)`。鍵素材・PEM・auth_id を含むエラーは決して投げない（R10）。
"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Optional

from .tachibana_auth import TachibanaError
from .tachibana_file_store import session_file_path
from .tachibana_pubkey import load_private_key, load_private_key_from_file

__all__ = [
    "CredentialsError",
    "ResolvedCredentials",
    "resolve_credentials",
    "env_keys_for",
    "EnvKeys",
]


@dataclass(frozen=True, slots=True)
class EnvKeys:
    """Per-environment credential env-var names + secure_config filename.

    立花 v4r9 では認証ID・秘密鍵・公開鍵が **demo と prod で別セット**
    （references/pubkey_auth.md）。環境ごとに別の env / ファイルから読むことで、
    demo の認証情報を prod へ送る取り違えを構造的に防ぐ。
    """

    decrypt_key: str
    secure_config_path: str
    secure_config_filename: str
    dev_auth_id: str
    dev_private_key_path: str


# owner 規約: 本番=無印、デモ=`_DEMO` サフィックス（demo の認証情報を prod へ送る取り違えを
# 構造的に防ぐ。prod live は別途 TACHIBANA_ALLOW_PROD=1 ゲート / R1 配下）。
_DEMO_KEYS = EnvKeys(
    decrypt_key="API_DECRYPT_KEY_DEMO",
    secure_config_path="TACHIBANA_SECURE_CONFIG_PATH_DEMO",
    secure_config_filename="secure_config_demo.enc",
    dev_auth_id="DEV_TACHIBANA_AUTH_ID_DEMO",
    dev_private_key_path="DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO",
)

_PROD_KEYS = EnvKeys(
    decrypt_key="API_DECRYPT_KEY",
    secure_config_path="TACHIBANA_SECURE_CONFIG_PATH",
    secure_config_filename="secure_config.enc",
    dev_auth_id="DEV_TACHIBANA_AUTH_ID",
    dev_private_key_path="DEV_TACHIBANA_PRIVATE_KEY_PATH",
)


def env_keys_for(*, is_demo: bool) -> EnvKeys:
    """Return the credential env-var names for the demo or prod environment."""
    return _DEMO_KEYS if is_demo else _PROD_KEYS


class CredentialsError(TachibanaError):
    """Raised when v4r9 credentials cannot be resolved.

    Rooted under ``TachibanaError`` so callers can catch the whole venue error
    tree. Messages name only env-var keys / file paths — never the secret
    values (R10).
    """


@dataclass(frozen=True, slots=True)
class ResolvedCredentials:
    auth_id: str
    private_key: Any  # RSA key object (Cryptodome). Never log / repr.

    def __repr__(self) -> str:  # R10: keep key material out of logs / tracebacks
        return f"ResolvedCredentials(auth_id=<redacted len={len(self.auth_id)}>, private_key=<redacted>)"


def _secure_config_path(keys: EnvKeys, env: Mapping[str, str]) -> Path:
    """Default Fernet secure_config location for `keys` (override via env).

    Defaults next to the session cache dir so it sits with the other Tachibana
    per-user secrets and outside the repo tree.
    """
    override = env.get(keys.secure_config_path)
    if override:
        return Path(override)
    return session_file_path().parent / keys.secure_config_filename


def _resolve_from_fernet(
    keys: EnvKeys, env: Mapping[str, str]
) -> Optional[ResolvedCredentials]:
    """Try the Fernet secure_config path for `keys`. Returns None if not configured.

    Configured = both the decrypt-key env set AND the secure_config file exists.
    A configured-but-broken setup raises CredentialsError (do not silently fall
    through to the dev path, which would mask a misconfiguration).
    """
    key = env.get(keys.decrypt_key)
    path = _secure_config_path(keys, env)
    if not key or not path.exists():
        return None

    try:
        from cryptography.fernet import Fernet, InvalidToken
    except ImportError as exc:  # pragma: no cover - dep wiring guard
        raise CredentialsError(
            "cryptography (Fernet) is required to read secure_config.enc"
        ) from exc

    try:
        encrypted = path.read_bytes()
        plaintext = Fernet(key.encode()).decrypt(encrypted)
    except InvalidToken as exc:
        raise CredentialsError(
            f"failed to decrypt {keys.secure_config_filename} with "
            f"{keys.decrypt_key} (wrong key or corrupt file)"
        ) from exc
    except (ValueError, TypeError) as exc:
        # Malformed Fernet key (not url-safe base64). Never echo the key (R10).
        raise CredentialsError(
            f"{keys.decrypt_key} is not a valid Fernet key"
        ) from exc

    try:
        config = json.loads(plaintext.decode("utf-8"))
        # TypeError covers a non-dict JSON payload (e.g. a list/int → config["..."]).
        auth_id = str(config["auth_id"]).strip()
        pem_text = str(config["private_key"]).strip()
    except (json.JSONDecodeError, KeyError, TypeError) as exc:
        raise CredentialsError(
            "secure_config.enc payload is malformed (expected JSON with "
            "'auth_id' and 'private_key')"
        ) from exc

    return ResolvedCredentials(auth_id=auth_id, private_key=load_private_key(pem_text))


def _resolve_from_dev_env(
    keys: EnvKeys, env: Mapping[str, str]
) -> Optional[ResolvedCredentials]:
    """Try the dev env path (auth_id value + private-key PEM file path)."""
    auth_id = (env.get(keys.dev_auth_id) or "").strip()
    key_path = (env.get(keys.dev_private_key_path) or "").strip()
    if not auth_id and not key_path:
        return None
    # Partially configured: fail loud naming the missing key (never the values).
    missing = [
        k
        for k, v in (
            (keys.dev_auth_id, auth_id),
            (keys.dev_private_key_path, key_path),
        )
        if not v
    ]
    if missing:
        raise CredentialsError(
            f"incomplete dev credentials: missing {', '.join(missing)}"
        )
    # load_private_key_from_file owns the BOM/strip/import + missing-file→typed
    # error contract (shared with the tkinter dialog).
    return ResolvedCredentials(
        auth_id=auth_id, private_key=load_private_key_from_file(key_path)
    )


def resolve_credentials(
    *,
    is_demo: bool,
    is_debug_build: bool,
    env: Optional[Mapping[str, str]] = None,
) -> ResolvedCredentials:
    """Resolve v4r9 credentials for the demo or prod environment.

    立花は demo/prod で認証情報が別セット（pubkey_auth.md）なので `is_demo` で
    読む env / ファイルを切り替える（demo の認証情報を prod へ送る取り違えを防ぐ）。
    Order: Fernet secure_config (allowed in any build) → dev env path (debug build
    only). Raises CredentialsError when no source is configured, or
    PubkeyCryptoError when a configured key is malformed — never leaking the
    secret values (R10).
    """
    env = env if env is not None else os.environ
    keys = env_keys_for(is_demo=is_demo)

    fernet = _resolve_from_fernet(keys, env)
    if fernet is not None:
        return fernet

    if is_debug_build:
        dev = _resolve_from_dev_env(keys, env)
        if dev is not None:
            return dev

    # Nothing configured. Name the supported keys so the operator can fix it.
    hint = (
        f"set {keys.decrypt_key} + {keys.secure_config_filename}, "
        f"or (debug build) {keys.dev_auth_id} + {keys.dev_private_key_path}"
    )
    raise CredentialsError(
        f"no Tachibana v4r9 {'demo' if is_demo else 'prod'} credentials "
        f"configured ({hint})"
    )
