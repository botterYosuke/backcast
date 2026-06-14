"""spike.live_adapter.mock_inject — throwaway mock-injection helper for #20 (Live adapter tracer).

findings 0011 D2: 生産 API（InprocLiveServer）に mock 専用メソッドを足さず、この throwaway
helper が InprocLiveServer 内部の共有 MockVenueAdapter（live_adapter_factory 由来）へ
live_loop.call_soon_threadsafe(...) で tick / depth / fill-outcome を注入する。

C# (LiveAdapterTracerProbe) は lifecycle（register/login/set_execution_mode/start/stop/
logout/close）を InprocLiveServer facade で直接駆動し、注入だけ本 helper を呼ぶ。
"""
from __future__ import annotations

from pathlib import Path

from engine.live.adapter import DepthLevel, KlineUpdate
from engine.live.order_types import AccountPositionData

IID = "8918.TSE"
DAY_NS = 86_400 * 1_000_000_000
TWIN_PATH = str(
    Path(__file__).resolve().parents[1] / "fixtures" / "strategies" / "kernel_spike_buy_sell.py"
)


def _mgr(server):
    """InprocLiveServer → 内部 LiveLoopManager（white-box・throwaway）。"""
    return server._svc._srv._live_mgr


def _adapter(server):
    mgr = _mgr(server)
    sess = mgr._session
    return sess.runner.adapter if sess is not None else None


def _loop(server):
    return _mgr(server)._live_loop


def set_next_order_outcome(server, *, status: str, filled_qty: float, avg_price: float) -> None:
    _adapter(server).set_next_order_outcome(
        status=status, filled_qty=filled_qty, avg_price=avg_price
    )


def set_account_snapshot(server, *, cash: float, buying_power: float, positions=()) -> None:
    _adapter(server).set_account_snapshot(
        cash=cash, buying_power=buying_power, positions=positions
    )


def make_position(symbol: str, qty: int, avg_price: float, unrealized_pnl: float = 0.0):
    return AccountPositionData(
        symbol=symbol, qty=qty, avg_price=avg_price, unrealized_pnl=unrealized_pnl
    )


def inject_kline(server, i: int, close: float) -> None:
    adapter = _adapter(server)
    loop = _loop(server)
    ev = KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )
    loop.call_soon_threadsafe(adapter.inject_tick, ev)


def emit_depth(server, i: int, bid: float, ask: float) -> None:
    adapter = _adapter(server)
    loop = _loop(server)
    bids = [DepthLevel(price=bid, size=300.0)]
    asks = [DepthLevel(price=ask, size=300.0)]
    loop.call_soon_threadsafe(adapter.emit_depth_snapshot, IID, i * DAY_NS, bids, asks)


def _levels_from_csv(prices_csv: str, sizes_csv: str) -> list[DepthLevel]:
    """CSV("p1,p2"), CSV("s1,s2") → [DepthLevel,...]。空文字列は片側欠の空板を表す。"""
    if not prices_csv:
        return []
    prices = [float(p) for p in prices_csv.split(",")]
    sizes = [float(s) for s in sizes_csv.split(",")] if sizes_csv else []
    return [
        DepthLevel(price=prices[k], size=(sizes[k] if k < len(sizes) else 0.0))
        for k in range(len(prices))
    ]


def emit_depth_ladder(
    server,
    i: int,
    bid_prices: str,
    bid_sizes: str,
    ask_prices: str,
    ask_sizes: str,
) -> None:
    """multi-level の非対称な板を 1 スナップショットとして注入する（#26 用・throwaway）。

    pythonnet の InvokeMethod は list を渡しにくいため price/size を CSV 文字列で受け、
    ここで DepthLevel list に整形する。`emit_depth`（単段）の multi-level 版。空文字列を
    渡すと片側欠の空板になる。"""
    adapter = _adapter(server)
    loop = _loop(server)
    bids = _levels_from_csv(bid_prices, bid_sizes)
    asks = _levels_from_csv(ask_prices, ask_sizes)
    loop.call_soon_threadsafe(adapter.emit_depth_snapshot, IID, i * DAY_NS, bids, asks)


def arm_order(server, status: str, filled_qty: float, avg_price: float) -> None:
    """positional wrapper of set_next_order_outcome（pythonnet InvokeMethod は kwargs を
    渡しにくいため、C# probe 用に positional シグネチャを用意する。throwaway）。"""
    set_next_order_outcome(server, status=status, filled_qty=filled_qty, avg_price=avg_price)


def arm_account_position(
    server,
    cash: float,
    buying_power: float,
    symbol: str,
    qty: float,
    avg_price: float,
    unrealized_pnl: float,
) -> None:
    """positional wrapper: 単一建玉つき口座スナップショットを仕込む（C# probe 用・throwaway）。"""
    set_account_snapshot(
        server,
        cash=cash,
        buying_power=buying_power,
        positions=[make_position(symbol, int(qty), avg_price, unrealized_pnl)],
    )
