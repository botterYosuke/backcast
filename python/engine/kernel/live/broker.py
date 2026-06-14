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
from engine.live.order_types import OrderResult, is_filled

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

        取消拒否(REJECTED)は注文自体の拒否ではないので元注文を live に保つが、結果に embedded された増分 fill
        （取消待ち中の約定）は捨てず会計する（modify / apply_venue_update と同型・review）。
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
            # 取消拒否: 元注文は live のまま（注文自体の REJECTED ではない）。報告された埋め込み約定だけは
            # 会計する（捨てると Portfolio が venue と desync）。約定があれば status は会計側が進め、無ければ
            # prior（live）に戻す。malformed / 増分無しは捨てる（取消拒否で注文を REJECTED にしない）。
            order.venue_order_id = order.venue_order_id or order.client_order_id
            fill_events = self._apply_guarded_fill(order, res, source="cancel_result")
            if not fill_events:
                order.status = prior
            return fill_events
        return self.apply_venue_update(order, res, source="cancel_result")

    async def modify(
        self, order: Order, *, new_price: float | None = None, new_qty: float | None = None
    ) -> list:
        """venue で訂正。CANCELED/EXPIRED/約定を返したら対応する終端/約定イベントを emit する（nautilus 経路同型）。

        status に依らず、結果に embedded された増分 fill を**先に会計してから** status を処理する。kabu の
        cancel-replace 訂正は成立(ACCEPTED)でも取消待ち約定を `filled_qty` で返すため、捨てると Portfolio が
        venue 建玉と desync する（#25 review finding 1）。拒否系 terminal（REJECTED/DENIED）は約定が無ければ
        更新を適用せず元状態へ復帰する（注文全体を terminal にしない・D6）。

        `new_qty` / `new_price` は **有限かつ正数**でなければ venue へ送らず order を据え置く（不正値を
        adapter に渡さない・#25 review finding 2）。`<= 0` だけでは NaN を取りこぼすため isfinite を明示。
        `new_qty` が **既約定数量を下回る**訂正は不整合（`filled_qty > quantity`）になるので venue へ送らない
        （#25 review round8 finding 2）。
        """
        if order.status in _TERMINAL_STATES:
            return []
        if new_qty is not None and not _is_finite_positive(new_qty):
            return []
        if new_qty is not None and new_qty < order.filled_qty:
            return []  # 既約定数量を下回る訂正は不整合・venue へ送らず据え置き（finding 2）
        if new_price is not None and not _is_finite_positive(new_price):
            return []
        prior = order.status
        prior_qty = order.quantity
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

        # 訂正が venue に届いた（拒否系でない）場合のみ訂正後数量を FILLED 判定の目標に反映する。
        # REJECTED/DENIED は訂正不成立で元注文が旧数量のまま live なので prior_qty を維持する（finding 1）。
        modify_took_effect = res.status not in ("REJECTED", "DENIED")
        if new_qty is not None and modify_took_effect:
            order.quantity = new_qty

        # over-fill 上限は「venue がこの注文に対して保持しうる最大数量」。訂正成立時は約定が旧数量・新数量
        # どちらに対しても起こりうるので max(旧, 新)。訂正不成立(REJECTED/DENIED)では元注文は旧数量のままなので
        # prior_qty（増額拒否に max を使うと旧数量超の約定を会計し over-fill を見逃す・max review）。FILLED 判定の
        # 目標数量(order.quantity)とは別概念（減額訂正は目標↓だが約定は旧数量まで起こりうる・finding 2）。
        ceiling = max(prior_qty, new_qty) if (new_qty is not None and modify_took_effect) else prior_qty

        if is_filled(res.status):
            # fill ステータス(FILLED/PARTIALLY_FILLED): fill が authority。malformed な増分（非有限数量・
            # 価格欠落等）は fail-closed で注文を REJECTED 終端する（_invalid_fill_guard）。`> 0` ガードは
            # NaN を取りこぼす（NaN>0 は False）ので付けず、健全性判定と dedup は guard / 会計側に委ねる。
            invalid = self._invalid_fill_guard(order, res, overfill_ceiling=ceiling)
            if invalid is not None:
                return invalid
            return self._book_fill_increment(order, res, source="modify_result")

        # 非 fill ステータス(ACCEPTED/CANCELED/EXPIRED/REJECT系): status が authority。status に依らず
        # embedded された増分 fill を先に会計してから status を処理する。kabu の cancel-replace 訂正は
        # 成立(ACCEPTED)・拒否(REJECTED)いずれでも取消待ち約定を filled_qty で返すので、捨てると Portfolio が
        # venue と desync する（#25 review finding 1）。malformed / 増分無しは捨てる（status が authority・
        # order は REJECTED にしない）。target_qty は clamp 前の目標数量（FILLED 完了判定に使う）。
        target_qty = order.quantity
        fill_events = self._apply_guarded_fill(order, res, source="modify_result", overfill_ceiling=ceiling)

        if res.status in ("CANCELED", "EXPIRED"):
            # 埋め込み約定が(訂正後)目標を**ちょうど**満たして完了したら FILLED を維持する（CANCELED/EXPIRED で
            # 上書きしない）。減額競合で目標超の約定（cumulative>target、_book_fill_increment が quantity を
            # 引き上げて FILLED 化した clamp アーティファクト）は完了ではないので終端ラベルを優先する。
            if order.status is OrderStatus.FILLED and res.filled_qty <= target_qty:
                return fill_events
            order.venue_order_id = order.venue_order_id or order.client_order_id  # 約定無しでも確定（apply_venue_update と対称）
            order.status = OrderStatus.CANCELED if res.status == "CANCELED" else OrderStatus.EXPIRED
            terminal = self._canceled(order) if res.status == "CANCELED" else self._expired(order)
            return [*fill_events, terminal]
        # ここに到達するのは拒否系 terminal(REJECTED/DENIED) と live(ACCEPTED 等)のみ。約定が無ければ
        # 元 status へ復帰する（注文全体を terminal にしない・D6。新数量は modify_took_effect が制御済み）。
        # embedded fill があればその会計（PARTIALLY_FILLED 等）を保持する。
        if not fill_events:
            order.status = prior
        return fill_events

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

        if res.status in ("REJECTED", "DENIED"):
            # 拒否(REJECTED) / 却下(DENIED): 結果に embedded された約定を先に会計する（捨てると約定が宙に浮き
            # Portfolio が venue と desync する・CANCELED/EXPIRED と同型 finding 1）。既約定>0 なら CANCELED で
            # 終端化して fill を保持し、未約定なら REJECTED で終端する（REJECTED は ACCEPTED を経由しない FSM
            # 不変条件・既約定を捨てない finding 5）。venue 由来 DENIED は precheck DENIED（INITIALIZED→DENIED）と
            # 区別して REJECTED 扱いにし、status と OrderRejected を一致させる（DENIED で OrderAccepted/phantom
            # fill を出さない・max review）。
            order.venue_order_id = order.venue_order_id or order.client_order_id
            fill_events = self._apply_guarded_fill(order, res, source=source)
            if order.status is OrderStatus.FILLED:
                return fill_events  # 埋め込み約定が完了させた → FILLED 維持（reject ラベルで上書きしない）
            if order.filled_qty > 0:
                order.status = OrderStatus.CANCELED
                return [*fill_events, self._canceled(order)]
            order.status = OrderStatus.REJECTED
            order.denied_reason = res.reject_reason or res.status
            return [*fill_events, self._rejected(order, order.denied_reason)]

        # 終端だが約定でも reject でもない（CANCELED / EXPIRED）: ACCEPTED へ進めず終端化する。
        # apply_venue_update は同期 OrderResult と非同期 EC イベントの**単一入口**なので、ここで
        # 取りこぼすと async EC（#23）が CANCELED/EXPIRED を運んだとき誤って ACCEPTED に化ける
        # （codex review #25）。EXPIRED は OrderExpired で終端通知する（status と外部通知を一致させる・
        # finding 3。OrderCanceled で代用すると projection が CANCELED に化け FSM と矛盾する）。
        # CANCELED/EXPIRED に embedded された約定（kabu: 取消中の部分約定）は捨てず**先に会計**してから
        # 終端化する（捨てると Portfolio が venue 建玉と desync・#25 review round8 finding 1）。
        if res.status in ("CANCELED", "EXPIRED"):
            order.venue_order_id = order.venue_order_id or order.client_order_id
            fill_events = self._apply_guarded_fill(order, res, source=source)
            if order.status is OrderStatus.FILLED:
                return fill_events  # 埋め込み約定が完了させた → FILLED 維持（CANCELED/EXPIRED で上書きしない）
            if res.status == "CANCELED":
                order.status = OrderStatus.CANCELED
                return [*fill_events, self._canceled(order)]
            order.status = OrderStatus.EXPIRED
            return [*fill_events, self._expired(order)]

        # fail-closed: fill ステータス（FILLED/PARTIALLY_FILLED）の malformed な増分（価格欠落・非有限/
        # 範囲外の累積数量・非正の増分価格）は ACCEPTED emit より前に処理する。REJECTED は ACCEPTED を
        # 経由しない FSM 不変条件を保つため（findings 1/2。未約定からの不正会計の REJECT 終端は
        # _invalid_fill_guard が担う）。
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

        # status に依らず埋め込み約定を会計する。ACCEPTED に部分約定を載せる venue / async EC（#23）でも
        # 捨てない（fill ステータスのみ会計していた旧実装は ACCEPTED+約定を取りこぼし Portfolio が desync した・
        # modify 経路 finding 1 と同型の穴が単一入口側にもあった）。fill ステータスの健全性は上の
        # _invalid_fill_guard が保証済みで、_apply_guarded_fill の再検証は valid なら no-op。
        events.extend(self._apply_guarded_fill(order, res, source=source))
        return events

    # ── helpers ──────────────────────────────────────────────────────────────

    def _fill_violation(
        self, order: Order, res: OrderResult, *, overfill_ceiling: float | None = None
    ) -> str | None:
        """増分 fill が venue 契約違反なら理由を返す。健全 or 増分無し / dedup なら `None`（findings 1/2）。

        会計する前の純粋な fail-closed 検証。`res.status` には依存しない（terminal 結果 CANCELED/EXPIRED に
        embedded された約定にも使う・round8 finding 1）。実 venue は **有限・正の累積数量（注文数量以内）と
        正の約定価格** を報告するので、それ以外は契約違反として弾く:
          - 非有限の累積数量（NaN / inf）→ `cash=NaN`・`position=(inf, NaN)` を防ぐ（finding 1）。
          - 累積数量 > 上限 → over-fill（建玉が注文を超える）を防ぐ（finding 1）。
          - 価格欠落 / 非正の約定価格 → 0 円・負値会計を防ぐ（前回 finding 1）。
          - 増分価格が非正 → 累積平均が既約定分を下回ると increment_px が負になり、BUY fill が現金を
            増やす誤会計になる（finding 2）。

        `overfill_ceiling` は over-fill 判定の上限を上書きする（既定は `order.quantity`）。減額訂正では
        約定が旧数量まで起こりうるため modify が max(旧, 新)数量を渡す。これを使わず訂正後数量で判定すると
        競合約定を取りこぼす（finding 2）。
        """
        ceiling = overfill_ceiling if overfill_ceiling is not None else order.quantity
        cumulative = res.filled_qty
        if cumulative is None or not math.isfinite(cumulative):
            return f"FILL_NONFINITE_QTY: non-finite cumulative filled_qty {cumulative!r}"
        delta = cumulative - order.filled_qty
        if cumulative <= 0 or delta <= 0:
            return None  # 増分無し / duplicate / stale（_book_fill_increment が dedup・契約違反ではない）
        if cumulative > ceiling:
            return (
                f"FILL_EXCEEDS_ORDER_QTY: cumulative {cumulative:g} exceeds order quantity "
                f"{ceiling:g}"
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

    def _invalid_fill_guard(
        self, order: Order, res: OrderResult, *, overfill_ceiling: float | None = None
    ) -> list | None:
        """fill ステータス（FILLED / PARTIALLY_FILLED）の malformed な増分を fail-closed 処理する
        （#25 review findings 1/2）。

        契約違反を検知したら **その fill を会計せず** 閉じる:
          - 未約定（filled_qty==0）から → 注文を REJECTED 終端（0 円/不正会計を防ぐ）。
          - 既に正常な部分約定済み → 不正な増分だけ捨て、確定済み fill は live のまま保持（`[]`）。
        返り値 `None` は「健全 or 非 fill」で、呼び出し側は `_book_fill_increment` で通常会計する。
        `overfill_ceiling` は over-fill 上限の上書き（modify 経路用・`_fill_violation` 参照）。

        fill ステータスは **正の累積数量** を報告するのが契約。0・負の累積数量は malformed で、`_fill_violation`
        は `cumulative<=0` を「約定無し」として None にする（非 fill ステータスの `filled_qty=0`＝embedded 約定無し
        は正常なので区別が要る）が、FILLED/PARTIALLY_FILLED でそれは契約違反。ここで補う（review High）。
        `cumulative>0` だが delta<=0（同一累積の再報告）は dedup であって違反ではない。
        """
        if not is_filled(res.status):
            return None
        reason = self._fill_violation(order, res, overfill_ceiling=overfill_ceiling)
        if reason is None and not (res.filled_qty > 0):
            # fill ステータスなのに正の累積数量が無い（0/負）。非有限は _fill_violation が既に検知済み。
            reason = f"FILL_NONPOSITIVE_QTY: {res.status} reported non-positive filled_qty {res.filled_qty!r}"
        if reason is None:
            return None
        if order.filled_qty > 0:
            return []  # 確定済みの正常 fill を保持し、不正な増分のみ捨てる
        order.status = OrderStatus.REJECTED
        order.denied_reason = reason
        return [self._rejected(order, reason)]

    def _apply_guarded_fill(
        self, order: Order, res: OrderResult, *, source: str, overfill_ceiling: float | None = None
    ) -> list:
        """status に依らず、結果に embedded された約定を fail-closed 検証してから会計する（finding 1）。

        どの status（ACCEPTED / CANCELED / EXPIRED / REJECT系、および apply_venue_update の post-ACK 経路の
        FILLED / PARTIALLY_FILLED）でも、結果に増分約定が載っていれば会計する。kabu の cancel-replace 訂正は
        成立・取消・拒否いずれでも取消待ち約定を `filled_qty` で返すので、捨てると Portfolio が venue 建玉と
        desync する。健全な増分のみ会計し、malformed / 増分無しは `[]`（status / terminal イベントが authority
        なので order は REJECTED にしない＝未約定から REJECT 終端する `_invalid_fill_guard` との違い）。
        `overfill_ceiling` は over-fill 上限の上書き（modify 経路用・`_fill_violation` 参照）。"""
        if self._fill_violation(order, res, overfill_ceiling=overfill_ceiling) is not None:
            return []
        return self._book_fill_increment(order, res, source=source)

    def _book_fill_increment(self, order: Order, res: OrderResult, *, source: str) -> list:
        """cumulative dedup を適用し、増分 fill があれば OrderFilled(last_qty=delta) を返す。

        venue は **累積**平均価格（`res.avg_price`）と累積数量（`res.filled_qty`）を報告する。Portfolio に
        渡す増分 fill の価格は累積平均ではなく **累積約定代金の差から算出した増分価格**でなければならない:
            increment_px = (new_cum_qty×new_cum_avg − prev_cum_qty×prev_cum_avg) / delta_qty
        例: 50@8 の後に累積 100@9 が来たら後半 50 は @10。累積平均 9 を増分価格に使うと Portfolio が
        平均 8.5 になり誤る（codex review #25 finding 1）。`order.avg_px` は venue 累積平均を保持する。

        数量・価格・増分価格の健全性は呼び出し側（`_invalid_fill_guard` / `_apply_guarded_fill`）が保証済み。
        `res.status` には依存しない（terminal 結果 embedded fill にも使う）ので fill ステータスの判定は
        呼び出し側が行う。
        """
        if res.filled_qty is None:
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
        # 約定が目標数量を超えたら（減額訂正の競合約定: over-fill 上限 max(旧,新) を会計が通過した場合）
        # 目標を実約定まで引き上げ、filled<=quantity 不変条件を保つ。通常経路は cumulative<=quantity
        # （ceiling=quantity を _fill_violation が保証）なので no-op（max review）。
        order.quantity = max(order.quantity, incoming_cumulative)
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
