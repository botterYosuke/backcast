"""ManualOrderFacade — Phase 9 Step 2 の手動発注 dispatch（軽量 facade）。

ADR (Phase 9 §7「Step 2 は手動発注 facade」): Nautilus ExecEngine / RiskEngine の
本格 wiring は Phase 10 / LiveAuto に延期。Step 2 は `adapter.submit_order` /
`cancel_order` を直接叩き、`OrderResult` を正規化した `OrderEventData` を返す薄い
dispatch に留める。

責務:
- place(...) -> OrderEventData          : adapter.submit_order を await し正規化・track
- cancel(order_id) -> OrderEventData     : adapter.cancel_order を await し track 更新
- get_status(order_id) -> OrderEventData | None : 直近 state を参照（同期・GIL 安全）
- get_intent(order_id) -> OrderIntent | None   : place 時の静的属性を参照（#236）

設計メモ:
- **transport 非依存**: proto を import しない。proto 変換と `publish_backend_event`
  は gRPC handler（_backend_impl.py）の責務。token / execution mode 検証 /
  VENUE_LOGIN_REQUIRED 判定も handler 側。facade は adapter 稼働前提で呼ばれる。
- **内部ストア (#236)**: `_states: dict[str, OrderState]`（動的 status/fill）と
  `_intents: dict[str, OrderIntent]`（place 時の静的属性）の 2 マップで保持。
  EC-stream 由来など intent 不明な経路は `_intents` に登録されない（intent=None 相当）。
  公開メソッドは `_assemble()` で両マップを合成した `OrderEventData` を返す。
- place / cancel は live loop thread 上で await され、get_status は gRPC worker
  thread から同期で呼ばれる cross-thread 構造のため、`_states` / `_intents` を
  `threading.Lock` で保護する（SecretVault / BackendEventStream と同じ方針。lock 内で
  await しない）。
- `_states` / `_intents` は session lifetime で増え続ける（eviction なし）。1 セッションの
  手動発注回数程度の蓄積で漏洩面でも実害でもないため Phase 9 scope では掃除しない（SecretVault
  の `_targets`/`_ttl_armed` と同じ据え置き判断）。問題化したら TTL/max-size を後続で追加。
- 第二暗証番号 (`second_secret`) は Step 2 では受理して無視する（SecretVault 結線は
  Step 5 で Tachibana に追加。mock / kabu は不要）。adapter kwargs にも転送しない
  （平文 secret を adapter ログ・**extra に漏らさないため）。**この param は facade で
  意図的に終端する**（adapter には届かない）。実際の Tachibana secret 経路は SecretVault
  （`SecretRequired` push → `submit_secret` RPC、§1.3）であり、`second_secret arg`
  と二重チャネルになる懸念は Step 5 で一本化する（Phase 9 plan の Step 5/6 handoff 参照）。
- **timeout 注意（Step 5/6 向け）**: gRPC handler は `future.result(timeout)` で待つ。実
  venue adapter が timeout を超えて応答した場合、RPC は失敗を返すが **注文は venue 側で
  成立している可能性がある**（mock は即時応答のため Step 2 では発生しない）。handler は
  この場合 `PLACE_TIMEOUT` / `CANCEL_TIMEOUT` を返し、UI に「結果不明・venue で要確認」を
  促す。reconciliation（get_orders 突合）は Step 8 で実装する。

RPC 成功セマンティクス（handler が踏襲）:
- place: 発注往復が完了すれば常に OrderEventData を返す（venue REJECTED も status に
  反映、RPC success=True）。検証エラーは OrderFacadeError を raise。
- cancel: CANCELED 成立で OrderEventData（RPC success=True）。venue が取消を拒否したら
  OrderFacadeError("CANCEL_REJECTED")（RPC success=False、元注文は store 上で不変）。
  既に終端状態（FILLED 等）の注文は OrderFacadeError("ORDER_NOT_CANCELABLE")（venue 未到達）。
- 未知 order_id / 不正パラメータ: OrderFacadeError（RPC success=False、event 無し）。
"""
from __future__ import annotations

import asyncio
import math
import threading
import time

