"""Step 5 (#25) — mock venue Live AFK roundtrip: start→order→fill→position→stop。

KernelLiveEngineController を **本番と同じ seam**（background live loop thread・共有 LiveRunner.bus・
MockVenueAdapter・run_coroutine_threadsafe 越しの attach/detach）で end-to-end 駆動する。kernel-native
twin（KernelSpikeBuySell）が bar 3 で BUY、bar 40 で SELL し、同期 OrderResult で約定して終端 FLAT になる。

import-purity（nautilus 非ロード）は sys.modules を汚さない fresh subprocess で別途 gate する
（test_kernel_live_purity.py・D5 layer 2）。本書は in-process の挙動 gate。
"""
from __future__ import annotations

import asyncio
import threading
import time

import pytest

from engine.kernel.live.controller import KernelLiveEngineController
from engine.live.adapter import KlineUpdate
from engine.live.mock_adapter import MockVenueAdapter
from engine.live.live_runner import LiveRunner
from engine.live.order_types import AccountPositionData
from engine.live.safety_rails import SafetyLimits, SafetyRails

# kernel twin（spike/fixtures/strategies/kernel_spike_buy_sell.py を import）。
import importlib.util
import os

_TWIN_PATH = os.path.join(
    os.path.dirname(os.path.dirname(__file__)),
    "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py",
)
_spec = importlib.util.spec_from_file_location("kernel_spike_buy_sell_fixture", _TWIN_PATH)
_twin = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_twin)
KernelSpikeBuySell = _twin.KernelSpikeBuySell

IID = "8918.TSE"
DAY_NS = 86_400 * 1_000_000_000
SCENARIO = {
    "schema_version": 2,
    "instruments": [IID],
    "start": "2024-10-01",
    "end": "2025-01-10",
    "granularity": "Daily",
    "initial_cash": 10_000_000,
}


def _kline(i: int, close: float) -> KlineUpdate:
    return KlineUpdate(
        kind="kline", instrument_id=IID, ts_ns=i * DAY_NS,
        open=close, high=close, low=close, close=close, volume=100.0, is_closed=True,
    )


