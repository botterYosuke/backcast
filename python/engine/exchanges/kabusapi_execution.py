"""KabuExecutionEngine — 約定経路を KabuStationAdapter から分離したエンジン (Issue #219)。"""

from __future__ import annotations

import asyncio
import logging
import time
import uuid
from dataclasses import dataclass
from typing import Callable, Literal

import httpx

from engine.exchanges import kabusapi_orders as _orders
from engine.exchanges.kabusapi_auth import KabuApiError, auth_headers, check_response
from engine.exchanges.kabusapi_url import endpoint
from engine.exchanges.kabusapi_ratelimit import KabuRateLimits
from engine.exchanges.order_registry import NormalizedReport, OrderRegistry
from engine.live.adapter import InstrumentId, OnOrderEvent, OnVenueLogout, SecretResolver
from engine.live.order_types import AccountPositionData, AccountSnapshot, OrderResult

logger = logging.getLogger(__name__)

_ORDER_TIMEOUT = httpx.Timeout(connect=10.0, read=30.0, write=10.0, pool=5.0)
_ORDERS_POLL_INTERVAL_S = 1.0
# 訂正 (取消→新規) で取消確定を待つ最大ポーリング回数 (×_ORDERS_POLL_INTERVAL_S)。
_MODIFY_CANCEL_WAIT_POLLS = 10
_POLL_MAX_BACKOFF_S = 30.0


@dataclass
class _KabuOrderRef:
    """発注済み注文の追跡情報 (取消/訂正/polling・訂正の再発注で使う)。

    kabu の訂正は「取消 → 新規発注」変換 (§2.2) で venue OrderId が更新されるため、
    本 ref は **mutable** にして同一 client_order_id に新 OrderId を再マップする。
    ``filled_base`` / ``notional_base`` は訂正で捨てた旧 venue leg の累計約定を退避する。
    """

    client_order_id: str
    order_id: str  # venue 採番の OrderId (訂正で更新される)
    symbol: str
    exchange: int
    side: str
    qty: float
    price: float | None
    order_type: str
    time_in_force: str
    account_type: int
    filled_base: float = 0.0
    notional_base: float = 0.0


