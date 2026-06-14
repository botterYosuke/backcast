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

import math
from typing import Any

from engine.kernel.orders import (
    Order,
    OrderAccepted,
    OrderCanceled,
    OrderEngine,
    OrderExpired,
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


def _is_finite_positive(value: float | None) -> bool:
    """有限かつ正の数値か（`None` / NaN / inf / <=0 を弾く）。

    価格・数量を venue へ送る／会計する前のガード。`> 0` 比較だけでは NaN を取りこぼす
    （NaN との比較は常に False）ため isfinite を明示する。
    """
    return value is not None and math.isfinite(value) and value > 0


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
            return [self._rejected(order, f"ADAPTER_ERROR: {exc}")]
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

        `new_qty` / `new_price` は **有限かつ正数**でなければ venue へ送らず order を据え置く（不正値を
        adapter に渡さない・#25 review finding 2）。`<= 0` だけでは NaN を取りこぼすため isfinite を明示。
        """
        if order.status in _TERMINAL_STATES:
            return []
        if new_qty is not None and not _is_finite_positive(new_qty):
            return []
        if new_price is not None and not _is_finite_positive(new_price):
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
            invalid = self._invalid_fill_guard(order, res)
            if invalid is not None:
                return invalid
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
            events.append(self._rejected(order, order.denied_reason))
            return events

        # 終端だが約定でも reject でもない（CANCELED / EXPIRED）: ACCEPTED へ進めず終端化する。
        # apply_venue_update は同期 OrderResult と非同期 EC イベントの**単一入口**なので、ここで
        # 取りこぼすと async EC（#23）が CANCELED/EXPIRED を運んだとき誤って ACCEPTED に化ける
        # （codex review #25）。EXPIRED は OrderExpired で終端通知する（status と外部通知を一致させる・
        # finding 3。OrderCanceled で代用すると projection が CANCELED に化け FSM と矛盾する）。
        if res.status in ("CANCELED", "EXPIRED"):
            order.venue_order_id = order.venue_order_id or order.client_order_id
            if res.status == "CANCELED":
                order.status = OrderStatus.CANCELED
                return [self._canceled(order)]
            order.status = OrderStatus.EXPIRED
            return [self._expired(order)]

        # fail-closed: malformed な増分 fill（価格欠落・非有限/範囲外の累積数量・非正の増分価格）は
        # ACCEPTED emit より前に処理する。REJECTED は ACCEPTED を経由しない FSM 不変条件を保つため
        # （findings 1/2。不正会計の防止は _invalid_fill_guard が担う）。
        invalid = self._invalid_fill_guard(order, res)
        if invalid is not None:
            return invalid

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

    def _fill_violation(self, order: Order, res: OrderResult) -> str | None:
        """増分 fill が venue 契約違反なら理由を返す。健全 or 非 fill / dedup なら `None`（findings 1/2）。

        会計する前の純粋な fail-closed 検証。実 venue は **有限・正の累積数量（注文数量以内）と正の約定
        価格** を報告するので、それ以外は契約違反として弾く:
          - 非有限の累積数量（NaN / inf）→ `cash=NaN`・`position=(inf, NaN)` を防ぐ（finding 1）。
          - 累積数量 > 注文数量 → over-fill（建玉が注文を超える）を防ぐ（finding 1）。
          - 価格欠落 / 非正の約定価格 → 0 円・負値会計を防ぐ（前回 finding 1）。
          - 増分価格が非正 → 累積平均が既約定分を下回ると increment_px が負になり、BUY fill が現金を
            増やす誤会計になる（finding 2）。
        """
        if not is_filled(res.status):
            return None
        cumulative = res.filled_qty
        if cumulative is None or not math.isfinite(cumulative):
            return f"FILL_NONFINITE_QTY: non-finite cumulative filled_qty {cumulative!r}"
        delta = cumulative - order.filled_qty
        if cumulative <= 0 or delta <= 0:
            return None  # fill 無し / duplicate / stale（_apply_fill が dedup・契約違反ではない）
        if cumulative > order.quantity:
            return (
                f"FILL_EXCEEDS_ORDER_QTY: cumulative {cumulative:g} exceeds order quantity "
                f"{order.quantity:g}"
            )
        if not _is_finite_positive(res.avg_price):
            return "FILL_WITHOUT_PRICE: venue reported a fill without an execution price"
        increment_px = (cumulative * float(res.avg_price) - order.filled_qty * order.avg_px) / delta
        if not _is_finite_positive(increment_px):
            return (
                f"FILL_NONPOSITIVE_INCREMENT_PRICE: increment price {increment_px:g} "
                f"(cumulative average dropped below the already-booked fills)"
            )
        return None

    def _invalid_fill_guard(self, order: Order, res: OrderResult) -> list | None:
        """malformed な増分 fill を fail-closed 処理する（#25 review findings 1/2）。

        `_fill_violation` が契約違反を検知したら **その fill を会計せず** 閉じる:
          - 未約定（filled_qty==0）から → 注文を REJECTED 終端（0 円/不正会計を防ぐ）。
          - 既に正常な部分約定済み → 不正な増分だけ捨て、確定済み fill は live のまま保持（`[]`）。
        返り値 `None` は「健全 or 非 fill」で、呼び出し側は `_apply_fill` で通常会計する。
        """
        reason = self._fill_violation(order, res)
        if reason is None:
            return None
        if order.filled_qty > 0:
            return []  # 確定済みの正常 fill を保持し、不正な増分のみ捨てる
        order.status = OrderStatus.REJECTED
        order.denied_reason = reason
        return [self._rejected(order, reason)]

    def _apply_fill(self, order: Order, res: OrderResult, *, source: str) -> list:
        """cumulative dedup を適用し、増分 fill があれば OrderFilled(last_qty=delta) を返す。

        venue は **累積**平均価格（`res.avg_price`）と累積数量（`res.filled_qty`）を報告する。Portfolio に
        渡す増分 fill の価格は累積平均ではなく **累積約定代金の差から算出した増分価格**でなければならない:
            increment_px = (new_cum_qty×new_cum_avg − prev_cum_qty×prev_cum_avg) / delta_qty
        例: 50@8 の後に累積 100@9 が来たら後半 50 は @10。累積平均 9 を増分価格に使うと Portfolio が
        平均 8.5 になり誤る（codex review #25 finding 1）。`order.avg_px` は venue 累積平均を保持する。

        数量・価格・増分価格の健全性は呼び出し側が `_invalid_fill_guard` で保証済み（malformed fill は
        ここに到達しない）。
        """
        if not (is_filled(res.status) and res.filled_qty and res.filled_qty > 0):
            return []
        incoming_cumulative = float(res.filled_qty)
        delta = incoming_cumulative - order.filled_qty
        if delta <= 0:
            return []  # duplicate / stale（受信イベント数ではなく累積数量で判定・D1）
        new_cum_avg = float(res.avg_price)
        # この増分の約定代金 = 新累積代金 − 旧累積代金。delta>0 なので 0 除算しない。
        increment_notional = incoming_cumulative * new_cum_avg - order.filled_qty * order.avg_px
        increment_px = increment_notional / delta
        order.avg_px = new_cum_avg  # venue 報告の累積平均を保持
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

    def _expired(self, order: Order) -> OrderExpired:
        return OrderExpired(
            client_order_id=order.client_order_id,
            venue_order_id=order.venue_order_id or order.client_order_id,
            strategy_id=order.strategy_id,
            instrument_id=order.instrument_id,
            side=order.side,
            ts_event_ns=order.ts_last_ns,
        )

    def _rejected(self, order: Order, reason: str) -> OrderRejected:
        return OrderRejected(
            client_order_id=order.client_order_id,
            strategy_id=order.strategy_id,
            instrument_id=order.instrument_id,
            side=order.side,
            reason=reason,
            ts_event_ns=order.ts_last_ns,
        )
