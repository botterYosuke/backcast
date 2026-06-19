import json
import logging
import os
import re
import sys
import asyncio
import tempfile
import threading
import time
from concurrent import futures
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from .live.adapter import OrderingVenueAdapter

# gRPC transport removed — InProc only

from .core import DataEngine
from .live._build_mode import IS_DEBUG_BUILD
from .live.live_adapter_factory import build_live_adapter_factory
from .live.live_runner import LiveRunner
from .live.reducer_bridge import LiveReducerBridge
from .live.last_price_cache import LastPriceCache
from .live.depth_cache import DepthCache
from .live.state_machine import VenueStateMachine
from .live.backend_event_bus import BackendEventStream
from .live import backend_events
from .live.secret_vault import SecretVault
from .live.secret_provider import SecondSecretResolver, SecretTimeoutError
from .live.order_facade import ManualOrderFacade, OrderFacadeError
from .live.order_types import OrderEventData
from .live.account_sync import AccountSync
from .live.run_registry import RunRegistry
from .live.strategy_registry import StrategyRegistry, StrategyRegistryError
from .live.strategy_host import LiveStrategyHost, LiveStrategyHostError, StartParams
from .live.safety_rails import RailViolation, SafetyLimits, SafetyRails
from .live.health_watchdog import VenueHealthWatchdog
from .live.instruments_scheduler import InstrumentsScheduler
from .live import instruments_store
from .mode_manager import ModeManager
from .models import PerInstrumentState
from dataclasses import dataclass, field


from .replay import BaseReplayProvider
from .jquants_loader import JQuantsLoader
from .paths import PYTHON_SRC_ROOT, listed_symbols_artifact_path
# normalize_granularity は nautilus 非依存の kernel 側（DuckDB 直読み reader）から取得。
from engine.kernel.duckdb_bars import normalize_granularity


@dataclass(frozen=True)
class CommandAck:
    """proto を持たない最小 command 応答（success / error_code のみ）。"""
    success: bool
    error_code: str = ""


@dataclass(frozen=True)
class OrderCommandResult:
    """proto を持たない order RPC 応答（place/cancel/modify 共有）。
    order_event は facade 由来の OrderEventData（transport 非依存ドメイン値）。
    strategy_id は §2.9 のタグ規則で handler が確定する transport 属性。"""
    success: bool
    error_code: str = ""
    order_event: OrderEventData | None = None
    strategy_id: str = ""


@dataclass(frozen=True)
class OrdersResult:
    """proto を持たない get_orders 応答。orders は OrderEventData の list
    （transport 非依存ドメイン値）。strategy_id は handler が確定するタグ規則値。"""
    success: bool
    error_code: str = ""
    orders: list[OrderEventData] = field(default_factory=list)
    strategy_id: str = ""


@dataclass(frozen=True)
class VenueSessionResult:
    """proto を持たない venue login 応答（session 確立の役割名）。"""
    success: bool
    error_code: str = ""
    venue_state: str = ""
    instruments_loaded: int = 0


@dataclass(frozen=True)
class BacktestRunResult:
    """proto を持たない strategy backtest run 応答（同期 start_engine 用）。"""
    success: bool
    error_code: str = ""
    error_message: str = ""
    run_id: str = ""
    summary_json: str = ""


@dataclass(frozen=True)
class InstrumentInfo:
    """proto を持たない instrument 1 件（id/name/market のドメイン値）。"""
    id: str
    name: str
    market: str


@dataclass(frozen=True)
class InstrumentListResult:
    """proto を持たない instrument 一覧応答（list/live 共有の役割名）。
    error_message は edge で error_code dict key に写す（既存契約踏襲）。"""
    success: bool
    error_message: str = ""
    instrument_ids: list[str] = field(default_factory=list)
    instruments: list[InstrumentInfo] = field(default_factory=list)


@dataclass(frozen=True)
class ListedSymbolsResult:
    """proto を持たない上場銘柄スキャン応答（resolved_end_date を含む役割名）。"""
    success: bool
    error_message: str = ""
    instrument_ids: list[str] = field(default_factory=list)
    resolved_end_date: str = ""


@dataclass(frozen=True)
class LiveStrategyRegisterResult:
    """proto を持たない live strategy 登録応答（検証系・strategy_id 発行の役割名）。"""
    success: bool
    error_code: str = ""
    error_message: str = ""
    request_id: str = ""
    strategy_id: str = ""


@dataclass(frozen=True)
class LiveStrategyStartResult:
    """proto を持たない live strategy 起動応答（run 採番の役割名）。"""
    success: bool
    error_code: str = ""
    error_message: str = ""
    request_id: str = ""
    run_id: str = ""


@dataclass(frozen=True)
class PortfolioPositionInfo:
    """proto を持たない建玉 1 件（symbol/qty/avg_price/unrealized_pnl）。"""
    symbol: str
    qty: int
    avg_price: float
    unrealized_pnl: float


@dataclass(frozen=True)
class PortfolioOrderInfo:
    """proto を持たない注文 1 件（symbol/side/qty/price/status/ts_ms）。"""
    symbol: str
    side: str
    qty: float
    price: float
    status: str
    ts_ms: int


@dataclass(frozen=True)
class PortfolioResult:
    """proto を持たない口座スナップショット応答（buying_power/cash/equity + positions/orders）。

    #65: realized_pnl/unrealized_pnl は RunResult running-view（走行中）の pn: / unrlz: セル用。
    Python 権威（realized は cost 履歴が要り C# 側導出不能）。完了後の確定 snapshot
    (compute_portfolio) はこれらを持たないので 0.0 既定にフォールバックする。
    """
    success: bool
    buying_power: float = 0.0
    cash: float = 0.0
    equity: float = 0.0
    positions: list[PortfolioPositionInfo] = field(default_factory=list)
    orders: list[PortfolioOrderInfo] = field(default_factory=list)
    realized_pnl: float = 0.0
    unrealized_pnl: float = 0.0


# Per-bar wallclock throttle for both production Replay runners (legacy catalog + DuckDB
# kernel): releases the GIL between bars so the poll thread reads the bar-by-bar chart
# (#29). Kept in one place so the two paths animate at the same cadence.
_REPLAY_BAR_INTERVAL_SEC = 0.01


