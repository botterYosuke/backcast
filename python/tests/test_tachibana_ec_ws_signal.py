"""issue #85 Q1 (A'): TachibanaAdapter は EC WS の handshake 成立シグナルを
2 つの ts_ms property で露出する (findings 0053 §issue#85 改訂):

- ``ec_ws_first_recv_ts_ms``: EC WS 初回フレーム受信時刻 (UTC ms) — sticky bool 兼用
  (None ⇒ まだ確立していない / not None ⇒ SSL ハンドシェイク済み)
- ``ec_ws_last_recv_ts_ms``: 直近フレーム受信時刻 — staleness 検出用

_dispatch_event_frame の冒頭 (frame_type 分岐より前) で両方を set し、KP / SS / EC
どの種別でも signal が前進する (市場閉局時は SS=閉局 push しか来ないため、frame_type
を絞ると閉局時に signal が立たず誤陰性になる)。

reset は _stop_ec_stream のみ — 単発の WS reconnect (run() ループ内の backoff) では
_stop_ec_stream は呼ばれないため、signal は sticky に保たれる (一度確立した EC WS が
一時的に切れて即復帰しても発注 gate が無闇に false に戻らない)。
"""
from __future__ import annotations

import asyncio

import pytest

from engine.exchanges.tachibana import TachibanaAdapter


def _adapter() -> TachibanaAdapter:
    return TachibanaAdapter(environment="demo")


def test_dispatch_event_frame_sets_first_and_last_recv_ts() -> None:
    a = _adapter()
    assert a.ec_ws_first_recv_ts_ms is None
    assert a.ec_ws_last_recv_ts_ms is None

    async def _run():
        # KP (keepalive) は _on_order_event 未設定でも signal を進める必要がある。
        await a._dispatch_event_frame("KP", {"p_cmd": "KP"}, recv_ts_ms=1_000_000)

    asyncio.run(_run())

    assert a.ec_ws_first_recv_ts_ms == 1_000_000
    assert a.ec_ws_last_recv_ts_ms == 1_000_000


def test_dispatch_event_frame_keeps_first_updates_last() -> None:
    """2 度目以降の dispatch では first は固定 / last のみ前進する (staleness 用)。"""
    a = _adapter()

    async def _run():
        await a._dispatch_event_frame("KP", {"p_cmd": "KP"}, recv_ts_ms=1_000_000)
        await a._dispatch_event_frame("KP", {"p_cmd": "KP"}, recv_ts_ms=2_500_000)

    asyncio.run(_run())

    assert a.ec_ws_first_recv_ts_ms == 1_000_000
    assert a.ec_ws_last_recv_ts_ms == 2_500_000


def test_dispatch_event_frame_signal_works_for_ss_and_ec_too() -> None:
    """SS (閉局 push) や EC (約定通知) でも signal は前進する — frame_type を
    絞らないので市場閉局時の SS-only 経路でも誤陰性にならない。"""
    a = _adapter()

    async def _run():
        await a._dispatch_event_frame("SS", {"sSystemStatus": "1"}, recv_ts_ms=10)

    asyncio.run(_run())
    assert a.ec_ws_first_recv_ts_ms == 10
    assert a.ec_ws_last_recv_ts_ms == 10


def test_stop_ec_stream_resets_both_signals() -> None:
    """_stop_ec_stream は session-level teardown — 次セッションで stale ts が漏れる
    のを防ぐため、first / last の両方を None に戻す。"""
    a = _adapter()

    async def _run():
        await a._dispatch_event_frame("KP", {"p_cmd": "KP"}, recv_ts_ms=1_000_000)
        await a._stop_ec_stream()

    asyncio.run(_run())

    assert a.ec_ws_first_recv_ts_ms is None
    assert a.ec_ws_last_recv_ts_ms is None
