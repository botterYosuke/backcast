"""LiveVenueAdapter の kabuStation 実装。"""

from __future__ import annotations

import asyncio
import logging
import os
import time as _time_module
from typing import AsyncIterator, Awaitable, Callable, Literal, Optional

import httpx

from engine.exchanges import kabusapi_orders as _orders
from engine.exchanges import kabusapi_ws  # patch 対象を module 経由で参照
from engine.exchanges.kabusapi_auth import (
    KabuApiError,
    KabuRegisterFullError,
    auth_headers,
    check_response,
)
from engine.exchanges.kabusapi_register import RegisterSet
from engine.exchanges.kabusapi_execution import KabuExecutionEngine
from engine.exchanges.kabusapi_url import endpoint
from engine.exchanges.kabusapi_ratelimit import KabuRateLimits
from engine.exchanges.kabusapi_ws_codec import KabuPushFrameProcessor
from engine.live.adapter import (
    Channel,
    DepthLevel,
    DepthUpdate,
    InstrumentId,
    InstrumentRaw,
    LiveEvent,
    OnOrderEvent,
    OnVenueLogout,
    SecretResolver,
    SubscriptionLimitExceeded,
    TradesUpdate,
    VenueCredentials,
)
from engine.live.logging import suppress_third_party_http_logs
from engine.live.order_types import (
    AccountPositionData,
    AccountSnapshot,
    OrderResult,
)

logger = logging.getLogger(__name__)

_ENV_API_PASSWORD = "DEV_KABU_API_PASSWORD"


# kabuステーション本体がログアウト / 未ログインのときに REST が返す Code (R7、
# ptal/error.html)。`4001007`=ログイン認証エラー / `4001017`=ログイン認証エラー
# (本体未ログイン)。Watchdog (Phase 9 §3.5 / Step 7) が check_health() でこれを検出し、
# 本体早朝強制ログアウト → 再ログイン誘導の起点にする。
_VENUE_LOGGED_OUT_CODES = frozenset({4001007, 4001017})

# 発注/取消/口座系 REST のタイムアウト (localhost なので短くて十分)。
_ORDER_TIMEOUT = httpx.Timeout(connect=10.0, read=30.0, write=10.0, pool=5.0)




