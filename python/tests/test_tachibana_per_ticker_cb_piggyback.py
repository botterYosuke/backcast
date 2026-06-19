"""issue #85 follow-up: piggyback architecture — per-ticker FD callback (`_make_callback`)
は **account-level frame (ST/KP/EC/SS/US)** を `_dispatch_event_frame` に転送する。

これによって、`_ensure_ec_stream` を no-op にしても `ec_ws_first_recv_ts_ms` sticky の
前進と EC→OrderEvent / SS→閉局検知のルーティングが per-ticker FD WS 経由で行われる。
FD frame は従来通り processor.process(...) で depth/trades を生成する。
"""
from __future__ import annotations

import asyncio

from engine.exchanges.tachibana import TachibanaAdapter
from engine.exchanges.tachibana_ws_codec import FdFrameProcessor
from engine.live.adapter import InstrumentId


def _adapter() -> TachibanaAdapter:
    return TachibanaAdapter(environment="demo")


def test_per_ticker_callback_forwards_kp_to_dispatch() -> None:
    """KP frame は account-level signal の前進 (ec_ws_first_recv_ts_ms set) を引き起こす。
    これが piggyback の中核 (FD WS の最初の KP で step 2.5 gate が通る)。"""
    a = _adapter()
    cb = a._make_callback(InstrumentId("7203.TSE"), FdFrameProcessor(row="1"))
    asyncio.run(cb("KP", {"p_cmd": "KP"}, recv_ts_ms=1_000_000))
    assert a.ec_ws_first_recv_ts_ms == 1_000_000
    assert a.ec_ws_last_recv_ts_ms == 1_000_000


def test_per_ticker_callback_forwards_ss_to_dispatch() -> None:
    """SS frame も転送される (閉局検知 + sticky 更新)。"""
    a = _adapter()
    cb = a._make_callback(InstrumentId("7203.TSE"), FdFrameProcessor(row="1"))
    asyncio.run(cb("SS", {"sSystemStatus": "1"}, recv_ts_ms=2_000_000))
    assert a.ec_ws_first_recv_ts_ms == 2_000_000


def test_per_ticker_callback_forwards_st_error_clears_sticky() -> None:
    """per-ticker WS でも ST p_errno!=0 は sticky をリセットする (Runner gate 再 spin)。"""
    a = _adapter()
    cb = a._make_callback(InstrumentId("7203.TSE"), FdFrameProcessor(row="1"))
    # 先に KP で healthy 化
    asyncio.run(cb("KP", {"p_cmd": "KP"}, recv_ts_ms=300))
    assert a.ec_ws_first_recv_ts_ms == 300
    # ST p_errno=2 で sticky 剥がし
    asyncio.run(cb("ST", {"p_cmd": "ST", "p_errno": "2"}, recv_ts_ms=400))
    assert a.ec_ws_first_recv_ts_ms is None
    assert a.ec_ws_last_recv_ts_ms == 400


def test_per_ticker_callback_fd_frame_advances_signal_and_runs_processor() -> None:
    """FD frame: account-level signal も前進し (piggyback) かつ depth/trades 生成も走る。"""
    a = _adapter()
    cb = a._make_callback(InstrumentId("7203.TSE"), FdFrameProcessor(row="1"))
    # FD frame (空でもよい — processor が空の depth/trades を返す)
    asyncio.run(cb("FD", {"p_cmd": "FD"}, recv_ts_ms=500))
    assert a.ec_ws_first_recv_ts_ms == 500, "FD frame must advance EC WS signal too"
    assert a.ec_ws_last_recv_ts_ms == 500


def test_per_ticker_callback_ec_frame_advances_signal_but_routes_via_dispatch() -> None:
    """EC frame は signal 前進 + `_dispatch_event_frame` の EC 経路に乗る (約定 push)。
    on_order_event 未設定 (本テスト) では OrderEvent push されないが signal は立つ。"""
    a = _adapter()
    cb = a._make_callback(InstrumentId("7203.TSE"), FdFrameProcessor(row="1"))
    asyncio.run(cb("EC", {"p_cmd": "EC"}, recv_ts_ms=600))
    assert a.ec_ws_first_recv_ts_ms == 600
    assert a.ec_ws_last_recv_ts_ms == 600
