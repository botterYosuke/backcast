"""LiveVenueAdapter Protocol and related type skeletons.

Phase 8 §1.3 で定義した、venue 非依存の adapter インターフェース。
Tachibana / kabu 等の具体実装は後続 step で追加する。

LiveEvent は KlineUpdate / TradesUpdate / DepthUpdate の discriminated
union（kind discriminator）。
"""

from __future__ import annotations

from typing import (
    TYPE_CHECKING,
    Annotated,
    AsyncIterator,
    Callable,
    Literal,
    Protocol,
    Union,
    runtime_checkable,
)

from pydantic import BaseModel, Field, model_validator

from engine.live.order_types import OrderEventData

if TYPE_CHECKING:
    from engine.live.order_types import AccountSnapshot, OrderResult

# --- 基本型エイリアス ---

InstrumentId = str
"""Nautilus InstrumentId 文字列形式（例: '7203.TSE'）。
Nautilus 型への変換は別 step（reducer 側）で行う。"""

Channel = Literal["price", "trades", "depth"]
"""購読チャネル種別。venue 横断で共通。"""


class SubscriptionLimitExceeded(Exception):
    """venue のハード購読上限超過を venue 非依存に表す例外（#107・方針 ADR-0022）。

    kabu の 50 銘柄登録上限（`KabuRegisterFullError` / errno 4002006）のような
    *venue 側の実上限* を、orchestrator が venue 固有例外を知らずに typed な
    `SUBSCRIPTION_LIMIT_EXCEEDED` として surface するための境界型。各 venue adapter が
    自分の上限例外をこの型へ翻訳して raise する。立花は per-ticker WS で実上限が無いので
    raise しない。orchestrator は人工的な件数 cap を持たず（撤去済み）、この例外でのみ上限を知る。
    """

    def __init__(self, message: str = "", venue_code: object = None) -> None:
        super().__init__(message)
        self.venue_code = venue_code

# --- credentials / instrument の骨格 ---

class VenueCredentials(BaseModel):
    """ログイン要求の入力。

    重要: 平文 password を含まない。credentials_source ベース
    （session_cache / env / prompt_result）で resolve する。具体的な
    credential 値は adapter 内部で env / cache から取得する。#181/ADR-0040 で
    対話 "prompt" は廃止（ログイン UI は Unity uGUI モーダルへ移管）。kabu の
    モーダルログインは headless 認証で得た token を prompt_result で渡す。
    """

    credentials_source: Literal["session_cache", "env", "prompt_result"]
    environment_hint: str | None = None  # "prod" / "demo" 等のヒント
    token: str | None = None  # kabu prompt_result 専用

    model_config = {"frozen": True}

    @model_validator(mode="after")
    def _validate_prompt_result_requires_token(self) -> "VenueCredentials":
        if self.credentials_source == "prompt_result" and not self.token:
            raise ValueError(
                "credentials_source='prompt_result' requires a non-empty token"
            )
        return self


class InstrumentRaw(BaseModel):
    """venue が返す instrument の生形式。

    Nautilus Instrument への正規化は別 step。最小フィールドのみ。
    """

    code: str  # 銘柄コード（例: "7203"）
    name: str  # 銘柄名
    market: str  # 市場コード（例: "TSE"）
    tick_size: float
    lot_size: int

    model_config = {"frozen": True}


# --- Market data event union ---


class KlineUpdate(BaseModel):
    """OHLCV bar update（Replay の KlineUpdate と同形式）。

    `is_closed`: True = 確定バー（aggregator の bucket-rollover or venue 直送）、False = 進行中の
    partial スナップショット（`LiveRunner._partial_push` が UI 用に毎秒 publish するもの）。kernel
    live driver は **確定バーだけ** strategy.on_bar に渡す（partial を渡すと毎秒重複発注する・#25 D3）。
    既定 True なので、venue 直送 bar や既存呼び出しは確定バー扱い（後方互換）。
    """

    kind: Literal["kline"]
    instrument_id: InstrumentId
    ts_ns: int
    open: float
    high: float
    low: float
    close: float
    volume: float
    is_closed: bool = True

    model_config = {"frozen": True}


class TradesUpdate(BaseModel):
    """単一約定 tick。"""

    kind: Literal["trades"]
    instrument_id: InstrumentId
    ts_ns: int
    price: float
    size: float
    aggressor_side: Literal["buy", "sell"]

    model_config = {"frozen": True}


class DepthLevel(BaseModel):
    """板の 1 段（price/size のみ）。"""

    price: float
    size: float

    model_config = {"frozen": True}


class DepthUpdate(BaseModel):
    """板更新（bids/asks 各 0-10 段、空も許容）。"""

    kind: Literal["depth"]
    instrument_id: InstrumentId
    ts_ns: int
    bids: Annotated[tuple[DepthLevel, ...], Field(max_length=10)]
    asks: Annotated[tuple[DepthLevel, ...], Field(max_length=10)]

    model_config = {"frozen": True}


