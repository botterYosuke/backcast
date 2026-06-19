"""issue #85 regression guard: ws:// (非 TLS) URL を渡したときは connect_kwargs に
"ssl" キーが入らない。websockets は `ws://` + `ssl=` の組合せで ValueError を投げる
ため、wss:// 限定で gate する必要がある (findings 0053 §issue#85 改訂)。
"""
from __future__ import annotations

import asyncio

from engine.exchanges import tachibana_ws


class _CapturedConnect(Exception):
    pass


def test_ws_url_does_not_set_ssl_kwarg(monkeypatch) -> None:
    captured: dict = {}

    def fake_connect(url, **kwargs):
        captured["url"] = url
        captured["kwargs"] = kwargs
        raise _CapturedConnect()

    monkeypatch.setattr(tachibana_ws.websockets, "connect", fake_connect)

    stop_event = asyncio.Event()
    ws = tachibana_ws.TachibanaEventWs(
        "ws://localhost:8765/ws", stop_event, ticker="EVENT",
    )

    async def _run():
        try:
            await ws._connect_once(lambda *a, **k: None)
        except _CapturedConnect:
            pass

    asyncio.run(_run())

    assert "ssl" not in captured["kwargs"], (
        "ws:// must not carry an ssl kwarg; passing one makes websockets raise ValueError"
    )
