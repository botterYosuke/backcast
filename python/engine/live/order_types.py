"""Order submission result types.

OrderResult は venue adapter の submit_order が返す結果。
field は BackendEvent.OrderEvent の wire フィールド（status / filled_qty / avg_price /
client_order_id）と対応する。reject_reason のみ OrderResult 固有（REJECTED 時の理由文字列）。
"""

from __future__ import annotations

from pydantic import BaseModel, field_validator

# Canonical Nautilus OrderStatus member names (core/rust/model.pxd:352 / model/enums.py).
# venue adapter が返す status を契約として固定する。typo（"CANCELLED" 等）や
# 自由文字列が UI まで素通りするのを境界で止める（Step 5/6 の実 adapter ドリフト対策）。
VALID_ORDER_STATUSES: frozenset[str] = frozenset(
    {
        "INITIALIZED",
        "DENIED",
        "EMULATED",
        "RELEASED",
        "SUBMITTED",
        "ACCEPTED",
        "REJECTED",
        "CANCELED",
        "EXPIRED",
        "TRIGGERED",
        "PENDING_UPDATE",
        "PENDING_CANCEL",
        "PARTIALLY_FILLED",
        "FILLED",
    }
)


_TERMINAL_STATUSES: frozenset[str] = frozenset(
    {"FILLED", "CANCELED", "REJECTED", "EXPIRED", "DENIED"}
)
_FILLED_STATUSES: frozenset[str] = frozenset({"FILLED", "PARTIALLY_FILLED"})
assert _TERMINAL_STATUSES <= VALID_ORDER_STATUSES, _TERMINAL_STATUSES - VALID_ORDER_STATUSES
assert _FILLED_STATUSES <= VALID_ORDER_STATUSES, _FILLED_STATUSES - VALID_ORDER_STATUSES


def is_terminal(status: str) -> bool:
    return status in _TERMINAL_STATUSES


def is_filled(status: str) -> bool:
    return status in _FILLED_STATUSES


class OrderResult(BaseModel):
    """submit_order の結果。OrderEvent wire フィールドに対応 + reject_reason。"""

    status: str  # "FILLED" / "REJECTED" / "PARTIALLY_FILLED" 等（Nautilus OrderStatus name）
    filled_qty: float
    avg_price: float | None
    client_order_id: str
    reject_reason: str | None = None

    model_config = {"frozen": True}

    @field_validator("status")
    @classmethod
    def _status_must_be_nautilus_name(cls, v: str) -> str:
        if v not in VALID_ORDER_STATUSES:
            raise ValueError(
                f"invalid OrderStatus name: {v!r} "
                f"(must be one of the Nautilus OrderStatus members)"
            )
        return v


class OrderEventData(BaseModel):
    """ManualOrderFacade が返す正規化済み注文イベント。

    facade は transport 非依存（proto を import しない）。`order_id` は UI が
    扱う安定ハンドルで、mock では `client_order_id` と同値（venue 採番が無いため
    `venue_order_id` は空文字）。

    issue #29 Slice3a: `symbol`/`side`/`qty`/`price` は発注時の静的属性。get_orders
    による接続/再起動後の seed で UI が完全な注文行（銘柄・売買・数量・指値）を復元
    できるよう facade が place 時に載せる（`symbol` は instrument_id、MARKET は
    `price=None`）。EC stream 由来など静的属性が不明な経路では既定値（""/0.0/None）
    のまま残り、UI 側は「非空が勝つ」マージ規則で既知の値を保持する。
    """

    order_id: str
    venue_order_id: str
    client_order_id: str
    status: str
    filled_qty: float
    avg_price: float
    ts_ms: int
    symbol: str = ""
    side: str = ""
    qty: float = 0.0
    price: float | None = None

    model_config = {"frozen": True}


class OrderIntent(BaseModel):
    """発注時の静的属性。place() 時にのみ生成される（EC-stream 由来には存在しない）。

    #236: OrderEventData から静的属性を分離した型。`symbol`/`side`/`qty`/`price` は
    すべて必須（empty デフォルトなし）。`ManualOrderFacade` が place 時に生成して
    `_intents` マップで保持し、EC-stream 由来の注文には intent=None が対応する。
    """

    symbol: str
    side: str
    qty: float
    price: float | None

    model_config = {"frozen": True}


class OrderState(BaseModel):
    """注文の動的状態（status / fill）。place / EC-stream 両経路に共通。

    #236: OrderEventData から動的フィールドのみを分離した型。
    `venue_order_id` は mock 環境では空文字のまま（論点 B 現行維持）。
    """

    order_id: str
    venue_order_id: str = ""
    client_order_id: str
    status: str
    filled_qty: float
    avg_price: float
    ts_ms: int

    model_config = {"frozen": True}


class AccountPositionData(BaseModel):
    """口座の 1 保有銘柄。

    transport 非依存（account_sync / mock が用いる正規化モデル）。
    """

    symbol: str
    qty: int
    avg_price: float
    unrealized_pnl: float

    model_config = {"frozen": True}


class AccountSnapshot(BaseModel):
    """口座スナップショット（余力 + 建玉一覧）。

    **ts_ms は持たない**: 等価判定（差分 emit）から時刻を排除するため。push 時に
    `publish_backend_event` が `int(time.time()*1000)` を採番して `AccountEvent.ts_ms`
    に詰める。AccountSync は同一 snapshot の連続 emit を `==`（pydantic frozen の
    field 比較）で抑止するので、時刻がここに混じると常に「変化あり」と誤判定する。

    NaN/Inf validator は付けない（mock では発生しない。OrderResult と同方針で、
    実 venue 値の境界 sanitize は Step 5/6 adapter の責務）。
    """

    cash: float
    buying_power: float
    positions: tuple[AccountPositionData, ...]

    model_config = {"frozen": True}


def fill_static_attrs(
    partial: "OrderEventData", intent: "OrderIntent"
) -> "OrderEventData":
    """Issue #236 Plan B: backend-side 'non-empty wins' for static order intent.

    Fills empty symbol / side / qty / price in *partial* from *intent*.
    Non-empty values in *partial* are always preserved (non-empty wins).
    Moves the static-attr gap-fill merge rule from Rust's seed_working to the
    Python backend; Rust keeps it as a safety net for edge cases (orders created
    via EC-stream events before the first GetOrders response).
    """
    update: dict = {}
    if not partial.symbol and intent.symbol:
        update["symbol"] = intent.symbol
    if not partial.side and intent.side:
        update["side"] = intent.side
    if partial.qty == 0.0 and intent.qty != 0.0:
        update["qty"] = intent.qty
    if partial.price is None and intent.price is not None:
        update["price"] = intent.price
    return partial.model_copy(update=update) if update else partial
