"""issue #85 (δ): TachibanaEventWs(ssl_ctx=...) で渡した SSLContext が、
module-level の _TLS_CTX (certifi default) ではなく優先される。
将来の社内 CA / proxy 対応のための future-proof な seam (findings 0053 §issue#85 改訂)。

monkeypatch.setattr(tachibana_ws, "_TLS_CTX", fake) は TickerEventWsHub 経由など
ctor 引数が届かない経路用の global fallback で、ここでは ctor 注入が module default
より strict に優先することを石化する。
"""
from __future__ import annotations

import asyncio
import ssl

from engine.exchanges import tachibana_ws


class _CapturedConnect(Exception):
    pass


def test_ssl_ctx_ctor_injection_overrides_module_default(monkeypatch) -> None:
    captured: dict = {}

    def fake_connect(url, **kwargs):
        captured["kwargs"] = kwargs
        raise _CapturedConnect()

    monkeypatch.setattr(tachibana_ws.websockets, "connect", fake_connect)

    # Sentinel: 明らかに module default と別の SSLContext を ctor から渡す。
    injected = ssl.create_default_context()
    # 識別子として CA を 1 つ手で load せず、空 ctx のままにしておく
    # (module default は certifi の root 入り → get_ca_certs() が non-empty)。

    stop_event = asyncio.Event()
    ws = tachibana_ws.TachibanaEventWs(
        "wss://example.com/ws",
        stop_event,
        ticker="EVENT",
        ssl_ctx=injected,
    )

    async def _run():
        try:
            await ws._connect_once(lambda *a, **k: None)
        except _CapturedConnect:
            pass

    asyncio.run(_run())

    assert captured["kwargs"].get("ssl") is injected, (
        "ctor-injected ssl_ctx must take precedence over module-level _TLS_CTX"
    )