from engine.live.adapter import InstrumentId, OrderingVenueAdapter
from engine.live.order_types import OrderEventData, OrderIntent, OrderResult, OrderState, is_terminal
from engine.live.pre_trade_gate import evaluate_pre_trade
from engine.live.safety_rails import (
    SafetyRails,
    check_buying_power,
)

_VALID_SIDES = {"BUY", "SELL"}
_VALID_ORDER_TYPES = {"MARKET", "LIMIT"}


class OrderFacadeError(Exception):
    """facade レベルの既知エラー。`error_code` を gRPC Res にそのまま載せる。"""

    def __init__(self, error_code: str) -> None:
        super().__init__(error_code)
        self.error_code = error_code


def _now_ms() -> int:
    return int(time.time() * 1000)


class ManualOrderFacade:
    def __init__(self, adapter: OrderingVenueAdapter, *, safety_rails: SafetyRails | None = None) -> None:
        self._adapter = adapter
        self._safety_rails = safety_rails
        # #236: 動的状態（status/fill）と静的属性（intent）を別マップで保持。
        # client_order_id -> OrderState（直近の動的状態）
        self._states: dict[str, OrderState] = {}
        # client_order_id -> OrderIntent（place 時に生成。EC-stream 由来は登録されない）
        self._intents: dict[str, OrderIntent] = {}
        # 二重発注防止 (S4 #107 / ADR D2): idempotency_key -> order_id。
        # 同一 key の place は venue に再送せず最初の event を返す（二重 seed しない）。
        # 終端注文（FILLED 等）も再発注を防ぐため `_states` と別に永続させる。
        self._idempotency: dict[str, str] = {}
        # 進行中の place の予約 (M2 #114): idempotency_key -> 結果 Future。最初の await の前に
        # 予約することで、同一 key の並行 place が「未登録」を観測して二重 submit するレースを
        # 塞ぐ。後続の同一 key はこの Future を await し、owner の結果（or 例外）を共有する。
        self._inflight: dict[str, asyncio.Future[OrderEventData]] = {}
        # cross-thread (_states / _intents write / sync read) のため dict 操作を保護。
        self._lock = threading.Lock()

    def _assemble(self, order_id: str) -> OrderEventData | None:
        """_states + _intents を合成して OrderEventData を返す（#236）。

        lock を内部で取得する。lock を保持したまま呼ぶと deadlock になるため、
        必ず lock を解放した後に呼ぶこと。
        """
        with self._lock:
            state = self._states.get(order_id)
            if state is None:
                return None
            intent = self._intents.get(order_id)
        return OrderEventData(
            order_id=state.order_id,
            venue_order_id=state.venue_order_id,
            client_order_id=state.client_order_id,
            status=state.status,
            filled_qty=state.filled_qty,
            avg_price=state.avg_price,
            ts_ms=state.ts_ms,
            symbol=intent.symbol if intent is not None else "",
            side=intent.side if intent is not None else "",
            qty=intent.qty if intent is not None else 0.0,
            price=intent.price if intent is not None else None,
        )

    def get_intent(self, order_id: str) -> OrderIntent | None:
        """place 時の静的属性を返す（#236 / _backend_impl.py fill_static_attrs 用）。

        EC-stream 由来など place を経由していない注文は None を返す。
        """
        with self._lock:
            return self._intents.get(order_id)

    async def place(
        self,
        *,
        venue: str,
        instrument_id: InstrumentId,
        side: str,
        qty: float,
        order_type: str,
        time_in_force: str,
        price: float | None = None,
        second_secret: str | None = None,  # Step 2 では受理して無視（Step 5 で結線）
        buying_power: float | None = None,  # S4 #107: 余力（None=チェックしない）
        idempotency_key: str | None = None,  # S4 #107: 二重発注防止キー（None=従来通り）
        current_position_value_jpy: float | None = None,  # #178: SafetyRails cap check
        regulated_instruments: list[str] | None = None,   # #178: 信用規制フィルター
        net_signed_qty: float | None = None,              # #178: 現在の符号付き建玉
    ) -> OrderEventData:
        side_n = side.upper()
        type_n = order_type.upper()
        # 空文字の idempotency_key は「キー無し」とみなす（dedup しない）。`is not None`
        # ガードだけだと "" が正規キー扱いになり、空キーの発注が全部1件に潰れる事故を招く
        # （実弾経路の防御。確認モーダルは常に `manual-{seq}` を送るが getattr 既定 "" 等の
        # 取りこぼしに備える）。
        if not idempotency_key:
            idempotency_key = None
        if not venue:
            raise OrderFacadeError("INVALID_VENUE")
        if not instrument_id:
            raise OrderFacadeError("INVALID_INSTRUMENT")
        if side_n not in _VALID_SIDES:
            raise OrderFacadeError("INVALID_SIDE")
        if type_n not in _VALID_ORDER_TYPES:
            raise OrderFacadeError("INVALID_ORDER_TYPE")
        # NaN/Inf は proto double で wire 通過する。`<= 0` は NaN を弾けない
        # （NaN との比較は常に False）ため isfinite を明示。
        if not math.isfinite(qty) or qty <= 0:
            raise OrderFacadeError("INVALID_QTY")
        if type_n == "LIMIT" and (price is None or not math.isfinite(price) or price <= 0):
            raise OrderFacadeError("INVALID_PRICE")

        # 二重発注防止 (ADR D2 / M2 #114): 同一 idempotency_key の place は venue に
        # 1回しか届かせない（place_order は1回・二重 seed しない）。検証 OK の後に判定する
        # ので、不正パラメータは従来通り先に弾かれる。
        #
        # reserve-before-submit: 確定 record（過去の成功）→ in-flight 予約（進行中の place）の
        # 順で確認し、どちらも無ければ **最初の await の前に** Future を予約する。get→put は
        # 同一 event loop 上で lock 内同期に行うので atomic で、並行 coroutine が「未登録」を
        # 観測して二重 submit するレースを塞ぐ。後続の同一 key は owner の Future を await し、
        # 結果（or 例外）を共有する。
        inflight: asyncio.Future[OrderEventData] | None = None  # この呼び出しが owner なら set
        existing: asyncio.Future[OrderEventData] | None = None  # 進行中の place（follower 用）
        _prior_id: str | None = None
        if idempotency_key is not None:
            with self._lock:
                _prior_id = self._idempotency.get(idempotency_key)
                if _prior_id is None:
                    existing = self._inflight.get(idempotency_key)
                    if existing is None:
                        inflight = asyncio.get_running_loop().create_future()
                        # owner が raise する経路で awaiter ゼロだと asyncio が
                        # "Future exception was never retrieved" を警告するため consume する。
                        inflight.add_done_callback(lambda f: f.cancelled() or f.exception())
                        self._inflight[idempotency_key] = inflight  # 予約（await 前）
            if _prior_id is not None:
                # 確定済み → _assemble で合成して返す（lock 解放後に呼ぶ）
                ev = self._assemble(_prior_id)
                if ev is None:
                    raise OrderFacadeError("INTERNAL_STATE_ERROR")  # 確定済みなので必ず存在するが防御
                return ev
            if inflight is None:
                # 進行中の place がある → その結果を待って共有する（submit しない）。
                return await existing

        try:
            # MARKET は price を venue に渡さない（指値解釈の取り違えを防ぐ）。
            effective_price = price if type_n == "LIMIT" else None
            order_notional = qty * effective_price if effective_price is not None else 0.0

            # 余力超過拒否 (ADR D2): 約定金額が確定する LIMIT のみ pre-trade で評価する
            # （MARKET は約定価格未確定なので余力チェックを課さず venue 任せ）。余力超過は
            # venue に送らず弾く（約定しない）。判定そのものは safety_rails.check_buying_power。
            if buying_power is not None and effective_price is not None:
                if check_buying_power(
                    order_notional_jpy=order_notional,
                    buying_power_jpy=buying_power,
                ) is not None:
                    raise OrderFacadeError("BUYING_POWER_EXCEEDED")

            # Pre-trade gate（rail 合成を evaluate_pre_trade に委譲 #199）。
            # 余力チェックは LIMIT caller 側の責務としてここより上で完結済み。
            current_pos = current_position_value_jpy if current_position_value_jpy is not None else 0.0
            net_qty = net_signed_qty if net_signed_qty is not None else 0.0
            reg_provider = (lambda: regulated_instruments) if regulated_instruments is not None else None
            violation = evaluate_pre_trade(
                instrument_id=instrument_id,
                is_buy=side_n == "BUY",
                qty=qty,
                order_notional_jpy=order_notional,
                current_position_value_jpy=current_pos,
                net_signed_qty=net_qty,
                rails=self._safety_rails,
                regulation_provider=reg_provider,
            )
            if violation is not None:
                raise OrderFacadeError(violation.kind)

            res: OrderResult = await self._adapter.submit_order(
                venue=venue,
                instrument_id=instrument_id,
                side=side_n,
                qty=qty,
                price=effective_price,
                order_type=type_n,
                time_in_force=time_in_force,
            )

            # #236: 動的状態と静的属性を分離して保持
            state = OrderState(
                order_id=res.client_order_id,
                venue_order_id="",
                client_order_id=res.client_order_id,
                status=res.status,
                filled_qty=res.filled_qty,
                avg_price=res.avg_price if res.avg_price is not None else 0.0,
                ts_ms=_now_ms(),
            )
            intent = OrderIntent(
                # issue #29 Slice3a: 静的属性を載せて get_orders seed で完全行を復元可能にする。
                # symbol は instrument_id、price は LIMIT のときだけ（MARKET は指値なし → None）。
                symbol=instrument_id,
                side=side_n,
                qty=qty,
                price=effective_price,
            )
            with self._lock:
                self._states[state.order_id] = state
                self._intents[state.order_id] = intent
                if idempotency_key is not None:
                    self._idempotency[idempotency_key] = state.order_id
                    # 確定したので予約を片付ける（次に来た同一 key は確定 record を見る）。
                    self._inflight.pop(idempotency_key, None)
            event = self._assemble(state.order_id)
            if event is None:
                raise OrderFacadeError("INTERNAL_STATE_ERROR")
            if inflight is not None:
                inflight.set_result(event)
            return event
        except BaseException as exc:
            # 失敗（BP超過 / venue reject / 例外）: 予約を解放し、待っている follower にも
            # 同じ例外を伝播する。確定 record は残さないので、後続の同一 key 再試行は通常通り。
            if inflight is not None:
                with self._lock:
                    self._inflight.pop(idempotency_key, None)
                if not inflight.done():
                    inflight.set_exception(exc)
            raise

    async def cancel(
        self,
        *,
        venue: str,
        order_id: str,
        second_secret: str | None = None,  # Step 2 では受理して無視
    ) -> OrderEventData:
        with self._lock:
            prior_state = self._states.get(order_id)
        if prior_state is None:
            raise OrderFacadeError("UNKNOWN_ORDER_ID")
        if is_terminal(prior_state.status):
            raise OrderFacadeError("ORDER_NOT_CANCELABLE")

        res: OrderResult = await self._adapter.cancel_order(
            venue=venue,
            order_id=order_id,
        )

        if res.status == "REJECTED":
            # 取消拒否: 元注文は live のまま。store は変更しない。
            raise OrderFacadeError("CANCEL_REJECTED")

        # CANCELED 成立: 既存の約定量 / 平均価格は維持したまま終端状態に遷移させる
        # （取消は約定済み数量を巻き戻さない）。_intents は symbol/side/qty/price を保持（不変）。
        new_state = OrderState(
            order_id=order_id,
            venue_order_id=prior_state.venue_order_id,
            client_order_id=order_id,
            status="CANCELED",
            filled_qty=prior_state.filled_qty,
            avg_price=prior_state.avg_price,
            ts_ms=_now_ms(),
        )
        with self._lock:
            self._states[order_id] = new_state
        ev = self._assemble(order_id)
        if ev is None:
            raise OrderFacadeError("INTERNAL_STATE_ERROR")
        return ev

    async def modify(
        self,
        *,
        venue: str,
        order_id: str,
        new_price: float | None = None,
        new_qty: float | None = None,
        second_secret: str | None = None,  # Step 4 では受理して無視（Step 5 で結線）
    ) -> OrderEventData:
        """既存注文の訂正（価格 / 数量）。adapter.modify_order に委譲する。

        **OrderResult は status/fill のみを返す**: adapter 応答は status（例 ACCEPTED）/
        filled_qty / avg_price のみで、訂正後の新数量・新価格は含まない。
        新数量 / 新価格は `new_qty` / `new_price` 引数から直接 `_intents` を更新するため、
        UI への qty/price 反映は facade 内で完結する（#236 型分離後の契約）。
        """
        with self._lock:
            prior_state = self._states.get(order_id)
            prior_intent = self._intents.get(order_id) if prior_state is not None else None
        if prior_state is None:
            raise OrderFacadeError("UNKNOWN_ORDER_ID")
        if is_terminal(prior_state.status):
            raise OrderFacadeError("ORDER_NOT_MODIFIABLE")
        if new_price is None and new_qty is None:
            raise OrderFacadeError("NOTHING_TO_MODIFY")
        # 指定された値のみ検証（None は「変更しない」を意味する）。NaN/Inf は proto
        # double で wire 通過するため isfinite を明示（place の流儀踏襲）。
        if new_price is not None and (not math.isfinite(new_price) or new_price <= 0):
            raise OrderFacadeError("INVALID_PRICE")
        if new_qty is not None and (not math.isfinite(new_qty) or new_qty <= 0):
            raise OrderFacadeError("INVALID_QTY")

        res: OrderResult = await self._adapter.modify_order(
            venue=venue,
            order_id=order_id,
            new_price=new_price,
            new_qty=new_qty,
        )

        if res.status == "REJECTED":
            # 訂正拒否: 元注文は live のまま。store は変更しない。
            raise OrderFacadeError("MODIFY_REJECTED")

        # 訂正受理: adapter 応答 status を反映。fill 量は adapter が 0/None を返す場合
        # 既存の約定量 / 平均価格を維持する（訂正は約定済み数量を巻き戻さない。
        # cancel の fill 保全と同方針）。
        new_state = OrderState(
            order_id=order_id,
            venue_order_id=prior_state.venue_order_id,
            client_order_id=order_id,
            status=res.status,
            filled_qty=res.filled_qty if res.filled_qty else prior_state.filled_qty,
            avg_price=res.avg_price if res.avg_price is not None else prior_state.avg_price,
            ts_ms=_now_ms(),
        )
        # #236: qty/price はインテントに属するため、変更があれば _intents も更新する。
        # symbol/side は訂正不変。prior_intent が None（EC-stream 由来）の場合は更新しない。
        if prior_intent is not None:
            new_intent = OrderIntent(
                symbol=prior_intent.symbol,
                side=prior_intent.side,
                qty=new_qty if new_qty is not None else prior_intent.qty,
                price=new_price if new_price is not None else prior_intent.price,
            )
        else:
            new_intent = None
        with self._lock:
            self._states[order_id] = new_state
            if new_intent is not None:
                self._intents[order_id] = new_intent
        ev = self._assemble(order_id)
        if ev is None:
            raise OrderFacadeError("INTERNAL_STATE_ERROR")
        return ev

    def get_status(self, order_id: str) -> OrderEventData | None:
        """同期参照: 指定 order_id の直近イベントを返す。"""
        return self._assemble(order_id)

    def list_orders(self) -> list[OrderEventData]:
        """稼働中（非終端）注文の snapshot（get_orders / §3.8 reconcile 用、同期参照）。

        終端注文（FILLED/CANCELED/...）は「稼働中」ではないので除外する。再起動直後の
        fresh backend はこの store が空なので [] を返す（= UI 楽観的状態との diff で
        「状態不明」を炙り出す reconcile primitive）。
        """
        with self._lock:
            result = []
            for oid, s in self._states.items():
                if is_terminal(s.status):
                    continue
                intent = self._intents.get(oid)
                ev = OrderEventData(
                    order_id=s.order_id,
                    venue_order_id=s.venue_order_id,
                    client_order_id=s.client_order_id,
                    status=s.status,
                    filled_qty=s.filled_qty,
                    avg_price=s.avg_price,
                    ts_ms=s.ts_ms,
                    symbol=intent.symbol if intent is not None else "",
                    side=intent.side if intent is not None else "",
                    qty=intent.qty if intent is not None else 0.0,
                    price=intent.price if intent is not None else None,
                )
                result.append(ev)
        return result
