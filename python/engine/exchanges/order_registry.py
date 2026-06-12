"""Order Registry — venue_order_id↔client_order_id 解決・累計約定 bookkeeping・state dedup。

KabuStationAdapter (poll ループ) と TachibanaAdapter (EC push) が共有する
以下のロジックを一箇所に集約する:
  - venue_order_id → client_order_id 解決
  - 累計約定/加重平均 bookkeeping（kabu modify の carry を含む）
  - state-based dedup（同一 (status, total_filled) の重複 push 抑止）
  - OrderEventData 構築
  - terminal 注文の自動 unregister

参照: issue #187。kabu modify の取消→新規補償オーケストレーションは
KabuStationAdapter に残す。TachibanaAdapter のリコネクト dedup (_seen_ec) は
mark-after-success が必要なため adapter 側に残置し、本 registry の state-based dedup
はその後段の二次フィルタとして機能する。
"""

from __future__ import annotations

from dataclasses import dataclass

from engine.live.order_types import OrderEventData


@dataclass
class OrderRef:
    """Registry が管理する注文追跡エントリ。"""

    venue_order_id: str
    filled_base: float = 0.0
    notional_base: float = 0.0


@dataclass
class NormalizedReport:
    """Adapter が venue-specific parse の後に渡す共通正規化済みレポート。

    ``filled_qty`` は現 leg の累計約定数量（旧 leg carry を含まない）。
    """

    venue_order_id: str
    status: str
    filled_qty: float
    avg_price: float | None
    terminal: bool
    ts_ms: int


class OrderRegistry:
    """venue_order_id↔cid マッピング・bookkeeping・state-based dedup を集約するクラス。

    adapter ごとに 1 インスタンスを生成し、login/logout で clear() を呼ぶ。
    asyncio 単一スレッドを前提とするため内部ロックは持たない。
    """

    def __init__(self) -> None:
        self._refs: dict[str, OrderRef] = {}
        self._venue_to_cid: dict[str, str] = {}
        self._last_state: dict[str, tuple[str, float]] = {}

    def register(self, cid: str, venue_id: str) -> None:
        ref = OrderRef(venue_order_id=venue_id)
        self._refs[cid] = ref
        self._venue_to_cid[venue_id] = cid

    def unregister(self, cid: str) -> None:
        ref = self._refs.pop(cid, None)
        if ref is not None:
            self._venue_to_cid.pop(ref.venue_order_id, None)
        self._last_state.pop(cid, None)

    def remap(
        self,
        cid: str,
        new_venue_id: str,
        carry_filled: float,
        carry_notional: float,
    ) -> None:
        """kabu modify（取消→新規）の venue_order_id 再マップ。"""
        ref = self._refs.get(cid)
        if ref is None:
            return
        self._venue_to_cid.pop(ref.venue_order_id, None)
        ref.venue_order_id = new_venue_id
        ref.filled_base = carry_filled
        ref.notional_base = carry_notional
        self._venue_to_cid[new_venue_id] = cid
        self._last_state.pop(cid, None)

    def lookup_cid(self, venue_id: str) -> str | None:
        return self._venue_to_cid.get(venue_id)

    def get_ref(self, cid: str) -> OrderRef | None:
        return self._refs.get(cid)

    def has_active(self) -> bool:
        return bool(self._refs)

    def fold_report(self, report: NormalizedReport) -> OrderEventData | None:
        """正規化済みレポートを受け取り、push すべき OrderEventData を返す。

        None を返す場合:
          - venue_id が未登録
          - state-based dedup により変化なし
        """
        cid = self._venue_to_cid.get(report.venue_order_id)
        if cid is None:
            return None

        ref = self._refs[cid]
        base_qty = ref.filled_base
        base_notional = ref.notional_base

        total_filled = base_qty + report.filled_qty
        status = report.status
        if base_qty > 0.0 and status == "ACCEPTED":
            status = "PARTIALLY_FILLED"

        if total_filled > 0.0 and report.avg_price is not None:
            avg_price: float | None = (
                base_notional + report.filled_qty * report.avg_price
            ) / total_filled
        else:
            avg_price = report.avg_price

        state_key = (status, total_filled)
        if self._last_state.get(cid) == state_key:
            return None
        self._last_state[cid] = state_key

        event = OrderEventData(
            order_id=cid,
            venue_order_id=report.venue_order_id,
            client_order_id=cid,
            status=status,
            filled_qty=total_filled,
            avg_price=avg_price if avg_price is not None else 0.0,
            ts_ms=report.ts_ms,
        )

        if report.terminal:
            self.unregister(cid)

        return event

    def clear(self) -> None:
        self._refs.clear()
        self._venue_to_cid.clear()
        self._last_state.clear()
