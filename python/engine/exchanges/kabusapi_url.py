"""kabusapi URL builder (kabu skill R1 / R4)."""

from __future__ import annotations

from typing import Literal

BASE_URL_PROD = "http://localhost:18080/kabusapi/"
BASE_URL_VERIFY = "http://localhost:18081/kabusapi/"

Env = Literal["prod", "verify"]


def base_url(env: Env) -> str:
    """Return base URL for given env.

    - verify: 検証 18081 (kabu skill R1: 既定).
    - prod: 本番 18080. ADR-0027: prod 解禁の env ゲート (KABU_ALLOW_PROD) は廃止。
      本番接続の可否はユーザーがダイアログで prod を選ぶこと・本物の prod 資格情報・
      prod 本体 (18080) の起動で決まる (D2)。URL builder は env をそのまま URL にする。
    """
    if env == "verify":
        return BASE_URL_VERIFY
    if env == "prod":
        return BASE_URL_PROD
    raise ValueError("invalid env")


def endpoint(path: str, *, env: Env) -> str:
    """Join base URL and path (strips leading slash on path)."""
    return f"{base_url(env)}{path.lstrip('/')}"


def symbol_key(symbol: str, exchange: int) -> str:
    """Return kabu symbol key as '<symbol>@<exchange>' (kabu skill R4).

    exchange の妥当性検証は instrument_mapping 層の責務。ここでは単純結合。
    """
    if not symbol:
        raise ValueError("INVALID_SYMBOL: empty")
    return f"{symbol}@{exchange}"


KabuEnv = Env


def ws_url(env: Env) -> str:
    """Return WebSocket URL for given env (PUSH 配信は ws://.../kabusapi/websocket)."""
    return base_url(env).replace("http://", "ws://", 1) + "websocket"
