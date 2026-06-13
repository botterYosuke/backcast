"""engine.kernel.live.controller — KernelLiveEngineController (#25)。

`NautilusLiveEngineController`（`NautilusKernel` を起こし Rust core をロードする）を置換する
pure-Python の `LiveEngineController` 実体。`attach()` で run ごとに kernel の OrderEngine /
Portfolio / RiskEngine / LiveBroker / Strategy / driver を組み、共有 `LiveRunner.bus` から bar/tick を
流して live 発注する。**Rust core を一切 import しない**（import-purity gate・D5）。

`NautilusLiveEngineController` と**同一 ctor seam**（loop/adapter/runner provider・on_order_event/
on_telemetry/on_strategy_log/on_safety_violation・run_gate_provider）を満たし、`live_orchestrator` の
swap は生成箇所の class 名だけ（D2）。Phase 単一 run 制約（§0.7）に合わせ同時に 1 driver だけ保持する。

Nautilus-free。
"""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Any, Callable, Optional

from engine.kernel.instrument_id import is_valid_instrument_id
from engine.kernel.live.broker import LiveBroker
from engine.kernel.live.driver import KernelLiveDriver
from engine.kernel.orders import (
    Order,
    OrderAccepted,
    OrderCanceled,
    OrderDenied,
    OrderEngine,
    OrderFilled,
    OrderRejected,
)
from engine.kernel.portfolio import Portfolio
from engine.kernel.risk import RiskEngine
from engine.kernel.strategy import Strategy as KernelStrategy
from engine.live.order_types import OrderEventData
from engine.live.safety_rails import SafetyLimits, SafetyRails

log = logging.getLogger(__name__)

# attach 本体は live loop 上で `wait_for(_attach_timeout_s)` が時間制限する。boundary 側の
# `fut.result` はそれを **必ず後から** 取りこぼすため、内側より長く待つ（この差が縮むと boundary が
# 先に諦め、wait_for の cancel/rollback が走る前に孤児 driver を残す fail-open に戻る）。
_ATTACH_RESULT_BACKSTOP_SLACK_S = 5.0