LiveEvent = Annotated[
    Union[KlineUpdate, TradesUpdate, DepthUpdate],
    Field(discriminator="kind"),
]
"""price / trades / depth update の discriminated union（kind discriminator）。

reducer 側は `kind` フィールドで分岐する。pydantic v2 の
`TypeAdapter(LiveEvent).validate_python(...)` でも分岐可能。
"""


# --- Adapter Protocol ---

@runtime_checkable
class LiveVenueAdapter(Protocol):
    """venue 非依存の live adapter インターフェース（Phase 8 §1.3）。

    実装は asyncio タスクとして動き、events() から非同期に
    market data event を yield する。
    """

    venue_id: str  # "TACHIBANA" / "KABU"

    # Whether the venue can enumerate its instrument master via fetch_instruments().
    # Adapters that cannot (e.g. kabu MVP, which returns []) declare this False so
    # the backend never serves a persisted store snapshot as the authoritative live
    # universe (issue #253). Callers treat a missing attribute as True.
    enumerates_instruments: bool

    @property
    def is_logged_in(self) -> bool: ...

    async def login(self, creds: VenueCredentials) -> None: ...
    async def logout(self) -> None: ...
    async def fetch_instruments(self) -> list[InstrumentRaw]: ...
    async def subscribe(
        self, instrument_id: InstrumentId, channels: set[Channel]
    ) -> None: ...
    async def unsubscribe(self, instrument_id: InstrumentId) -> None: ...
    def events(self) -> AsyncIterator[LiveEvent]: ...


class SecretResolver(Protocol):
    """第二暗証番号 resolver の共通 Protocol。

    第二暗証番号を必要とする venue（立花など）が使用する。
    不要な venue（kabu など）は ``set_execution_hooks`` で受理して無視する。
    """

    async def resolve(self, venue: str, purpose: str) -> str: ...


OnOrderEvent = Callable[[OrderEventData], None]
"""約定イベントのコールバック型。venue が OrderEventData を生成したとき呼び出す。"""

OnVenueLogout = Callable[[str], None]
"""venue ログアウト検知のコールバック型。引数は venue_id。

検知方法は venue の責務: 立花は EVENT WS SS=閉局フレーム（push）、
kabu は VenueHealthWatchdog (check_health → GET /apisoftlimit、poll）を使う。
"""


@runtime_checkable
class OrderingVenueAdapter(LiveVenueAdapter, Protocol):
    """発注可能な venue adapter（Phase 9）。LiveVenueAdapter に手動発注経路を足す。

    Phase 9 Step 2 の ManualOrderFacade はこの契約に依存する。MockVenueAdapter は
    既にこれを満たし、Tachibana / kabu の具体実装は Step 5/6 でこの契約を満たすこと
    （発注は本来 ExecutionClient の責務だが、Step 2 では adapter に薄く委譲する。
    真正 Nautilus ExecEngine wiring は Phase 10 / LiveAuto、ADR §7 参照）。
    submit_order / cancel_order は OrderResult を返す（engine.live.order_types）。
    set_execution_hooks で発注実行に必要なフックを注入してから発注を開始すること。
    """

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
    ) -> "OrderResult": ...

    async def cancel_order(
        self,
        *,
        venue: str,
        order_id: str,
    ) -> "OrderResult": ...

    async def modify_order(
        self,
        *,
        venue: str,
        order_id: str,
        new_price: float | None = None,
        new_qty: float | None = None,
    ) -> "OrderResult":
        """既存注文の訂正（価格 / 数量）。OrderResult を返す。

        venue 別の実体（Step 5/6 の adapter 実装の責務、Step 4 は mock のみ）:
        - **Tachibana**: `CLMKabuCorrectOrder`（venue 側 atomic な訂正 API）に直接マップ。
        - **kabu**: 訂正 API が無いため adapter 内部で「取消 → 新規発注」に変換する
          （atomicity は保証されない。UI に警告バナーを出すのは §3.11 / Step 6）。
        """
        ...

    async def fetch_account(self) -> "AccountSnapshot": ...

    async def fetch_working_orders(self) -> "list[OrderEventData]": ...

    def set_execution_hooks(
        self,
        *,
        secret_resolver: "SecretResolver | None",
        on_order_event: OnOrderEvent,
        on_venue_logout: "OnVenueLogout | None" = None,
    ) -> None:
        """発注実行フックを注入する（発注を開始する前に呼ぶこと）。

        ``secret_resolver`` は第二暗証番号を必要とする venue のみ使用する。
        不要な venue（kabu など）は受理して無視する（accept-and-ignore）。
        ``on_venue_logout`` の検知方法は venue の責務:
        - **立花**: EVENT WS の SS=閉局フレーム（push 型）
        - **kabu**: VenueHealthWatchdog の check_health polling（poll 型）
        統一されるのは「何を注入されるか」であり、「どう検知するか」ではない。
        """
        ...

    async def check_health(self) -> bool:
        """venue 接続の生死確認（polling 用）。

        接続が正常なら True、異常なら False を返す。
        push 型でログアウトを検知する venue（立花: EVENT WS の SS=閉局フレーム）は
        poll 型 watchdog を必要としないため True 固定の no-op で実装してよい。
        kabu など poll 型の venue は実際に接続を確認して bool を返す。
        """
        ...
