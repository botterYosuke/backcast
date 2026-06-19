"""issue #85 不具合 1: tachibana_ws.py の wss:// ハンドシェイクは certifi ベースの
SSLContext を明示で渡す。Windows-Unity-embedded Python は OS の system trust store を
引かず、`ssl.create_default_context()` の自動 fallback で CERTIFICATE_VERIFY_FAILED に
落ちるため (findings 0053 §issue#85 改訂)。

REQUEST 経路は httpx の transitive 依存で意図せず certifi を使えており login が通る一方、
WS は同じ trust store にぶら下がっていなかった (issue 本文の根本原因)。
"""
from __future__ import annotations

import asyncio
import ssl

import pytest

from engine.exchanges import tachibana_ws


class _CapturedConnect(Exception):
    """websockets.connect の引数を捕まえたら即座に脱出するための sentinel。"""


def _spy_connect(captured: dict):
    def fake_connect(url, **kwargs):
        captured["url"] = url
        captured["kwargs"] = kwargs
        raise _CapturedConnect()
    return fake_connect


def _drive_once(ws: tachibana_ws.TachibanaEventWs) -> None:
    """_connect_once を 1 回だけ走らせ、spy が raise する _CapturedConnect で抜ける。"""
    async def _run():
        try:
            await ws._connect_once(lambda *a, **k: None)
        except _CapturedConnect:
            pass
    asyncio.run(_run())


def test_wss_url_passes_certifi_ssl_context(monkeypatch) -> None:
    """wss:// を渡すと websockets.connect が `ssl=` で SSLContext を受け取り、
    そこに少なくとも 1 つの CA 証明書がロードされている (certifi 由来)。"""
    captured: dict = {}
    monkeypatch.setattr(tachibana_ws.websockets, "connect", _spy_connect(captured))

    stop_event = asyncio.Event()
    ws = tachibana_ws.TachibanaEventWs(
        "wss://example.com/ws", stop_event, ticker="EVENT",
    )
    _drive_once(ws)

    ssl_ctx = captured["kwargs"].get("ssl")
    assert isinstance(ssl_ctx, ssl.SSLContext), (
        f"wss:// must receive an ssl.SSLContext, got {type(ssl_ctx).__name__}"
    )
    ca_certs = ssl_ctx.get_ca_certs()
    assert ca_certs, (
        "SSLContext must have certifi CA certs loaded "
        "(Windows-Unity-embedded Python cannot rely on system trust store)"
    )