class KernelLiveEngineController:
    """共有 venue adapter を kernel live engine に bridge する controller（`NautilusLiveEngineController` 置換）。"""

    def __init__(
        self,
        *,
        loop_provider: Callable[[], Any],
        adapter_provider: Callable[[], Any],
        runner_provider: Optional[Callable[[], Any]] = None,
        on_safety_violation: Optional[Callable[[Any], None]] = None,
        on_order_event: Optional[Callable[[Any, str], None]] = None,
        on_telemetry: Optional[Callable[[str, dict], None]] = None,
        on_strategy_log: Optional[Callable[[Any, str], None]] = None,
        run_gate_provider: Optional[Callable[[str], bool]] = None,
        regulation_provider: Optional[Callable[[], Any]] = None,
        on_strategy_error: Optional[Callable[[BaseException], None]] = None,
        attach_timeout_s: float = 60.0,
        trader_id: str = "LIVEHOST-001",
    ) -> None:
        self._loop_provider = loop_provider
        self._adapter_provider = adapter_provider
        self._runner_provider = runner_provider
        self._on_safety_violation = on_safety_violation
        self._on_order_event = on_order_event
        self._on_telemetry = on_telemetry
        self._on_strategy_log = on_strategy_log
        self._run_gate_provider = run_gate_provider
        self._regulation_provider = regulation_provider
        # 走行中の戦略例外を run の障害（host.fail_run）へ伝える seam（#25 finding 5）。
        self._on_strategy_error = on_strategy_error
        self._attach_timeout_s = attach_timeout_s
        self._trader_id = trader_id

        # 単一 run（§0.7）。
        self._driver: Optional[KernelLiveDriver] = None
        self._nsid: Optional[str] = None

    # ── Protocol: attach / detach / cancel_inflight_orders ──────────────────────

    def attach(
        self,
        *,
        strategy_cls: Any,
        scenario: dict,
        instrument_id: str,
        venue: str,
        params: dict[str, str],
        nautilus_strategy_id: str,
        session: Any,
        safety_rails: Any = None,
    ) -> None:
        loop = self._loop_provider()
        adapter = self._adapter_provider()
        if loop is None or adapter is None:
            raise RuntimeError("live loop / venue adapter not available for attach")
        # attach 本体を **live loop 上で** `wait_for` により時間制限する。boundary（run_coroutine_threadsafe）
        # 側の result(timeout) では、超過してもコルーチンは cancel されず走り続け、後から self._driver を
        # commit して consumer を起動する「孤児 driver」が残る（run は host 側で既に unregister 済みなので
        # gate が開き、ガバナンス無しで venue に発注し得る fail-open・#25 review finding 1）。wait_for が
        # 超過時にコルーチンを cancel すると、下の rollback（BaseException も捕捉）が driver を teardown し、
        # self._driver は commit されない（fail-closed）。外側は wait_for を先に発火させるため少し長く待つ。
        fut = asyncio.run_coroutine_threadsafe(
            asyncio.wait_for(
                self._do_attach(
                    strategy_cls=strategy_cls,
                    scenario=scenario,
                    instrument_id=instrument_id,
                    params=params,
                    nautilus_strategy_id=nautilus_strategy_id,
                    adapter=adapter,
                    safety_rails=safety_rails,
                ),
                self._attach_timeout_s,
            ),
            loop,
        )
        fut.result(timeout=self._attach_timeout_s + _ATTACH_RESULT_BACKSTOP_SLACK_S)

    async def _do_attach(
        self,
        *,
        strategy_cls,
        scenario,
        instrument_id,
        params,
        nautilus_strategy_id,
        adapter,
        safety_rails,
    ) -> None:
        if not is_valid_instrument_id(instrument_id):
            raise ValueError(f"invalid instrument_id: {instrument_id!r}")
        venue_str = instrument_id.split(".")[-1]
        rails: SafetyRails = safety_rails if safety_rails is not None else SafetyRails(SafetyLimits())

        instrument_ids = self._live_instrument_ids(
            primary_instrument_id=instrument_id, scenario=scenario
        )

        # Portfolio seed: venue snapshot（cash＋既存建玉）。取得失敗は fail-closed（D7）。
        snapshot = await adapter.fetch_account()
        portfolio = Portfolio(initial_cash=float(getattr(snapshot, "cash", 0.0) or 0.0))
        for pos in getattr(snapshot, "positions", ()) or ():
            qty = float(getattr(pos, "qty", 0.0) or 0.0)
            if qty == 0.0:
                continue
            # venue snapshot の symbol は **bare code**（kabu `Symbol`="7203" / tachibana
            # `sUriOrderIssueCode`）。kernel Portfolio は `SYMBOL.VENUE` で keying し、pre-trade cap も
            # `net_signed_qty(instrument_id)` で引くので、venue を付けて instrument_id 化しないと seed が
            # 不可視になり cap を誤判定する（codex review #25）。既に dot を含むなら instrument_id とみなす。
            raw_symbol = str(getattr(pos, "symbol", ""))
            if not raw_symbol:
                continue
            seed_iid = raw_symbol if "." in raw_symbol else f"{raw_symbol}.{venue_str}"
            portfolio.seed_position(seed_iid, qty, float(getattr(pos, "avg_price", 0.0) or 0.0))

        risk = RiskEngine(rails, regulation_provider=self._regulation_provider)
        order_engine = OrderEngine(risk_engine=risk, venue=venue_str)
        broker = LiveBroker(adapter=adapter, venue=venue_str)

        # 戦略インスタンス化（kernel base・共通 kwargs 契約）。kernel は Nautilus BarType を使わないので
        # bar_type_str は渡さない（instrument_id ＋ params だけ）。基底 Strategy が instrument_id/**params を
        # 受けるので、専用 __init__ を持たない最小戦略でも生成できる（#25 finding 2）。
        kwargs: dict[str, Any] = {"instrument_id": instrument_id}
        kwargs.update(params)
        strategy: KernelStrategy = strategy_cls(**kwargs)
        # run identity を強制する（Nautilus controller の change_id 相当）。戦略が自前で付けた id に
        # 関わらず、当該 run の発注主体 = nautilus_strategy_id にする（Order.strategy_id もこれを運ぶ）。
        strategy.id = nautilus_strategy_id

        runner = self._runner_provider() if self._runner_provider is not None else None
        if runner is None:
            raise RuntimeError("kernel live controller requires a LiveRunner (bus) provider")
        bus = runner.bus

        driver = KernelLiveDriver(
            strategy=strategy,
            order_engine=order_engine,
            portfolio=portfolio,
            broker=broker,
            bus=bus,
            instrument_ids=instrument_ids,
            nautilus_strategy_id=nautilus_strategy_id,
            emit_order=self._make_emit_order(nautilus_strategy_id),
            emit_log=self._make_emit_log(nautilus_strategy_id),
            is_run_gated=self._make_is_run_gated(nautilus_strategy_id),
            on_safety_violation=self._on_safety_violation,
            on_strategy_error=self._on_strategy_error,
            max_orders_per_minute=rails.limits.max_orders_per_minute,
        )
        # telemetry は driver の counters（order_count/fill_count）を読むため driver 確定後に差す。
        driver.set_telemetry_emitter(
            self._make_emit_telemetry(nautilus_strategy_id, portfolio, driver)
        )

        # attach 順（D8）: register → bus.subscribe() 同期確立（以降の event は iterator の queue に
        # 蓄積され取りこぼさない） → runner.subscribe → on_start + drain → consumer task 起動。
        # consumer を on_start **後** に起動するのが要点: subscribe〜on_start の間に届いた bar/tick は
        # queue に積まれ、consumer 起動後に FIFO で処理されるので、戦略の on_start が必ず最初の on_bar/
        # on_tick より前に走る（Nautilus の "on_start precedes data" 不変条件を保つ・codex review #25）。
        # 失敗時は逆順 rollback（consumer/driver を残さない）。共有 runner の購読は detach 同様に外さない（D8）。
        strategy.register(driver.ctx)
        driver.subscribe_bus()
        try:
            for iid in instrument_ids:
                await runner.subscribe(iid)
            await driver.run_on_start()
            driver.start_consumer()
        except BaseException:  # noqa: BLE001 — timeout/cancel(CancelledError) も含め必ず teardown する
            log.exception("kernel live attach failed during start; rolling back")
            try:
                await driver.stop()
            except Exception:  # noqa: BLE001
                log.exception("rollback driver.stop failed")
            raise

        self._driver = driver
        self._nsid = nautilus_strategy_id

    def detach(self, *, nautilus_strategy_id: str) -> None:
        driver = self._driver
        self._driver = None
        self._nsid = None
        if driver is None:
            return
        loop = self._loop_provider()
        if loop is None:
            return
        try:
            asyncio.run_coroutine_threadsafe(driver.stop(), loop).result(timeout=10.0)
        except Exception:  # noqa: BLE001 — 停止失敗でも run state は terminal にする
            log.exception("kernel live driver stop failed during detach")

    def cancel_inflight_orders(self, *, nautilus_strategy_id: str) -> None:
        driver = self._driver
        if driver is None:
            return
        loop = self._loop_provider()
        if loop is None:
            return
        try:
            asyncio.run_coroutine_threadsafe(driver.cancel_inflight(), loop).result(timeout=6.0)
        except Exception:  # noqa: BLE001 — best-effort
            log.exception("kernel live cancel_inflight scheduling failed")

    # ── callback factories（UI 投影・telemetry・run gate） ──────────────────────

    def _make_emit_order(self, nsid: str) -> Callable[[Order, Any], None]:
        def _emit(order: Order, event: Any) -> None:
            if self._on_order_event is None:
                return
            # status は **event の種類**から導く（order は同一 apply_venue_update 内で ACCEPTED→FILLED
            # まで mutate 済みなので、order.status を読むと ACCEPTED event が FILLED に化ける）。
            status, filled_qty, avg_price = self._project_event(order, event)
            ev = OrderEventData(
                order_id=order.client_order_id,
                venue_order_id=order.venue_order_id or "",
                client_order_id=order.client_order_id,
                status=status,
                filled_qty=filled_qty,
                avg_price=avg_price,
                ts_ms=int(time.time() * 1000),
            )
            self._on_order_event(ev, nsid)

        return _emit

    @staticmethod
    def _project_event(order: Order, event: Any) -> tuple[str, float, float]:
        """FSM event → (status name, filled_qty, avg_price)。order.status には依存しない。"""
        if isinstance(event, OrderAccepted):
            return "ACCEPTED", 0.0, 0.0
        if isinstance(event, OrderRejected):
            return "REJECTED", float(order.filled_qty), float(order.avg_px)
        if isinstance(event, OrderCanceled):
            return "CANCELED", float(order.filled_qty), float(order.avg_px)
        if isinstance(event, OrderDenied):
            return "DENIED", 0.0, 0.0
        if isinstance(event, OrderFilled):
            # FILLED vs PARTIALLY_FILLED は broker が _apply_fill で既に決定済み（order.status）。
            # fill event は最新 fill の直後に配られるので order.status はその fill の結果を表す
            # ＝単一ソース（threshold 判定を controller 側で再計算しない・altitude）。
            return order.status.value, float(event.cumulative_filled_qty), float(order.avg_px)
        # 未知 event は order の現状態を素直に投影（防御的フォールバック）。
        return order.status.value, float(order.filled_qty), float(order.avg_px)

    def _make_emit_telemetry(
        self, nsid: str, portfolio: Portfolio, driver: KernelLiveDriver
    ) -> Callable[[], None]:
        def _emit() -> None:
            if self._on_telemetry is None:
                return
            self._on_telemetry(nsid, self._compute_telemetry(portfolio, driver))

        return _emit

    def _make_emit_log(self, nsid: str) -> Callable[[Any], None]:
        def _emit(record) -> None:
            if self._on_strategy_log is None:
                return
            self._on_strategy_log(record, nsid)

        return _emit

    def _make_is_run_gated(self, nsid: str) -> Callable[[], bool]:
        def _gated() -> bool:
            if self._run_gate_provider is None:
                return False
            return bool(self._run_gate_provider(nsid))

        return _gated

    @staticmethod
    def _compute_telemetry(portfolio: Portfolio, driver: KernelLiveDriver) -> dict:
        unrealized = 0.0
        for pos in portfolio.open_positions():
            last = driver.last_prices.get(pos.instrument_id, pos.avg_px)
            unrealized += pos.quantity * (last - pos.avg_px)
        return {
            "realized_pnl": portfolio.realized_pnl,
            "unrealized_pnl": unrealized,
            "order_count": driver.order_count,
            "fill_count": driver.fill_count,
        }

    @staticmethod
    def _live_instrument_ids(*, primary_instrument_id: str, scenario: dict) -> list[str]:
        result: list[str] = []
        seen: set[str] = set()
        for candidate in [primary_instrument_id, *(scenario.get("instruments") or [])]:
            if not isinstance(candidate, str) or not candidate or candidate in seen:
                continue
            seen.add(candidate)
            result.append(candidate)
        return result
