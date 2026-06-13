"""engine.kernel.live.broker — LiveBroker: kernel OrderEngine ↔ venue adapter bridge + order FSM (#25).

Replay の `ReplayBroker`（bar close で決定的約定）に対応する Live 実体。mock venue tracer の
authoritative fill source は同期 `OrderResult`（D1）。同期結果と（将来の）非同期 EC イベントを
**同一入口 `apply_venue_update` に正規化**し、fill 重複排除は **累積約定数量 delta**（受信イベント数
ではない・D1）。

order FSM（D1）:
    INITIALIZED → DENIED                         # OrderEngine.precheck（venue 未到達・broker 外）
                → SUBMITTED → REJECTED           # adapter/venue reject（ACCEPTED を経由しない）
                            → ACCEPTED
                                → PARTIALLY_FILLED
                                → FILLED
                                → CANCELED

broker は **adapter I/O と Order の FSM 遷移**だけを担い、Portfolio 反映 / strategy.on_order /
UI 投影は呼び出し側（driver / controller）が events を受けて行う（ReplayBroker と同じく副作用を持たない）。
Nautilus-free。
"""
from __future__ import annotations

from typing import Any

from engine.kernel.orders import (
    Order,
    OrderAccepted,
    OrderCanceled,
    OrderEngine,
    OrderFilled,
    OrderRejected,
    OrderStatus,
)
from engine.live.order_types import OrderResult, is_filled, is_terminal

_TERMINAL_STATES = frozenset(
    {
        OrderStatus.FILLED,
        OrderStatus.CANCELED,
        OrderStatus.REJECTED,
        OrderStatus.EXPIRED,
        OrderStatus.DENIED,
    }
)


