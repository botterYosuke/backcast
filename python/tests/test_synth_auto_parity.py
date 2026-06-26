"""SYNTH-AUTO-01/02 + SYNTH-PARITY-01 — 合成データ Auto feed と Replay↔Auto パリティ（#152 · findings 0120）.

* SYNTH-AUTO-01 … auto_trades の TradesUpdate 列を TickBarAggregator に通すと元の OHLCV を厳密復元する
  （末尾 flush で最終 bar も close する）。
* SYNTH-AUTO-02 … "synthetic" feed が _make_feed と同じ decode(rec)->TradesUpdate 規約で subscribe-gated な
  MockVenueAdapter に流れる（未購読 id は drop）。
* SYNTH-PARITY-01 … 同一 cell・同一シナリオで Replay と Auto の決定（picks/orders）が一致する。
"""
from __future__ import annotations

import asyncio
import os
import sys
import threading
import time

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="parity drives a marimo cell (prod dep since ADR-0012)")

from engine.kernel.duckdb_bars import granularity_to_interval_ns  # noqa: E402
from engine.kernel.live.controller import KernelLiveEngineController  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.stepper import KernelStepper  # noqa: E402
from engine.live.adapter import TradesUpdate  # noqa: E402
from engine.live.aggregator import TickBarAggregator  # noqa: E402
from engine.live.live_runner import LiveRunner  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.live.order_types import OrderResult  # noqa: E402
from engine.strategy_runtime.backtester import Backtester  # noqa: E402
from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader  # noqa: E402
from engine.strategy_runtime.notebook_session import NotebookSession  # noqa: E402
from engine.synth import auto_trades, constant, explicit, synth_bars, universe_bars  # noqa: E402
from engine.synth.feed import SyntheticFeed  # noqa: E402

_A = "7203.TSE"
_C = "9984.TSE"
_CELL = os.path.join(_PY, "spike", "fixtures", "strategies", "synth_rank_cell.py")
_DAILY_NS = granularity_to_interval_ns("Daily")

# 順位入替シナリオ（A 下落・C 上昇 → 途中で C が A を抜く）。Replay/Auto 両経路で picks=[A,C]。
def _flip_scenario() -> dict:
    return synth_bars(
        [_A, _C], "2025-01-06", "2025-01-10", "Daily",
        path={_A: explicit([200, 190, 160, 150, 140]), _C: explicit([100, 130, 160, 165, 170])},
    )


# ── SYNTH-AUTO-01 ─────────────────────────────────────────────────────────────────────────────
@pytest.mark.scenario("SYNTH-AUTO-01")
def test_auto_trades_reconstructs_bars_through_aggregator() -> None:
    """auto_trades → TickBarAggregator が元の OHLCV を厳密復元する（flush で最終 bar も close）。

    litmus: OHLC 復元（4-tick）を 1-tick(close) に縮める / 末尾 flush を外すと最終 bar が欠落して RED。
    """
    # 非 flat な OHLC を含むシナリオ（trend で寄り gap・spread つき）。
    bars = synth_bars(
        [_A, _C], "2025-01-06", "2025-01-10", "Daily",
        path={_A: explicit([200, 190, 160, 150, 140], spread=2.0),
              _C: explicit([100, 130, 160, 165, 170], spread=1.0)},
    )
    trades = auto_trades(bars, interval_ns=_DAILY_NS)
    aggs = {iid: TickBarAggregator(iid, _DAILY_NS) for iid in bars}
    recon: dict = {iid: [] for iid in bars}
    for tu in trades:
        closed = aggs[tu.instrument_id].on_tick(tu)
        if closed is not None:
            recon[tu.instrument_id].append((closed.open, closed.high, closed.low, closed.close, closed.volume))
    for iid in bars:
        orig = [(b.open, b.high, b.low, b.close, b.volume) for b in bars[iid]]
        assert recon[iid] == orig, f"{iid}: aggregator did not reconstruct the bars"
    # 末尾 flush が無ければ最終 bar は never-closed。flush 込みで全 bar が出る。
    assert all(len(recon[iid]) == len(bars[iid]) for iid in bars)

    # interval が bar 間隔より粗いと複数 bar が同一 bucket に潰れる → fail-loud で弾く（silent 欠落防止）。
    with pytest.raises(ValueError, match="bucket"):
        auto_trades(bars, interval_ns=_DAILY_NS * 10)  # Daily bar を 10 日 bucket に潰す