def test_mock_live_afk_roundtrip():
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="test-live-loop", daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    order_events: list = []
    telemetry: list = []

    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=lambda ev, sid: order_events.append((ev, sid)),
        on_telemetry=lambda sid, m: telemetry.append(m),
    )

    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())

        # BUY の約定価格を仕込んでから attach（attach 後 on_start→bar 供給で発注が走る）。
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        controller.attach(
            strategy_cls=KernelSpikeBuySell,
            scenario=SCENARIO,
            instrument_id=IID,
            venue="MOCK",
            params={},
            nautilus_strategy_id="LIVE-afk00001",
            session=object(),
        )

        def _inject_and_wait(bars, want_filled):
            for i, close in bars:
                loop.call_soon_threadsafe(adapter.inject_tick, _kline(i, close))
            deadline = time.time() + 5.0
            while time.time() < deadline:
                if sum(1 for ev, _ in order_events if ev.status == "FILLED") >= want_filled:
                    return
                time.sleep(0.02)
            raise AssertionError(
                f"timeout: {sum(1 for ev,_ in order_events if ev.status=='FILLED')} fills, want {want_filled}"
            )

        # bars 1..3 → bar 3 で BUY → FILLED @8.0（long 100）。
        _inject_and_wait([(i, 8.0) for i in range(1, 4)], want_filled=1)
        portfolio = controller._driver._portfolio
        assert portfolio.net_signed_qty(IID) == 100.0
        assert portfolio.open_positions()[0].avg_px == 8.0
        # run identity が strategy に inject されている（fixture の "spike-buy-sell" ではなく run id）。
        assert controller._driver._strategy.id == "LIVE-afk00001"

        # SELL の約定価格を仕込み、bars 4..40 → bar 40 で SELL → FILLED @10.0（終端 FLAT）。
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=10.0)
        _inject_and_wait([(i, 10.0) for i in range(4, 41)], want_filled=2)

        # 約定 UI イベント: BUY/SELL とも FILLED で当該 run の strategy_id を運ぶ。
        filled = [(ev, sid) for ev, sid in order_events if ev.status == "FILLED"]
        assert len(filled) == 2
        assert all(sid == "LIVE-afk00001" for _, sid in filled)
        assert {ev.filled_qty for ev, _ in filled} == {100.0}

        # 終端 FLAT・realized = 100*(10-8) = 200・fill_count=2。
        assert portfolio.open_positions() == []
        assert portfolio.realized_pnl == 200.0
        assert controller._driver.fill_count == 2
        assert telemetry and telemetry[-1]["fill_count"] == 2

        # stop（graceful）: cancel_inflight → detach。on_stop が呼ばれ driver が解放される。
        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-afk00001")
        controller.detach(nautilus_strategy_id="LIVE-afk00001")
        assert controller._driver is None
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_pretrade_deny_fires_on_live_path():
    """allowlist 外への BUY が live 経路で DENIED + on_safety_violation 発火（AC: pre gate）。venue 未到達。"""
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    order_events: list = []
    violations: list = []
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=lambda ev, sid: order_events.append((ev.status, sid)),
        on_safety_violation=lambda v: violations.append(v),
    )
    # 8918.TSE は allowlist 外 → BUY は弾かれる。
    rails = SafetyRails(SafetyLimits(allowed_instruments=("7203.TSE",)))
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())
        controller.attach(
            strategy_cls=KernelSpikeBuySell, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-deny0001",
            session=object(), safety_rails=rails,
        )
        for i in range(1, 4):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(i, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and not violations:
            time.sleep(0.02)
        assert violations, "pre-trade rail did not fire on the live path"
        assert any(s == "DENIED" for s, _ in order_events)
        # venue 未到達（約定なし・建玉なし）。
        assert adapter.submit_order_call_count == 0
        assert controller._driver._portfolio.open_positions() == []
        assert controller._driver.fill_count == 0
        controller.detach(nautilus_strategy_id="LIVE-deny0001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_strategy_exception_signals_error():
    """走行中 on_bar 例外を握り潰さず on_strategy_error へ伝える（AC: 戦略障害 → fail_run・#25 finding 5）。"""
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _BoomStrategy(KernelStrategyBase):
        def on_bar(self, bar):  # noqa: D401
            raise RuntimeError("boom in on_bar")

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    errors: list = []
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_strategy_error=lambda exc: errors.append(exc),
    )
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())
        controller.attach(
            strategy_cls=_BoomStrategy, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-boom0001", session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and not errors:
            time.sleep(0.02)
        assert errors, "strategy on_bar exception was swallowed (no on_strategy_error)"
        assert isinstance(errors[0], RuntimeError)
        controller.detach(nautilus_strategy_id="LIVE-boom0001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def _live_harness(on_safety_violation=None, on_strategy_error=None, on_order_event=None):
    """controller + background live loop + MockVenueAdapter を組んで (loop, run, adapter, controller) を返す。"""
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=on_order_event,
        on_safety_violation=on_safety_violation,
        on_strategy_error=on_strategy_error,
    )
    run(adapter.login(None))
    adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    run(runner.start())
    return loop, thread, run, adapter, runner, controller


def test_mock_live_max_order_value_denies_oversized_order():
    """MARKET 注文の概算金額（直近価格×数量）が max_order_value_jpy 超過なら DENIED・venue 未到達（#25 finding 1）。"""
    violations: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_safety_violation=lambda v: violations.append(v)
    )
    rails = SafetyRails(SafetyLimits(max_order_value_jpy=500))  # 100 株 @8 = 800 > 500
    try:
        controller.attach(
            strategy_cls=KernelSpikeBuySell, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-cap00001",
            session=object(), safety_rails=rails,
        )
        for i in range(1, 4):
            loop.call_soon_threadsafe(adapter.inject_tick, _kline(i, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and not violations:
            time.sleep(0.02)
        assert violations and violations[0].kind == "MAX_ORDER_VALUE"
        assert adapter.submit_order_call_count == 0  # venue 未到達
        assert controller._driver._portfolio.open_positions() == []
        controller.detach(nautilus_strategy_id="LIVE-cap00001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_max_orders_per_minute_rate_limits():
    """max_orders_per_minute 超過分は venue へ送らず DENIED（旧 native rate rail の kernel 実装・#25 finding 1）。"""
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _Burst(KernelStrategyBase):
        def on_bar(self, bar):
            for _ in range(3):
                self.submit_market(self.instrument_id, OrderSide.BUY, 100)

    violations: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_safety_violation=lambda v: violations.append(v)
    )
    rails = SafetyRails(SafetyLimits(max_orders_per_minute=2))  # 他 rail は 0=無効
    try:
        controller.attach(
            strategy_cls=_Burst, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-rate0001", session=object(), safety_rails=rails,
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and not any(
            v.kind == "MAX_ORDERS_PER_MINUTE" for v in violations
        ):
            time.sleep(0.02)
        assert any(v.kind == "MAX_ORDERS_PER_MINUTE" for v in violations)
        assert adapter.submit_order_call_count == 2  # 2 件だけ venue 到達、3 件目は rate-denied
        controller.detach(nautilus_strategy_id="LIVE-rate0001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_no_send_of_queued_order_after_strategy_exception():
    """on_order で積んだ注文の後に例外を投げたら、その注文は venue へ送らない（#25 finding 2）。"""
    from engine.kernel.orders import OrderFilled, OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _QueueThenRaise(KernelStrategyBase):
        def __init__(self, **kw):
            super().__init__(**kw)
            self._raised = False

        def on_bar(self, bar):
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)  # order 1

        def on_order(self, event):
            if isinstance(event, OrderFilled) and not self._raised:
                self._raised = True
                self.submit_market(self.instrument_id, OrderSide.BUY, 100)  # order 2: 送られてはいけない
                raise RuntimeError("boom after queueing order 2")

    errors: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_strategy_error=lambda exc: errors.append(exc)
    )
    try:
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        controller.attach(
            strategy_cls=_QueueThenRaise, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-q0000001", session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and not errors:
            time.sleep(0.02)
        assert errors  # 例外は伝播した
        # order 1 のみ venue 到達。例外後にキュー済みだった order 2 は破棄される。
        assert adapter.submit_order_call_count == 1
        controller.detach(nautilus_strategy_id="LIVE-q0000001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_no_send_when_on_start_raises_after_queueing():
    """on_start が intent を積んだ直後に例外を投げたら、その intent は venue へ送らない（#25 finding 1）。

    attach 失敗（on_start で artifact 不足等を fail-loud に raise）でも `finally` で drain すると
    注文が venue に到達してしまう回帰を塞ぐ。成功時のみ drain し、失敗時はキューを破棄する。
    """
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _QueueInOnStartThenRaise(KernelStrategyBase):
        def on_start(self):
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)  # 送られてはいけない
            raise RuntimeError("boom in on_start (attach fail)")

    loop, thread, run, adapter, runner, controller = _live_harness()
    try:
        # 仮にバグで drain されてしまった場合に venue 到達を確実に観測できるよう fill outcome を仕込む。
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        with pytest.raises(Exception):
            controller.attach(
                strategy_cls=_QueueInOnStartThenRaise, scenario=SCENARIO, instrument_id=IID,
                venue="MOCK", params={}, nautilus_strategy_id="LIVE-os000001", session=object(),
            )
        assert adapter.submit_order_call_count == 0  # on_start 失敗時、キュー済み intent は破棄
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_pretrade_denied_order_does_not_consume_rate_limit():
    """pre-trade DENIED 注文は rate-limit 枠を消費しない（rate check は precheck 後・venue 直前・#25 review finding 2）。"""
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _BigThenSmall(KernelStrategyBase):
        def on_bar(self, bar):
            self.submit_market(self.instrument_id, OrderSide.BUY, 1000)  # 8000>500 → MAX_ORDER_VALUE deny
            self.submit_market(self.instrument_id, OrderSide.BUY, 10)    # 80<500 → 正常・venue へ届くべき

    violations: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_safety_violation=lambda v: violations.append(v)
    )
    rails = SafetyRails(SafetyLimits(max_orders_per_minute=1, max_order_value_jpy=500))
    try:
        adapter.set_next_order_outcome(status="FILLED", filled_qty=10.0, avg_price=8.0)
        controller.attach(
            strategy_cls=_BigThenSmall, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-rate0002", session=object(), safety_rails=rails,
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and adapter.submit_order_call_count == 0:
            time.sleep(0.02)
        time.sleep(0.1)  # 後続イベントが来ないことを確認するための余白
        # 大口は MAX_ORDER_VALUE で deny（rate 枠を消費しない）→ 小口だけ venue 到達。
        assert adapter.submit_order_call_count == 1, [v.kind for v in violations]
        assert not any(v.kind == "MAX_ORDERS_PER_MINUTE" for v in violations), [v.kind for v in violations]
        assert any(v.kind == "MAX_ORDER_VALUE" for v in violations)
        controller.detach(nautilus_strategy_id="LIVE-rate0002")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_position_cap_allows_reducing_order():
    """建玉を減らす注文（決済 SELL）は max_position_size cap で弾かれず venue に届く（#25 review finding 4）。"""
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _SellToClose(KernelStrategyBase):
        def on_bar(self, bar):
            self.submit_market(self.instrument_id, OrderSide.SELL, 100)  # long 300 を 200 へ減らす決済

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    violations: list = []
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_safety_violation=lambda v: violations.append(v),
    )
    # 既存 long 300 @9 = 2700。cap=3000。決済 SELL の概算金額 100@9=900。旧ロジックは
    # projected=2700+900=3600>3000 で MAX_POSITION_SIZE deny（決済を弾く）。新ロジックは
    # 減少注文なので cap をスキップ → venue 到達。
    rails = SafetyRails(SafetyLimits(max_position_size_jpy=3000))
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(
            cash=1_000_000.0,
            buying_power=1_000_000.0,
            positions=[AccountPositionData(symbol="8918", qty=300, avg_price=9.0, unrealized_pnl=0.0)],
        )
        run(runner.start())
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=9.0)
        controller.attach(
            strategy_cls=_SellToClose, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-close001", session=object(), safety_rails=rails,
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 9.0))
        deadline = time.time() + 5.0
        while time.time() < deadline and adapter.submit_order_call_count == 0:
            time.sleep(0.02)
        time.sleep(0.1)
        assert adapter.submit_order_call_count == 1, [v.kind for v in violations]
        assert not any(v.kind == "MAX_POSITION_SIZE" for v in violations), [v.kind for v in violations]
        controller.detach(nautilus_strategy_id="LIVE-close001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_position_cap_uses_market_value():
    """値上がり後の建て増しが時価で判定され上限を回避できない（#25 review finding 2、live 経路）。

    既存 long 100株@取得10（原価1000）。現在値 20 に値上がり後 1株 BUY すると約定後建玉は
    101株×20=2020。旧ロジックは取得原価 1000 + 注文 20 = 1020 ≤ 上限1500 で素通ししたが、
    driver が参照価格（直近 close=20）を rail に渡すので 2020 > 1500 で MAX_POSITION_SIZE deny。
    """
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _BuyOneMore(KernelStrategyBase):
        def on_bar(self, bar):
            self.submit_market(self.instrument_id, OrderSide.BUY, 1)  # 値上がり後の建て増し

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    violations: list = []
    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_safety_violation=lambda v: violations.append(v),
    )
    rails = SafetyRails(SafetyLimits(max_position_size_jpy=1500))
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(
            cash=1_000_000.0,
            buying_power=1_000_000.0,
            positions=[AccountPositionData(symbol="8918", qty=100, avg_price=10.0, unrealized_pnl=0.0)],
        )
        run(runner.start())
        adapter.set_next_order_outcome(status="FILLED", filled_qty=1.0, avg_price=20.0)
        controller.attach(
            strategy_cls=_BuyOneMore, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-mktcap01", session=object(), safety_rails=rails,
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 20.0))  # 現在値 20
        deadline = time.time() + 5.0
        while time.time() < deadline and not violations:
            time.sleep(0.02)
        assert violations, "market-value position cap did not fire"
        assert violations[0].kind == "MAX_POSITION_SIZE", [v.kind for v in violations]
        assert adapter.submit_order_call_count == 0  # venue 未到達
        controller.detach(nautilus_strategy_id="LIVE-mktcap01")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_seeded_position_in_pretrade():
    """venue 既存建玉が seed され pre-trade position cap に効く（D7）。"""
    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=DAY_NS)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
    )
    try:
        run(adapter.login(None))
        # venue snapshot は **bare code**（"8918"）を返す（kabu Symbol / tachibana 同様）。controller が
        # venue を付けて instrument_id 化（"8918.TSE"）してから seed しないと cap が誤判定する（#25 review）。
        adapter.set_account_snapshot(
            cash=1_000_000.0,
            buying_power=1_000_000.0,
            positions=[AccountPositionData(symbol="8918", qty=300, avg_price=9.0, unrealized_pnl=0.0)],
        )
        run(runner.start())
        controller.attach(
            strategy_cls=KernelSpikeBuySell, scenario=SCENARIO, instrument_id=IID,
            venue="MOCK", params={}, nautilus_strategy_id="LIVE-seed0001", session=object(),
        )
        pf = controller._driver._portfolio
        # bare "8918" が "8918.TSE" に正規化されて pre-trade cap の lookup key と一致する。
        assert pf.net_signed_qty(IID) == 300.0
        seeded = pf.open_positions()
        assert len(seeded) == 1 and seeded[0].quantity == 300.0 and seeded[0].avg_px == 9.0
        controller.detach(nautilus_strategy_id="LIVE-seed0001")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_controller_projects_expired_as_expired():
    """controller projection は OrderExpired を EXPIRED として外部通知する（#25 review finding 3）。

    OrderCanceled に collapse すると CANCELED に化けて内部 order.status=EXPIRED と矛盾する。
    """
    from engine.kernel.orders import Order, OrderExpired, OrderSide, OrderStatus

    order = Order(
        client_order_id="O-1", strategy_id="LIVE-x", instrument_id=IID,
        side=OrderSide.BUY, quantity=100.0, status=OrderStatus.EXPIRED,
    )
    event = OrderExpired(
        client_order_id="O-1", venue_order_id="O-1", strategy_id="LIVE-x",
        instrument_id=IID, side=OrderSide.BUY, ts_event_ns=0,
    )
    status, filled_qty, avg_price = KernelLiveEngineController._project_event(order, event)
    assert status == "EXPIRED"