# Phase 10 §2.9 / M6: 発注主体を示す OrderEvent.strategy_id のタグ規則。
# - 手動発注（ManualOrderFacade 由来の unary 応答）→ "MANUAL-001"。
# - 自動発注（auto 戦略の kernel bridge 由来）→ "LIVE-{run[:8]}"（host が採番）。
# - 共有 adapter の EC stream（_publish_order_event）→ "" のまま（どちらの注文か区別
#   できないため。UI 側のマージ規則「非空が勝つ・空は既知値を消さない」に委ねる）。
MANUAL_STRATEGY_ID = "MANUAL-001"

# Statuses that change BP or Positions; EC stream events with these trigger
# an immediate account_sync.force_resync() so the panel reflects the new state
# without waiting for the next 30s poll (#29 Slice 4).
_ACCOUNT_REFETCH_STATUSES: frozenset[str] = frozenset(
    {"ACCEPTED", "PARTIALLY_FILLED", "FILLED", "CANCELED", "EXPIRED"}
)


def _resolve_python_executable() -> str:
    """Return the Python interpreter path for subprocess spawn.

    In PyO3 in-proc mode sys.executable is the host exe (e.g. backcast.exe),
    not a Python interpreter. Spawning it with -m would launch another Bevy
    app instead of the dialog. Detect this case and fall back to a real Python.

    Resolution order:
      1. TTWR_PYTHON_BIN env var (set by run_inproc.ps1 via _pyenv.ps1)
      2. sys.executable if it looks like a Python interpreter
      3. Scripts/python.exe or bin/python relative to sys.base_prefix
      4. python.exe in the install ROOT (base_prefix / prefix) — uv layout
    """
    env_override = os.environ.get("TTWR_PYTHON_BIN")
    if env_override and os.path.isfile(env_override):
        return env_override
    exe = sys.executable
    if os.path.basename(exe).lower().startswith("python"):
        return exe
    # In-proc mode: sys.executable is the host exe. Find real Python via base_prefix.
    for script_dir in ("Scripts", "bin"):
        for name in ("python.exe", "python3.exe", "python"):
            candidate = os.path.join(sys.base_prefix, script_dir, name)
            if os.path.isfile(candidate):
                return candidate
    # uv-style installs place python.exe in the install ROOT (Scripts/ empty),
    # so probe base_prefix / prefix directly before falling back to the host exe.
    for root in (sys.base_prefix, sys.prefix):
        for name in ("python.exe", "python3.exe", "python"):
            candidate = os.path.join(root, name)
            if os.path.isfile(candidate):
                return candidate
    return exe


def _login_subprocess_env() -> dict[str, str]:
    """Build the environment for the login_dialog_runner subprocess.

    In PyO3 in-proc mode the `engine` package is placed on `sys.path` at runtime
    by the Rust host (`transport.rs`), and that injection does NOT propagate to
    child processes. A bare-inherited env therefore makes
    `python -m engine.live.login_dialog_runner` fail with
    `No module named 'engine'`. The out-of-proc supervisor avoids this by setting
    `PYTHONPATH=<cwd>/python` (`supervisor.rs`); mirror that here so the dialog
    subprocess can import `engine` regardless of how the parent acquired it.
    """
    env = os.environ.copy()
    src_root = str(PYTHON_SRC_ROOT)  # `<repo>/python` — must be importable for `import engine`
    # Propagate the venv site-packages too (#23 Windows HITL fix). Under embedded Python
    # (Unity/pythonnet) `_resolve_python_executable()` returns the BASE CPython — `sys.executable`
    # is the host exe — which lacks the venv's third-party deps (httpx, …). Those live on the
    # embedded interpreter's `sys.path` (host-injected VenvSite) but do NOT propagate to children,
    # so the dialog subprocess crashes with `ModuleNotFoundError: httpx` → LOGIN_SUBPROCESS_CRASHED
    # (tachibana_login_flow → tachibana_auth imports httpx). Mirror the parent's site-packages onto
    # PYTHONPATH so any resolved interpreter can import them. Harmless when the resolved python
    # already owns them (venv-activated / PyO3 in-proc).
    site_dirs = [
        p for p in sys.path if p and ("site-packages" in p or "dist-packages" in p)
    ]
    existing = env.get("PYTHONPATH", "")
    seen: set[str] = set()
    parts: list[str] = []
    for p in [src_root, *site_dirs, *existing.split(os.pathsep)]:
        if p and p not in seen:
            seen.add(p)
            parts.append(p)
    env["PYTHONPATH"] = os.pathsep.join(parts)
    return env


class _LiveSessionView:
    """`LiveStrategyHost` が借用する live session の read-only ビュー (Phase 10 §1.1)。

    既存 Phase 9 live session を共有所有権で借りるための薄いラッパ。host は
    `is_logged_in` だけを見て二重 login しない。`_order_facade` の存在を logged-in の
    根拠にする（place_order の VENUE_LOGIN_REQUIRED 判定と同じ基準）。
    """

    __slots__ = ("is_logged_in",)

    def __init__(self, is_logged_in: bool) -> None:
        self.is_logged_in = is_logged_in


_INSTRUMENT_ID_RE = re.compile(r"^(.+?)-\d+-[A-Z]")


def _artifact_path_for(end_date: str) -> Path:
    return listed_symbols_artifact_path(end_date)


def _read_artifact(end_date: str) -> Optional[list[str]]:
    path = _artifact_path_for(end_date)
    if not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        logging.warning("list_all_listed_symbols: artifact read failed: %s", exc)
        return None
    if not isinstance(data, dict):
        return None
    if data.get("schema_version") != 1:
        return None
    if data.get("end_date") != end_date:
        return None
    ids = data.get("instrument_ids")
    if not isinstance(ids, list) or not all(isinstance(x, str) for x in ids):
        return None
    return ids