class KabuExecutionEngine:
    """kabuStation 約定経路エンジン (KabuStationAdapter から分離)。"""

    def __init__(
        self,
        client: httpx.AsyncClient,
        rl: KabuRateLimits,
        env: Literal["prod", "verify"],
        time_source: Callable[[], float],
    ) -> None:
        self._client = client
        self._rl = rl
        self._env = env
        self._time_source = time_source
        self._token: str | None = None
        # --- 約定追跡フィールド ---
        self._on_order_event: OnOrderEvent | None = None
        self._orders_ref: dict[str, _KabuOrderRef] = {}
        self._registry = OrderRegistry()
        self._modifying: set[str] = set()
        self._rate_limit_sleep = asyncio.sleep
        self._orders_poll_task: asyncio.Task | None = None
        self._last_error: BaseException | None = None

    def on_login(self, token: str) -> None:
        """adapter が login 後に呼ぶ。polling を開始する（後続ステップで実装）。"""
        self._token = token

    async def on_logout(self) -> None:
        """adapter が logout 時に呼ぶ（poll 停止・state クリア）。"""
        await self._stop_orders_poll()
        self._orders_ref.clear()
        self._registry.clear()
        self._modifying.clear()
        self._token = None

    def set_execution_hooks(
        self,
        *,
        secret_resolver: SecretResolver | None = None,
        on_order_event: OnOrderEvent,
        on_venue_logout: OnVenueLogout | None = None,
    ) -> None:
        """_backend_impl が OrderEvent push を注入する。

        kabu は Password 不要 (R3) のため ``secret_resolver`` は受理して無視する。
        約定通知は GET /orders polling 由来なので、polling は最初の ``submit_order``
        で遅延起動する (idle polling 回避)。
        ``on_venue_logout`` も受理して無視する: kabu の本体ログアウト検知は
        poll 型の VenueHealthWatchdog で行う (§3.5)。
        """
        self._on_order_event = on_order_event

    @staticmethod
    def _rejected_result(
        client_order_id: str, ack: "_orders.SendOrderAck"
    ) -> OrderResult:
        """発注エラー (Result != 0) を REJECTED な OrderResult に正規化する。"""
        return OrderResult(
            status="REJECTED",
            filled_qty=0.0,
            avg_price=None,
            client_order_id=client_order_id,
            reject_reason=f"{ack.reject_code}:{ack.reject_text}",
        )

    def _register_order(self, ref: _KabuOrderRef) -> None:
        self._orders_ref[ref.client_order_id] = ref
        self._registry.register(ref.client_order_id, ref.order_id)

    def _unregister_order(self, client_order_id: str) -> None:
        self._orders_ref.pop(client_order_id, None)
        self._registry.unregister(client_order_id)

    def _parse_instrument_id(self, instrument_id: InstrumentId) -> tuple[str, int]:
        symbol, _, suffix = instrument_id.rpartition(".")
        if suffix != "TSE":
            raise ValueError(f"unsupported exchange suffix: {suffix!r} (MVP supports TSE only)")
        return symbol, 1

    async def _send_order(self, payload: dict[str, object]) -> "_orders.SendOrderAck":
        """POST /sendorder を流量抑制付きで叩き、HTTP/Code + Result を判定して ack を返す。"""
        await self._rl.gate("sendorder")
        resp = await self._client.post(
            endpoint("sendorder", env=self._env),
            headers=auth_headers(self._token or ""),
            json=payload,
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        check_response(data, resp.status_code)
        return _orders.parse_send_order_response(data)

    def _ensure_orders_poll(self) -> None:
        """OrderEvent push が設定済みなら polling task を 1 本起動する (idempotent)。"""
        if self._on_order_event is None:
            return
        if self._orders_poll_task is not None and not self._orders_poll_task.done():
            return
        self._orders_poll_task = asyncio.create_task(self._run_orders_poll())

    async def _stop_orders_poll(self) -> None:
        task = self._orders_poll_task
        if task is not None and not task.done():
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass
            except Exception as exc:
                logger.warning(
                    "kabu orders poll task errored during stop: %s", exc
                )
        self._orders_poll_task = None

    async def _run_orders_poll(self) -> None:
        """1 秒間隔で GET /orders を叩き、状態変化を OrderEvent に変換して push する。

        全注文が終端化して追跡対象がなくなったら自己終了する (idle な 1 秒ループを
        畳む)。次の ``submit_order`` が ``_ensure_orders_poll`` で再起動する。

        連続失敗時 (本体ログアウト / 401 / 接続断) は指数バックオフで間隔を延ばし、
        1Hz hot-loop と R5 流量浪費を避ける。成功で間隔は通常の 1 秒へ戻す。
        """
        backoff_s = 0.0
        while True:
            if not self._orders_ref:
                return
            try:
                await self._rate_limit_sleep(backoff_s or _ORDERS_POLL_INTERVAL_S)
            except asyncio.CancelledError:
                return
            try:
                await self._poll_orders_once()
                backoff_s = 0.0
            except asyncio.CancelledError:
                raise
            except BaseException as exc:  # noqa: BLE001
                self._last_error = exc
                backoff_s = min(
                    (backoff_s or _ORDERS_POLL_INTERVAL_S) * 2, _POLL_MAX_BACKOFF_S
                )
                logger.warning(
                    "kabu orders poll failed, backing off %.0fs: %s", backoff_s, exc
                )

    async def _poll_orders_once(self) -> None:
        """GET /orders を 1 回叩き、追跡中注文の状態変化のみ push する。"""
        if self._token is None or self._on_order_event is None or not self._orders_ref:
            return
        await self._rl.gate("orders")
        resp = await self._client.get(
            endpoint("orders", env=self._env),
            headers=auth_headers(self._token or ""),
            params={"product": 1},  # 現物のみ
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        check_response(data, resp.status_code)
        for order in data if isinstance(data, list) else []:
            if not isinstance(order, dict):
                continue
            venue_id = str(order.get("ID", ""))
            cid = self._registry.lookup_cid(venue_id)
            if cid is None or cid in self._modifying:
                continue
            report = _orders.parse_order_status(order)
            if report is None:
                continue
            normalized = NormalizedReport(
                venue_order_id=report.order_id,
                status=report.status,
                filled_qty=report.filled_qty,
                avg_price=report.avg_price,
                terminal=report.terminal,
                ts_ms=report.ts_ms or int(time.time() * 1000),
            )
            event = self._registry.fold_report(normalized)
            if event is not None:
                self._on_order_event(event)
            if normalized.terminal:
                self._orders_ref.pop(cid, None)
                # fold_report の dedup (state-key 一致) は early-return し unregister を
                # スキップする。terminal は dedup 有無に関わらず registry を必ずクリアする。
                self._registry.unregister(cid)

    async def submit_order(
        self,
        *,
        venue: str,
        instrument_id: InstrumentId,
        side: str,
        qty: float,
        price: float | None,
        order_type: str,
        time_in_force: str,
        client_order_id: str | None = None,
    ) -> OrderResult:
        """POST /sendorder で現物新規発注する (Password 不要)。

        受付成立で ACCEPTED を返す。約定 (FILLED/PARTIALLY_FILLED) は GET /orders polling
        で後追いし OrderEvent として push する。発注エラー Result != 0 は REJECTED に正規化
        (ただし Result == -1 の異常終了は KabuApiError を上層へ伝播、§2.2)。
        """
        if self._token is None:
            raise RuntimeError("submit_order requires login; call login() first")
        symbol, exchange = self._parse_instrument_id(instrument_id)
        payload = _orders.build_send_order_payload(
            symbol=symbol,
            exchange=exchange,
            side=side,
            qty=qty,
            price=price,
            order_type=order_type,
            time_in_force=time_in_force,
        )
        ack = await self._send_order(payload)
        client_order_id = client_order_id or uuid.uuid4().hex
        if ack.rejected:
            if ack.reject_code == "-1":
                raise KabuApiError(-1, ack.reject_text or "kabu sendorder system error")
            return self._rejected_result(client_order_id, ack)
        if not ack.order_id:
            raise KabuApiError(
                0, "kabu sendorder accepted but returned no OrderId"
            )
        self._register_order(
            _KabuOrderRef(
                client_order_id=client_order_id,
                order_id=ack.order_id,
                symbol=symbol,
                exchange=exchange,
                side=side.upper(),
                qty=qty,
                price=price,
                order_type=order_type.upper(),
                time_in_force=time_in_force,
                account_type=_orders.DEFAULT_ACCOUNT_TYPE,
            )
        )
        self._ensure_orders_poll()
        return OrderResult(
            status="ACCEPTED", filled_qty=0.0, avg_price=None,
            client_order_id=client_order_id,
        )

    async def _cancel_venue_order(self, order_id: str) -> "_orders.SendOrderAck":
        """PUT /cancelorder を流量抑制付きで叩く (OrderID のみ・Password 不要、R3)。"""
        await self._rl.gate("cancelorder")
        resp = await self._client.put(
            endpoint("cancelorder", env=self._env),
            headers=auth_headers(self._token or ""),
            json=_orders.build_cancel_order_payload(order_id=order_id),
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        check_response(data, resp.status_code)
        return _orders.parse_send_order_response(data)

    async def cancel_order(
        self, *, venue: str, order_id: str
    ) -> OrderResult:
        """PUT /cancelorder で取消する (OrderID のみ・Password 不要)。

        取消受付成立で **PENDING_CANCEL** を返す (#23・findings 0014・(a))。ack-then-poll
        venue では PUT /cancelorder 成立は取消「受付」にすぎず、注文はまだ open。終端の取消
        確定 (CANCELED・約定残ゼロ) は GET /orders polling が後追いで運ぶ。受付を終端 CANCELED と
        返すと、受付〜確定の隙間で起きた競合約定を consumer が取りこぼす
        (CONTEXT.md「取消受付 / 取消確定」)。Result != 0 の取消拒否は REJECTED (facade が
        CANCEL_REJECTED に変換し元注文は live のまま)。
        """
        if self._token is None:
            raise RuntimeError("cancel_order requires login; call login() first")
        if order_id in self._modifying:
            # 訂正 (取消→新規) 進行中の注文を並行取消すると、modify が remap した新 leg を
            # 孤児化させうる (cancel↔modify re-entrancy)。modify 完了まで弾く。
            return OrderResult(
                status="REJECTED", filled_qty=0.0, avg_price=None,
                client_order_id=order_id, reject_reason="MODIFY_IN_PROGRESS",
            )
        ref = self._orders_ref.get(order_id)
        if ref is None:
            return OrderResult(
                status="REJECTED", filled_qty=0.0, avg_price=None,
                client_order_id=order_id, reject_reason="UNKNOWN_VENUE_ORDER",
            )
        ack = await self._cancel_venue_order(ref.order_id)
        if ack.rejected:
            return self._rejected_result(order_id, ack)
        return OrderResult(
            status="PENDING_CANCEL", filled_qty=0.0, avg_price=None,
            client_order_id=order_id,
        )

    async def modify_order(
        self,
        *,
        venue: str,
        order_id: str,
        new_price: float | None = None,
        new_qty: float | None = None,
    ) -> OrderResult:
        """訂正を「取消 → 新規発注」変換で実現する (kabu に訂正 API は無い、§2.2)。

        atomicity は保証されない。補償結果を facade 契約に合わせて OrderResult.status で
        表現する (proto 非変更の Step 5 方針を踏襲):

        - 取消失敗 → ``REJECTED`` (facade が MODIFY_REJECTED。**元注文は live のまま**)。
        - 取消成功 + 新規失敗 → ``CANCELED`` (facade が同一注文を CANCELED 終端化。
          **元注文は取消済み**で新規は出ていない → UI は取消済みとして正しく表示。
          ユーザーは再発注すればよい)。
        - 取消確定待ちタイムアウト → ``REJECTED`` (新規は見送り。元注文の確定状態は
          polling が後追いで反映する)。
        - 全成功 → ``ACCEPTED`` (同一 client_order_id に新 OrderId を再マップ。polling は
          新 OrderId を同じ注文として追跡する)。
        """
        if self._token is None:
            raise RuntimeError("modify_order requires login; call login() first")
        if order_id in self._modifying:
            return OrderResult(
                status="REJECTED", filled_qty=0.0, avg_price=None,
                client_order_id=order_id, reject_reason="MODIFY_IN_PROGRESS",
            )
        ref = self._orders_ref.get(order_id)
        if ref is None:
            return OrderResult(
                status="REJECTED", filled_qty=0.0, avg_price=None,
                client_order_id=order_id, reject_reason="UNKNOWN_VENUE_ORDER",
            )

        self._modifying.add(order_id)
        try:
            # 1. 元注文を取消す。
            cancel_ack = await self._cancel_venue_order(ref.order_id)
            if cancel_ack.rejected:
                return OrderResult(
                    status="REJECTED", filled_qty=0.0, avg_price=None,
                    client_order_id=order_id,
                    reject_reason="MODIFY_CANCEL_FAILED:原注文は残っています",
                )

            # 2. 取消確定 (State==5) を待ち、確定時点の OrderStatusReport を得る。
            terminal = await self._await_order_terminal(
                ref.order_id, max_polls=_MODIFY_CANCEL_WAIT_POLLS
            )
            if terminal is None:
                return OrderResult(
                    status="REJECTED", filled_qty=0.0, avg_price=None,
                    client_order_id=order_id,
                    reject_reason="MODIFY_CANCEL_TIMEOUT:取消の確定を確認できませんでした",
                )

            # 3. 取消が成立するまでに約定した数量を差し引いた「残数量」だけ再発注する。
            already_filled = terminal.filled_qty
            total_target = new_qty if new_qty is not None else ref.filled_base + ref.qty
            total_filled = ref.filled_base + already_filled
            merged_qty = total_target - total_filled
            merged_price = new_price if new_price is not None else ref.price
            total_notional = ref.notional_base + already_filled * (terminal.avg_price or 0.0)
            total_avg = total_notional / total_filled if total_filled > 0 else None
            if merged_qty <= 0:
                self._unregister_order(order_id)
                final_status = terminal.status
                if total_filled > 0 and final_status == "REJECTED":
                    final_status = "CANCELED"
                return OrderResult(
                    status=final_status,
                    filled_qty=total_filled,
                    avg_price=total_avg,
                    client_order_id=order_id,
                    reject_reason=(
                        None if final_status == "FILLED"
                        else "MODIFY_ALREADY_FILLED:原注文が目標数量まで約定済みのため再発注しません"
                    ),
                )
            new_payload = _orders.build_send_order_payload(
                symbol=ref.symbol,
                exchange=ref.exchange,
                side=ref.side,
                qty=merged_qty,
                price=merged_price,
                order_type=ref.order_type,
                time_in_force=ref.time_in_force,
                account_type=ref.account_type,
            )
            new_ack = await self._send_order(new_payload)
            if new_ack.rejected:
                if new_ack.reject_code == "-1":
                    raise KabuApiError(
                        -1, new_ack.reject_text or "kabu sendorder system error"
                    )
                self._unregister_order(order_id)
                return OrderResult(
                    status="CANCELED",
                    filled_qty=total_filled,
                    avg_price=total_avg,
                    client_order_id=order_id,
                    reject_reason="MODIFY_NEW_FAILED:原注文は取消済みです。再発注してください",
                )

            # 4. 全成功: 同一 client_order_id に新 OrderId を再マップする。
            self._registry.remap(order_id, new_ack.order_id, total_filled, total_notional)
            ref.order_id = new_ack.order_id
            ref.qty = merged_qty
            ref.price = merged_price
            ref.filled_base = total_filled
            ref.notional_base = total_notional
            self._ensure_orders_poll()
            return OrderResult(
                status="ACCEPTED",
                filled_qty=total_filled,
                avg_price=total_avg,
                client_order_id=order_id,
            )
        finally:
            self._modifying.discard(order_id)

    async def _await_order_terminal(
        self, order_id: str, *, max_polls: int
    ) -> "_orders.OrderStatusReport | None":
        """GET /orders?id=... を polling し、対象注文が終端 (State==5) になったら、その
        確定時点の ``OrderStatusReport`` を返す。確認できなければ ``None``。
        """
        for i in range(max_polls):
            await self._rl.gate("orders")
            resp = await self._client.get(
                endpoint("orders", env=self._env),
                headers=auth_headers(self._token or ""),
                params={"product": 1, "id": order_id},
                timeout=_ORDER_TIMEOUT,
            )
            data = resp.json()
            check_response(data, resp.status_code)
            rows = data if isinstance(data, list) else [data] if isinstance(data, dict) else []
            for order in rows:
                if not isinstance(order, dict):
                    continue
                report = _orders.parse_order_status(order)
                if report is not None and report.order_id == order_id and report.terminal:
                    return report
            if i < max_polls - 1:
                await self._rate_limit_sleep(_ORDERS_POLL_INTERVAL_S)
        return None

    async def _fetch_wallet_cash(self) -> dict:
        await self._rl.gate("wallet/cash")
        resp = await self._client.get(
            endpoint("wallet/cash", env=self._env),
            headers=auth_headers(self._token or ""),
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        check_response(data, resp.status_code)
        return data

    async def _fetch_positions(self) -> list:
        await self._rl.gate("positions")
        resp = await self._client.get(
            endpoint("positions", env=self._env),
            headers=auth_headers(self._token or ""),
            params={"product": 1, "addinfo": "true"},  # 現物のみ + 評価損益を含める
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        check_response(data, resp.status_code)
        return data

    async def fetch_account(self) -> AccountSnapshot:
        """GET /wallet/cash + GET /positions で口座同期。"""
        if self._token is None:
            raise RuntimeError("fetch_account requires login; call login() first")
        cash_data, pos_data = await asyncio.gather(
            self._fetch_wallet_cash(), self._fetch_positions()
        )
        buying_power = _orders.parse_float(
            cash_data.get("StockAccountWallet") if isinstance(cash_data, dict) else 0
        )
        rows = pos_data if isinstance(pos_data, list) else []
        positions = tuple(
            AccountPositionData(
                symbol=str(p.get("Symbol", "")),
                qty=int(_orders.parse_float(p.get("LeavesQty"))),
                avg_price=_orders.parse_float(p.get("Price")),
                unrealized_pnl=_orders.parse_float(p.get("ProfitLoss")),
            )
            for p in rows
            if isinstance(p, dict)
            and _orders.parse_float(p.get("LeavesQty")) > 0
        )
        return AccountSnapshot(
            cash=buying_power, buying_power=buying_power, positions=positions
        )

    async def fetch_working_orders(self) -> list:
        """kabu: 接続時 seed 用 working-orders 取得（Slice 3b stub）。"""
        return []
