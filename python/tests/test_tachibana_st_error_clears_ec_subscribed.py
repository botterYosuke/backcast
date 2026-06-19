"""issue #85 follow-up: ST p_errno!=0 は EC WS が live でない signal なので
``ec_ws_first_recv_ts_ms`` を立てない / 立っていればクリアする。

初回実走 (b30ca67 / 2026-06-19) で得た知見:
- SSL ハンドシェイクが成立すると ST frame が届くようになるが、内容が `p_errno=2`
  (仮想 URL 無効) の場合は subscription が成立していない。
- 旧設計 (Q1 (A')) は「frame_type 分岐より前で signal を前進」させたため、ST p_errno=2
  でも `ec_ws_first_recv_ts_ms` が立ち、Runner の step 2.5 gate を擦り抜けて place に
  進んでしまう (= 不具合 2 の trap door)。
- 修正: ST frame は `p_errno=="0"` のときのみ signal を前進。`p_errno!="0"` の場合は
  ``ec_ws_first_recv_ts_ms`` を None にリセット (sticky 解除) し、Runner が再 spin する
  ようにする。`ec_ws_last_recv_ts_ms` は受信活動の記録として常に更新する (staleness 用)。

これにより、SSL 修正で握手は通るが server が `p_errno=2` を返し続けるルート (URL
クエリ不整合・session 死) でも、Runner step 2.5 gate が fail-fast し place しない。
"""
from __future__ import annotations

import asyncio

from engine.exchanges.tachibana import TachibanaAdapter


def _adapter() -> TachibanaAdapter:
    return TachibanaAdapter(environment="demo")


def test_st_p_errno_zero_advances_first_recv_ts() -> None:
    """ST frame が正常 (`p_errno="0"`) ならば first_recv_ts は進む (KP 等と同じ扱い)。"""
    a = _adapter()
    asyncio.run(a._dispatch_event_frame("ST", {"p_cmd": "ST", "p_errno": "0"}, recv_ts_ms=100))
    assert a.ec_ws_first_recv_ts_ms == 100
    assert a.ec_ws_last_recv_ts_ms == 100


def test_st_p_errno_nonzero_does_not_set_first_recv_ts() -> None:
    """ST p_errno=2 (仮想URL無効) は subscription 不成立。first_recv_ts を立てない。"""
    a = _adapter()
    asyncio.run(a._dispatch_event_frame("ST", {"p_cmd": "ST", "p_errno": "2"}, recv_ts_ms=200))
    assert a.ec_ws_first_recv_ts_ms is None, (
        "ST p_errno=2 should NOT count as a valid handshake — would let Runner step 2.5 "
        "pass and place an order into a dead stream."
    )
    # last_recv_ts は受信活動の記録なので進む (staleness watchdog 用)。
    assert a.ec_ws_last_recv_ts_ms == 200


def test_st_p_errno_nonzero_clears_previously_set_first_recv_ts() -> None:
    """直前まで healthy だった EC WS が ST p_errno=2 を吐いた場合: server が session を
    殺したシグナルなので first_recv_ts を None にリセット。Runner gate が再 spin する。"""
    a = _adapter()
    # 先に KP で healthy 化
    asyncio.run(a._dispatch_event_frame("KP", {"p_cmd": "KP"}, recv_ts_ms=300))
    assert a.ec_ws_first_recv_ts_ms == 300

    # ST p_errno=1 (任意の非 0) でも同じく sticky を剥がす
    asyncio.run(a._dispatch_event_frame("ST", {"p_cmd": "ST", "p_errno": "1"}, recv_ts_ms=400))
    assert a.ec_ws_first_recv_ts_ms is None
    assert a.ec_ws_last_recv_ts_ms == 400


def test_st_missing_p_errno_treated_as_error_safe_side() -> None:
    """ST frame に p_errno が無い (parser 不一致 / server bug) ケースは安全側 (= error 扱い)
    で sticky を立てない。"""
    a = _adapter()
    asyncio.run(a._dispatch_event_frame("ST", {"p_cmd": "ST"}, recv_ts_ms=500))
    assert a.ec_ws_first_recv_ts_ms is None
    assert a.ec_ws_last_recv_ts_ms == 500


def test_non_st_frames_still_advance_signal() -> None:
    """KP / FD / EC / SS / US は frame_type 分岐より前で signal を進める従来仕様を保つ
    (市場閉局時の SS-only 経路でも誤陰性にならない invariant — findings 0053 Q1 (A'))。"""
    a = _adapter()
    for ftype, ts in [("KP", 10), ("FD", 20), ("EC", 30), ("SS", 40), ("US", 50)]:
        a.ec_ws_first_recv_ts_ms = None  # 各 case を独立に reset
        a.ec_ws_last_recv_ts_ms = None
        asyncio.run(a._dispatch_event_frame(ftype, {"p_cmd": ftype}, recv_ts_ms=ts))
        assert a.ec_ws_first_recv_ts_ms == ts, f"{ftype} should advance first_recv_ts"
        assert a.ec_ws_last_recv_ts_ms == ts, f"{ftype} should advance last_recv_ts"