def _write_artifact_atomic(end_date: str, instrument_ids: list[str], catalog_path: Optional[str]) -> None:
    path = _artifact_path_for(end_date)
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schema_version": 1,
        "end_date": end_date,
        "source": "nautilus_catalog",
        "catalog_path": str(catalog_path) if catalog_path else "",
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "instrument_ids": instrument_ids,
    }
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def _resolve_date_bounds_from_catalog(catalog_path: str) -> Optional[tuple[str, str]]:
    """Return (oldest_date, latest_date) as 'YYYY-MM-DD' from catalog parquet stats."""
    bar_dir = Path(catalog_path) / "data" / "bar"
    if not bar_dir.exists():
        return None
    oldest_ns: Optional[int] = None
    latest_ns: Optional[int] = None
    try:
        import pyarrow.parquet as pq
        for entry in bar_dir.iterdir():
            if not entry.is_dir() or entry.name == "backup":
                continue
            for pq_file in entry.glob("*.parquet"):
                try:
                    meta = pq.read_metadata(str(pq_file))
                    schema = meta.schema
                    for i in range(meta.num_row_groups):
                        rg = meta.row_group(i)
                        for c in range(rg.num_columns):
                            col = rg.column(c)
                            name = schema.column(c).name
                            if name in ("ts_event", "ts_init") and col.statistics is not None:
                                mn = col.statistics.min
                                mx = col.statistics.max
                                if isinstance(mn, int):
                                    if oldest_ns is None or mn < oldest_ns:
                                        oldest_ns = mn
                                if isinstance(mx, int):
                                    if latest_ns is None or mx > latest_ns:
                                        latest_ns = mx
                except Exception:
                    continue
    except Exception as exc:
        logging.warning("list_all_listed_symbols: catalog scan stats failed: %s", exc)
    if oldest_ns is None or latest_ns is None or latest_ns <= 0:
        return None

    def _to_date(ns: int) -> str:
        secs = ns / 1_000_000_000
        return datetime.fromtimestamp(secs, tz=timezone.utc).strftime("%Y-%m-%d")

    return _to_date(oldest_ns), _to_date(latest_ns)


def _resolve_latest_end_date_from_catalog(catalog_path: str) -> Optional[str]:
    bounds = _resolve_date_bounds_from_catalog(catalog_path)
    return bounds[1] if bounds else None


def _sweep_stale_cred_files(max_age_s: float = 60.0) -> None:
    """Delete leftover ttwr_cred_*.json files older than ``max_age_s`` seconds."""
    try:
        tmp_dir = Path(tempfile.gettempdir())
        now = time.time()
        for stale in tmp_dir.glob("ttwr_cred_*.json"):
            try:
                if now - stale.stat().st_mtime > max_age_s:
                    stale.unlink()
            except OSError:
                continue
    except OSError:
        pass


def _scan_catalog_instruments(catalog_path: str) -> list[str]:
    bar_dir = Path(catalog_path) / "data" / "bar"
    if not bar_dir.exists():
        return []
    seen: set[str] = set()
    for entry in bar_dir.iterdir():
        if not entry.is_dir() or entry.name == "backup":
            continue
        m = _INSTRUMENT_ID_RE.match(entry.name)
        if m:
            seen.add(m.group(1))
    return sorted(seen)


_ADAPTER_ERROR_CODES = frozenset({
    "SESSION_CACHE_MISSING",
    "SESSION_CACHE_EXPIRED",
    "PROMPT_RESULT_MISSING_TOKEN",
})


def _live_login_timeout_s() -> float:
    return float(os.environ.get("LIVE_LOGIN_TIMEOUT_S", "180"))


def _select_replay_strategy(strategy_file):
    """Pick the Replay strategy runtime for ``strategy_file`` (#76 S6a / ADR-0012).

    Detect-first, marimo-free (AST scan, no exec): a marimo notebook (module-level
    ``app = marimo.App()``) runs through the reactive thin-drain adapter (MarimoStrategy);
    anything else is an imperative ``engine.kernel.strategy.Strategy`` subclass loaded by the
    legacy loader. Returns ``(scenario, factory, label)`` where ``factory(primary_id)`` builds
    the (unregistered) strategy and ``label`` is for logging.

    Errors are unchanged from the imperative path (FileNotFoundError / StrategyLoadError /
    SyntaxError / ScenarioValidationError), so the caller's mapping still holds: a broken
    imperative file (no App()) surfaces its real load error on the loader path — never misrouted
    to marimo (exception-as-control-flow avoided). marimo/thin_drain are imported lazily (only
    on the marimo branch), so the seam stays marimo-free at module load (offline gate).
    """
    from engine.strategy_runtime.strategy_kind import is_marimo_app_file

    spath = Path(strategy_file)  # production passes a str; load_scenario/load_app need a Path
    if is_marimo_app_file(spath):
        from marimo._ast.load import load_app

        from engine.strategy_runtime.scenario import load_scenario
        from engine.strategy_runtime.strategy_loader import StrategyLoadError

        from engine.strategy_runtime.scorer_bindings import load_scorer_bindings

        scenario = load_scenario(spath)
        # Load (parse) the notebook HERE so a malformed marimo file maps to STRATEGY_LOAD_ERROR
        # at the dispatch site — symmetric with the imperative loader. The cold COMPILE (the
        # analog of imperative on_start) still happens later in run() → RUN_FAILED, like on_start.
        app = load_app(str(spath))
        if app is None:
            raise StrategyLoadError(f"{spath} is empty or not a valid marimo notebook")
        strategy_id = spath.stem
        # Resolve the sidecar scorer spec (if any) into the cell-facing service + constants
        # (R4). A file with no scorer key gets ({}, {}). The model load stays lazy (AC3).
        services, constants = load_scorer_bindings(spath, scenario)

        def factory(primary_id):
            from engine.strategy_runtime.marimo_strategy import MarimoStrategy

            return MarimoStrategy(
                app=app, strategy_id=strategy_id, instrument_id=primary_id,
                services=services, constants=constants,
            )

        return scenario, factory, f"marimo:{strategy_id}"

    from engine.kernel.strategy import Strategy as _KernelStrategy
    from engine.strategy_runtime.strategy_loader import load as _load_strategy

    _module, scenario, strategy_cls = _load_strategy(strategy_file, base_cls=_KernelStrategy)

    def factory(primary_id):
        return strategy_cls(instrument_id=primary_id)

    return scenario, factory, strategy_cls.__name__