# ── SYNTH-AUTO-02 ─────────────────────────────────────────────────────────────────────────────
@pytest.mark.scenario("SYNTH-AUTO-02")
def test_synthetic_feed_joins_make_feed_subscribe_gated() -> None:
    """"synthetic" feed が _make_feed と同じ decode(rec)->TradesUpdate 規約で subscribe-gated に流れる。

    litmus: subscribe gating を外す（inject が未購読を通す）と未購読 id が漏れて RED。
    """
    bars = _flip_scenario()
    trades = auto_trades(bars, interval_ns=_DAILY_NS)
    feed = SyntheticFeed([_A, _C])
    records = SyntheticFeed.records(trades)

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="synth-feed", daemon=True)
    thread.start()

    def run(coro, t=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(t)

    adapter = MockVenueAdapter()
    try:
        run(adapter.login(None))
        # A だけ subscribe（C は未購読 = drop されることを subscribe-gating で証明）。
        run(adapter.subscribe(_A, {"trades"}))

        injected = 0
        for rec in records:  # kabu/tachibana と同一の駆動コード（decode→inject_tick）。
            ev = feed.decode(rec)
            if ev is None:
                continue
            loop.call_soon_threadsafe(adapter.inject_tick, ev)
            injected += 1
        time.sleep(0.1)

        drained: list = []
        while not adapter._queue.empty():
            drained.append(adapter._queue.get_nowait())
        ids = {e.instrument_id for e in drained}
        assert _A in ids, "subscribed id A must flow"
        assert _C not in ids, "unsubscribed id C must be dropped (subscribe-gated)"
        # decode 規約: 未知 id の rec は None（kabu/tachibana と同じ）。
        assert feed.decode({"trade": TradesUpdate(
            kind="trades", instrument_id="0000.TSE", ts_ns=1, price=1.0, size=1.0, aggressor_side="buy")}) is None
    finally:
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


# ── SYNTH-PARITY-01 ───────────────────────────────────────────────────────────────────────────
def _replay_picks(bars_by_iid) -> list:
    """同一 cell を Replay（KernelStepper + Backtester）で駆動し、BUY 銘柄列を返す（#95 production seam）。"""
    class _Sink:
        def __init__(self): self.buys: list = []
        def push_bar(self, bar): ...
        def push_order(self, fill):
            if fill.side is OrderSide.BUY:
                self.buys.append(fill.instrument_id)
        def push_portfolio(self, pf): ...
        def on_equity(self, ts_ms, equity, cash): ...
        def push_run_complete(self, run_id, summary): ...

    sink = _Sink()
    stepper = KernelStepper(
        bars=universe_bars(bars_by_iid),
        instrument_ids=[_A, _C],
        initial_cash=10_000_000.0,
        strategy=None,  # the cell body plays on_bar between stepper calls
        strategy_id="synth-rank",
        sink=sink,
    )
    bt = Backtester(stepper)
    src = open(_CELL, encoding="utf-8").read()
    session = NotebookSession()
    try:
        session.run_pressed(src, pressed_index=0, inject={"bt": bt})  # single @app.cell
    finally:
        session.close()
    return sink.buys


class _FillingMockAdapter(MockVenueAdapter):
    """MARKET を確定約定させる mock。production mock は price=None の MARKET を avg_price=None で返し
    broker の fill-guard が REJECTED 化するので、parity（決定列比較・約定価格非依存）では一定価格で
    FILLED にする。decisions は注入 trade（market data）から決まり fill 価格には依存しない。"""

    async def submit_order(self, *, venue, instrument_id, side, qty, price=None,
                           order_type, time_in_force, client_order_id=None):
        self._require_login()
        self.submit_order_call_count += 1
        return OrderResult(
            status="FILLED", filled_qty=qty, avg_price=1.0,
            client_order_id=client_order_id or "synth", reject_reason=None,
        )


def _auto_picks(bars_by_iid) -> list:
    """同一 cell を Auto（controller.attach + auto_trades 注入）で駆動し、FILLED 銘柄列を返す。"""
    _app, scenario, bridge_factory = build_live_marimo_loader()(_CELL)

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="synth-auto-loop", daemon=True)
    thread.start()

    def run(coro, t=15.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(t)

    order_events: list = []
    adapter = _FillingMockAdapter()
    runner = LiveRunner(adapter, interval_ns=_DAILY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=lambda ev, sid: order_events.append(ev),
        buying_power_provider=lambda: 10_000_000.0,
    )

    def _filled() -> list:
        return [ev for ev in order_events if ev.status == "FILLED"]

    def _wait(pred, timeout=30.0, what="condition") -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if pred():
                return
            time.sleep(0.02)
        raise AssertionError(f"timeout waiting for {what}; filled={len(_filled())}")

    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())
        controller.attach(
            strategy_cls=bridge_factory,
            scenario=scenario,
            instrument_id=_A,
            venue="TSE",
            params={},
            nautilus_strategy_id="LIVE-synth-rank",
            session=object(),
        )
        assert controller._driver is not None

        trades = auto_trades(bars_by_iid, interval_ns=_DAILY_NS)
        for tu in trades:
            loop.call_soon_threadsafe(adapter.inject_tick, tu)
            time.sleep(0.002)  # let the loop consume → aggregate → drive cell → drain
        _wait(lambda: len(_filled()) >= 2, what="both rank picks to fill")
        time.sleep(0.2)
        # live cell の FILLED event は静的属性（symbol）を持たないので、client_order_id →
        # driver._orders の instrument_id で引く（order_events は提出＝発火順）。
        orders = controller._driver._orders
        return [orders[ev.client_order_id].instrument_id for ev in _filled() if ev.client_order_id in orders]
    finally:
        try:
            controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-synth-rank")
            controller.detach(nautilus_strategy_id="LIVE-synth-rank")
        except Exception:
            pass
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=3.0)


@pytest.mark.scenario("SYNTH-PARITY-01")
def test_replay_auto_decision_parity() -> None:
    """同一戦略セル・同一合成シナリオで Replay と Auto の決定（picks/orders）が一致する（seamless の floor）。

    litmus: auto_trades の OHLC 復元 / flush / subscribe-gating のどれかが崩れると Auto の決定列が
    Replay と乖離し RED。
    """
    bars = _flip_scenario()
    replay = _replay_picks(bars)
    assert replay == [_A, _C], f"replay picks unexpected: {replay}"  # rank flip A→C
    auto = _auto_picks(bars)
    assert auto == replay, f"Auto decisions {auto} diverged from Replay {replay}"


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v", "-s"]))