class LiveBroker:
    """venue adapter を叩き、`OrderResult` から kernel order FSM を駆動する。

    `apply_venue_update` は純粋（I/O 無し・テスト可能）。`submit`/`cancel`/`modify` はその async
    ラッパで、adapter 呼び出し→正規化を行う。venue_order_id は mock 契約で client_order_id を流用する
    （実 venue 採番は #23）。
    """

    def __init__(self, *, adapter: Any, venue: str) -> None:
        self._adapter = adapter
        self._venue = venue

    # ── async adapter I/O ────────────────────────────────────────────────────

    async def submit(self, order: Order) -> list:
        """precheck 通過済みの order を venue へ送る。SUBMITTED→adapter→正規化。

        order は OrderEngine.precheck() を通過し INITIALIZED のまま渡されること（caller が precheck）。
        """
        order.status = OrderStatus.SUBMITTED
        try:
            res: OrderResult = await self._adapter.submit_order(
                venue=self._venue,
                instrument_id=order.instrument_id,
                side=order.side.value,
                qty=order.quantity,
                price=None,  # tracer: MARKET（price 未確定）。LIMIT は #23
                order_type="MARKET",
                time_in_force="GTC",
                client_order_id=order.client_order_id,
            )
        except Exception as exc:  # noqa: BLE001 — adapter/venue 失敗は REJECTED に正規化
            order.status = OrderStatus.REJECTED
            return [
                OrderRejected(
                    client_order_id=order.client_order_id,
                    strategy_id=order.strategy_id,
                    instrument_id=order.instrument_id,
                    side=order.side,
                    reason=f"ADAPTER_ERROR: {exc}",
                    ts_event_ns=order.ts_last_ns,
                )
            ]
        return self.apply_venue_update(order, res, source="submit_result")

    async def cancel(self, order: Order) -> list:
        """当該 order を venue で取消。取消拒否なら live 据え置き（D8）。

        キャンセル競合で venue が約定（FILLED/PARTIALLY_FILLED）や EXPIRED を応答した場合に取りこぼさ
        ないよう、**取消拒否以外は `apply_venue_update` に通して**正規化する。素朴に CANCELED へ強制すると
        競合約定が Portfolio に反映されず kernel は flat と誤認し venue 建玉と desync する（#25 review finding 3）。
        """
        if order.status in _TERMINAL_STATES:
            return []
        prior = order.status
        try:
            res: OrderResult = await self._adapter.cancel_order(
                venue=self._venue, order_id=order.client_order_id
            )
        except Exception:  # noqa: BLE001 — 取消の adapter 失敗は live 据え置き（best-effort）
            order.status = prior
            return []
        if res.status == "REJECTED":
            order.status = prior  # 取消拒否: 元注文は live のまま（注文自体の REJECTED ではない）
            return []
        return self.apply_venue_update(order, res, source="cancel_result")

    async def modify(
        self, order: Order, *, new_price: float | None = None, new_qty: float | None = None
    ) -> list:
        """venue で訂正。CANCELED/約定を返したら対応する終端/約定イベントを emit する（nautilus 経路同型）。

        拒否系 terminal（REJECTED/DENIED/EXPIRED 等）は更新を適用せず元状態へ復帰する（注文全体を
        terminal にしない・D6）。
        """
        if order.status in _TERMINAL_STATES:
            return []
        prior = order.status
        try:
            res: OrderResult = await self._adapter.modify_order(
                venue=self._venue,
                order_id=order.client_order_id,
                new_price=new_price,
                new_qty=new_qty,
            )
        except Exception:  # noqa: BLE001
            order.status = prior
            return []
        if res.status == "CANCELED":
            order.status = OrderStatus.CANCELED
            return [self._canceled(order)]
        if is_filled(res.status) and res.filled_qty > 0:
            return self._apply_fill(order, res, source="modify_result")
        if is_terminal(res.status):
            # modify 拒否系 terminal（REJECTED/DENIED/EXPIRED 等）: 更新を適用せず元状態へ復帰
            # （注文全体を terminal にしない・D6）。venue 由来 EXPIRED の reconcile は #23 scope。
            order.status = prior
            return []
        # price/qty 更新成立（status は据え置き live）。新規数量があれば quantity を更新。
        if new_qty is not None:
            order.quantity = new_qty
        order.status = prior
        return []

    # ── pure normalization (sync, testable) ──────────────────────────────────

    def apply_venue_update(self, order: Order, res: OrderResult, *, source: str) -> list:
        """同期 `OrderResult` / 非同期 event を同一入口で正規化し FSM を進める（D1）。

        - REJECTED: SUBMITTED→REJECTED（既に terminal なら無視）。
        - CANCELED / EXPIRED: ACCEPTED に進めず終端化する（約定でも reject でもない terminal）。
        - それ以外: 未 ACCEPTED なら ACCEPTED を 1 回 emit、その後 fill（cumulative dedup）。
        fill 重複排除は **累積約定数量 delta**: delta = incoming_cumulative − order.filled_qty。
        delta<=0 は duplicate/stale として無視、delta>0 のみ OrderFilled(last_qty=delta) を返す。
        """
        if order.status in _TERMINAL_STATES:
            return []  # 終端後の遅延イベントは無視（dedup の一種）

        events: list = []

        if res.status == "REJECTED":
            # 既に部分約定済みに後から REJECTED が来た（async EC の stale/残数量 reject 等）場合、注文全体を
            # REJECTED にすると既約定分の会計が宙に浮く。約定済みなら CANCELED で終端化し fill を保持する
            # （REJECTED は未約定からのみ・#25 review finding 5）。
            if order.filled_qty > 0:
                order.status = OrderStatus.CANCELED
                order.venue_order_id = order.venue_order_id or order.client_order_id
                return [self._canceled(order)]
            order.status = OrderStatus.REJECTED
            order.denied_reason = res.reject_reason or "REJECTED"
            events.append(
                OrderRejected(
                    client_order_id=order.client_order_id,
                    strategy_id=order.strategy_id,
                    instrument_id=order.instrument_id,
                    side=order.side,
                    reason=order.denied_reason,
                    ts_event_ns=order.ts_last_ns,
                )
            )
            return events

        # 終端だが約定でも reject でもない（CANCELED / EXPIRED）: ACCEPTED へ進めず終端化する。
        # apply_venue_update は同期 OrderResult と非同期 EC イベントの**単一入口**なので、ここで
        # 取りこぼすと async EC（#23）が CANCELED/EXPIRED を運んだとき誤って ACCEPTED に化ける
        # （codex review #25）。tracer では EXPIRED 専用 event を設けず OrderCanceled で終端通知する。
        if res.status in ("CANCELED", "EXPIRED"):
            order.status = (
                OrderStatus.CANCELED if res.status == "CANCELED" else OrderStatus.EXPIRED
            )
            order.venue_order_id = order.venue_order_id or order.client_order_id
            return [self._canceled(order)]

        # venue ACK: 未 ACCEPTED（SUBMITTED）なら ACCEPTED を 1 回だけ emit。
        if order.status is OrderStatus.SUBMITTED:
            order.venue_order_id = order.venue_order_id or order.client_order_id
            order.status = OrderStatus.ACCEPTED
            events.append(
                OrderAccepted(
                    client_order_id=order.client_order_id,
                    venue_order_id=order.venue_order_id,
                    strategy_id=order.strategy_id,
                    instrument_id=order.instrument_id,
                    side=order.side,
                    ts_event_ns=order.ts_last_ns,
                )
            )

        events.extend(self._apply_fill(order, res, source=source))
        return events

    # ── helpers ──────────────────────────────────────────────────────────────

    def _apply_fill(self, order: Order, res: OrderResult, *, source: str) -> list:
        """cumulative dedup を適用し、増分 fill があれば OrderFilled(last_qty=delta) を返す。

        venue は **累積**平均価格（`res.avg_price`）と累積数量（`res.filled_qty`）を報告する。Portfolio に
        渡す増分 fill の価格は累積平均ではなく **累積約定代金の差から算出した増分価格**でなければならない:
            increment_px = (new_cum_qty×new_cum_avg − prev_cum_qty×prev_cum_avg) / delta_qty
        例: 50@8 の後に累積 100@9 が来たら後半 50 は @10。累積平均 9 を増分価格に使うと Portfolio が
        平均 8.5 になり誤る（codex review #25 finding 1）。`order.avg_px` は venue 累積平均を保持する。
        """
        if not (is_filled(res.status) and res.filled_qty and res.filled_qty > 0):
            return []
        incoming_cumulative = float(res.filled_qty)
        delta = incoming_cumulative - order.filled_qty
        if delta <= 0:
            return []  # duplicate / stale（受信イベント数ではなく累積数量で判定・D1）
        if res.avg_price is not None:
            new_cum_avg = float(res.avg_price)
            # この増分の約定代金 = 新累積代金 − 旧累積代金。delta>0 なので 0 除算しない。
            increment_notional = incoming_cumulative * new_cum_avg - order.filled_qty * order.avg_px
            increment_px = increment_notional / delta
            order.avg_px = new_cum_avg  # venue 報告の累積平均を保持
        else:
            # venue が約定価格を報告しない degenerate ケース（実 venue は報告する・mock は常に設定）。
            increment_px = order.avg_px
        order.filled_qty = incoming_cumulative
        order.venue_order_id = order.venue_order_id or order.client_order_id
        order.status = (
            OrderStatus.FILLED
            if incoming_cumulative >= order.quantity
            else OrderStatus.PARTIALLY_FILLED
        )
        return [
            OrderFilled(
                client_order_id=order.client_order_id,
                venue_order_id=order.venue_order_id,
                strategy_id=order.strategy_id,
                instrument_id=order.instrument_id,
                side=order.side,
                last_qty=delta,
                last_px=increment_px,
                ts_event_ns=order.ts_last_ns,
                cumulative_filled_qty=incoming_cumulative,
            )
        ]

    def _canceled(self, order: Order) -> OrderCanceled:
        return OrderCanceled(
            client_order_id=order.client_order_id,
            venue_order_id=order.venue_order_id or order.client_order_id,
            strategy_id=order.strategy_id,
            instrument_id=order.instrument_id,
            side=order.side,
            ts_event_ns=order.ts_last_ns,
        )
