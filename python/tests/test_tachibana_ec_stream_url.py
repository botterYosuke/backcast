"""issue #85 follow-up: piggyback architecture — account-level EC は
per-ticker FD subscription の WS に相乗りさせる (e-station 参照実装に揃える)。

初回実走 (b30ca67) で SSL/gate/cancel の 3 修正は GREEN。続く第 2 回実走で
`_ensure_ec_stream` の `p_rid=0` 修正を試したが ST p_errno=2 が再発し、
`p_rid` 値に関わらず account-level standalone EC stream が立ち上がらないことが
empirically 確定 (2026-06-19 後場 2 回連続)。

採用: e-station 方式の **piggyback**
- `subscribe(instrument_id, ...)` が開く per-ticker FD WS に EC/SS/US を相乗り
  (`p_evt_cmd="ST,KP,EC,SS,US,FD"`)
- `_ensure_ec_stream()` は no-op (backward-compat のため残す)
- `_make_callback` が non-FD frame を `_dispatch_event_frame` へ転送し、
  `ec_ws_first_recv_ts_ms` の前進と EC→OrderEvent / SS→閉局検知を共通化

本テストは subscribe() の URL 構造を assert する (口座レベル EC が piggyback
される唯一の経路を文書化する役割)。
"""
from __future__ import annotations

import asyncio
from unittest.mock import patch

from engine.exchanges.tachibana import TachibanaAdapter
from engine.exchanges.tachibana_url import EventUrl, RequestUrl, MasterUrl, PriceUrl


class _StubSession:
    """`self._session` の最小スタブ — subscribe() は `url_event_ws` を読む。"""
    def __init__(self) -> None:
        self.url_event_ws = "wss://demo-kabuka.e-shiten.jp/e_api_v4r7/event_ws/ND=stubtoken/"
        self.url_request = RequestUrl("https://demo/req/")
        self.url_master = MasterUrl("https://demo/mst/")
        self.url_price = PriceUrl("https://demo/prc/")
        self.url_event = EventUrl("https://demo/evt/")
        self.zyoutoeki_kazei_c = "1"


async def _capture_subscribe_url(instrument_id: str = "7203.TSE") -> str:
    """subscribe(instrument_id) を呼んで TickerEventWsHub に渡される URL を捕まえる。

    実 WS には繋がない (TickerEventWsHub の __init__ で URL を保持するのを利用)。
    """
    adapter = TachibanaAdapter(environment="demo")
    adapter._session = _StubSession()
    captured: dict[str, str] = {}

    class _StubHub:
        def __init__(self, ws_url, *, ticker, **_kw):
            captured["url"] = ws_url
            captured["ticker"] = ticker
            self._closed = False

        async def subscribe(self, *_args, **_kw):
            # 何もせず即 return — 本テストは URL のみ assert する。
            return

        async def unsubscribe(self, *_args, **_kw):
            return

        async def aclose(self):
            self._closed = True

        @property
        def subscriber_count(self) -> int:
            return 1

    with patch("engine.exchanges.tachibana.TickerEventWsHub", _StubHub):
        await adapter.subscribe(instrument_id, set())
    return captured["url"]


def test_subscribe_url_includes_ec_ss_us_in_p_evt_cmd() -> None:
    """`p_evt_cmd=ST,KP,EC,SS,US,FD` が含まれる (piggyback 契約の正本)。
    EC/SS/US が抜けると OrderEvent / 閉局検知 / 運用ステータスが届かない。"""
    url = asyncio.run(_capture_subscribe_url())
    assert "p_evt_cmd=ST,KP,EC,SS,US,FD" in url, (
        f"expected piggyback p_evt_cmd in {url!r}"
    )
    # raw comma (R2 EVENT 例外); `%2C` は server に認識されない。
    assert "%2C" not in url, f"comma must be raw, not %2C: {url!r}"


def test_subscribe_url_targets_event_ws_with_ticker_params() -> None:
    """per-ticker FD subscription の必須 3 点 (p_gyou_no/p_issue_code/p_mkt_code) を持つ。
    account-level standalone EC で empirically NG だった ticker params 抜き構成からの回帰防止。"""
    url = asyncio.run(_capture_subscribe_url("7203.TSE"))
    assert "p_gyou_no=1" in url, f"expected p_gyou_no=1 (row) in {url!r}"
    assert "p_issue_code=7203" in url, f"expected p_issue_code=7203 (ticker) in {url!r}"
    assert "p_mkt_code=00" in url, f"expected p_mkt_code=00 (TSE) in {url!r}"
    assert url.startswith("wss://demo-kabuka.e-shiten.jp/e_api_v4r7/event_ws/ND="), (
        f"EC WS URL must be built on sUrlEventWebSocket: {url!r}"
    )


def test_subscribe_url_keeps_p_rid_22_and_board_no() -> None:
    """`p_rid=22` (App No.2 = e支店・API、時価配信機能あり) と `p_board_no=1000` 保持。
    `p_rid=22` は FD ticker params の前提値で、ticker params とペアであれば EC も受ける。"""
    url = asyncio.run(_capture_subscribe_url())
    assert "p_rid=22" in url, f"expected p_rid=22 in {url!r}"
    assert "p_board_no=1000" in url, f"expected p_board_no=1000 in {url!r}"
    assert "p_eno=0" in url, f"expected p_eno=0 in {url!r}"


def test_ensure_ec_stream_is_a_noop_after_piggyback_adoption() -> None:
    """`_ensure_ec_stream` は no-op になった (URL/タスクを作らない)。
    呼び出し側 (login の各分岐) が破壊的に呼んでも害が無いことを保証。"""
    adapter = TachibanaAdapter(environment="demo")
    adapter._session = _StubSession()
    adapter._on_order_event = lambda *_a, **_kw: None
    # 呼んでも何も起きない (例外も task も WS も作られない)。
    adapter._ensure_ec_stream()
    assert adapter._ec_task is None, "no separate EC task should be created"
    assert adapter._ec_ws is None, "no separate EC WS should be created"
    assert adapter._ec_stop is None, "no separate EC stop event should be created"
