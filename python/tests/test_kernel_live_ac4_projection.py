"""AC④ projection 互換ゲート（#25 D2）— CPython half。

AC④ は「Live UI 配送路を EventSink に変える」要求ではなく、**同じ kernel fill / Portfolio state を
既存 `EventSink` serializer に投影すると #24 §3 の push_order / push_portfolio JSON 契約になる**、を
担保する projection 互換ゲート。ここでは Live 経路で生じた OrderFilled / Portfolio を `EventSink`
（Replay と同一 serializer）へ流し、payload の key/value が findings 0008 §3 契約どおりか検証する。

無改修 C# `ReplayPanelDecoder` による実 decode は Unity-Mono probe（D5 layer 3）が担う。本書はその
Python 側 contract 固定（#24 `KernelSinkDecodeProbe` と同型の意図）。
"""
from __future__ import annotations

import json

from engine.kernel.orders import OrderFilled, OrderSide
from engine.kernel.portfolio import Portfolio
from engine.kernel.sink import EventSink


class _CaptureTarget:
    def __init__(self) -> None:
        self.orders: list[dict] = []
        self.portfolios: list[dict] = []

    def push_bar(self, payload: str) -> None:  # pragma: no cover - not exercised here
        pass

    def push_order(self, payload: str) -> None:
        self.orders.append(json.loads(payload))

    def push_portfolio(self, payload: str) -> None:
        self.portfolios.append(json.loads(payload))

    def push_run_complete(self, run_id: str, payload: str) -> None:  # pragma: no cover
        pass


def test_live_fill_projects_to_replay_sink_order_contract():
    target = _CaptureTarget()
    sink = EventSink(target)
    # Live 経路で生じた約定（cumulative_filled_qty 付き）を Replay sink へ投影。
    fill = OrderFilled(
        client_order_id="O-LIVE-x-1",
        venue_order_id="O-LIVE-x-1",
        strategy_id="LIVE-x",
        instrument_id="8918.TSE",
        side=OrderSide.BUY,
        last_qty=100.0,
        last_px=8.0,
        ts_event_ns=3 * 86_400 * 1_000_000_000,
        cumulative_filled_qty=100.0,
    )
    sink.push_order(fill)
    assert len(target.orders) == 1
    o = target.orders[0]
    # findings 0008 §3 の push_order 契約（C# ReplayPanelDecoder.DecodeOrder が読む key）。
    assert set(o) == {
        "symbol", "client_order_id", "venue_order_id", "strategy_id",
        "side", "status", "qty", "price", "timestamp_ms",
    }
    assert o["symbol"] == "8918.TSE"
    assert o["side"] == "BUY"
    assert o["status"] == "FILLED"  # sink は FILLED のみ push（GUI bridge 契約）
    assert o["qty"] == 100.0
    assert o["price"] == 8.0


def test_live_portfolio_projects_to_replay_sink_portfolio_contract():
    target = _CaptureTarget()
    sink = EventSink(target)
    portfolio = Portfolio(initial_cash=10_000_000.0)
    portfolio.apply_fill(
        OrderFilled(
            client_order_id="O-LIVE-x-1", venue_order_id="O-LIVE-x-1", strategy_id="LIVE-x",
            instrument_id="8918.TSE", side=OrderSide.BUY, last_qty=100.0, last_px=8.0,
            ts_event_ns=0, cumulative_filled_qty=100.0,
        )
    )
    sink.push_portfolio(portfolio)
    assert len(target.portfolios) == 1
    p = target.portfolios[0]
    assert set(p) == {"buying_power", "equity", "positions", "orders"}
    assert p["buying_power"] == portfolio.cash
    assert p["equity"] == portfolio.equity
    assert len(p["positions"]) == 1
    pos = p["positions"][0]
    assert set(pos) == {"symbol", "qty", "avg_price"}
    assert pos["symbol"] == "8918.TSE"
    assert pos["qty"] == 100.0
    assert pos["avg_price"] == 8.0