def test_instrument_id_rejects_trailing_newline():
    """is_valid_instrument_id は末尾改行を弾く（`$` でなく fullmatch・#25 review finding 8）。"""
    from engine.kernel.instrument_id import is_valid_instrument_id

    assert is_valid_instrument_id("7203.TSE")
    assert not is_valid_instrument_id("7203.TSE\n")
    assert not is_valid_instrument_id("7203")        # venue サフィックス欠落
    assert not is_valid_instrument_id("7203.")       # venue 空
    assert not is_valid_instrument_id("7203 .TSE")   # 空白混入


def test_market_data_bus_subscription_close_unsubscribes_without_iteration():
    """consumer を一度も iterate せず close しても bus 購読が外れる（rollback leak 回避・#25 review finding 7）。"""
    from engine.live.event_bus import MarketDataBus

    bus = MarketDataBus()
    sub = bus.subscribe()
    assert len(bus._subscribers) == 1
    sub.close()
    assert len(bus._subscribers) == 0
    sub.close()  # 冪等
    assert len(bus._subscribers) == 0


def test_attach_timeout_does_not_leave_orphan_driver():
    """attach がタイムアウトしたら driver を commit せず bus 購読も残さない（fail-closed・#25 review finding 1/7）。"""
    from engine.live.event_bus import MarketDataBus

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, daemon=True)
    thread.start()

    def run(coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    bus = MarketDataBus()

    class _SlowRunner:
        def __init__(self):
            self.bus = bus

        async def subscribe(self, instrument_id):
            # attach_timeout（0.3s）を超え、かつ test の待ち（1.5s）より短くハングする。修正前は
            # wait_for が無いので boundary timeout 後も _do_attach が走り続け、ここを抜けて
            # self._driver を commit（孤児）。修正後は 0.3s で cancel され rollback で teardown。
            await asyncio.sleep(1.0)

    adapter = MockVenueAdapter()
    runner = _SlowRunner()
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        attach_timeout_s=0.3,
    )
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=1_000_000.0, buying_power=1_000_000.0, positions=())
        with pytest.raises(Exception):
            controller.attach(
                strategy_cls=KernelSpikeBuySell, scenario=SCENARIO, instrument_id=IID,
                venue="MOCK", params={}, nautilus_strategy_id="LIVE-orph0001", session=object(),
            )
        # _do_attach が（修正前なら）commit を終えるであろう時刻まで待ってから検証する。
        time.sleep(1.5)
        assert controller._driver is None, "orphan driver committed after attach timeout"
        assert len(bus._subscribers) == 0, "bus subscription leaked after attach timeout"
    finally:
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_on_start_order_without_price_denied_when_money_rail_set():
    """市場データ受信前の on_start 発注は、金額rail設定時に参照価格無しで素通しせず DENIED（#25 review finding 1）。

    notional を `last_prices.get(.., 0.0) * qty` で 0 円扱いすると、on_start 注文が
    max_order_value / max_position_size cap を常に回避して venue に到達する。参照価格未取得かつ
    金額rail有効なら bypass せず deny する。
    """
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _OrderInOnStart(KernelStrategyBase):
        def on_start(self):
            self.submit_market(self.instrument_id, OrderSide.BUY, 100)  # 価格不明・送られてはいけない

    violations: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_safety_violation=lambda v: violations.append(v)
    )
    rails = SafetyRails(SafetyLimits(max_order_value_jpy=1, max_position_size_jpy=1))
    try:
        # 仮にバグで素通ししたら venue 到達を観測できるよう fill outcome を仕込む。
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        controller.attach(
            strategy_cls=_OrderInOnStart, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-onstart1", session=object(), safety_rails=rails,
        )
        # market data を一切注入しない。on_start 発注は参照価格無しで deny されねばならない。
        deadline = time.time() + 3.0
        while time.time() < deadline and not violations:
            time.sleep(0.02)
        assert violations, "on_start order was not denied without a reference price"
        assert violations[0].kind == "NO_REFERENCE_PRICE", [v.kind for v in violations]
        assert adapter.submit_order_call_count == 0  # venue 未到達
        controller.detach(nautilus_strategy_id="LIVE-onstart1")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_mock_live_invalid_quantity_denied_before_venue():
    """quantity<=0 / NaN / inf は precheck 前に DENIED、venue 未到達（#25 review finding 2）。"""
    from engine.kernel.orders import OrderSide
    from engine.kernel.strategy import Strategy as KernelStrategyBase

    class _BadQty(KernelStrategyBase):
        def on_bar(self, bar):
            self.submit_market(self.instrument_id, OrderSide.BUY, -100)        # 負数
            self.submit_market(self.instrument_id, OrderSide.BUY, 0)           # ゼロ
            self.submit_market(self.instrument_id, OrderSide.BUY, float("nan"))  # NaN
            self.submit_market(self.instrument_id, OrderSide.BUY, float("inf"))  # inf

    statuses: list = []
    loop, thread, run, adapter, runner, controller = _live_harness(
        on_order_event=lambda ev, sid: statuses.append(ev.status)
    )
    try:
        adapter.set_next_order_outcome(status="FILLED", filled_qty=100.0, avg_price=8.0)
        controller.attach(
            strategy_cls=_BadQty, scenario=SCENARIO, instrument_id=IID, venue="MOCK",
            params={}, nautilus_strategy_id="LIVE-badqty01", session=object(),
        )
        loop.call_soon_threadsafe(adapter.inject_tick, _kline(1, 8.0))
        deadline = time.time() + 3.0
        while time.time() < deadline and statuses.count("DENIED") < 4:
            time.sleep(0.02)
        # rails 無も valid instrument なので、4 注文が DENIED になる原因は数量検証以外に無い。
        assert statuses.count("DENIED") == 4, statuses
        assert adapter.submit_order_call_count == 0  # 不正数量は venue へ送られない
        # 各 Order が DENIED 終端し、人間可読な理由（finite positive 違反）が刻まれている。
        denied = [o for o in controller._driver._orders.values() if o.status.value == "DENIED"]
        assert len(denied) == 4
        assert all("finite positive" in o.denied_reason for o in denied), [o.denied_reason for o in denied]
        controller.detach(nautilus_strategy_id="LIVE-badqty01")
    finally:
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)


def test_broker_modify_rejects_invalid_new_qty():
    """broker.modify は new_qty が非有限/非正なら venue へ送らず order を変えない（#25 review finding 2）。"""
    from engine.kernel.live.broker import LiveBroker
    from engine.kernel.orders import Order, OrderSide, OrderStatus

    class _SpyAdapter:
        def __init__(self):
            self.modify_calls = 0

        async def modify_order(self, **kw):
            self.modify_calls += 1
            raise AssertionError("venue must not be called for invalid new_qty")

    adapter = _SpyAdapter()
    broker = LiveBroker(adapter=adapter, venue="MOCK")

    for bad in (-1.0, 0.0, float("nan"), float("inf")):
        order = Order(
            client_order_id="O-1", strategy_id="s", instrument_id=IID,
            side=OrderSide.BUY, quantity=100.0, status=OrderStatus.ACCEPTED,
        )
        events = asyncio.run(broker.modify(order, new_qty=bad))
        assert events == []
        assert order.quantity == 100.0  # 不正 modify で数量は据え置き
        assert order.status == OrderStatus.ACCEPTED  # live のまま
    assert adapter.modify_calls == 0