class KabuStationAdapter:
    venue_id: str = "KABU"
    # kabuStation MVP does not enumerate an instrument master (fetch_instruments
    # returns []). Declaring this False keeps a persisted store snapshot from being
    # served as the authoritative live universe (issue #253).
    enumerates_instruments: bool = False

    def __init__(
        self,
        environment: Literal["prod", "verify"] = "verify",
        *,
        time_source: Optional[Callable[[], float]] = None,
    ):
        if environment not in ("prod", "verify"):
            raise ValueError("environment must be 'prod' or 'verify'")
        # R10 / INV-T3-SECRET: kabu に第二暗証番号は無いが、X-API-KEY token は秘密。
        # httpx/httpcore の request ログ沈黙を全 venue 共通で効かせる (#19, findings/0009)。
        suppress_third_party_http_logs()
        self._env = environment
        self._token: str | None = None
        self._client: httpx.AsyncClient = httpx.AsyncClient()
        self._register_set: RegisterSet = RegisterSet()
        # Key by (Symbol, Exchange) per R4 — symbol alone collides across exchanges
        # (TSE=1, 名証=3, ...).
        self._processors: dict[tuple[str, int], KabuPushFrameProcessor] = {}
        self._queue: asyncio.Queue = asyncio.Queue()
        self._ws_task: asyncio.Task | None = None
        self._last_error: Optional[BaseException] = None
        # Per-symbol "warned once" set for ambiguous Exchange routing. Reset on
        # login()/logout() so a fresh session emits the warning again.
        self._exchange_ambiguity_warned: set[str] = set()
        # Rate-limit token buckets (R5). Tests inject _rate_limit_sleep.
        self._time_source: Callable[[], float] = time_source or _time_module.monotonic
        self._rate_limit_sleep: Callable[[float], Awaitable[None]] = asyncio.sleep
        self._rl = KabuRateLimits(
            time_source=self._time_source,
            sleep=lambda d: self._rate_limit_sleep(d),
        )
        # KabuExecutionEngine — 約定経路を engine に分離 (Issue #219)
        self._execution_engine = KabuExecutionEngine(
            client=self._client,
            rl=self._rl,
            env=self._env,
            time_source=self._time_source,
        )
        # テスト注入 (a._rate_limit_sleep = _noop_sleep) を engine の polling にも伝播する。
        self._execution_engine._rate_limit_sleep = lambda d: self._rate_limit_sleep(d)

    @property
    def _token(self) -> str | None:
        return self.__token

    @_token.setter
    def _token(self, value: str | None) -> None:
        # engine とトークンを同期。テストの a._token = "tkn" 直接代入も含む。
        # engine 生成前 (__init__ 序盤) は hasattr で skip する。
        self.__token = value
        if hasattr(self, '_execution_engine'):
            self._execution_engine._token = value

    @property
    def is_logged_in(self) -> bool:
        return self._token is not None

    @property
    def last_error(self) -> Optional[BaseException]:
        # polling エラーは engine._last_error に記録される。adapter の WS/login エラーを優先。
        return self._last_error or self._execution_engine._last_error

    async def login(self, creds: VenueCredentials) -> None:
        # Clear _last_error only on the SUCCESS path (immediately before setting
        # _token). If a credential-validation raise happens first, callers keep
        # the prior error state instead of seeing a false "healthy" snapshot.
        if self._client.is_closed:
            self._client = httpx.AsyncClient()
            self._execution_engine._client = self._client
        # Re-login without an intervening logout(): tear down the prior orders
        # poll loop and order registry so stale notifications / id mappings don't
        # bleed across sessions (Tachibana adapter と同方針)。
        await self._execution_engine.on_logout()
        source = creds.credentials_source
        if source == "session_cache":
            raise ValueError("UNSUPPORTED_FOR_VENUE: kabu does not support session_cache")
        if source == "prompt_result":
            if not creds.token:
                raise ValueError("PROMPT_RESULT_MISSING_TOKEN")
            self._last_error = None
            self._exchange_ambiguity_warned.clear()
            self._token = creds.token
            return
        if source == "prompt":
            raise NotImplementedError("prompt credentials_source not yet supported for kabu")
        if source != "env":
            raise ValueError(f"unknown credentials_source: {source!r}")

        api_password = os.environ.get(_ENV_API_PASSWORD)
        if not api_password:
            raise ValueError(
                f"missing env credentials: {_ENV_API_PASSWORD} "
                f"(credentials_source='env')"
            )

        from engine.exchanges.kabusapi_auth import fetch_token

        token = await fetch_token(api_password, env=self._env)
        self._last_error = None
        self._exchange_ambiguity_warned.clear()
        self._token = token

    async def logout(self) -> None:
        # Best-effort PUT /unregister/all (R6 cleanup). Tolerate any error —
        # token may already be invalid or kabu body may be down.
        if (
            self._token is not None
            and not self._client.is_closed
            and len(self._register_set) > 0
        ):
            try:
                await self._rl.gate("unregister/all")
                # 5s timeout — enough for localhost; body is best-effort cleanup.
                await self._client.put(
                    endpoint("unregister/all", env=self._env),
                    headers={"X-API-KEY": self._token},
                    timeout=httpx.Timeout(5.0),
                )
            except asyncio.CancelledError:
                raise
            except (
                httpx.HTTPError,
                asyncio.TimeoutError,
                RuntimeError,
                OSError,
            ) as exc:
                # OSError / ConnectionResetError can bubble up from a
                # closed/half-open transport during shutdown races.
                logger.warning("kabu unregister/all failed during logout: %s", exc)

        if self._ws_task is not None:
            self._ws_task.cancel()
            try:
                await self._ws_task
            except asyncio.CancelledError:
                pass
            except Exception as exc:
                # 想定は CancelledError のみ。WS task のシャットダウン時バグは握り潰さず
                # ログに残す (silent failure 回避)。
                logger.warning("kabu WS task errored during logout: %s", exc)
        await self._execution_engine.on_logout()
        self._processors.clear()
        self._register_set.unregister_all()
        self._exchange_ambiguity_warned.clear()
        await self._client.aclose()
        self._token = None

    async def _put_register(self, symbols: list[tuple[str, int]]) -> bool:
        """PUT /register with R5 rate-limit + R7 two-stage error check.

        Raises:
            KabuApiError / KabuTokenExpiredError / KabuRegisterFullError /
            KabuRateLimitError on non-success responses (HIGH-1).

        Returns True on success (Code == 0).
        """
        await self._rl.gate("register")
        resp = await self._client.put(
            endpoint("register", env=self._env),
            headers={"X-API-KEY": self._token},
            json={"Symbols": [{"Symbol": s, "Exchange": ex} for s, ex in symbols]},
        )
        data = resp.json()
        # Some endpoints return ResultCode, others Code — normalize for check_response.
        if isinstance(data, dict) and "Code" not in data and "ResultCode" in data:
            data = {**data, "Code": data["ResultCode"]}
        check_response(data, resp.status_code)
        return True

    async def fetch_instruments(self) -> list[InstrumentRaw]:
        return []

    def _parse_instrument_id(self, instrument_id: InstrumentId) -> tuple[str, int]:
        symbol, _, suffix = instrument_id.rpartition(".")
        if suffix != "TSE":
            raise ValueError(f"unsupported exchange suffix: {suffix!r} (MVP supports TSE only)")
        return symbol, 1

    async def _reset_all_processors(self) -> None:
        """HIGH-3: reset every processor's DV/quote state. Called on WS
        reconnect (codec docstring contract).
        """
        for proc in self._processors.values():
            proc.reset()

    async def subscribe(
        self, instrument_id: InstrumentId, channels: set[Channel]
    ) -> None:
        if self._token is None:
            raise RuntimeError("login required before subscribe")
        symbol, exchange = self._parse_instrument_id(instrument_id)
        was_registered = (symbol, exchange) in self._register_set
        try:
            self._register_set.register(symbol, exchange)
        except KabuRegisterFullError as exc:
            # #107: venue 実上限（50 銘柄）を venue 非依存の typed 例外へ翻訳して surface する。
            # 人工的件数 cap は orchestrator から撤去済み（ADR-0022）。membership は触らない。
            raise SubscriptionLimitExceeded(str(exc), venue_code=exc.code) from exc
        try:
            await self._put_register(self._register_set.all_symbols())
        except BaseException:
            if not was_registered:
                self._register_set.unregister(symbol, exchange)
            raise
        if (symbol, exchange) not in self._processors:
            self._processors[(symbol, exchange)] = KabuPushFrameProcessor(symbol=symbol)
        if self._ws_task is None or self._ws_task.done():
            self._last_error = None
            self._ws_task = asyncio.create_task(self._run_ws())

    async def _run_ws(self) -> None:
        """Wrap kabusapi_ws.connect with last_error capture (MEDIUM-3)."""
        try:
            await kabusapi_ws.connect(
                env=self._env,
                on_message=self._on_frame,
                register_set=self._register_set,
                put_register=self._put_register,
                on_reconnect=self._reset_all_processors,
            )
        except asyncio.CancelledError:
            raise
        except BaseException as exc:
            self._last_error = exc
            raise

    async def _on_frame(self, msg: dict) -> None:
        symbol = msg.get("Symbol")
        if symbol is None:
            return
        # Round2 MEDIUM-2: key by (Symbol, Exchange). When the frame omits
        # Exchange, do NOT default to TSE=1 — silently mis-routing to the
        # wrong venue corrupts DV/quote state. Instead look up matching
        # processors and route only when unambiguous; otherwise drop with
        # a warning.
        exchange = msg.get("Exchange")
        if exchange is None:
            if symbol in self._exchange_ambiguity_warned:
                logger.debug(
                    "kabu frame for symbol %r missing Exchange; dropping (ambiguous routing)",
                    symbol,
                )
                return
            matches = [ex for (sym, ex) in self._processors.keys() if sym == symbol]
            if len(matches) == 1:
                exchange = matches[0]
            else:
                # Log once per symbol per session; subsequent drops are DEBUG to
                # avoid spam at kabu PUSH rates (hundreds of msg/sec).
                self._exchange_ambiguity_warned.add(symbol)
                logger.warning(
                    "kabu frame for symbol %r has no Exchange and matches "
                    "%d processors (%s); dropping (ambiguous routing). "
                    "Further occurrences for this symbol will log at DEBUG.",
                    symbol,
                    len(matches),
                    matches,
                )
                return
        proc = self._processors.get((symbol, exchange))
        if proc is None:
            return
        trade, depth = proc.process(msg)
        instrument_id = f"{symbol}.TSE"
        if depth is not None:
            self._queue.put_nowait(
                DepthUpdate(
                    kind="depth",
                    instrument_id=instrument_id,
                    ts_ns=depth["ts_ns"] or 0,
                    bids=tuple(DepthLevel(price=p, size=s) for p, s in depth["bids"]),
                    asks=tuple(DepthLevel(price=p, size=s) for p, s in depth["asks"]),
                )
            )
        if trade is not None:
            self._queue.put_nowait(
                TradesUpdate(
                    kind="trades",
                    instrument_id=instrument_id,
                    ts_ns=trade["ts_ns"] or 0,
                    price=trade["price"],
                    size=trade["size"],
                    aggressor_side=trade["aggressor_side"],
                )
            )

    async def unsubscribe(self, instrument_id: InstrumentId) -> None:
        if self._token is None:
            return
        symbol, exchange = self._parse_instrument_id(instrument_id)
        if (symbol, exchange) not in self._register_set:
            return
        remaining = [s for s in self._register_set.all_symbols() if s != (symbol, exchange)]
        await self._put_register(remaining)
        self._register_set.unregister(symbol, exchange)
        self._processors.pop((symbol, exchange), None)

    async def events(self) -> AsyncIterator[LiveEvent]:
        while True:
            if self._queue.empty() and self._ws_task is not None and self._ws_task.done():
                exc = self._ws_task.exception()
                if exc is not None:
                    raise exc
                return
            get_task = asyncio.ensure_future(self._queue.get())
            try:
                if self._ws_task is None or self._ws_task.done():
                    yield await get_task
                    continue
                done, _pending = await asyncio.wait(
                    {get_task, self._ws_task},
                    return_when=asyncio.FIRST_COMPLETED,
                )
                if get_task in done:
                    yield get_task.result()
                else:
                    get_task.cancel()
                    exc = self._ws_task.exception()
                    if exc is not None:
                        raise exc
                    return
            except BaseException:
                if not get_task.done():
                    get_task.cancel()
                raise

    # ------------------------------------------------------------------
    # Phase 9 Step 6+7: OrderingVenueAdapter — KabuExecutionEngine 委譲
    # ------------------------------------------------------------------

    def set_execution_hooks(
        self,
        *,
        secret_resolver: SecretResolver | None = None,
        on_order_event: OnOrderEvent,
        on_venue_logout: OnVenueLogout | None = None,
    ) -> None:
        self._execution_engine.set_execution_hooks(
            secret_resolver=secret_resolver,
            on_order_event=on_order_event,
            on_venue_logout=on_venue_logout,
        )

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
        return await self._execution_engine.submit_order(
            venue=venue,
            instrument_id=instrument_id,
            side=side,
            qty=qty,
            price=price,
            order_type=order_type,
            time_in_force=time_in_force,
            client_order_id=client_order_id,
        )

    async def cancel_order(
        self, *, venue: str, order_id: str
    ) -> OrderResult:
        return await self._execution_engine.cancel_order(
            venue=venue, order_id=order_id
        )

    async def modify_order(
        self,
        *,
        venue: str,
        order_id: str,
        new_price: float | None = None,
        new_qty: float | None = None,
    ) -> OrderResult:
        return await self._execution_engine.modify_order(
            venue=venue,
            order_id=order_id,
            new_price=new_price,
            new_qty=new_qty,
        )

    async def fetch_account(self) -> AccountSnapshot:
        return await self._execution_engine.fetch_account()

    async def fetch_working_orders(self) -> list:
        return await self._execution_engine.fetch_working_orders()

    # ------------------------------------------------------------------
    # Venue Health Watchdog (Phase 9 §3.5 / Step 7)
    # ------------------------------------------------------------------

    async def check_health(self) -> bool:
        """GET /apisoftlimit を軽量 ping して本体ログイン状態を確認する (§3.5)。

        kabuステーション本体は早朝に強制ログアウトされる仕様 (kabusapi skill S1)。
        ログアウトすると REST は `4001007` / `4001017` (ログイン認証エラー) を返す。
        VenueHealthWatchdog が 30 秒間隔でこれを呼び、戻り値で再ログイン誘導を判断する。

        - **本体ログイン中** → ``True``。
        - **本体ログアウト / 未ログイン** (`4001007` / `4001017`) → ``False``
          (Watchdog が VenueLogoutDetected を push する)。
        - **transient 障害** (接続断・流量・その他 API エラー) → 例外を伝播する。
          Watchdog 側は best-effort で握り潰しバックオフするので、一過性の失敗で
          誤って再ログイン modal を出さない。

        ``GET /apisoftlimit`` を選ぶ理由 (§3.5): info 系 (10 req/sec) の最軽量エンドポイント
        で副作用が無い。``HEAD`` は `4001014 許可されていないHTTPメソッド` で失敗し、新規
        ``/token`` 発行は本体に負荷をかけるため使わない。
        """
        if self._token is None:
            # Watchdog は login 後にのみ起動・logout 前に停止されるため通常は到達しない。
            # teardown との race で token が消えた中間状態を「ログアウト検出」と誤認して
            # spurious な modal を出さないよう、transient 扱い (例外) にする。
            raise RuntimeError("check_health requires login; call login() first")
        await self._rl.gate("apisoftlimit")
        resp = await self._client.get(
            endpoint("apisoftlimit", env=self._env),
            headers=auth_headers(self._token),
            timeout=_ORDER_TIMEOUT,
        )
        data = resp.json()
        # ログアウト Code を check_response より先に判定する (check_response は logout も
        # 汎用 KabuApiError に丸めるため、ここで bool に変換しないと watchdog が transient と
        # 区別できない)。本体ログアウトは HTTP 200 + Code、または HTTP 401 + Code で来うる。
        code = data.get("Code") if isinstance(data, dict) else None
        if code in _VENUE_LOGGED_OUT_CODES:
            return False
        # ログアウト以外のエラー (流量 429・接続断・想定外 Code) は transient として伝播。
        check_response(data, resp.status_code)
        return True

    # ------------------------------------------------------------------
    # 内部フォワーダー (test_kabusapi_exec.py 互換レイヤ)
    # adapter 経由の内部フィールドアクセスを engine に委譲する。
    # ------------------------------------------------------------------

    @property
    def _on_order_event(self):
        return self._execution_engine._on_order_event

    @property
    def _orders_ref(self):
        return self._execution_engine._orders_ref

    @property
    def _registry(self):
        return self._execution_engine._registry

    @property
    def _modifying(self):
        return self._execution_engine._modifying

    @property
    def _orders_poll_task(self):
        return self._execution_engine._orders_poll_task

    @_orders_poll_task.setter
    def _orders_poll_task(self, value) -> None:
        self._execution_engine._orders_poll_task = value

    def _register_order(self, ref) -> None:
        self._execution_engine._register_order(ref)

    @property
    def _poll_orders_once(self):
        return self._execution_engine._poll_orders_once

    @_poll_orders_once.setter
    def _poll_orders_once(self, value) -> None:
        self._execution_engine._poll_orders_once = value

    async def _run_orders_poll(self) -> None:
        await self._execution_engine._run_orders_poll()

    async def _stop_orders_poll(self) -> None:
        await self._execution_engine._stop_orders_poll()
