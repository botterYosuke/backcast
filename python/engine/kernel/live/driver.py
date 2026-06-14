"""engine.kernel.live.driver — LiveRunner.bus → kernel Strategy のドライバ (#25 D3/D8)。

`LiveRunner.bus` を単一 async consumer で消費し、確定 `KlineUpdate` を kernel `Bar` に、`TradesUpdate`
を tick に変換して strategy の `on_bar` / `on_tick` を駆動する。strategy が `submit_market` で積んだ
intent は、各 callback 後に **再入防止 FIFO drain loop** で OrderEngine.precheck → LiveBroker.submit に
流す。fill / deny は Portfolio 反映・strategy.on_order・UI 投影 callback へ配る。

すべて live loop thread 上で動く。strategy callback 内で adapter を await しない（intent を積むだけ）。
Nautilus-free。
"""
from __future__ import annotations

import asyncio
import logging
import math
import time
from collections import deque
from dataclasses import dataclass
from typing import Any, Callable, Optional

from engine.kernel.bars import Bar
from engine.kernel.live.broker import LiveBroker
from engine.kernel.orders import (
    Order,
    OrderDenied,
    OrderEngine,
    OrderFilled,
    OrderSide,
    OrderStatus,
)
from engine.kernel.portfolio import Portfolio
from engine.kernel.strategy import Strategy
from engine.live.adapter import KlineUpdate, TradesUpdate
from engine.live.order_types import OrderResult
from engine.live.safety_rails import (
    KIND_MAX_ORDERS_PER_MINUTE,
    KIND_NO_REFERENCE_PRICE,
    RailViolation,
)
from engine.live.strategy_log import StrategyLogRecord

log = logging.getLogger(__name__)

# 不正数量（非有限 / 非正）の deny kind。安全rail違反ではなく注文の形式不正なので
# on_safety_violation には乗せず、OrderDenied で戦略へ返すだけ（手動経路の order_facade と同じ規約）。
KIND_INVALID_QTY = "INVALID_QTY"

_OPEN_STATES = frozenset(
    {
        OrderStatus.SUBMITTED,
        OrderStatus.ACCEPTED,
        OrderStatus.PARTIALLY_FILLED,
        OrderStatus.PENDING_UPDATE,
        OrderStatus.PENDING_CANCEL,
    }
)


@dataclass
class _Intent:
    client_order_id: str
    instrument_id: str
    side: OrderSide
    quantity: float


class _Ctx:
    """StrategyContext 実装: submit_market は intent を積むだけ（adapter は await しない）。"""

    def __init__(self, driver: "KernelLiveDriver") -> None:
        self._driver = driver

    def submit_market(
        self, *, strategy_id: str, instrument_id: str, side: OrderSide, quantity: float
    ) -> None:
        self._driver._enqueue(instrument_id=instrument_id, side=side, quantity=quantity)

    def log(self, message: str) -> None:
        self._driver._on_strategy_log(message)