class DataEngineBackend:
    def __init__(
        self,
        engine: DataEngine,
        mode_manager=None,
        venue_sm=None,
        live_adapter_factory=None,
        live_venue_id: Optional[str] = None,
        engine_controller=None,
    ):
        self.engine = engine
        self.mode_manager = mode_manager
        self.venue_sm = venue_sm
        # Phase 9 Step 0: backend -> frontend event push.
        self._backend_event_bus: BackendEventStream = BackendEventStream()
        # Issue #266: Curated Set (backend-authoritative instrument list).
        # Each entry: {"id": str, "live_status": str, "replay_status": str}
        self._curated_set: list[dict] = []
        self._curated_editable: bool = True  # Finding #2: mirrors editable from seed_instruments
        # Issue #217: live loop + venue lifecycle delegated to LiveLoopManager.
        from engine.live.live_orchestrator import LiveLoopManager
        self._live_mgr = LiveLoopManager(
            engine=engine,
            mode_manager=mode_manager,
            venue_sm=venue_sm,
            live_adapter_factory=live_adapter_factory,
            live_venue_id=live_venue_id,
            engine_controller=engine_controller,
            publish_backend_event_callback=self.publish_backend_event,
        )
        # Backward-compat: tests set servicer._strategy_registry directly
        # -> forwarded to _live_mgr via property setter below.

    # Issue #266: Curated Set management methods

    @staticmethod
    def _make_curated_entry(id_: str) -> dict:
        return {"id": id_}

    def _curated_ids(self) -> list[str]:
        return [e["id"] for e in self._curated_set]

    def seed_instruments(self, ids: list[str], editable: bool = True) -> dict:
        """Replace the curated set with the given ids. Returns CuratedSetSnapshot response."""
        self._curated_set = [self._make_curated_entry(id_) for id_ in ids]
        self._curated_editable = editable
        return {"success": True, "ids": ids, "editable": editable}

    def add_instrument(self, id: str) -> dict:
        """Add an instrument to the curated set. Returns CuratedSetSnapshot response."""
        if id not in {e["id"] for e in self._curated_set}:
            self._curated_set.append(self._make_curated_entry(id))
        return {"success": True, "ids": self._curated_ids(), "editable": self._curated_editable}

    def remove_instrument(self, id: str) -> dict:
        """Remove an instrument from the curated set. Returns CuratedSetSnapshot response."""
        self._curated_set = [e for e in self._curated_set if e["id"] != id]
        return {"success": True, "ids": self._curated_ids(), "editable": self._curated_editable}

    _KNOWN_VENUES = {"TACHIBANA", "KABU", "MOCK"}  # D26: MOCK added
    _KNOWN_CRED_SOURCES = {"prompt", "session_cache", "env", "prompt_result"}
    _KNOWN_MODES = {"Replay", "LiveManual", "LiveAuto"}
    _MAX_LIVE_SUBSCRIPTIONS = 50

    # Backward-compat properties: tests monkey-patch these on the servicer.
    @property
    def _strategy_registry(self):
        return self._live_mgr._strategy_registry

    @_strategy_registry.setter
    def _strategy_registry(self, value):
        self._live_mgr._strategy_registry = value

    @property
    def _strategy_host(self):
        return self._live_mgr._strategy_host

    @_strategy_host.setter
    def _strategy_host(self, value):
        self._live_mgr._strategy_host = value

    @staticmethod
    def _make_test_session(**overrides):
        """テスト専用: フィールドを 1 か所で定義した minimal session を生成。"""
        import types as _types
        fields = dict(
            runner=None, bridge=None, price_cache=None, depth_cache=None,
            order_facade=None, account_sync=None,
            health_watchdog=None, instruments_scheduler=None,
        )
        fields.update(overrides)
        return _types.SimpleNamespace(**fields)

    @property
    def _live_runner(self):
        sess = self._live_mgr._session
        return sess.runner if sess is not None else None

    @_live_runner.setter
    def _live_runner(self, value):
        sess = self._live_mgr._session
        if value is None:
            pass  # teardown は _session=None で担う; setter で消さない
        elif sess is None:
            # テスト専用: runner だけ差し込む minimal _session を組む
            self._live_mgr._session = self._make_test_session(runner=value)
        else:
            sess.runner = value

    @property
    def _account_sync(self):
        sess = self._live_mgr._session
        return sess.account_sync if sess is not None else None

    @_account_sync.setter
    def _account_sync(self, value):
        sess = self._live_mgr._session
        if sess is None and value is not None:
            self._live_mgr._session = self._make_test_session(account_sync=value)
        elif sess is not None:
            sess.account_sync = value

    @property
    def _order_facade(self):
        sess = self._live_mgr._session
        return sess.order_facade if sess is not None else None

    @_order_facade.setter
    def _order_facade(self, value):
        sess = self._live_mgr._session
        if sess is None and value is not None:
            self._live_mgr._session = self._make_test_session(order_facade=value)
        elif sess is not None:
            sess.order_facade = value

    # == Additional backward-compat delegations ==

    def _publish_account_snapshot(self, snapshot) -> None:
        self._live_mgr._publish_account_snapshot(snapshot)

    def _publish_account_sync_error(self, record) -> None:
        self._live_mgr._publish_account_sync_error(record)

    def _is_live_ordering_mode(self) -> bool:
        return self._live_mgr._is_live_ordering_mode()

    # == Live lifecycle delegations (issue #217) ==

    def _start_bg_component_after_login(self, component, label: str) -> None:
        self._live_mgr._start_bg_component_after_login(component, label)

    def _start_account_sync_after_login(self) -> None:
        self._live_mgr._start_account_sync_after_login()

    def _start_health_watchdog_after_login(self) -> None:
        self._live_mgr._start_health_watchdog_after_login()

    def _start_instruments_scheduler_after_login(self) -> None:
        self._live_mgr._start_instruments_scheduler_after_login()

    def _teardown_live_components(self):
        self._live_mgr._teardown_live_components()

    def _ensure_live_loop(self):
        return self._live_mgr._ensure_live_loop()

    def stop_live_loop(self, timeout=None) -> bool:
        # Propagate the live-loop join result so the host can gate runtime
        # finalize on it (True=joined clean=safe to finalize; False=hung=unsafe).
        # #22 Gap4 — dropping it here would silence the fail-closed signal.
        return self._live_mgr.stop_live_loop(timeout=timeout)

    def _resolve_live_last_error(self):
        return self._live_mgr._resolve_live_last_error()

    def venue_login(self, venue_id, credentials_source, environment_hint):
        return self._live_mgr.venue_login(venue_id, credentials_source, environment_hint)

    def venue_logout(self):
        return self._live_mgr.venue_logout()

    def set_execution_mode(self, mode: str) -> CommandAck:
        return self._live_mgr.set_execution_mode(mode)

    def subscribe_market_data(self, instrument_id: str) -> CommandAck:
        return self._live_mgr.subscribe_market_data(instrument_id)

    def unsubscribe_market_data(self, instrument_id: str) -> CommandAck:
        return self._live_mgr.unsubscribe_market_data(instrument_id)

    def force_account_snapshot(self) -> CommandAck:
        return self._live_mgr.force_account_snapshot()

    def submit_secret(self, request_id: str, secret: str) -> CommandAck:
        return self._live_mgr.submit_secret(request_id, secret)

    def place_order(self, venue, instrument_id, side, qty, price, order_type,
                    time_in_force, second_secret, idempotency_key=None):
        return self._live_mgr.place_order(venue, instrument_id, side, qty, price,
                                          order_type, time_in_force, second_secret,
                                          idempotency_key)

    def cancel_order(self, venue, order_id, second_secret):
        return self._live_mgr.cancel_order(venue, order_id, second_secret)

    def modify_order(self, venue, order_id, new_price, new_qty, second_secret):
        return self._live_mgr.modify_order(venue, order_id, new_price, new_qty, second_secret)

    def get_orders(self, venue: str) -> OrdersResult:
        return self._live_mgr.get_orders(venue)

    def register_live_strategy(self, strategy_file, expected_sha256="",
                                request_id="", original_path=""):
        return self._live_mgr.register_live_strategy(strategy_file, expected_sha256,
                                                     request_id, original_path)

    def start_live_strategy(self, strategy_id, instrument_id, venue,
                             safety_limits_dict=None, params=None, request_id=""):
        return self._live_mgr.start_live_strategy(strategy_id, instrument_id, venue,
                                                  safety_limits_dict, params, request_id)

    def stop_live_strategy(self, run_id: str) -> CommandAck:
        return self._live_mgr.stop_live_strategy(run_id)

    def pause_live_strategy(self, run_id: str) -> CommandAck:
        return self._live_mgr.pause_live_strategy(run_id)

    def resume_live_strategy(self, run_id: str) -> CommandAck:
        return self._live_mgr.resume_live_strategy(run_id)

    def get_state_json(self) -> str:
        err = self._live_mgr._resolve_live_last_error()
        # Clear-on-toggle: hide the error that existed when suppression was armed.
        # A *different* (freshly raised) error object means the new lifecycle hit a
        # real failure — show it and drop suppression.
        if self._live_mgr._suppress_live_last_error and (err is None or err is self._live_mgr._suppressed_error_baseline):
            live_last_error = None
        else:
            if self._live_mgr._suppress_live_last_error and err is not None:
                self._live_mgr._suppress_live_last_error = False
                self._live_mgr._suppressed_error_baseline = None
            live_last_error = f"{type(err).__name__}: {err}" if err is not None else None

        # D8: mode-aware last_prices dispatch
        mode = self.mode_manager.current_mode if self.mode_manager else "Replay"
        state = self.engine.get_current_state()
        merged_pi = state.per_instrument
        if mode in ("LiveManual", "LiveAuto"):
            _sess = self._live_mgr._session
            raw = (
                _sess.price_cache.snapshot()
                if _sess is not None and _sess.price_cache is not None
                else {}
            )
            # D20 二段ガード: filter by subscribed_ids to prevent stale prices
            runner = _sess.runner if _sess is not None else None
            if runner is not None:
                try:
                    subscribed = runner.subscribed_ids()
                    last_prices = {k: v for k, v in raw.items() if k in subscribed}
                except Exception:
                    last_prices = raw  # subscribed_ids broken → fall back
            else:
                last_prices = raw
            depth_by_id = (
                _sess.depth_cache.snapshot()
                if _sess is not None and _sess.depth_cache is not None
                else {}
            )
            base_pi = state.per_instrument
            merged_pi = {
                k: (v.model_copy(update={"depth": d}) if (d := depth_by_id.get(k)) else v)
                for k, v in base_pi.items()
            }
            # depth はあるが kline 未着の銘柄 (base_pi に居ない) を補完
            for k, d in depth_by_id.items():
                if k not in merged_pi:
                    merged_pi[k] = PerInstrumentState(depth=d)
        else:  # Replay
            last_prices = self.engine.get_replay_last_prices()

        # poll の venue_id は「現在接続中の venue identity」。SM に載らないスカラを各
        # reset サイトで mirror すると漏れる（whack-a-mole）ので、唯一の権威である
        # venue 接続状態から導出する。接続中（CONNECTED/SUBSCRIBED/RECONNECTING）のみ
        # configured venue（= 接続可能な唯一の venue）を載せ、それ以外は None。これで
        # logout / auth 失敗 / 外部切断のどの非接続状態でも stale バッジが残らない
        # （Live→Replay は venue を切断しないので接続中は引き続き表示＝[D9]）。
        # venue_state と同じ snapshot（get_current_state, lock 下）から導出し、live loop
        # スレッドの遷移と TOCTOU で食い違わないようにする。
        connected = state.venue_state in ("CONNECTED", "SUBSCRIBED", "RECONNECTING")
        # #85 Q1 (A'): adapter から EC WS handshake シグナルを venue-agnostic に読み出す。
        # `_sess` は line 731 で同じ `self._live_mgr._session` snapshot を取っているのでそれを流用
        # （TOCTOU 回避 / Replay 経路では `_sess` 未定義なので両分岐共通の局所 sess を作り直す）。
        # `getattr(..., None)` を 2 段重ねるのは「runner が adapter 属性を持たない (kabu/mock runner)」
        # と「adapter が ec_ws_* 属性を持たない (非 Tachibana)」の両方を venue-agnostic に防ぐため。
        # 名前は under-score なしで読む — 単一 underscore は Python 慣習で「module-private」を意味し、
        # cross-module 文字列読みすると rename refactor で silent regression する (#85 code-review B#3)。
        # ec フィールド 0 sentinel: Pydantic の Optional[int]=None だと JSON で `null` を emit するが、
        # Unity JsonUtility の long フィールドは null 入力で例外を投げ得るため、Python 側で常に int
        # を emit する (#85 code-review G#3)。0 = まだ受信していない、を C# 側と統一の sentinel に。
        sess = self._live_mgr._session
        runner = sess.runner if sess is not None else None
        adapter = getattr(runner, "adapter", None) if runner is not None else None
        first_recv_ts = getattr(adapter, "ec_ws_first_recv_ts_ms", None)
        last_recv_ts = getattr(adapter, "ec_ws_last_recv_ts_ms", None)
        state = state.model_copy(
            update={
                "live_last_error": live_last_error,
                "last_prices": last_prices,
                "configured_venue": self._live_mgr._live_venue_id,
                "venue_id": self._live_mgr._live_venue_id if connected else None,
                "per_instrument": merged_pi,
                "ec_ws_subscribed": first_recv_ts is not None,
                "last_event_ws_recv_ts_ms": last_recv_ts if last_recv_ts is not None else 0,
            }
        )
        return state.model_dump_json()

    def start_engine(self, strategy_file):
        logging.info(f"start_engine: strategy_file={strategy_file!r}")

        if not strategy_file:
            return BacktestRunResult(
                success=False,
                error_code="MISSING_STRATEGY_FILE",
                error_message="start_engine requires config.strategy_file",
            )

        # ADR-0006 / #50: Replay runs exclusively through the nautilus-free DuckDB→kernel
        # path. The legacy nautilus catalog + BacktestEngine branch was removed in #50.
        duckdb_root = self.engine.replay_duckdb_root
        if not duckdb_root:
            return BacktestRunResult(
                success=False,
                error_code="REPLAY_DATA_NOT_LOADED",
                error_message=(
                    "start_engine requires a DuckDB replay root "
                    "(call LoadReplayData first)"
                ),
            )
        return self._start_engine_duckdb(strategy_file, duckdb_root)

    def _start_engine_duckdb(self, strategy_file, duckdb_root):
        """ADR-0006 (#49): run the production Replay through the DuckDB→kernel path.

        Same output seam as the legacy catalog path — per-bar apply_replay_event (reducer →
        GetState polling) + RunBuffer fills/equity → compute_portfolio → get_portfolio — so the
        C# decoder is unchanged. Differs only in source (DuckDB direct-read) and engine
        (nautilus-free KernelRunner). Imports no nautilus_trader.
        """
        import json as _json

        # Detect-first marimo-vs-imperative dispatch (#76 S6a / ADR-0012): a marimo notebook
        # runs through the reactive thin-drain adapter, an imperative Strategy subclass through
        # the legacy loader (base_cls=engine.kernel.strategy.Strategy, never imports nautilus —
        # D4). See _select_replay_strategy. KernelRunner is unchanged either way (#24 golden
        # byte-identical), so the marimo adapter just conforms to its per-bar Strategy contract.
        try:
            scenario, strategy_factory, strategy_label = _select_replay_strategy(strategy_file)
        except FileNotFoundError as exc:
            logging.error(f"start_engine(duckdb): strategy file not found: {exc}")
            return BacktestRunResult(
                success=False, error_code="STRATEGY_FILE_NOT_FOUND", error_message=str(exc)
            )
        except Exception as exc:
            logging.error(f"start_engine(duckdb): strategy load failed: {exc}")
            return BacktestRunResult(
                success=False, error_code="STRATEGY_LOAD_ERROR", error_message=str(exc)
            )

        instruments = scenario.get("instruments") or [scenario.get("instrument", "unknown")]
        primary_id = instruments[0]
        # Normalize case/whitespace ('daily' / ' Daily ' → 'Daily') so a non-canonical
        # scenario granularity runs, matching the legacy catalog path (load_bars_for_scenario
        # → normalize_granularity). Without this the kernel's _granularity() raises → RUN_FAILED.
        granularity = normalize_granularity(scenario["granularity"])
        initial_cash = scenario.get("initial_cash", 10_000_000)
        logging.info(
            "start_engine(duckdb): cls=%r instruments=%r granularity=%r start=%r end=%r root=%r",
            strategy_label, instruments, granularity,
            scenario.get("start"), scenario.get("end"), duckdb_root,
        )

        # The kernel construction contract (#25): strategy_cls(instrument_id=..., **params); the
        # marimo factory mirrors it (the adapter takes instrument_id + a host-bound strategy_id).
        try:
            strategy = strategy_factory(primary_id)
        except Exception as exc:
            logging.exception("start_engine(duckdb): strategy construction failed")
            return BacktestRunResult(
                success=False, error_code="STRATEGY_LOAD_ERROR", error_message=str(exc)
            )

        # Transition LOADED → RUNNING before the run so PauseReplay works mid-run.
        se_ok, se_err = self.engine.start_engine()
        if not se_ok:
            return BacktestRunResult(
                success=False, error_code="INVALID_STATE", error_message=se_err or ""
            )
        # #65: clear the previous run's portfolio so "loaded but not running" is honest-empty
        # "(no data)". The observer republishes from bar 1's on_equity (initial cash / flat book),
        # so the gap is only the instant before the first bar. Without this the stale prior-run
        # snapshot would leak into the new run's pre-first-bar window.
        self.engine.last_portfolio = None

        try:
            from engine.strategy_runtime.run_buffer import (
                RunBuffer,
                make_run_id,
                get_run_buffer_base_dir,
            )
            from engine.strategy_runtime.replay_kernel_observer import ReplayKernelObserver
            from engine.kernel.runner import KernelRunner
        except ImportError as exc:
            self.engine.force_stop_replay()
            logging.error("start_engine(duckdb): import failed: %s", exc)
            return BacktestRunResult(
                success=False, error_code="RUN_FAILED", error_message=str(exc)
            )

        run_id = make_run_id(strategy_file, primary_id)
        rb = RunBuffer(
            run_id=run_id,
            strategy_file=str(strategy_file),
            scenario=scenario,
            base_dir=get_run_buffer_base_dir(),
        )
        observer = ReplayKernelObserver(engine=self.engine, run_buffer=rb)
        # Teardown ownership (#76 S6a): a marimo MarimoStrategy opens a headless marimo kernel in
        # on_start and must tear it down even if a cell raises mid-bar — KernelRunner.run wraps no
        # try/finally, so a leaked thread-local context would kill the NEXT run ("RuntimeContext
        # already initialized"). The strategy owns the lifetime; we call its close() in finally.
        # Imperative strategies have no close() → no-op (byte-identical golden path).
        strategy_close = getattr(strategy, "close", None)
        try:
            try:
                KernelRunner(
                    data_root=duckdb_root,
                    instrument_ids=list(instruments),
                    granularity=granularity,
                    start=scenario["start"],
                    end=scenario["end"],
                    initial_cash=initial_cash,
                    strategy=strategy,
                    sink=observer,
                    bar_interval_sec=_REPLAY_BAR_INTERVAL_SEC,
                    stop_event=self.engine.replay_stop_event,  # #76 S6b-β: force_stop teardown only
                ).run()
            finally:
                if strategy_close is not None:
                    strategy_close()
            summary = self._finalize_run(rb, scenario)
            logging.info(
                "start_engine(duckdb): run complete run_id=%s run_dir=%s summary=%r",
                run_id, rb.run_dir, summary,
            )
        except Exception as exc:
            rb.abort()
            self.engine.force_stop_replay()
            logging.exception("start_engine(duckdb): kernel run failed")
            return BacktestRunResult(
                success=False, error_code="RUN_FAILED", error_message=str(exc)
            )

        self.engine.force_stop_replay()
        return BacktestRunResult(
            success=True,
            run_id=run_id,
            summary_json=_json.dumps(summary, ensure_ascii=False),
        )

    def _finalize_run(self, rb, scenario):
        """Shared finalize tail for both Replay runners (legacy catalog + DuckDB kernel).

        Flushes the RunBuffer, derives the summary, and rebuilds last_portfolio (the
        get_portfolio source) from the same fills+equity — so both paths report an identical
        portfolio/summary shape to the unchanged C# decoder. Returns the summary dict.
        """
        from engine.strategy_runtime.run_buffer_reader import RunBufferReader
        from engine.strategy_runtime.summary import (
            compute_summary,
            equity_curve_stats,
            write_summary_json,
        )
        from engine.strategy_runtime.portfolio import compute_portfolio

        rb.finish()
        reader = RunBufferReader(rb.run_dir)
        summary = compute_summary(reader.fills, reader.equity_points)
        # #65 §4-a: union sharpe/sortino into the summary so the launcher's summary_json carries the
        # full TTWR RunSummary {fills_count, equity_points, total_pnl, max_drawdown, sharpe, sortino}.
        # max_drawdown stays SINGLE-SOURCED from compute_summary — equity_curve_stats recomputes it
        # identically, so we take only sharpe/sortino to avoid two sources drifting (§4-a ⚠).
        stats = equity_curve_stats([ep.equity for ep in reader.equity_points])
        summary["sharpe"] = stats["sharpe"]
        summary["sortino"] = stats["sortino"]
        write_summary_json(rb.run_dir, summary)
        self.engine.last_portfolio = compute_portfolio(
            reader.fills, reader.equity_points, scenario
        )
        return summary

    def get_portfolio(self):
        p = self.engine.last_portfolio
        if p is None:
            return PortfolioResult(success=True)

        positions = [
            PortfolioPositionInfo(
                symbol=pos.get("symbol", ""),
                qty=int(pos.get("qty", 0)),
                avg_price=float(pos.get("avg_price", 0.0)),
                unrealized_pnl=float(pos.get("unrealized_pnl", 0.0)),
            )
            for pos in p.get("positions", [])
        ]
        orders = [
            PortfolioOrderInfo(
                symbol=ord_.get("symbol", ""),
                side=ord_.get("side", ""),
                qty=float(ord_.get("qty", 0.0)),
                price=float(ord_.get("price", 0.0)),
                status=ord_.get("status", ""),
                ts_ms=int(ord_.get("ts_ms", 0)),
            )
            for ord_ in p.get("orders", [])
        ]
        return PortfolioResult(
            success=True,
            buying_power=float(p.get("buying_power", 0.0)),
            cash=float(p.get("cash", 0.0)),
            equity=float(p.get("equity", 0.0)),
            positions=positions,
            orders=orders,
            # #65: present in the running snapshot; absent in the post-run compute_portfolio dict
            # (RunResult switches to full stats at completion), so default 0.0.
            realized_pnl=float(p.get("realized_pnl", 0.0)),
            unrealized_pnl=float(p.get("unrealized_pnl", 0.0)),
        )

    def list_instruments(self, source: str):
        # D1: source dispatch — "local" (default) vs "live"
        source = (source or "local").lower()
        if source not in {"local", "live"}:
            return InstrumentListResult(
                success=False,
                error_message=f"unknown source: {source}",
            )

        if source == "live":
            return self._list_instruments_live()
        return self._list_instruments_local()

    def _list_instruments_live(self):
        """D1/D10: Fetch instruments from live adapter (must be logged in).

        Phase 9 Step 9: store-first — InstrumentsScheduler persists the universe to
        parquet at login + daily (5:00 JST), so we serve the persisted snapshot when
        present and only hit the venue on a miss (then persist for next time).
        """
        _sess = self._live_mgr._session
        runner = _sess.runner if _sess is not None else None
        if runner is None or not runner.is_logged_in():
            return InstrumentListResult(
                success=False,
                error_message="LIVE_VENUE_NOT_LOGGED_IN",
            )
        # Issue #253: a venue that cannot enumerate its instrument master (kabu MVP:
        # fetch_instruments() returns []) must not have a persisted store snapshot
        # served as the authoritative live universe — a stale snapshot would prune
        # the user's registry. Short-circuit before any store read / venue fetch so
        # the UI treats the live universe as unsupported (current_universe()=None).
        adapter = runner.adapter
        if not getattr(adapter, "enumerates_instruments", True):
            return InstrumentListResult(
                success=False,
                error_message="LIVE_UNIVERSE_UNSUPPORTED",
            )
        venue = runner.venue_id
        raws = None
        if venue:
            try:
                raws = instruments_store.read_instruments(venue)
            except Exception:
                logging.exception("list_instruments: store read failed; fetching live")
                raws = None
        if not raws:
            # Issue #32 Slice 2: scheduler の初回 refresh が進行中（warming）の cold-store
            # miss は、60s の blocking fetch を避けて独立した PENDING を返す（store-first を
            # 維持）。UI はこれを Loading spinner にマップし、store が埋まったら再 fetch する。
            scheduler = (
                self._live_mgr._session.instruments_scheduler
                if self._live_mgr._session is not None
                else None
            )
            if scheduler is not None and scheduler.is_warming():
                return InstrumentListResult(
                    success=False,
                    error_message="LIVE_UNIVERSE_PENDING",
                )
            try:
                raws = runner.fetch_instruments_blocking(
                    timeout=self._live_mgr._instruments_timeout_s
                )
            except futures.TimeoutError:
                # Issue #32: concurrent.futures.TimeoutError.__str__() は '' なので
                # f"...: {exc}" だと空メッセージ ("fetch_instruments failed: ") になる。
                # 原因の分かる文言を返す。
                return InstrumentListResult(
                    success=False,
                    error_message=(
                        f"instruments fetch timed out after "
                        f"{self._live_mgr._instruments_timeout_s:.0f}s "
                        "(venue still loading universe; retry shortly)"
                    ),
                )
            except Exception as exc:
                return InstrumentListResult(
                    success=False,
                    error_message=f"fetch_instruments failed: {exc}",
                )
            # Best-effort persist so subsequent calls hit the store (does not gate the response).
            if raws and venue:
                try:
                    instruments_store.write_instruments(venue, raws)
                except Exception:
                    logging.exception("list_instruments: persist after fetch failed")
        # v4 fix: empty list == adapter not implemented, treat as failure
        if not raws:
            return InstrumentListResult(
                success=False,
                error_message="LIVE_UNIVERSE_UNSUPPORTED",
            )
        instruments = [
            InstrumentInfo(
                id=f"{r.code}.{r.market}",
                name=r.name,
                market=r.market,
            )
            for r in raws
        ]
        return InstrumentListResult(
            success=True,
            instrument_ids=[i.id for i in instruments],
            instruments=instruments,
        )


    def _list_instruments_local(self):
        """D1: List instruments from local catalog (existing logic)."""
        catalog_path = self.engine.last_replay_catalog_path or self.engine._jquants_catalog_path
        if not catalog_path:
            return InstrumentListResult(
                success=False,
                error_message="No catalog_path available",
            )

        try:
            bar_dir = Path(catalog_path) / "data" / "bar"
            if not bar_dir.exists():
                return InstrumentListResult(
                    success=True,
                    instrument_ids=[],
                )

            seen: set[str] = set()
            for entry in bar_dir.iterdir():
                if not entry.is_dir() or entry.name == "backup":
                    continue
                m = re.match(r"^(.+?)-\d+-[A-Z]", entry.name)
                if m:
                    seen.add(m.group(1))

            ids = sorted(seen)
            logging.info("list_instruments: found %d instruments: %s", len(ids), ids)
            instruments = [
                InstrumentInfo(id=i, name=i, market="") for i in ids
            ]
            return InstrumentListResult(
                success=True,
                instrument_ids=ids,
                instruments=instruments,
            )
        except Exception as exc:
            logging.error("list_instruments: error: %s", exc)
            return InstrumentListResult(
                success=False,
                error_message=str(exc),
            )

    def list_all_listed_symbols(self, end_date: str):
        end_date = (end_date or "").strip()
        catalog_path = (
            self.engine.last_replay_catalog_path or self.engine._jquants_catalog_path
        )

        resolved_end_date = end_date
        if not resolved_end_date:
            if catalog_path:
                resolved_end_date = _resolve_latest_end_date_from_catalog(catalog_path) or ""
            if not resolved_end_date:
                resolved_end_date = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        else:
            try:
                datetime.strptime(resolved_end_date, "%Y-%m-%d")
            except ValueError as exc:
                return ListedSymbolsResult(
                    success=False,
                    error_message=f"Invalid end_date '{resolved_end_date}': {exc}",
                )

        # Fast path: if the artifact already exists for the requested end_date,
        # serve it without scanning catalog parquet metadata. The bounds-resolve
        # scan walks every per-instrument parquet (~600 files, ~40s on cold cache)
        # and is only needed to clamp out-of-range end_dates / detect before_oldest;
        # any artifact on disk was written for a valid in-range end_date, so skipping
        # the scan is safe here.
        if end_date:
            fast_cached = _read_artifact(resolved_end_date)
            if fast_cached is not None:
                logging.info(
                    "list_all_listed_symbols: artifact hit (fast path) end_date=%s count=%d",
                    resolved_end_date, len(fast_cached),
                )
                return ListedSymbolsResult(
                    success=True,
                    instrument_ids=fast_cached,
                    resolved_end_date=resolved_end_date,
                )

        before_oldest = False
        if end_date and catalog_path:
            bounds = _resolve_date_bounds_from_catalog(catalog_path)
            if bounds is not None:
                oldest_date, latest_date = bounds
                if resolved_end_date > latest_date:
                    resolved_end_date = latest_date
                if resolved_end_date < oldest_date:
                    before_oldest = True

        if before_oldest:
            try:
                _write_artifact_atomic(resolved_end_date, [], catalog_path)
            except Exception as exc:
                logging.warning("list_all_listed_symbols: artifact write failed: %s", exc)
            logging.info(
                "list_all_listed_symbols: end_date=%s before catalog oldest -> empty ids",
                resolved_end_date,
            )
            return ListedSymbolsResult(
                success=True,
                instrument_ids=[],
                resolved_end_date=resolved_end_date,
            )

        cached = _read_artifact(resolved_end_date)
        if cached is not None:
            logging.info("list_all_listed_symbols: artifact hit end_date=%s count=%d", resolved_end_date, len(cached))
            return ListedSymbolsResult(
                success=True,
                instrument_ids=cached,
                resolved_end_date=resolved_end_date,
            )

        if not catalog_path:
            return ListedSymbolsResult(
                success=False,
                error_message="No catalog_path available",
                resolved_end_date=resolved_end_date,
            )

        try:
            ids = _scan_catalog_instruments(catalog_path)
        except Exception as exc:
            logging.error("list_all_listed_symbols: scan failed: %s", exc)
            return ListedSymbolsResult(
                success=False,
                error_message=str(exc),
                resolved_end_date=resolved_end_date,
            )

        ids = sorted(set(ids))

        try:
            _write_artifact_atomic(resolved_end_date, ids, catalog_path)
        except Exception as exc:
            logging.warning("list_all_listed_symbols: artifact write failed: %s", exc)

        logging.info("list_all_listed_symbols: miss->write end_date=%s count=%d", resolved_end_date, len(ids))
        return ListedSymbolsResult(
            success=True,
            instrument_ids=ids,
            resolved_end_date=resolved_end_date,
        )


    def publish_backend_event(self, event: backend_events.BackendEvent) -> None:
        """Fan a BackendEvent out to all BackendEventStream subscribers."""
        self._backend_event_bus.publish(event)
        # Phase 3: also forward to in-proc Rust channel if registered
        sink = self.engine._rust_event_sink
        if sink is not None:
            try:
                sink.push_json(json.dumps(_backend_event_to_wire_dict(event)).encode("utf-8"))
            except Exception:
                logging.warning("[inproc] rust event sink push_json failed", exc_info=True)


# Issue #217: wire 関数群は engine.live.event_wire に移動。後方互換 re-export。
from engine.live.event_wire import (  # noqa: E402
    _WIRE,
    _backend_event_to_wire_dict,
    _order_event_wire,
    _account_event_wire,
    _secret_required_wire,
    _venue_logout_detected_wire,
    _live_strategy_event_wire,
    _safety_rail_violation_wire,
    _strategy_log_message_wire,
    _live_strategy_telemetry_wire,
    _backend_error_wire,
)


def advance_loop(engine: DataEngine, interval: float = 1.0):
    """Advance the engine on a fixed background interval while it is running."""
    logging.info(f"Starting advance loop with interval {interval}s")
    while True:
        time.sleep(interval)
        if engine.is_running:
            engine.advance()
    logging.info("Advance loop stopped")
