"""live_orchestrator — LiveLoopManager (issue #217).

Live loop thread + venue lifecycle + order facade routing +
live auto strategy lifecycle + event publish callbacks,
extracted from DataEngineBackend.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from concurrent import futures
from datetime import datetime, timezone
from pathlib import Path
from dataclasses import dataclass
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from engine.live.adapter import OrderingVenueAdapter

from engine.live._build_mode import IS_DEBUG_BUILD
from engine.live.live_runner import LiveRunner
from engine.live.reducer_bridge import LiveReducerBridge
from engine.live.last_price_cache import LastPriceCache
from engine.live.depth_cache import DepthCache
from engine.live.backend_event_bus import BackendEventStream
from engine.live import backend_events
from engine.live.secret_vault import SecretVault
from engine.live.secret_provider import SecondSecretResolver, SecretTimeoutError
from engine.live.order_facade import ManualOrderFacade, OrderFacadeError
from engine.live.order_types import OrderEventData, fill_static_attrs
from engine.live.account_sync import AccountSync
from engine.live.run_registry import RunRegistry
from engine.live.strategy_registry import StrategyRegistry, StrategyRegistryError
from engine.live.strategy_host import LiveStrategyHost, LiveStrategyHostError, StartParams
from engine.live.safety_rails import RailViolation, SafetyLimits, SafetyRails
from engine.live.post_trade_gate import evaluate_post_trade, equity_from_snapshot
from engine.live.health_watchdog import VenueHealthWatchdog
from engine.live.instruments_scheduler import InstrumentsScheduler
from engine.live import instruments_store
from engine.live.logging import mask_secrets
from engine.paths import PYTHON_SRC_ROOT
from engine.live.event_wire import _backend_event_to_wire_dict

# re-exported result types from _backend_impl (used by LiveLoopManager methods)
from engine._backend_impl import (
    CommandAck,
    OrderCommandResult,
    OrdersResult,
    VenueSessionResult,
    InstrumentListResult,
    LiveStrategyRegisterResult,
    LiveStrategyStartResult,
    MANUAL_STRATEGY_ID,
    _ACCOUNT_REFETCH_STATUSES,
    _ADAPTER_ERROR_CODES,
    _LiveSessionView,
    _resolve_python_executable,
    _login_subprocess_env,
    _live_login_timeout_s,
    _sweep_stale_cred_files,
)


@dataclass
class LiveSession:
    """Live セッションの 8 コンポーネントをまとめる値オブジェクト。

    `_start_live_components_async` の末尾で生成し、
    `_teardown_live_components` の finally で `self._session = None` に収束する。
    `strategy_host.LiveSessionView` Protocol を満たす（`is_logged_in` 属性を持つ）。
    """

    runner: "LiveRunner"
    bridge: "LiveReducerBridge"
    price_cache: "LastPriceCache"
    depth_cache: "DepthCache"
    order_facade: "ManualOrderFacade"
    account_sync: "AccountSync"
    health_watchdog: "VenueHealthWatchdog"
    instruments_scheduler: "InstrumentsScheduler"

    @property
    def is_logged_in(self) -> bool:
        """LiveSessionView Protocol 実装: セッションが存在する = ログイン済み。"""
        return True


class LiveLoopManager:
    """Live loop thread, venue lifecycle, order facade, live auto strategy lifecycle.

    Extracted from DataEngineBackend (issue #217).

    ``publish_backend_event_callback`` は DataEngineBackend.publish_backend_event への
    参照を渡すこと。LiveLoopManager はイベント publish を全てこのコールバック経由で行う。
    """

    _KNOWN_VENUES = {"TACHIBANA", "KABU", "MOCK"}
    _KNOWN_CRED_SOURCES = {"prompt", "session_cache", "env", "prompt_result"}
    _KNOWN_MODES = {"Replay", "LiveManual", "LiveAuto"}

    def __init__(
        self,
        engine,
        mode_manager,
        venue_sm,
        live_adapter_factory,
        live_venue_id: Optional[str],
        engine_controller,
        publish_backend_event_callback,
    ) -> None:
        self._engine = engine
        self.mode_manager = mode_manager
        self.venue_sm = venue_sm
        self._live_adapter_factory = live_adapter_factory
        self._live_venue_id: Optional[str] = live_venue_id.upper() if live_venue_id else None
        self._emit = publish_backend_event_callback

        # live loop state
        self._session: Optional[LiveSession] = None
        self._live_loop = None
        self._live_thread = None
        self._live_timeout_s = 5.0
        self._order_timeout_s = 40.0
        self._instruments_timeout_s = 60.0
        self._suppress_live_last_error: bool = False
        self._suppressed_error_baseline: Optional[BaseException] = None

        # secret vault
        self._secret_vault: SecretVault = SecretVault()

        # live auto strategy
        # #25: kernel-native loader（nautilus Strategy ではなく engine.kernel.strategy.Strategy の
        # subclass を検出 = Rust core 非ロード）。register_live_strategy（StrategyRegistry）と
        # start_live_strategy（LiveStrategyHost）の **両方** に同じ kernel loader を差す。register 側を
        # nautilus 既定のままにすると、①kernel 戦略ファイルが 0 subclass で登録できず ②register 時点で
        # Rust core をロードし LiveAuto import-purity を破る（codex review #25）。
        import functools

        from engine.kernel.strategy import Strategy as _KernelStrategy
        from engine.strategy_runtime import strategy_loader as _strategy_loader

        _kernel_loader = functools.partial(_strategy_loader.load, base_cls=_KernelStrategy)

        self._run_registry: RunRegistry = RunRegistry()
        self._strategy_registry: StrategyRegistry = StrategyRegistry(loader=_kernel_loader)
        if engine_controller is None:
            # #25: 既定 controller を pure-Python kernel 実体に置換（Live でも Rust core 非ロード・
            # ADR-0004 案 C）。ctor seam は NautilusLiveEngineController と同一なので引数は不変。
            from engine.kernel.live.controller import KernelLiveEngineController
            engine_controller = KernelLiveEngineController(
                loop_provider=self._ensure_live_loop,
                adapter_provider=self._live_adapter,
                runner_provider=self._live_runner_provider,
                on_safety_violation=self._on_pretrade_violation,
                on_order_event=self._on_auto_order_event,
                on_telemetry=self._on_auto_telemetry,
                on_strategy_log=self._on_auto_strategy_log,
                run_gate_provider=self._is_run_gated,
                on_strategy_error=self._on_auto_strategy_error,
            )
        self._strategy_host: LiveStrategyHost = LiveStrategyHost(
            run_registry=self._run_registry,
            session_provider=self._live_session_view,
            engine_controller=engine_controller,
            loader=_kernel_loader,
        )
        self._live_strategy_lock = threading.Lock()
        self._run_rails_lock = threading.Lock()
        self._run_rails: dict = {}
        self._run_equity_baseline: dict = {}

    def _ensure_live_loop(self):
        if self._live_loop is not None:
            return self._live_loop

        loop = asyncio.new_event_loop()

        def _loop_exception_handler(_loop, ctx):
            # Post-merge fix (MEDIUM-5): mask any secrets that may have ended up
            # in the asyncio context (exception args / message) before logging.
            try:
                masked = mask_secrets({k: str(v) for k, v in ctx.items()})
            except Exception:
                masked = {"message": "<context masking failed>"}
            logging.error("phase8-live-loop uncaught asyncio exception: %s", masked)

        def run_loop():
            asyncio.set_event_loop(loop)
            loop.set_exception_handler(_loop_exception_handler)
            try:
                loop.run_forever()
            except BaseException:
                # Post-merge fix (MEDIUM-5): never let the loop thread die
                # silently — log loudly so the failure is observable.
                logging.exception(
                    "phase8-live-loop thread crashed in run_forever()"
                )
                raise

        self._live_loop = loop
        self._live_thread = threading.Thread(
            target=run_loop, name="phase8-live-loop", daemon=True
        )
        self._live_thread.start()
        return loop

    async def _start_live_components_async(self, adapter: "OrderingVenueAdapter"):
        if self._session is not None:
            return

        # Step 8: partial_push_interval_s=1.0 → 進行中バーのスナップショットを 1 秒間隔で
        # bus に publish（UI 用 partial bar、§2.3）。確定バーは on_tick が emit する。
        runner = LiveRunner(
            adapter=adapter,
            interval_ns=60 * 1_000_000_000,
            partial_push_interval_s=1.0,
        )
        # D10: wire the event loop reference so fetch_instruments_blocking works
        runner._loop = self._live_loop
        bridge = LiveReducerBridge(
            bus=runner.bus,
            data_engine=self._engine,
            mode_provider=lambda: (self.mode_manager.current_mode if self.mode_manager else "Replay"),
        )
        cache = LastPriceCache(bus=runner.bus)
        depth_cache = DepthCache(bus=runner.bus)
        await bridge.start()
        await cache.start()
        await depth_cache.start()
        await runner.start()
        # Phase 9 Step 2: manual order facade wraps this session's adapter.
        order_facade = ManualOrderFacade(adapter)
        # Phase 9 Step 5: Tachibana 第二暗証番号の都度収集 + EC 約定通知 push を
        # adapter に注入する。SecondSecretResolver が SecretVault と SecretRequired
        # push (transport) を束ね、adapter.submit/cancel/modify_order の内側で
        # `await resolve()` する (facade は second_secret を終端し続ける = 単一経路)。
        # kabu は Password 不要なので secret_resolver を受理して無視する (約定通知は
        # GET /orders polling 由来)。mock は no-op の set_execution_hooks を持つ。
        resolver = SecondSecretResolver(
            self._secret_vault, self._publish_secret_required
        )
        adapter.set_execution_hooks(
            secret_resolver=resolver,
            on_order_event=self._publish_order_event,
            # Phase 9 Step 7: Tachibana は EVENT WS の SS=閉局フレームでログアウトを
            # push 検知し VenueLogoutDetected を出す。kabu は受理して無視 (poll 型の
            # VenueHealthWatchdog で検知する。secret_resolver と同じく accept-and-ignore)。
            on_venue_logout=self._publish_venue_logout,
        )
        # Phase 9 Step 7: kabu 本体ログアウトの poll 型 watchdog。全 adapter が
        # check_health() を持つため、ガードなしで生成する。Tachibana は上の hook で
        # push 検知するため watchdog は no-op になるが、Protocol を統一するために生成する。
        # account_sync と同じく login 前に生成するだけで、start はログイン成功後 (§_attempt)。
        health_watchdog = VenueHealthWatchdog(
            adapter,
            venue_id=getattr(adapter, "venue_id", "") or "",
            on_venue_logout=self._publish_venue_logout,
            interval_s=30.0,
        )
        # Phase 9 Step 4: account sync pushes AccountEvent on the backend stream.
        # The callback runs on the live-loop thread; BackendEventStream is threadsafe
        # (Step 0), so publishing directly from here is safe. AccountSync is
        # transport-agnostic — proto conversion + ts_ms stamping happens here.
        account_sync = AccountSync(
            adapter,
            on_account_event=self._publish_account_snapshot,
            interval_s=30.0,
            on_error=self._publish_account_sync_error,
            mode_provider=lambda: (
                self.mode_manager.current_mode if self.mode_manager else "Replay"
            ),
        )
        # Phase 9 Step 9: instruments daily refresh. login 直後の初期 refresh で
        # parquet を全置換 (= ログイン時 persist) し、以降は営業日 5:00 JST 毎に更新する。
        # account_sync と同じく login 前に生成し、start はログイン成功後 (§_attempt)。
        instruments_scheduler = InstrumentsScheduler(
            adapter,
            venue_id=runner.venue_id,
        )
        # Collect all 8 components into the LiveSession value object.
        self._session = LiveSession(
            runner=runner,
            bridge=bridge,
            price_cache=cache,
            depth_cache=depth_cache,
            order_facade=order_facade,
            account_sync=account_sync,
            health_watchdog=health_watchdog,
            instruments_scheduler=instruments_scheduler,
        )
        # NOTE: do NOT start the sync here. _start_live_components runs *before*
        # adapter.login() in the venue_login handler, so the forced initial
        # fetch_account() would hit a not-logged-in adapter — the guaranteed first
        # emit would be swallowed and the UI would show no balance/positions until
        # the first 30s interval tick. Started right after a successful login
        # instead (see venue_login `_attempt` → `_start_account_sync_after_login`).

    def _start_live_components(self, environment_hint: Optional[str] = None):
        if self._session is not None:
            return
        if self._live_adapter_factory is None:
            return
        # PermissionError on Windows can leak ttwr_cred_*.json from a prior
        # venue_login; sweep here where no concurrent login holds the file.
        _sweep_stale_cred_files()
        adapter = self._live_adapter_factory(environment_hint)
        loop = self._ensure_live_loop()
        future = asyncio.run_coroutine_threadsafe(
            self._start_live_components_async(adapter), loop
        )
        future.result(timeout=self._live_timeout_s)

    def _start_bg_component_after_login(self, component, label: str) -> None:
        """Start a background live component (account sync / health watchdog) after
        a successful login.

        These components are *created* in `_start_live_components_async` (which runs
        *before* `adapter.login()` in the venue_login handler) but *started* here so
        their first call sees a live session — the account sync's forced initial
        `fetch_account()` returns balances/positions immediately rather than after
        the first 30s tick, and the watchdog's first `check_health()` pings a
        logged-in body. Best-effort: a failure to start must not fail an
        already-successful login. A None component is a no-op (defensive guard;
        all three components are unconditionally assigned in
        _start_live_components_async, so None is only reachable on future
        refactors that make assignment conditional).
        """
        if component is None:
            return
        try:
            loop = self._ensure_live_loop()
            asyncio.run_coroutine_threadsafe(component.start(), loop).result(
                timeout=self._live_timeout_s
            )
            mode = self.mode_manager.current_mode if self.mode_manager else "Replay"
            logging.info("started %s after login (mode=%s)", label, mode)
        except Exception as exc:  # noqa: BLE001 — best-effort; login already succeeded
            logging.exception("failed to start %s after login", label)
            # 握り潰さず UI トースト用に surface する（issue #29 D2 / B 経路）。
            # ただし shutdown 中など bus が closed のときは publish が RuntimeError を
            # 投げる。A 経路（account_sync の on_error）と同様、publish 失敗で
            # 起動経路を止めない（issue #29 Slice1 レビュー指摘 #1）。
            try:
                self._emit(
                    backend_events.BackendError(
                        source="backend_service",
                        detail=f"{label}: {type(exc).__name__}: {exc}" if str(exc) else f"{label}: {exc!r}",
                        ts_ms=int(time.time() * 1000),
                    )
                )
            except Exception:  # noqa: BLE001 — publish failure must not fail startup
                logging.warning(
                    "failed to publish backend_error for %s start failure", label
                )

    def _start_account_sync_after_login(self) -> None:
        sess = self._session
        self._start_bg_component_after_login(
            sess.account_sync if sess is not None else None, "account sync"
        )

    def _start_health_watchdog_after_login(self) -> None:
        sess = self._session
        self._start_bg_component_after_login(
            sess.health_watchdog if sess is not None else None, "health watchdog"
        )

    def _start_instruments_scheduler_after_login(self) -> None:
        sess = self._session
        self._start_bg_component_after_login(
            sess.instruments_scheduler if sess is not None else None, "instruments scheduler"
        )

    async def _teardown_live_components_async(self):
        sess = self._session
        if sess is None:
            return
        bridge = sess.bridge
        cache = sess.price_cache
        depth_cache = sess.depth_cache
        runner = sess.runner
        account_sync = sess.account_sync
        health_watchdog = sess.health_watchdog
        instruments_scheduler = sess.instruments_scheduler
        # Stop the instruments refresh first (it only touches parquet; cheap to stop).
        if instruments_scheduler is not None:
            await instruments_scheduler.stop()
        # Stop the watchdog first so no VenueLogoutDetected is pushed mid-teardown
        # (the adapter is about to be torn down → check_health would race on a
        # closing transport).
        if health_watchdog is not None:
            await health_watchdog.stop()
        # Stop the account push next so no AccountEvent is emitted mid-teardown.
        if account_sync is not None:
            await account_sync.stop()
        if bridge is not None:
            await bridge.stop()
        if cache is not None:
            await cache.stop()
        if depth_cache is not None:
            await depth_cache.stop()
        if runner is not None:
            await runner.aclose()

    def _teardown_live_components(self):
        if self._session is None:
            return
        loop = self._live_loop
        # Capture adapter before the finally block nulls self._session.
        # adapter.logout() is called after the fast teardown with its own budget
        # so it does not consume the shared _live_timeout_s (issue #16 fix).
        adapter = self._session.runner.adapter if self._session is not None else None
        try:
            if loop is not None and loop.is_running():
                future = asyncio.run_coroutine_threadsafe(
                    self._teardown_live_components_async(), loop
                )
                future.result(timeout=self._live_timeout_s)
        except Exception:
            logging.exception("SetExecutionMode: failed to stop live components")
        finally:
            # Arm clear-on-toggle: a prior lifecycle's last_error must not bleed
            # into the next Live session or stay visible after returning to Replay.
            # Must be called before self._session = None (session is the source).
            self._suppressed_error_baseline = self._resolve_live_last_error()
            self._session = None
            self._suppress_live_last_error = True
        # adapter.logout() gets its own budget: PUT /unregister/all can take several
        # seconds and must not inflate _live_timeout_s (shared with start/subscribe).
        if adapter is not None and loop is not None and loop.is_running():
            try:
                asyncio.run_coroutine_threadsafe(
                    adapter.logout(), loop
                ).result(timeout=12.0)
            except Exception:
                logging.exception("adapter.logout() failed during teardown")
        # v5.2 Claim 2: reset venue_sm to DISCONNECTED so next Live entry
        # requires venue_login again (ensures adapter.is_logged_in invariant).
        if self.venue_sm is not None and self.venue_sm.current != "DISCONNECTED":
            self.venue_sm.reset()

    def stop_live_loop(self, timeout: float | None = None) -> None:
        """Stop the live asyncio loop thread spawned by _ensure_live_loop().

        issue #64 finding #6: _teardown_live_components() stops runner/bridge/
        account-sync but leaves the loop thread in run_forever(). On InProc
        worker shutdown the thread would otherwise leak. Safe to call when the
        loop was never started (None guard).
        """
        loop = self._live_loop
        thread = self._live_thread
        if loop is not None:
            try:
                loop.call_soon_threadsafe(loop.stop)
            except Exception:
                logging.exception("stop_live_loop: failed to signal loop.stop()")
        if thread is not None:
            try:
                thread.join(timeout=timeout if timeout is not None else self._live_timeout_s)
            except Exception:
                logging.exception("stop_live_loop: failed to join loop thread")
        self._live_loop = None
        self._live_thread = None

    def _resolve_live_last_error(self) -> Optional[BaseException]:
        sess = self._session
        if sess is None:
            return None
        err = sess.runner.last_error if sess.runner is not None else None
        if err is None and sess.bridge is not None:
            err = sess.bridge.last_error
        return err


    async def _handle_prompt_login(
        self, venue_id: str, env_hint: str
    ) -> tuple[bool, str, Optional[str]]:
        """Spawn login_dialog_runner subprocess and handle cross-platform IPC.

        Returns (success, error_code, token_or_none).
        Tachibana: token_or_none is always None (uses session_cache on disk).
        Kabu: token_or_none is the bearer token from cred-path file.
        """
        cred_path = ""
        if venue_id.upper() == "KABU":
            fd, cred_path = tempfile.mkstemp(prefix="ttwr_cred_", suffix=".json")
            os.close(fd)
            if os.name == "posix":
                os.chmod(cred_path, 0o600)
        args = [
            _resolve_python_executable(), "-m", "engine.live.login_dialog_runner",
            "--venue", venue_id.lower(),
            "--env", env_hint,
        ]
        if cred_path:
            args.extend(["--cred-path", cred_path])
        stderr_drain = None
        try:
            proc = await asyncio.create_subprocess_exec(
                *args,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                env=_login_subprocess_env(),
            )
            stderr_drain = asyncio.ensure_future(proc.stderr.read())

            async def _drain_stderr_text() -> str:
                try:
                    data = await asyncio.wait_for(stderr_drain, timeout=5.0)
                except (asyncio.TimeoutError, asyncio.CancelledError):
                    data = b""
                return data.decode("utf-8", errors="replace")

            try:
                line = await asyncio.wait_for(
                    proc.stdout.readline(),
                    timeout=_live_login_timeout_s(),
                )
            except asyncio.TimeoutError:
                proc.kill()
                await proc.wait()
                stderr_drain.cancel()
                return False, "LOGIN_TIMEOUT", None

            if not line:
                logging.error(
                    "login_dialog_runner exited without result: %s",
                    await _drain_stderr_text(),
                )
                await proc.wait()
                return False, "LOGIN_SUBPROCESS_CRASHED", None

            try:
                result = json.loads(line)
            except json.JSONDecodeError:
                proc.kill()
                await proc.wait()
                logging.error(
                    "login_dialog_runner emitted non-JSON stdout: %s",
                    await _drain_stderr_text(),
                )
                return False, "LOGIN_INVALID_RESPONSE", None

            if not result.get("success"):
                try:
                    await asyncio.wait_for(proc.wait(), timeout=5.0)
                except asyncio.TimeoutError:
                    proc.kill()
                    await proc.wait()
                return False, result.get("error_code") or "AUTH_FAILED", None

            try:
                await asyncio.wait_for(proc.wait(), timeout=10.0)
            except asyncio.TimeoutError:
                proc.kill()
                await proc.wait()
                return False, "LOGIN_TIMEOUT", None

            if proc.returncode != 0:
                logging.warning(
                    "login_dialog_runner exited rc=%d after success-line: %s",
                    proc.returncode,
                    await _drain_stderr_text(),
                )
                return False, result.get("error_code") or "LOGIN_NONZERO_EXIT", None

            token: Optional[str] = None
            if cred_path:
                try:
                    with open(cred_path, "rb") as f:
                        blob = f.read()
                except OSError as exc:
                    logging.warning("cred_path read failed: %s", exc)
                    return False, "LOGIN_INVALID_RESPONSE", None
                if not blob:
                    return False, "LOGIN_INVALID_RESPONSE", None
                try:
                    payload = json.loads(blob.decode("utf-8"))
                except (json.JSONDecodeError, UnicodeDecodeError):
                    return False, "LOGIN_INVALID_RESPONSE", None
                if not isinstance(payload, dict):
                    return False, "LOGIN_INVALID_RESPONSE", None
                tok = payload.get("token")
                if not isinstance(tok, str) or not tok:
                    return False, "LOGIN_INVALID_RESPONSE", None
                token = tok
            return True, "", token
        finally:
            if stderr_drain is not None and not stderr_drain.done():
                stderr_drain.cancel()
                try:
                    await stderr_drain
                except (asyncio.CancelledError, Exception):
                    pass
            if cred_path:
                try:
                    os.unlink(cred_path)
                except FileNotFoundError:
                    pass
                except PermissionError:
                    logging.warning(
                        "cred_path leak (Windows handle race): %s — "
                        "stale file will be swept on next _start_live_components",
                        cred_path,
                    )

    def venue_login(self, venue_id, credentials_source, environment_hint):
        cred_source = credentials_source or "prompt"
        if cred_source not in self._KNOWN_CRED_SOURCES:
            return VenueSessionResult(
                success=False, error_code="INVALID_CREDENTIALS_SOURCE",
                venue_state=self.venue_sm.current if self.venue_sm is not None else "DISCONNECTED",
                instruments_loaded=0,
            )

        # D21: normalize venue_id to uppercase (UI sends lowercase "tachibana"/"kabu"/"mock")
        venue_id = (venue_id or "").upper()
        venue_state = self.venue_sm.current if self.venue_sm is not None else "DISCONNECTED"

        if venue_id not in self._KNOWN_VENUES:
            return VenueSessionResult(
                success=False, error_code="UNKNOWN_VENUE",
                venue_state=venue_state, instruments_loaded=0,
            )

        # Preserve backward compat: KABU session_cache is unsupported
        if venue_id == "KABU" and cred_source == "session_cache":
            return VenueSessionResult(
                success=False, error_code="UNSUPPORTED_FOR_VENUE",
                venue_state=venue_state, instruments_loaded=0,
            )

        # D26: validate against configured factory venue (1 backend = 1 venue)
        if self._live_adapter_factory is None:
            return VenueSessionResult(
                success=False, error_code="LIVE_ADAPTER_NOT_CONFIGURED",
                venue_state=venue_state, instruments_loaded=0,
            )

        configured_venue = (self._live_venue_id or venue_id).upper()
        if configured_venue != venue_id:
            return VenueSessionResult(
                success=False, error_code="VENUE_MISMATCH",
                venue_state=venue_state, instruments_loaded=0,
            )

        # Idempotent: already CONNECTED/SUBSCRIBED → no-op success — UNLESS the
        # runner/bridge died with a last_error. LiveRunner._run() never transitions
        # venue_sm to ERROR, so a crashed WS task leaves the state machine
        # stale-CONNECTED while no data flows. Detect the dead session, tear it
        # down, and fall through to a fresh login attempt so re-login recovers.
        if self.venue_sm is not None and self.venue_sm.current in ("CONNECTED", "SUBSCRIBED"):
            live_err = self._resolve_live_last_error()
            if live_err is None:
                return VenueSessionResult(
                    success=True, error_code="",
                    venue_state=self.venue_sm.current, instruments_loaded=0,
                )
            logging.warning(
                "venue_login: venue_sm=%s but live runner/bridge has last_error=%r; "
                "tearing down dead session to re-establish",
                self.venue_sm.current, live_err,
            )
            if self._session is not None:
                self._teardown_live_components()

        # AUTHENTICATING 中の二重起動防止
        if self.venue_sm is not None and self.venue_sm.current == "AUTHENTICATING":
            return VenueSessionResult(
                success=False, error_code="ALREADY_AUTHENTICATING",
                venue_state="AUTHENTICATING", instruments_loaded=0,
            )

        env_hint = environment_hint or None

        def _fail(error_code: str) -> VenueSessionResult:
            if self._session is not None:
                self._teardown_live_components()
            # _teardown_live_components only resets venue_sm when a live runner
            # existed; cover the "failed before _start_live_components" path so
            # AUTHENTICATING never sticks and dead-locks the next venue_login.
            if self.venue_sm is not None and self.venue_sm.current == "AUTHENTICATING":
                try:
                    self.venue_sm.transition_to("ERROR")
                except Exception:
                    pass
                self.venue_sm.reset()
            return VenueSessionResult(
                success=False, error_code=error_code,
                venue_state=self.venue_sm.current if self.venue_sm else "DISCONNECTED",
                instruments_loaded=0,
            )

        def _attempt(effective_source: str):
            """Returns (handled: bool, error_code: str).

            handled=True, error_code="" → success path
            handled=True, error_code!="" → _fail(error_code)
            handled=False, error_code="NO_DISPLAY_AVAILABLE" → retry with "env" (debug only)
            """
            try:
                self._start_live_components(environment_hint=env_hint)
                if self._session is None:
                    return True, "VENUE_ADAPTER_NOT_CONFIGURED"
                runner = self._session.runner
                adapter = runner.adapter
                loop = self._ensure_live_loop()

                if effective_source == "prompt":
                    if self.venue_sm is not None and self.venue_sm.current == "DISCONNECTED":
                        self.venue_sm.transition_to("AUTHENTICATING")

                    if venue_id == "TACHIBANA":
                        effective_env = env_hint if env_hint in ("demo", "prod") else "demo"
                    else:
                        effective_env = env_hint if env_hint in ("verify", "prod") else "verify"

                    fut = asyncio.run_coroutine_threadsafe(
                        self._handle_prompt_login(venue_id, effective_env),
                        loop,
                    )
                    # TODO(将来): VenueLoginStream で逐次 push できるようにする
                    success, ec, token = fut.result(timeout=_live_login_timeout_s() + 10)

                    if not success:
                        if ec == "NO_DISPLAY_AVAILABLE" and IS_DEBUG_BUILD:
                            return False, ec  # caller retries with "env"
                        return True, ec

                    from engine.live.adapter import VenueCredentials
                    if venue_id == "TACHIBANA":
                        adapter_creds = VenueCredentials(
                            credentials_source="session_cache",
                            environment_hint=effective_env,
                        )
                    else:
                        adapter_creds = VenueCredentials(
                            credentials_source="prompt_result",
                            environment_hint=effective_env,
                            token=token,
                        )
                else:
                    from engine.live.adapter import VenueCredentials
                    adapter_creds = VenueCredentials(
                        credentials_source=effective_source,
                        environment_hint=env_hint,
                    )

                if not adapter.is_logged_in:
                    login_fut = asyncio.run_coroutine_threadsafe(
                        adapter.login(adapter_creds), loop,
                    )
                    login_fut.result(timeout=_live_login_timeout_s())

                if self.venue_sm is not None and self.venue_sm.current == "DISCONNECTED":
                    self.venue_sm.transition_to("AUTHENTICATING")
                if self.venue_sm is not None and self.venue_sm.current == "AUTHENTICATING":
                    self.venue_sm.transition_to("CONNECTED")
                # Phase 9 Step 4: the adapter is now logged in — start the account
                # sync (deferred from _start_live_components_async, which runs before
                # login) so its forced initial fetch_account() sees a live session.
                self._start_account_sync_after_login()
                # Phase 9 Step 7: start the kabu health watchdog now that the
                # adapter is logged in (no-op for Tachibana/mock, which have none).
                self._start_health_watchdog_after_login()
                # Phase 9 Step 9: start the instruments daily refresh. Its forced
                # initial fetch+persist writes the parquet store now that the adapter
                # is logged in (= login-time persist), then refreshes at 5:00 JST.
                self._start_instruments_scheduler_after_login()
                # Arm clear-on-toggle: suppress stale errors from a prior session.
                self._suppressed_error_baseline = self._resolve_live_last_error()
                self._suppress_live_last_error = True
                return True, ""
            except ValueError as exc:
                # adapter 層が定義する判別可能エラーは error_code として透過。
                # それ以外の ValueError は VENUE_LOGIN_FAILED に丸める。
                code = str(exc)
                if code in _ADAPTER_ERROR_CODES:
                    logging.warning("venue_login adapter error (source=%s): %s", effective_source, code)
                    return True, code
                logging.exception("venue_login attempt failed (source=%s): %s", effective_source, exc)
                return True, "VENUE_LOGIN_FAILED"
            except Exception as exc:
                logging.exception("venue_login attempt failed (source=%s): %s", effective_source, exc)
                return True, "VENUE_LOGIN_FAILED"

        handled, error_code = _attempt(cred_source)
        if not handled and cred_source == "prompt":
            if IS_DEBUG_BUILD:
                if self._session is not None:
                    self._teardown_live_components()
                handled, error_code = _attempt("env")
            else:
                error_code = "NO_DISPLAY_AVAILABLE"

        if error_code:
            return _fail(error_code)

        return VenueSessionResult(
            success=True, error_code="",
            venue_state=self.venue_sm.current if self.venue_sm else "CONNECTED",
            instruments_loaded=0,
        )

    def venue_logout(self) -> CommandAck:
        # Fix 1: stop live runner, bridge, price cache, and reset venue state machine
        if self._session is not None:
            self._teardown_live_components()
        elif self.venue_sm is not None and self.venue_sm.current != "DISCONNECTED":
            self.venue_sm.reset()
        return CommandAck(success=True, error_code="")

    def set_execution_mode(self, mode: str) -> CommandAck:
        if mode not in self._KNOWN_MODES:
            return CommandAck(success=False, error_code="INVALID_MODE")

        if self.mode_manager is None:
            return CommandAck(success=False, error_code="NOT_IMPLEMENTED")

        if mode in ("LiveManual", "LiveAuto") and self._live_adapter_factory is None:
            return CommandAck(success=False, error_code="LIVE_ADAPTER_NOT_CONFIGURED")

        try:
            applied = self.mode_manager.set_execution_mode(mode)
        except ValueError as exc:
            msg = str(exc)
            code = "EXECUTION_MODE_PRECONDITION" if msg.startswith("EXECUTION_MODE_PRECONDITION") else "EXECUTION_MODE_ERROR"
            return CommandAck(success=False, error_code=code)
        if applied in ("LiveManual", "LiveAuto"):
            # D21: venue_login must have been called first. If runner is None, reject.
            if self._session is None:
                return CommandAck(success=False, error_code="VENUE_LOGIN_REQUIRED")
        if applied == "Replay":
            # Clear-on-toggle: arm the suppression flag so GetState reports
            # live_last_error as None until a *fresh* error appears. Do NOT clear
            # runner/bridge._last_error itself — venue_login's dead-session detection
            # (_resolve_live_last_error) must still see a crashed session's error to
            # avoid healthy-misjudging a stale-CONNECTED runner (issue #39 Slice 1).
            self._suppressed_error_baseline = self._resolve_live_last_error()
            self._suppress_live_last_error = True
        return CommandAck(success=True, error_code="")

    # kabuステーション API 上限 (R6). LiveRunner 自体に gating が無いので
    # servicer 層で拒否する。re-subscribe は cap 計算から外す。
    _MAX_LIVE_SUBSCRIPTIONS = 50

    def subscribe_market_data(self, instrument_id: str) -> CommandAck:
        # Live runner 未起動 (Replay モード等) は precondition reject
        if self._session is None:
            return CommandAck(success=False, error_code="EXECUTION_MODE_PRECONDITION")
        # 50 銘柄 cap: 新規 instrument のみカウント (re-subscribe は no-op)
        try:
            already = self._session.runner.subscribed_ids()
        except Exception:
            already = set()
        if (
            instrument_id not in already
            and len(already) >= self._MAX_LIVE_SUBSCRIPTIONS
        ):
            return CommandAck(success=False, error_code="SUBSCRIPTION_LIMIT_EXCEEDED")
        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                self._session.runner.subscribe(instrument_id), loop
            )
            future.result(timeout=self._live_timeout_s)
        except Exception as exc:
            logging.exception("subscribe_market_data failed: %s", exc)
            return CommandAck(success=False, error_code="SUBSCRIBE_FAILED")
        # #115: market-data 購読が成立したら badge を CONNECTED→SUBSCRIBED へ進める。
        # FSM 上 CONNECTED→SUBSCRIBED は合法・`_LIVE_OK_VENUE_STATES` は両方を許容するため
        # 副作用なし。既に SUBSCRIBED（re-subscribe）なら no-op。
        if self.venue_sm is not None and self.venue_sm.current == "CONNECTED":
            self.venue_sm.transition_to("SUBSCRIBED")
        return CommandAck(success=True, error_code="")

    def unsubscribe_market_data(self, instrument_id: str) -> CommandAck:
        # Live runner 未起動 (Replay モード等) は precondition reject
        if self._session is None:
            return CommandAck(success=False, error_code="EXECUTION_MODE_PRECONDITION")
        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                self._session.runner.unsubscribe(instrument_id), loop
            )
            future.result(timeout=self._live_timeout_s)
        except Exception as exc:
            logging.exception("unsubscribe_market_data failed: %s", exc)
            return CommandAck(success=False, error_code="UNSUBSCRIBE_FAILED")
        # D20: remove from price + depth caches to prevent stale data on re-add
        if self._session is not None:
            if self._session.price_cache is not None:
                self._session.price_cache.remove(instrument_id)
            if self._session.depth_cache is not None:
                self._session.depth_cache.remove(instrument_id)
        # A0: drop reducer per-id state so the symbol stops surfacing in per_instrument
        self._engine.forget_instrument(instrument_id)
        return CommandAck(success=True, error_code="")

    def force_account_snapshot(self) -> CommandAck:
        """issue #29 Slice 2': ExecutionModeChanged→Live reset 直後に Rust が呼ぶ。
        AccountSync.force_resync() を live loop で 1 発回し、dedup を貫通して
        AccountEvent を既存 backend event stream に再 push させる（BP/Positions refill）。
        snapshot 自体はインライン返却しない（既存 stream 経由で戻る）。
        """
        # TOCTOU 回避: 別 thread がセッションを差し替えても、local キャプチャした acct を一貫使用する。
        acct = self._session.account_sync if self._session is not None else None
        if acct is None:
            return CommandAck(
                success=False,
                error_code="VENUE_LOGIN_REQUIRED",
            )
        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                acct.force_resync(), loop
            )
            emitted = future.result(timeout=self._live_timeout_s)
        except Exception as exc:
            logging.exception("force_account_snapshot failed: %s", exc)
            return CommandAck(
                success=False,
                error_code="FORCE_RESYNC_FAILED",
            )
        if not emitted:
            # fetch 失敗・callback 失敗のいずれかで AccountEvent を再 push できなかった。
            # どちらの経路も AccountSync が on_error を呼ぶので BackendError は既に publish
            # 済み（issue #29 review残: callback 失敗も on_error で surface するよう修正）。
            # RPC は success=True を返してはならない。
            return CommandAck(
                success=False,
                error_code="FORCE_RESYNC_NO_EMIT",
            )
        return CommandAck(
            success=True,
            error_code="",
        )

    def submit_secret(self, request_id: str, secret: str) -> CommandAck:
        # secret は応答にもログにも残さない。
        try:
            self._secret_vault.submit(request_id, secret)
        except KeyError:
            logging.warning("submit_secret: unknown request_id=%s", request_id)
            return CommandAck(success=False, error_code="UNKNOWN_REQUEST_ID")
        return CommandAck(success=True, error_code="")

    # === Phase 9 Step 2: manual order execution facade ===

    @staticmethod
    def _order_event_to_typed(ev, strategy_id: str = "") -> "backend_events.OrderEvent":
        """ADR-0018 A2: OrderEventData → typed OrderEvent (8 sink-wire fields).

        strategy_id を付与して event sink 経由で publish する。
        8 field のみを載せる。symbol/side/qty/price は意図的に落とす（Rust OrderEvent に
        該当 field なし。それらは get_orders seed 用に place 時の OrderEventData が温存）。
        """
        return backend_events.OrderEvent(
            order_id=ev.order_id,
            venue_order_id=ev.venue_order_id,
            client_order_id=ev.client_order_id,
            status=ev.status,
            filled_qty=ev.filled_qty,
            avg_price=ev.avg_price,
            ts_ms=ev.ts_ms,
            strategy_id=strategy_id,
        )

    def _is_live_ordering_mode(self) -> bool:
        """Write order RPCs are allowed only in Live modes (Replay is rejected)."""
        mode = self.mode_manager.current_mode if self.mode_manager else "Replay"
        return mode in ("LiveManual", "LiveAuto")

    def _publish_account_snapshot(self, snapshot) -> None:
        """AccountSync callback: AccountSnapshot → proto AccountEvent → backend stream.

        Runs on the live-loop thread. The transport-agnostic snapshot has no ts_ms;
        stamp it here (push time). BackendEventStream is threadsafe (Step 0).

        issue #39 Slice 2: Replay 中の抑止は AccountSync._tick 入口の mode_provider gate
        （案A+Y）が担う。callback に到達した時点で既に emit 確定なので、ここでは無条件に
        push する（dedup の last_emitted を汚さない）。"""
        self._emit(
            backend_events.AccountEvent(
                cash=snapshot.cash,
                buying_power=snapshot.buying_power,
                positions=snapshot.positions,
                ts_ms=int(time.time() * 1000),
            )
        )
        # Phase 10 §2.4: post-trade max_daily_loss を口座スナップショット毎に評価する。
        self._evaluate_post_trade_loss(snapshot)

    def _publish_account_sync_error(self, record) -> None:
        """AccountSync on_error callback: LiveErrorRecord → BackendError → backend stream.

        Runs on the live-loop thread (fetch_account 失敗時)。issue #29 D2 / A 経路:
        握り潰さず UI トースト用に surface する。BackendEventStream は threadsafe (Step 0)。"""
        self._emit(
            backend_events.BackendError(
                source=record.source,
                detail=record.detail,
                ts_ms=int(time.time() * 1000),
            )
        )

    def _publish_secret_required(self, request_id, venue, kind, purpose) -> None:
        """SecondSecretResolver callback: SecretRequired を UI に push する。

        adapter の発注呼び出し (live-loop thread) から呼ばれる。BackendEventStream は
        threadsafe (Step 0)。secret 値そのものは載せない (request_id のみ)。"""
        self._emit(
            backend_events.SecretRequired(
                request_id=request_id, venue=venue, kind=kind, purpose=purpose,
            )
        )

    def _publish_venue_logout(self, venue: str) -> None:
        """Watchdog / Tachibana SS callback: venue 本体ログアウトを UI に push する (§3.5)。

        kabu は VenueHealthWatchdog (poll), Tachibana は EVENT WS の SS=閉局フレームから
        呼ばれる (どちらも live-loop thread)。UI は VenueLogoutDetected を受けて再ログイン
        modal を開く。BackendEventStream は threadsafe (Step 0)。"""
        self._emit(
            backend_events.VenueLogoutDetected(venue=venue)
        )

    def _publish_order_event(self, ev) -> None:
        """adapter on_order_event callback: EC 由来 OrderEventData を push する。

        EC WS タスク (live-loop thread) から呼ばれる。proto 変換は既存ヘルパを再利用。
        ⚠️ 共有 adapter の EC stream は manual / auto どちらの注文か区別できないため
        `strategy_id` は **空のまま**にする（§2.9）。unary 応答（MANUAL-001）や kernel
        bridge（LIVE-{run}）が先にタグした行を、UI の「非空が勝つ」マージ規則のもとで
        空イベントが上書きしないようにする。"""
        self._emit(self._order_event_to_typed(ev))
        if ev.status in _ACCOUNT_REFETCH_STATUSES:
            account_sync = self._session.account_sync if self._session is not None else None
            if account_sync is not None and self._live_loop is not None and self._live_loop.is_running():
                asyncio.ensure_future(account_sync.force_resync())

    def place_order(
        self,
        venue: str,
        instrument_id: str,
        side: str,
        qty: float,
        price: Optional[float],
        order_type: str,
        time_in_force: str,
        second_secret: Optional[str],
        idempotency_key: Optional[str] = None,
    ) -> "OrderCommandResult":
        # Replay (or no mode_manager) is structurally rejected — never reaches venue.
        if not self._is_live_ordering_mode():
            return OrderCommandResult(
                success=False, error_code="EXECUTION_MODE_PRECONDITION"
            )
        # Snapshot once: a concurrent SetExecutionMode→Replay teardown nulls
        # self._session, so re-reading the attribute below would race
        # (TOCTOU → AttributeError). Bind the live reference here.
        facade = self._session.order_facade if self._session is not None else None
        if facade is None:
            return OrderCommandResult(
                success=False, error_code="VENUE_LOGIN_REQUIRED"
            )

        # second_secret は facade に渡すが Step 2 では無視される（Step 5 で結線）。
        # ここでログに出さない（平文 secret の漏洩面を最小化）。
        # idempotency_key はクライアント供給（確認モーダルが Confirm ごとに採番）。二重発注
        # 防止 (ADR D2) のため facade に渡す。
        # buying_power は backend が供給する（クライアントは送らない）: AccountSync の最新
        # スナップショットの余力を読み、LIMIT の余力超過を pre-trade で弾く。snapshot 無し
        # （未ログイン直後など）は None のまま = 従来どおりチェックしない。MARKET は約定価格
        # 未確定なので facade 側が effective_price=None でスキップする。
        buying_power = None
        acct = self._session.account_sync if self._session is not None else None
        if acct is not None and acct.last_snapshot is not None:
            buying_power = acct.last_snapshot.buying_power

        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                facade.place(
                    venue=venue,
                    instrument_id=instrument_id,
                    side=side,
                    qty=qty,
                    order_type=order_type,
                    time_in_force=time_in_force,
                    price=price,
                    second_secret=second_secret,
                    buying_power=buying_power,
                    idempotency_key=idempotency_key,
                ),
                loop,
            )
            event = future.result(timeout=self._order_timeout_s)
        except OrderFacadeError as exc:
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except SecretTimeoutError as exc:
            # 第二暗証番号の入力が来なかった (Tachibana)。注文は未送信。
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except futures.TimeoutError:
            # 注文は venue 側で成立している可能性がある（reconcile は Step 8）。
            logging.warning("place_order timed out after %ss", self._order_timeout_s)
            return OrderCommandResult(success=False, error_code="PLACE_TIMEOUT")
        except Exception as exc:
            logging.exception("place_order failed: %s", exc)
            return OrderCommandResult(success=False, error_code="PLACE_FAILED")

        # 手動発注はこの unary 経路でのみ MANUAL-001 を確定タグできる（共有 adapter の
        # EC stream は strategy_id 空、§2.9）。
        self._emit(
            self._order_event_to_typed(event, strategy_id=MANUAL_STRATEGY_ID)
        )
        return OrderCommandResult(
            success=True, error_code="", order_event=event, strategy_id=MANUAL_STRATEGY_ID
        )

    def cancel_order(
        self,
        venue: str,
        order_id: str,
        second_secret: Optional[str],
    ) -> "OrderCommandResult":
        if not self._is_live_ordering_mode():
            return OrderCommandResult(
                success=False, error_code="EXECUTION_MODE_PRECONDITION"
            )
        # Snapshot once (see place_order): guard against concurrent teardown race.
        facade = self._session.order_facade if self._session is not None else None
        if facade is None:
            return OrderCommandResult(
                success=False, error_code="VENUE_LOGIN_REQUIRED"
            )

        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                facade.cancel(
                    venue=venue,
                    order_id=order_id,
                    second_secret=second_secret,
                ),
                loop,
            )
            event = future.result(timeout=self._order_timeout_s)
        except OrderFacadeError as exc:
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except SecretTimeoutError as exc:
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except futures.TimeoutError:
            logging.warning("cancel_order timed out after %ss", self._order_timeout_s)
            return OrderCommandResult(success=False, error_code="CANCEL_TIMEOUT")
        except Exception as exc:
            logging.exception("cancel_order failed: %s", exc)
            return OrderCommandResult(success=False, error_code="CANCEL_FAILED")

        self._emit(
            self._order_event_to_typed(event, strategy_id=MANUAL_STRATEGY_ID)
        )
        return OrderCommandResult(
            success=True, error_code="", order_event=event, strategy_id=MANUAL_STRATEGY_ID
        )

    def modify_order(
        self,
        venue: str,
        order_id: str,
        new_price: Optional[float],
        new_qty: Optional[float],
        second_secret: Optional[str],
    ) -> "OrderCommandResult":
        if not self._is_live_ordering_mode():
            return OrderCommandResult(
                success=False, error_code="EXECUTION_MODE_PRECONDITION"
            )
        # Snapshot once (see place_order): guard against concurrent teardown race.
        facade = self._session.order_facade if self._session is not None else None
        if facade is None:
            return OrderCommandResult(
                success=False, error_code="VENUE_LOGIN_REQUIRED"
            )

        loop = self._ensure_live_loop()
        try:
            future = asyncio.run_coroutine_threadsafe(
                facade.modify(
                    venue=venue,
                    order_id=order_id,
                    new_price=new_price,
                    new_qty=new_qty,
                    second_secret=second_secret,
                ),
                loop,
            )
            event = future.result(timeout=self._order_timeout_s)
        except OrderFacadeError as exc:
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except SecretTimeoutError as exc:
            return OrderCommandResult(success=False, error_code=exc.error_code)
        except futures.TimeoutError:
            logging.warning("modify_order timed out after %ss", self._order_timeout_s)
            return OrderCommandResult(success=False, error_code="MODIFY_TIMEOUT")
        except Exception as exc:
            logging.exception("modify_order failed: %s", exc)
            return OrderCommandResult(success=False, error_code="MODIFY_FAILED")

        self._emit(
            self._order_event_to_typed(event, strategy_id=MANUAL_STRATEGY_ID)
        )
        return OrderCommandResult(
            success=True, error_code="", order_event=event, strategy_id=MANUAL_STRATEGY_ID
        )

    def get_orders(self, venue: str) -> "OrdersResult":
        # 読み取り系: Replay でも reject しない（§3.2）。§3.8 reconcile primitive。
        # Snapshot once: a concurrent teardown nulling self._session between
        # the check and list_orders() would otherwise return a clean NO_LIVE_SESSION
        # instead of raising AttributeError mid-flight.
        facade = self._session.order_facade if self._session is not None else None
        if facade is None:
            return OrdersResult(success=False, error_code="NO_LIVE_SESSION", orders=[])

        facade_orders = facade.list_orders()

        # Slice 3b: venue 側の working-orders を取得してマージする。
        # facade 側に既知の venue_order_id はスキップ（facade が正（client_order_id あり））。
        venue_orders = []
        error_code = ""
        adapter = self._live_adapter()
        if adapter is not None and hasattr(adapter, "fetch_working_orders"):
            loop = self._ensure_live_loop()
            try:
                future = asyncio.run_coroutine_threadsafe(
                    adapter.fetch_working_orders(), loop
                )
                venue_orders = future.result(timeout=self._live_timeout_s)
            except futures.TimeoutError:
                logging.warning(
                    "get_orders: fetch_working_orders timed out after %ss",
                    self._live_timeout_s,
                )
                error_code = "VENUE_ORDERS_TIMEOUT"
            except Exception as exc:
                logging.warning("get_orders: fetch_working_orders failed: %s", exc)
                error_code = "VENUE_ORDERS_FETCH_FAILED"

        known_venue_ids = {o.venue_order_id for o in facade_orders if o.venue_order_id}
        # Issue #236 Plan B: backend-side static-attr merge. Enrich venue_orders
        # whose client_order_id matches a facade order so that Rust's seed_working
        # gap-fill becomes a safety net rather than the primary mechanism.
        merged = list(facade_orders)
        for vo in venue_orders:
            if vo.venue_order_id not in known_venue_ids:
                # #236: facade.get_intent() で OrderIntent を取得し fill_static_attrs に渡す。
                # EC-stream 由来など intent が無い場合は None → merge スキップ。
                intent = facade.get_intent(vo.client_order_id) if vo.client_order_id else None
                if intent is not None:
                    vo = fill_static_attrs(vo, intent)
                merged.append(vo)

        return OrdersResult(
            success=True,
            error_code=error_code,
            orders=merged,
            strategy_id=MANUAL_STRATEGY_ID,
        )

    # === Phase 10 Step 3: live strategy execution ===

    def _live_session_view(self):
        """`LiveStrategyHost` の session_provider。既存 live session を共有借用する。

        `_order_facade` の存在を logged-in の根拠にする（place_order の
        VENUE_LOGIN_REQUIRED 判定と同基準）。未ログインなら None を返し、host は
        VENUE_LOGIN_REQUIRED で reject する（新規 login はしない、§1.1）。
        """
        if self._session is None:
            return None
        return _LiveSessionView(is_logged_in=True)

    def _publish_live_strategy_event(self, record) -> None:
        """run の lifecycle 遷移を LiveStrategyEvent として UI に push する (§1.3 / M8)。"""
        self._emit(
            backend_events.LiveStrategyEvent(
                run_id=record.run_id,
                strategy_id=record.strategy_id,
                status=record.state_machine.current,
                ts_ms=int(time.time() * 1000),
            )
        )

    def _on_auto_order_event(self, ev, strategy_id: str) -> None:
        """controller の on_order_event callback: auto 戦略の OrderEvent を UI へ push (Step 7 C)。

        controller は kernel msgbus の order event を受け、UI 互換 `OrderEventData` と
        当該 run の `strategy_id`（"LIVE-{run}"）を渡す。ここで proto に詰めて
        `strategy_id` 付きで push する（run_id は不要、OrderEvent は strategy_id だけ運ぶ）。

        ⚠️ live loop thread から呼ばれる。`_live_strategy_lock` は取らない
        （Step 4 不変条件、自己デッドロック回避）。BackendEventStream は threadsafe。"""
        self._emit(
            self._order_event_to_typed(ev, strategy_id=strategy_id)
        )
        # #25 finding 4: kernel の同期 OrderResult fill 後は venue 口座を再 fetch して、UI の権威 position
        # 表示と post-trade（max_daily_loss）評価を即時発火させる。adapter EC 経路（_publish_order_event）と
        # 同じ refetch を auto 経路にも掛ける（mock は EC stream を出さないので、これが唯一の促進点）。
        if ev.status in _ACCOUNT_REFETCH_STATUSES:
            account_sync = self._session.account_sync if self._session is not None else None
            if account_sync is not None and self._live_loop is not None and self._live_loop.is_running():
                asyncio.ensure_future(account_sync.force_resync())

    def _on_auto_telemetry(self, strategy_id: str, metrics: dict) -> None:
        """controller の on_telemetry callback: run 別 telemetry を UI へ push (Step 7 D)。

        `strategy_id`（= nautilus_strategy_id）を RunRegistry の逆引きで run_id に解決し、
        `LiveStrategyTelemetry` を push する。逆引きできない（既に detach 済み等）場合は
        skip する（terminal run の遅延イベントを誤って report しない）。

        ⚠️ live loop thread から呼ばれる。`_live_strategy_lock` は取らない（RunRegistry は
        内部 lock で自衛する。Step 4 不変条件）。"""
        run_id = self._run_registry.run_id_for_nautilus_strategy(strategy_id)
        if not run_id:
            return
        self._emit(
            backend_events.LiveStrategyTelemetry(
                run_id=run_id,
                strategy_id=strategy_id,
                realized_pnl=metrics["realized_pnl"],
                unrealized_pnl=metrics["unrealized_pnl"],
                order_count=metrics["order_count"],
                fill_count=metrics["fill_count"],
                ts_ms=int(time.time() * 1000),
            )
        )

    def _on_auto_strategy_log(self, record, strategy_id: str) -> None:
        """controller の on_strategy_log callback: 戦略の UI ログ行を push する (§570)。

        `strategy_id`（= nautilus_strategy_id）を RunRegistry の逆引きで run_id に解決し
        （telemetry と同方針）、`StrategyLogMessage` を push する。逆引きできない
        （既に detach 済み等）場合は skip する。

        ⚠️ live loop thread から呼ばれる。`_live_strategy_lock` は取らない（§Step4 不変条件）。
        `record` は `engine.live.strategy_log.StrategyLogRecord`（level / message / ts_ns）。"""
        run_id = self._run_registry.run_id_for_nautilus_strategy(strategy_id)
        if not run_id:
            return
        self._emit(
            backend_events.StrategyLogMessage(
                run_id=run_id,
                level=record.level,
                message=record.message,
                ts_ms=record.ts_ns // 1_000_000,
            )
        )

    def _is_run_gated(self, strategy_id: str) -> bool:
        """controller の run_gate_provider: 当該 run が新規発注ゲートを閉じているか (Issue #6)。

        `strategy_id`（= nautilus_strategy_id "LIVE-{run}"）を RunRegistry の逆引きで run_id に
        解決し、state machine が RUNNING でなければ True（= deny）を返す。PAUSED は当然 gate を
        閉じる（§1.2）。逆引きできない（detach 済み等）なら gate しない（False）——遅延注文を
        必要以上に弾かず、teardown 経路の cancel に委ねる。

        ⚠️ exec client の `_submit_order` から live loop thread 上で呼ばれる。RunRegistry は
        内部 lock で自衛するので軽量な dict 引きのみ。`_live_strategy_lock` は取らない
        （§Step4 不変条件、自己デッドロック回避）。"""
        run_id = self._run_registry.run_id_for_nautilus_strategy(strategy_id)
        if not run_id:
            return False
        record = self._run_registry.get(run_id)
        if record is None:
            return False
        return not record.state_machine.is_running

    # ── Safety Rails (§2.4) ──────────────────────────────────────────────────

    def _live_adapter(self):
        """共有 live session の venue adapter（NautilusLiveEngineController に渡す）。"""
        facade = self._session.order_facade if self._session is not None else None
        return getattr(facade, "_adapter", None) if facade is not None else None

    def _live_runner_provider(self):
        """共有 LiveRunner（NautilusLiveEngineController の tick 供給源、Step 8）。

        controller は attach 時にここから runner を借り、tick listener を登録して
        live 約定を Nautilus aggregation へ流す。未起動なら None（tap を張らない）。
        """
        sess = self._session
        return sess.runner if sess is not None else None

    @staticmethod
    def _build_safety_rails(sl) -> SafetyRails:
        """typed dict `safety_limits` → transport 非依存 `SafetyRails`。"""
        sl = sl or {}
        return SafetyRails(
            SafetyLimits(
                max_position_size_jpy=sl.get("max_position_size_jpy", 0),
                max_order_value_jpy=sl.get("max_order_value_jpy", 0),
                max_daily_loss_jpy=sl.get("max_daily_loss_jpy", 0),
                max_orders_per_minute=sl.get("max_orders_per_minute", 0),
                allowed_instruments=tuple(sl.get("allowed_instruments", ()) or ()),
            )
        )

    def _release_run_rails_locked(self, run_id: str) -> None:
        """run の Safety Rails 状態（rails + equity baseline）を対で解放する。

        呼び出し元が `_run_rails_lock` を保持していること。両 dict を必ず一緒に外して
        「rails は消えたが baseline が残る」等の不整合を作らない。
        """
        self._run_rails.pop(run_id, None)
        self._run_equity_baseline.pop(run_id, None)

    def _publish_safety_rail_violation(self, run_id: str, violation: RailViolation) -> None:
        """`SafetyRailViolation` を UI に push する（§2.10 トースト、M8）。"""
        self._emit(
            backend_events.SafetyRailViolation(
                run_id=run_id,
                kind=violation.kind,
                detail=violation.detail,
                ts_ms=int(time.time() * 1000),
            )
        )

    def _on_pretrade_violation(self, violation: RailViolation) -> None:
        """exec client（live loop thread）からの独自 pre-trade 違反 callback。

        単一 run MVP なので active run に紐付けて push する（複数 run は Phase 11）。
        OrderDenied は exec client が既に発行済み（戦略は on_order_denied で受ける）。
        """
        active = self._run_registry.list_active()
        run_id = active[0].run_id if active else ""
        self._publish_safety_rail_violation(run_id, violation)

    def _evaluate_post_trade_loss(self, snapshot) -> None:
        """口座スナップショット毎に active run の max_daily_loss を評価する（post-trade）。

        live loop thread（AccountSync callback）から呼ばれる。違反時の run 停止は
        `fail_run`（controller teardown が同 loop へ blocking round-trip する）を **別スレッド**に
        逃がす（同 loop 上での `future.result()` 自己待ちデッドロックを避ける）。

        ⚠️ ここは live loop thread なので `_live_strategy_lock`（teardown 中に保持される）は
        **絶対に取らない**。rails dict 専用の `_run_rails_lock`（blocking round-trip を伴わない）
        だけを使う。
        """
        with self._run_rails_lock:
            active = self._run_registry.list_active()
            if not active:
                return
            record = active[0]  # 単一 run MVP（§0.7）
            rails = self._run_rails.get(record.run_id)
            if rails is None:
                return
            baseline = self._run_equity_baseline.get(record.run_id)
            if baseline is None:
                self._run_equity_baseline[record.run_id] = equity_from_snapshot(snapshot)
                return
            violation = evaluate_post_trade(
                snapshot=snapshot,
                rails=rails,
                baseline_equity=baseline,
            )
            if violation is None:
                return
            # 二重発火防止: 失敗確定でこの run の rails を外す（後続 snapshot はスキップ）。
            self._release_run_rails_locked(record.run_id)
            run_id = record.run_id

        threading.Thread(
            target=self._fail_run_for_loss,
            args=(run_id, violation),
            name="phase10-daily-loss-stop",
            daemon=True,
        ).start()

    def _fail_run_for_loss(self, run_id: str, violation: RailViolation) -> None:
        """worker thread: run を ERROR→STOPPED に落とし、違反 + 状態遷移を push する。"""
        try:
            with self._live_strategy_lock:
                record = self._strategy_host.fail_run(run_id, "MAX_DAILY_LOSS_EXCEEDED")
        except LiveStrategyHostError:
            return
        self._publish_safety_rail_violation(run_id, violation)
        self._publish_live_strategy_event(record)

    def _on_auto_strategy_error(self, exc: BaseException) -> None:
        """controller（live loop thread）からの走行中戦略例外 callback（#25 finding 5）。

        戦略の on_bar/on_tick/on_order が例外を投げたら run を握り潰さず fail させる。単一 run MVP
        なので active run に紐付ける。fail_run（controller.detach が同 loop へ blocking round-trip する）は
        **別スレッド**に逃がす（live loop 上での自己待ちデッドロック回避・_evaluate_post_trade_loss と同方針）。
        """
        active = self._run_registry.list_active()
        if not active:
            return
        run_id = active[0].run_id
        threading.Thread(
            target=self._fail_run_for_strategy_error,
            args=(run_id,),
            name="phase10-strategy-error-stop",
            daemon=True,
        ).start()

    def _fail_run_for_strategy_error(self, run_id: str) -> None:
        """worker thread: 戦略例外で run を ERROR→STOPPED に落とし、状態遷移を push する。"""
        try:
            with self._live_strategy_lock:
                record = self._strategy_host.fail_run(run_id, "STRATEGY_EXCEPTION")
        except LiveStrategyHostError:
            return
        self._publish_live_strategy_event(record)

    def register_live_strategy(self, strategy_file: str, expected_sha256: str = "", request_id: str = "", original_path: str = ""):
        # 検証系: saved .py をロードして strategy_id を発行する（mode gate なし、§2.5）。
        try:
            handle = self._strategy_registry.register(strategy_file, expected_sha256, original_path=original_path)
        except StrategyRegistryError as exc:
            return LiveStrategyRegisterResult(
                success=False,
                request_id=request_id,
                error_code=exc.error_code,
                error_message=str(exc.__cause__) if exc.__cause__ else str(exc),
            )
        return LiveStrategyRegisterResult(
            success=True,
            request_id=request_id,
            error_code="",
            strategy_id=handle.strategy_id,
        )

    def start_live_strategy(
        self,
        strategy_id: str,
        instrument_id: str,
        venue: str,
        safety_limits_dict: dict | None = None,
        params: dict | None = None,
        request_id: str = "",
    ):
        # write 系の precondition: ExecutionMode == LiveAuto（構造的 reject、§2.5）。
        mode = self.mode_manager.current_mode if self.mode_manager else "Replay"
        if mode != "LiveAuto":
            return LiveStrategyStartResult(
                success=False,
                request_id=request_id,
                error_code="EXECUTION_MODE_PRECONDITION",
            )
        # strategy_id（検証済みハンドル）を解決。生パスは受け取らない（M9）。
        try:
            handle = self._strategy_registry.resolve(strategy_id)
        except StrategyRegistryError as exc:
            return LiveStrategyStartResult(
                success=False,
                request_id=request_id,
                error_code=exc.error_code,
                error_message=str(exc.__cause__) if exc.__cause__ else str(exc),
            )
        # instrument_id を **kernel 構築前** に well-formed 検証する。malformed（venue
        # サフィックス欠落など）だと controller.attach の検証が kernel driver を live loop に
        # 撒いた **後** に落ち、STRATEGY_ATTACH_FAILED という不透明なエラーになる。controller と
        # 同じ `is_valid_instrument_id` をここで使い、許容条件を attach の検証と完全一致させたまま
        # 明示エラーに前倒しする。
        #
        # 注意（設計確認、Step 9 検証）: instrument の venue サフィックス（例 `.TSE`）が
        # live session の `venue_id`（例 `TACHIBANA` / `MOCK`）と一致する必要は **無い**。
        # 実 adapter（`exchanges/tachibana.py`）は購読した InstrumentId をそのまま
        # `TradesUpdate.instrument_id` に echo するため、instrument_id は
        # subscribe → tick filter → exec の間で内部一貫していれば良く（`venue_id` は
        # 別メタデータ）、現に mock 経路は `7203.TSE` + `MOCK` セッションで動作する。
        # よって venue 一致を強制する guard は **入れない**（設計と既存テストに反する）。
        # #25: 純 Python validator（nautilus InstrumentId.from_str は Rust core をロードし
        # LiveAuto import-purity gate を破る・D5/D8）。SYMBOL.VENUE の well-formedness を
        # kernel controller.attach の検証と同条件で前倒しチェックする。
        from engine.kernel.instrument_id import is_valid_instrument_id

        if not is_valid_instrument_id(instrument_id):
            return LiveStrategyStartResult(
                success=False,
                request_id=request_id,
                error_code="INVALID_INSTRUMENT_ID",
            )
        # Safety Rails（§2.4）: proto SafetyLimits → SafetyRails。ネイティブ rail は
        # controller が LiveRiskEngineConfig に、独自 rail は exec client の pre-trade に渡す。
        rails = self._build_safety_rails(safety_limits_dict)
        start_params = StartParams(
            strategy_id=handle.strategy_id,
            strategy_file=handle.resolved_path,
            instrument_id=instrument_id,
            venue=venue,
            params=dict(params or {}),
            original_path=handle.original_path,
            safety_rails=rails,
        )
        with self._live_strategy_lock:
            try:
                record = self._strategy_host.start_run(start_params)
            except LiveStrategyHostError as exc:
                return LiveStrategyStartResult(
                    success=False,
                    request_id=request_id,
                    error_code=exc.error_code,
                    error_message=str(exc.__cause__) if exc.__cause__ else str(exc),
                )
        # post-trade（max_daily_loss）評価用に run の rails を記録する。
        # rails dict は live loop の評価 callback と共有するので専用 lock で囲う
        # （_live_strategy_lock を live loop に晒さないため、外で取得する）。
        # #25 D7: post-trade baseline を run 開始時に **即時確立** する。旧実装は baseline を消去して
        # 次 snapshot で初設定していたため、最初の次 snapshot が fill 後だと初回損失を見逃した。
        # account_sync.last_snapshot を優先するが、初回 fetch が Replay mode で抑止されている等で None の
        # ときは **明示 fetch_account** で確定する（D7「無ければ controller が明示 fetch」）。round-trip は
        # _run_rails_lock の外で行う（live loop callback が同 lock を取るため、保持したまま待たない）。
        sess = self._session
        acct = sess.account_sync if sess is not None else None
        baseline_snapshot = acct.last_snapshot if acct is not None else None
        if baseline_snapshot is None:
            adapter = self._live_adapter()
            if adapter is not None:
                loop = self._ensure_live_loop()
                try:
                    baseline_snapshot = asyncio.run_coroutine_threadsafe(
                        adapter.fetch_account(), loop
                    ).result(timeout=self._live_timeout_s)
                except Exception:
                    logging.warning(
                        "start_live_strategy: baseline fetch_account failed", exc_info=True
                    )
                    baseline_snapshot = None
        with self._run_rails_lock:
            self._run_rails[record.run_id] = rails
            if baseline_snapshot is not None:
                self._run_equity_baseline[record.run_id] = equity_from_snapshot(baseline_snapshot)
            else:
                self._run_equity_baseline.pop(record.run_id, None)
        self._publish_live_strategy_event(record)
        return LiveStrategyStartResult(
            success=True,
            request_id=request_id,
            error_code="",
            run_id=record.run_id,
        )

    def _control_run(self, run_id, op) -> CommandAck:
        """Pause/Resume/Stop の共通骨子。run 存在チェック + host 呼び出し + event push。"""
        with self._live_strategy_lock:
            try:
                record = op(run_id)
            except LiveStrategyHostError as exc:
                return CommandAck(success=False, error_code=exc.error_code)
            terminal = record.state_machine.is_terminal
        if terminal:
            with self._run_rails_lock:
                self._release_run_rails_locked(record.run_id)
        self._publish_live_strategy_event(record)
        return CommandAck(success=True, error_code="")

    def stop_live_strategy(self, run_id: str) -> CommandAck:
        # graceful 停止は mode に依らず常に許可する（runaway を止められないと困る）。
        return self._control_run(run_id, self._strategy_host.stop_run)

    def pause_live_strategy(self, run_id: str) -> CommandAck:
        return self._control_run(run_id, self._strategy_host.pause_run)

    def resume_live_strategy(self, run_id: str) -> CommandAck:
        return self._control_run(run_id, self._strategy_host.resume_run)