class KernelLiveDriver:
    """1 run 分の kernel Strategy を live loop 上で駆動する（単一 run・§0.7）。"""

    def __init__(
        self,
        *,
        strategy: Strategy,
        order_engine: OrderEngine,
        portfolio: Portfolio,
        broker: LiveBroker,
        bus,
        instrument_ids: list[str],
        nautilus_strategy_id: str,
        emit_order: Callable[[Order, Any], None],
        emit_log: Optional[Callable[[StrategyLogRecord], None]] = None,
        is_run_gated: Optional[Callable[[], bool]] = None,
        on_safety_violation: Optional[Callable[[Any], None]] = None,
        on_strategy_error: Optional[Callable[[BaseException], None]] = None,
        max_orders_per_minute: int = 0,
    ) -> None:
        self._strategy = strategy
        self._order_engine = order_engine
        self._portfolio = portfolio
        self._broker = broker
        self._bus = bus
        self._instrument_ids = set(instrument_ids)
        self._nsid = nautilus_strategy_id
        self._emit_order = emit_order
        # telemetry emitter は driver の counters を読む closure のため、driver 構築後に
        # controller が set_telemetry_emitter で差す（chicken-and-egg 回避）。
        self._emit_telemetry: Optional[Callable[[], None]] = None
        self._emit_log = emit_log

        self._is_run_gated = is_run_gated
        self._on_safety_violation = on_safety_violation
        # 戦略 callback（on_bar/on_tick/on_order）が走行中に例外を投げたら、握り潰さず run を
        # 失敗させる経路（host.fail_run）へ伝える seam（#25 finding 5）。controller→orchestrator が注入。
        self._on_strategy_error = on_strategy_error

        self.ctx = _Ctx(self)
        self._intents: deque[_Intent] = deque()
        self._draining = False
        self._stopping = False
        self._failed = False
        self._bus_iter = None
        self._consumer_task: Optional[asyncio.Task] = None
        self._client_seq = 0
        self._orders: dict[str, Order] = {}
        self.fill_count = 0  # 実 fill イベント数（partial 含む counter・D7）
        self.last_prices: dict[str, float] = {}
        # max_orders_per_minute（旧 Nautilus native rate rail）の kernel 実装。venue へ送る注文の
        # monotonic 時刻を 60s 窓で保持し、超過を deny する（#25 finding 1）。0 は無効。
        self._max_orders_per_minute = int(max_orders_per_minute or 0)
        self._order_times: deque[float] = deque()

    def set_telemetry_emitter(self, emit_telemetry: Callable[[], None]) -> None:
        """telemetry emitter を後から差す（controller が driver の counters を読む closure を渡す）。"""
        self._emit_telemetry = emit_telemetry

    # ── lifecycle ────────────────────────────────────────────────────────────

    def subscribe_bus(self) -> None:
        """bus consumer iterator を **同期確立**する（task 開始前。初回 event を逃さない・D8）。"""
        self._bus_iter = self._bus.subscribe()

    def start_consumer(self) -> None:
        self._consumer_task = asyncio.create_task(self._consume())

    async def run_on_start(self) -> None:
        """strategy.on_start を呼び、**成功時のみ**積まれた intent を drain する（#25 finding 1）。

        on_start が例外を投げた（attach 失敗。例: artifact 不足で fail-loud に raise）場合は、例外前に
        キュー済みの intent を venue に送らず破棄して再送出する。`finally` で drain すると attach が
        失敗しても注文が venue に到達してしまうため、ここでは成功パスでのみ drain する。
        """
        try:
            self._strategy.on_start()
        except Exception:
            self._intents.clear()
            raise
        await self._drain()

    async def stop(self) -> None:
        """新規 intent 受付停止 → consumer 停止・await → bus 購読解除 → on_stop 最大 1 回（D8）。"""
        self._stopping = True
        task = self._consumer_task
        self._consumer_task = None
        if task is not None:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass
        # 共有 bus の購読を解除する。consumer が一度も起動していない rollback 経路では生成器の finally が
        # 走らず queue が bus に残り leak するため、明示 close で確実に外す（冪等・#25 review finding 7）。
        if self._bus_iter is not None:
            self._bus_iter.close()
            self._bus_iter = None
        try:
            self._strategy.on_stop()
        except Exception:  # noqa: BLE001 — teardown は best-effort
            log.exception("kernel strategy on_stop failed")

    async def cancel_inflight(self) -> None:
        """この run の open order（SUBMITTED/ACCEPTED/PARTIALLY_FILLED/PENDING_UPDATE/PENDING_CANCEL）を取消（D8）。

        各 cancel は個別 try で隔離し、1 件の失敗で残りを止めない。
        """
        for order in list(self._orders.values()):
            if order.status not in _OPEN_STATES:
                continue
            try:
                events = await self._broker.cancel(order)
            except Exception:  # noqa: BLE001
                log.exception("cancel_inflight: broker.cancel failed for %s", order.client_order_id)
                continue
            for ev in events:
                self._deliver(order, ev)

    def apply_venue_async_event(self, ev: Any) -> bool:
        """venue 非同期 event（poll/EC 由来）を kernel broker FSM へ正規化する（#23・findings 0014・(b)）。

        共有 adapter の async stream（kabu `GET /orders` poll 等）は manual / auto を区別しないため、
        orchestrator は **client_order_id でルーティング**する: この driver が `_orders` に持つ注文だけを
        `apply_venue_update` に通し、持たない注文（manual facade / 別 run）は無視する。受付
        （PENDING_CANCEL / PENDING_UPDATE）は broker が非終端で open に保ち、poll 終端
        （CANCELED / FILLED / EXPIRED）が確定させる。受付〜確定の隙間の競合約定もここで会計される
        （CONTEXT.md「取消受付 / 取消確定」）。`ev` は OrderEventData 互換（status / filled_qty /
        avg_price / client_order_id）。filled_qty は累積（broker の cumulative-delta dedup が前提）。

        live loop thread から sync で呼ばれる（`apply_venue_update` は I/O 無し・pure）。**この driver が
        当該注文を持つ（＝owned）なら True を返す**（orchestrator が空タグの二重 UI emit を避ける判定に使う）。
        """
        order = self._orders.get(ev.client_order_id)
        if order is None:
            return False  # この run の注文ではない（manual / 別 run）→ 無視
        try:
            # OrderResult 構築は status 検証を伴う（非 canonical status は弾く）。構築失敗も
            # owned 扱いで握る（後続 poll が再運搬。空タグ二重 emit は避ける）。
            res = OrderResult(
                status=ev.status,
                filled_qty=ev.filled_qty,
                avg_price=ev.avg_price,
                client_order_id=ev.client_order_id,
            )
            events = self._broker.apply_venue_update(order, res, source="venue_stream")
        except Exception:  # noqa: BLE001 — async 確定経路は best-effort（poll は後続でも届く）
            log.exception(
                "apply_venue_async_event: apply_venue_update failed for %s",
                ev.client_order_id,
            )
            return True
        for event in events:
            self._deliver(order, event)
        # 終端化した注文は _orders から外す（teardown が再 cancel しない・open leak 防止）。
        if order.status not in _OPEN_STATES:
            self._orders.pop(order.client_order_id, None)
        # async fill に反応して戦略が on_order で積んだ intent を drain する。本メソッドは sync なので
        # live loop に drain task を schedule する（_draining ガードが再入を直列化・consumer 経路と整合）。
        if self._intents and not self._stopping:
            try:
                asyncio.get_running_loop().create_task(self._drain())
            except RuntimeError:
                log.warning("apply_venue_async_event: no running loop to drain reaction intents")
        return True

    # ── bus consumer ─────────────────────────────────────────────────────────

    async def _consume(self) -> None:
        if self._bus_iter is None:
            self.subscribe_bus()
        try:
            async for evt in self._bus_iter:  # type: ignore[union-attr]
                try:
                    if getattr(evt, "instrument_id", None) not in self._instrument_ids:
                        continue
                    if isinstance(evt, TradesUpdate):
                        self.last_prices[evt.instrument_id] = evt.price
                        self._strategy.on_tick(evt)
                        await self._drain()
                    elif isinstance(evt, KlineUpdate) and evt.is_closed:
                        # 確定バーのみ on_bar（partial は弾く・D3）。
                        self.last_prices[evt.instrument_id] = evt.close
                        bar = Bar(
                            instrument_id=evt.instrument_id,
                            ts_event_ns=evt.ts_ns,
                            open=evt.open,
                            high=evt.high,
                            low=evt.low,
                            close=evt.close,
                            volume=evt.volume,
                        )
                        self._strategy.on_bar(bar)
                        await self._drain()
                except Exception as exc:  # noqa: BLE001 — 戦略/イベント処理失敗は run を fail させる
                    # 握り潰さない: on_bar/on_tick/drain（dup 等）例外は run の障害として host.fail_run へ
                    # 伝える（#25 finding 5）。consumer 自体は detach 経由で停止される。
                    log.exception("kernel live driver: strategy/event handling failed")
                    self._signal_strategy_error(exc)
        except asyncio.CancelledError:
            return
        except Exception:  # noqa: BLE001 — bus close 後など
            log.warning("kernel live driver consumer stopped on error", exc_info=True)
            return

    # ── intent submission ──────────────────────────────────────────────────────

    def _enqueue(self, *, instrument_id: str, side: OrderSide, quantity: float) -> None:
        self._client_seq += 1
        cid = f"O-{self._nsid}-{self._client_seq}"
        if self._stopping:
            # detach 開始後の新規発注は破棄でも DENIED でもなく**明示的 terminal event**で閉じる（D8）。
            order = Order(
                client_order_id=cid,
                strategy_id=self._strategy.id,
                instrument_id=instrument_id,
                side=side,
                quantity=quantity,
                status=OrderStatus.DENIED,
                denied_reason="RUN_STOPPING",
            )
            self._orders[order.client_order_id] = order
            self._deliver(
                order,
                self._denied(order, "RUN_STOPPING", "new orders rejected: run is stopping"),
            )
            return
        self._intents.append(
            _Intent(client_order_id=cid, instrument_id=instrument_id, side=side, quantity=quantity)
        )

    async def _drain(self) -> None:
        """積まれた intent を FIFO 逐次処理する。再入防止: drain 中の submit_market は同じ loop が拾う。"""
        if self._draining:
            return
        self._draining = True
        try:
            while self._intents:
                # teardown / 戦略例外後は積まれた intent を venue に送らず破棄する（#25 finding 2）。
                if self._stopping:
                    self._intents.clear()
                    break
                intent = self._intents.popleft()
                await self._process_intent(intent)
        finally:
            self._draining = False

    async def _process_intent(self, intent: _Intent) -> None:
        # finding 2: pop と process の間で stop/error が立った場合も venue に送らない。
        if self._stopping:
            return
        order = Order(
            client_order_id=intent.client_order_id,
            strategy_id=self._strategy.id,
            instrument_id=intent.instrument_id,
            side=intent.side,
            quantity=intent.quantity,
        )
        self._orders[order.client_order_id] = order

        # 数量検証: 有限かつ正数。precheck 前に弾き、不正数量を venue へ送らせない（#25 review finding 2）。
        # `<= 0` だけでは NaN を取りこぼす（NaN との比較は常に False）ため isfinite を明示する
        # （手動経路 order_facade._place と同じ規約）。形式不正は safety rail 違反ではないので toast しない。
        if not math.isfinite(intent.quantity) or intent.quantity <= 0:
            self._deny(order, KIND_INVALID_QTY,
                       f"order quantity {intent.quantity!r} is not a finite positive number")
            return

        # PAUSE / run gate は venue 送信前（precheck 前）に判定する（D8）。
        if self._is_run_gated is not None and self._is_run_gated():
            self._deny(order, "STRATEGY_PAUSED", "run is PAUSED; new orders gated")
            return

        # 独自 pre-trade（allowlist / max_position_size / max_order_value / 規制）。MARKET の概算約定金額は
        # 直近価格 × 数量（last_prices は当該 bar の close / tick price で更新済み）。
        # 参照価格が未取得（market data 受信前の on_start 発注等）で、かつ金額rail（order value /
        # position size）が有効なら、notional を 0 円扱いで素通しすると cap を回避して venue に到達する。
        # bypass せず明示 deny する（#25 review finding 1）。金額rail 無効時のみ 0 で precheck を通す。
        price = self.last_prices.get(intent.instrument_id)
        if price is None and self._order_engine.requires_reference_price:
            self._deny(
                order, KIND_NO_REFERENCE_PRICE,
                f"no reference price for {intent.instrument_id}; "
                f"cannot evaluate notional rails before market data",
                as_safety_violation=True,
            )
            return
        notional = (price if price is not None else 0.0) * intent.quantity
        violation = self._order_engine.precheck(
            order,
            net_signed_qty=self._portfolio.net_signed_qty(intent.instrument_id),
            reference_price=price,  # 約定後建玉の時価評価に使う直近値（#25 review findings 2/3）
            order_notional_jpy=notional,
        )
        if violation is not None:
            self._deny(order, violation.kind, violation.detail, as_safety_violation=True)
            return

        # max_orders_per_minute（旧 native rate rail）は **他の pre-trade rail を通過した後・venue 送信の
        # 直前**に判定する。precheck で DENIED になった注文は venue に送られないので rate 窓を消費させない
        # （rate check を precheck より前に置くと、拒否注文が枠を食って後続の正常注文を誤って rate-deny する
        # ・#25 review finding 2）。
        rate_violation = self._check_rate_limit()
        if rate_violation is not None:
            self._deny(order, rate_violation.kind, rate_violation.detail, as_safety_violation=True)
            return

        events = await self._broker.submit(order)
        for ev in events:
            self._deliver(order, ev)

    def _deny(
        self, order: Order, kind: str, detail: str, *, as_safety_violation: bool = False
    ) -> None:
        """注文を DENIED 終端し OrderDenied を配る。`as_safety_violation` なら safety rail 違反として
        `on_safety_violation`（UI トースト）にも乗せる。kind は OrderDenied に載るので denied_reason には
        人間可読な detail だけを置く（kind の二重化を避ける）。"""
        order.status = OrderStatus.DENIED
        order.denied_reason = detail
        self._deliver(order, self._denied(order, kind, detail))
        if as_safety_violation and self._on_safety_violation is not None:
            self._on_safety_violation(RailViolation(kind, detail))

    def _check_rate_limit(self) -> Optional[RailViolation]:
        """max_orders_per_minute（旧 Nautilus native rate rail）の kernel 実装。0 は無効。

        venue へ送る注文の monotonic 時刻を 60s 窓で保持し、窓内件数が上限以上なら違反を返す。
        違反でない場合のみ今回の時刻を記録する（denied 注文は窓を消費しない）。
        """
        if self._max_orders_per_minute <= 0:
            return None
        now = time.monotonic()
        cutoff = now - 60.0
        while self._order_times and self._order_times[0] < cutoff:
            self._order_times.popleft()
        if len(self._order_times) >= self._max_orders_per_minute:
            return RailViolation(
                KIND_MAX_ORDERS_PER_MINUTE,
                f"order submit rate exceeds {self._max_orders_per_minute}/min",
            )
        self._order_times.append(now)
        return None

    # ── event delivery ─────────────────────────────────────────────────────────

    def _deliver(self, order: Order, event) -> None:
        """1 FSM event を Portfolio / strategy.on_order / UI 投影へ配る。"""
        if isinstance(event, OrderFilled):
            self._portfolio.apply_fill(event)
            self.fill_count += 1
        # domain hook（戦略は on_order で新規 intent を積み得る → 同じ drain loop が拾う）。
        # Portfolio 反映後に呼ぶので、例外でも建玉会計は確定済み。例外は run の障害として伝える。
        try:
            self._strategy.on_order(event)
        except Exception as exc:  # noqa: BLE001
            log.exception("kernel strategy on_order failed")
            self._signal_strategy_error(exc)
        # UI 投影（controller が event の種類から OrderEventData にして on_order_event へ）。
        try:
            self._emit_order(order, event)
        except Exception:  # noqa: BLE001 — UI bridge は best-effort
            log.exception("emit_order failed")
        if isinstance(event, OrderFilled) and self._emit_telemetry is not None:
            try:
                self._emit_telemetry()
            except Exception:  # noqa: BLE001
                log.exception("emit_telemetry failed")

    def _denied(self, order: Order, kind: str, reason: str) -> OrderDenied:
        return OrderDenied(
            client_order_id=order.client_order_id,
            strategy_id=order.strategy_id,
            instrument_id=order.instrument_id,
            side=order.side,
            quantity=order.quantity,
            kind=kind,
            reason=reason,
            ts_event_ns=0,
        )

    def _signal_strategy_error(self, exc: BaseException) -> None:
        """走行中の戦略例外を run の障害として外へ通知する（host.fail_run 経路・#25 finding 5）。

        冪等（最初の 1 回だけ通知）。以降の新規 intent は受けない（_stopping）。実際の teardown
        （cancel_inflight → detach → driver.stop）は orchestrator が **別スレッド**で fail_run して行う
        （on_strategy_error は live loop thread から呼ばれるので、ここで blocking teardown はしない）。
        """
        if self._failed:
            return
        self._failed = True
        self._stopping = True
        self._intents.clear()  # finding 2: 失敗後はキュー済み注文を venue に送らない
        if self._on_strategy_error is not None:
            try:
                self._on_strategy_error(exc)
            except Exception:  # noqa: BLE001
                log.exception("on_strategy_error callback failed")

    def _on_strategy_log(self, message: str) -> None:
        if self._emit_log is None:
            return
        try:
            self._emit_log(
                StrategyLogRecord(level="INFO", message=str(message), ts_ns=time.time_ns())
            )
        except Exception:  # noqa: BLE001
            log.exception("emit_log failed")

    # ── telemetry inputs (read by the controller) ───────────────────────────────

    @property
    def order_count(self) -> int:
        return len(self._orders)
