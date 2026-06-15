import logging
import random
import threading
import time
from typing import Literal, Optional

from .jquants_to_catalog import instrument_id_to_bar_type  # noqa: F401 — re-exported for callers
from .models import EngineSnapshot, HistoryPoint, OhlcPoint, PerInstrumentState, TradingState
from .nautilus_catalog_loader import CatalogPrecisionMismatchError
from .reducer import KlineUpdate, ReducerState, ReplayEvent, ReplayTimeUpdated, apply_event
from .replay import BaseReplayProvider, NautilusBarsReplayProvider
from .replay_loader import ReplayLoader

# Placeholder price for a DuckDB Replay that is LOADED but not yet streaming (#49). It only
# satisfies TradingState's price>0 invariant for the brief pre-stream poll window and is
# overwritten by bar 0; it is never rendered (ohlc_points is empty until streaming). Value is
# arbitrary-positive — paired with timestamp_ms=1 it reads as an obvious "warming up" marker.
_REPLAY_WARMUP_PRICE = 1.0


class DataEngine:
    def __init__(
        self,
        replay_provider: Optional[BaseReplayProvider] = None,
        max_history_len: int = 1000,
        jquants_loader=None,
        nautilus_catalog_path: Optional[str] = None,
        jquants_catalog_path: Optional[str] = None,
        state_machine: Optional["VenueStateMachine"] = None,
        venue_id: Optional[str] = None,
        duckdb_root: Optional[str] = None,
    ):
        logging.info(
            f"Initializing DataEngine core (max_history_len: {max_history_len})"
        )
        self.state_machine = state_machine
        self.venue_id = venue_id
        self._subscribed_instruments: list[str] = []
        self.mode_manager: Optional["ModeManager"] = None
        self._lock = threading.Lock()
        self._is_running = False
        self._replay_state = "IDLE"
        # Gate for engine_runner: SET = running, CLEAR = paused.
        self._run_event = threading.Event()
        self._run_event.set()
        self._replay_provider = replay_provider
        self._mode: Literal["static", "replay"] = (
            "replay" if replay_provider else "static"
        )
        self._is_exhausted = False
        self._max_history_len = max_history_len
        self._jquants_loader = jquants_loader
        self._nautilus_catalog_path = nautilus_catalog_path
        self._jquants_catalog_path = jquants_catalog_path
        self._replay_loader = ReplayLoader(
            nautilus_catalog_path=nautilus_catalog_path,
            jquants_loader=jquants_loader,
            jquants_catalog_path=jquants_catalog_path,
        )
        self._event_log: list[ReplayEvent] = []
        self._last_replay_catalog_path: Optional[str] = None
        # ADR-0006 (#49): J-Quants DuckDB direct-read root. ctor arg overrides the .env
        # `BACKCAST_JQUANTS_DUCKDB_ROOT`; resolved lazily in load_replay_data so tests can
        # monkeypatch the env. `_replay_duckdb_root` is set when a DuckDB Replay is LOADED and
        # selects the nautilus-free kernel runner in start_engine.
        self._duckdb_root = duckdb_root
        self._replay_duckdb_root: Optional[str] = None
        self.last_portfolio: Optional[dict] = None
        # D9/D24: multi-instrument replay support
        self._replay_providers: dict[str, NautilusBarsReplayProvider] = {}
        self._replay_primary_id: str = ""
        # Phase 3: in-proc Rust event sink (set by inproc_python_worker via set_rust_event_sink)
        self._rust_event_sink = None

        # Initialize the first visible state.
        if self._mode == "replay" and self._replay_provider:
            self._prime_provider_locked(self._replay_provider)
        else:
            # Static mode fallback kept for Phase 1-5 compatibility.
            ts_ms = int(time.time() * 1000)
            self._rs = ReducerState(
                timestamp_ms=ts_ms,
                price=120.5,
                history=[118.0, 119.0, 121.0, 120.5],
                history_points=[
                    HistoryPoint(timestamp_ms=ts_ms - 3000, price=118.0),
                    HistoryPoint(timestamp_ms=ts_ms - 2000, price=119.0),
                    HistoryPoint(timestamp_ms=ts_ms - 1000, price=121.0),
                    HistoryPoint(timestamp_ms=ts_ms, price=120.5),
                ],
                max_history_len=max_history_len,
            )

    def attach_mode_manager(self, mm: "ModeManager") -> None:
        """ModeManager の循環参照回避のため setter で後付け注入する"""
        self.mode_manager = mm

    def set_rust_event_sink(self, sink) -> None:
        """Phase 3: register in-proc Rust mpsc sender for live event delivery."""
        self._rust_event_sink = sink

    @property
    def is_running(self) -> bool:
        with self._lock:
            return self._is_running

    @property
    def replay_state(self) -> str:
        with self._lock:
            return self._replay_state

    def apply_replay_event(self, event: ReplayEvent) -> None:
        with self._lock:
            self._apply_event_locked(event)

    def _apply_event_locked(self, event: ReplayEvent) -> None:
        self._event_log.append(event)
        apply_event(self._rs, event, primary_id=self._replay_primary_id or None)

    def _prime_provider_locked(self, provider: BaseReplayProvider) -> None:
        tick = provider.get_next_tick()
        if not tick:
            raise ValueError("Replay provider returned no data for priming")
        self._replay_provider = provider
        self._mode = "replay"
        ts, o, h, l, c, *_rest = tick
        ts_ms = int(ts * 1000)
        self._rs = ReducerState(
            timestamp_ms=ts_ms,
            price=c,
            open=o,
            high=h,
            low=l,
            history=[c],
            history_points=[HistoryPoint(timestamp_ms=ts_ms, price=c)],
            ohlc_points=[OhlcPoint(timestamp_ms=ts_ms, open_time_ms=ts_ms, open=o, high=h, low=l, close=c)],
            max_history_len=self._max_history_len,
        )
        self._is_exhausted = provider.is_exhausted()
        logging.info(f"Primed replay engine with first tick: {tick}")

    def start(self):
        with self._lock:
            logging.info(f"Starting DataEngine core (mode: {self._mode})")
            self._is_running = True
            self._replay_state = "RUNNING"

    def stop(self):
        with self._lock:
            logging.info("Stopping DataEngine core")
            self._is_running = False
            self._replay_state = "IDLE"

    def load_replay_data(
        self,
        instrument_ids: list[str] | None = None,
        start_date: str = "",
        end_date: str = "",
        granularity: str = "Trade",
        catalog_path: str | None = None,
    ) -> tuple[bool, str | None]:
        """Backward-compatible wrapper — delegates loader logic to ReplayLoader.

        State transitions (_replay_state, _prime_provider_locked, etc.) remain
        owned by DataEngine; ReplayLoader only builds the providers.
        """
        with self._lock:
            if self._replay_state != "IDLE":
                return False, "LoadReplayData is only allowed from IDLE"

            # ADR-0006 (#49): J-Quants DuckDB direct-read takes precedence over everything
            # else — an explicitly-passed catalog (the panel's vestigial arg) AND a stale
            # catalog provider left from a prior catalog run (resolved BEFORE the provider-
            # reuse guard so a later DuckDB-intended load can't be silently shadowed and
            # misrouted to the legacy path). Production resolves the root from .env; an unset
            # root is a hard error below, never a silent precision-bound catalog fallback.
            from engine.paths import jquants_duckdb_root

            root = self._duckdb_root or jquants_duckdb_root()
            if root is not None:
                ids = instrument_ids or []
                if not ids:
                    return False, "At least one instrument_id is required"
                return self._load_replay_duckdb_locked(
                    instrument_ids=ids, granularity=granularity, root=str(root)
                )

            if self._replay_provider is not None:
                self._replay_state = "LOADED"
                return True, None

            instrument_ids = instrument_ids or []
            if not instrument_ids:
                return False, "At least one instrument_id is required"

            effective_catalog_path = catalog_path or self._nautilus_catalog_path
            if effective_catalog_path is not None:
                try:
                    providers = self._replay_loader.load_catalog_providers(
                        instrument_ids=instrument_ids,
                        start_date=start_date,
                        end_date=end_date,
                        granularity=granularity,
                        catalog_path=effective_catalog_path,
                    )
                except CatalogPrecisionMismatchError:
                    raise
                except (ValueError, FileNotFoundError) as e:
                    return False, str(e)

                primary_iid = instrument_ids[0]
                self._prime_provider_locked(providers[primary_iid])
                self._replay_providers = providers
                self._replay_primary_id = primary_iid
                # A0: priming は primary の初バーを global ohlc_points だけに積むので per-id にも複製
                if self._rs.ohlc_points:
                    self._rs.per_id_ohlc_points[primary_iid] = list(self._rs.ohlc_points)
                    self._rs.per_id_close.setdefault(primary_iid, self._rs.price)
                self._last_replay_catalog_path = effective_catalog_path
                self._replay_state = "LOADED"
                return True, None

            if self._jquants_loader is not None:
                try:
                    provider, used_catalog_path = self._replay_loader.load_jquants_provider(
                        instrument_id=instrument_ids[0],
                        start_date=start_date,
                        end_date=end_date,
                        granularity=granularity,
                    )
                except (ValueError, FileNotFoundError) as e:
                    return False, str(e)

                self._prime_provider_locked(provider)
                self._replay_primary_id = str(instrument_ids[0])
                self._last_replay_catalog_path = used_catalog_path
                self._replay_state = "LOADED"
                return True, None

            return False, "Replay provider is not configured"

    def _load_replay_duckdb_locked(
        self, *, instrument_ids: list[str], granularity: str, root: str
    ) -> tuple[bool, str | None]:
        """LOADED transition for the ADR-0006 DuckDB direct-read path (called under _lock).

        Unlike the catalog branch this does NOT prime bar 0. It resets the reducer to a fresh
        replay state with timestamp_ms=0 so every streamed historical bar (ts > 0) is past the
        primary staleness guard and accumulates **exactly once** (no prime → no primary-skip;
        the chart's ohlc_points count equals the streamed primary-bar count). Bars are streamed
        externally by start_engine's kernel run via apply_replay_event, so no provider object is
        built here (_replay_provider stays None).
        """
        from engine.kernel.duckdb_bars import db_path

        # Early panel feedback: validate each instrument's per-symbol DuckDB file exists for
        # this granularity. The granularity here is the LoadReplayData arg (a hint); the run
        # uses the scenario's granularity. Only validate the two ADR-0006 granularities — for
        # anything else (e.g. the reducer's "Trade" default from a non-panel caller) skip the
        # check and let start_engine's load surface any real error, rather than hard-failing
        # LOAD on a granularity that isn't even what the run will use.
        if granularity in ("Daily", "Minute"):
            for iid in instrument_ids:
                try:
                    path = db_path(root, iid, granularity)
                except ValueError as exc:  # empty/unset root
                    return False, str(exc)
                if not path.exists():
                    return False, f"DuckDB {granularity} file not found: {path}"

        self._mode = "replay"
        self._replay_primary_id = str(instrument_ids[0])
        self._replay_duckdb_root = root
        # Replay-ready reducer in a "warming-up" state. We do NOT prime bar 0 (so every bar is
        # streamed exactly once — no primary-skip), but the LOADED state is still polled by the
        # UI before streaming starts, and TradingState requires price>0 & timestamp>0. So seed
        # a minimal valid placeholder: timestamp_ms=1 is below any historical bar ts (nothing is
        # stale-dropped by the reducer's `ts < state.timestamp_ms` guard) and is overwritten by
        # bar 0; ohlc_points stays empty so no candle renders the placeholder. Without this the
        # poll thread logs "2 validation errors for TradingState (price/timestamp > 0)" until the
        # first bar streams (the static-mode init used 'now', which would instead stale-drop the
        # whole historical series).
        self._rs = ReducerState(
            timestamp_ms=1,
            price=_REPLAY_WARMUP_PRICE,
            max_history_len=self._max_history_len,
        )
        self._replay_state = "LOADED"
        return True, None

    @property
    def replay_duckdb_root(self) -> str | None:
        return self._replay_duckdb_root

    @property
    def last_replay_catalog_path(self) -> str | None:
        return self._last_replay_catalog_path

    def start_engine(self) -> tuple[bool, str | None]:
        with self._lock:
            if self._replay_state != "LOADED":
                return False, "start_engine is only allowed from LOADED"

            self._is_running = True
            self._replay_state = "RUNNING"
            return True, None

    def pause_replay(self) -> tuple[bool, str | None]:
        with self._lock:
            if self._replay_state != "RUNNING":
                return False, "PauseReplay is only allowed from RUNNING"

            self._is_running = False
            self._replay_state = "PAUSED"
            self._run_event.clear()
            return True, None

    def resume_replay(self) -> tuple[bool, str | None]:
        with self._lock:
            if self._replay_state != "PAUSED":
                return False, "ResumeReplay is only allowed from PAUSED"

            self._is_running = True
            self._replay_state = "RUNNING"
            self._run_event.set()
            return True, None

    def stop_replay(self) -> tuple[bool, str | None]:
        with self._lock:
            if self._replay_state not in ("RUNNING", "PAUSED"):
                return False, "StopReplay is only allowed from RUNNING or PAUSED"

            self._is_running = False
            self._replay_state = "IDLE"
            self._replay_duckdb_root = None  # #49: see force_stop_replay
            self._run_event.set()
            return True, None

    def force_stop_replay(self) -> tuple[bool, str | None]:
        with self._lock:
            self._is_running = False
            self._replay_state = "IDLE"
            # Clear providers so the next load_replay_data() creates fresh ones
            # instead of hitting the early-return guard at line 167 (#70).
            self._replay_provider = None
            self._replay_providers = {}
            # #49: drop the DuckDB root too, so a subsequent catalog-based load is not
            # misrouted to the kernel path by a stale flag.
            self._replay_duckdb_root = None
            self._run_event.set()
            return True, None

    @property
    def run_event(self) -> threading.Event:
        return self._run_event

    def set_replay_speed(self, multiplier: int) -> tuple[bool, str | None]:
        with self._lock:
            if multiplier == 0:
                return False, "SetReplaySpeed multiplier must be greater than 0"

            return True, None

    def advance(self):
        """
        Advance one tick when the engine is running.

        The background advance loop calls this method. PAUSED replay sessions
        keep _is_running false, so they advance only through step_replay().
        """
        with self._lock:
            if not self._is_running:
                return

            self._advance_one_locked()

    def _advance_one_locked(self):
        """
        Advance exactly one tick.

        The _locked suffix means callers must already hold self._lock.

        D24: multi-instrument support — peek all providers, find min_ts,
        drain all providers with that ts in one tick (so same-timestamp bars
        from multiple instruments appear "simultaneously").
        """
        if self._replay_providers:
            # D24: multi-instrument path
            pending = []
            for iid, p in self._replay_providers.items():
                tick = p.peek_next_tick()
                if tick is not None:
                    pending.append((tick[0], iid, p))
            if not pending:
                self._is_exhausted = True
                logging.info("Replay data exhausted (all providers)")
                return
            pending.sort(key=lambda x: x[0])
            min_ts = pending[0][0]
            ts_ms = int(min_ts * 1000)
            # ReplayTimeUpdated fires once per ts group
            self._apply_event_locked(ReplayTimeUpdated(timestamp_ms=ts_ms))
            # Pop and emit KlineUpdate for all providers at min_ts
            for ts, iid, p in pending:
                if ts != min_ts:
                    break  # sorted, so all remaining are > min_ts
                popped = p.pop_next_tick()
                if popped is None:
                    continue
                _, o, h, l, c, *_rest = popped
                volume = _rest[0] if _rest else 0.0
                self._apply_event_locked(KlineUpdate(
                    timestamp_ms=ts_ms, close=c, open=o, high=h, low=l,
                    open_time_ms=ts_ms, instrument_id=iid,
                    volume=volume,
                ))
            self._is_exhausted = all(p.is_exhausted() for p in self._replay_providers.values())
        elif self._replay_provider:
            # Legacy single-provider path (backward compat)
            tick = self._replay_provider.get_next_tick()
            if tick:
                ts, o, h, l, c, *_rest = tick
                volume = _rest[0] if _rest else 0.0
                ts_ms = int(ts * 1000)
                self._apply_event_locked(ReplayTimeUpdated(timestamp_ms=ts_ms))
                self._apply_event_locked(KlineUpdate(timestamp_ms=ts_ms, close=c, open=o, high=h, low=l, open_time_ms=ts_ms, volume=volume, instrument_id=self._replay_primary_id))
                self._is_exhausted = self._replay_provider.is_exhausted()
            else:
                self._is_exhausted = True
                logging.info("Replay data exhausted")
        else:
            price = self._rs.price + random.uniform(-0.5, 0.5)
            ts_ms = int(time.time() * 1000)
            self._apply_event_locked(KlineUpdate(timestamp_ms=ts_ms, close=price, open=price, high=price, low=price))

    def step_replay(self) -> tuple[bool, str | None]:
        """Advance one tick while paused or loaded, then remain in the same state."""
        with self._lock:
            if self._replay_state not in ("PAUSED", "LOADED"):
                return False, "StepReplay is only allowed from PAUSED or LOADED"

            prev_state = self._replay_state
            self._advance_one_locked()
            self._is_running = False
            self._replay_state = prev_state
            return True, None

    def get_replay_last_prices(self) -> dict:
        """D8/D9: Return per-instrument last close prices for Replay mode sidebar."""
        with self._lock:
            return dict(self._rs.per_id_close)

    def forget_instrument(self, instrument_id: str) -> None:
        """Drop an instrument's per-id reducer state (last close + OHLC history) so it
        stops surfacing in per_instrument after unsubscribe. Without this the symbol
        persists every poll and its capped OHLC list stays resident."""
        with self._lock:
            self._rs.per_id_close.pop(instrument_id, None)
            self._rs.per_id_ohlc_points.pop(instrument_id, None)

    def _build_trading_state_locked(self, *, include_per_instrument: bool) -> TradingState:
        """Build a TradingState from current reducer state.

        include_per_instrument=True  → full live state for UI polling (get_current_state)
        include_per_instrument=False → compact snapshot for save/restore (take_snapshot);
                                       per_instrument is large and reconstructed from replay,
                                       so snapshots exclude it by design.
        """
        rs = self._rs
        per_instrument: dict[str, PerInstrumentState] = {}
        if include_per_instrument:
            for iid, close_px in rs.per_id_close.items():
                per_instrument[iid] = PerInstrumentState(
                    price=close_px if close_px > 0 else None,
                    ohlc_points=list(rs.per_id_ohlc_points.get(iid, [])),
                    depth=None,  # Live depth は GetState 側で per-instrument に注入される
                )
        return TradingState(
            price=rs.price,
            history=list(rs.history),
            timestamp=rs.timestamp_ms / 1000.0,
            timestamp_ms=rs.timestamp_ms,
            history_points=list(rs.history_points),
            ohlc_points=list(rs.ohlc_points),
            open=rs.open if rs.open != 0.0 else None,
            high=rs.high if rs.high != 0.0 else None,
            low=rs.low if rs.low != 0.0 else None,
            close=rs.price,
            open_time_ms=rs.open_time_ms if rs.open_time_ms != 0 else None,
            replay_state=self._replay_state,
            venue_state=self.state_machine.current if self.state_machine else "DISCONNECTED",
            execution_mode=self.mode_manager.current_mode if self.mode_manager else "Replay",
            venue_id=self.venue_id,
            subscribed_instruments=list(self._subscribed_instruments),
            instruments_loaded=len(self._subscribed_instruments),
            per_instrument=per_instrument,
        )

    def get_current_state(self) -> TradingState:
        """Return the current trading state as a read-only snapshot."""
        with self._lock:
            return self._build_trading_state_locked(include_per_instrument=True)

    def take_snapshot(self) -> EngineSnapshot:
        """Capture the current engine execution context."""
        with self._lock:
            source_path = None
            replay_index = 0
            if self._replay_provider:
                if hasattr(self._replay_provider, "file_path"):
                    source_path = self._replay_provider.file_path
                if hasattr(self._replay_provider, "current_index"):
                    replay_index = self._replay_provider.current_index

            return EngineSnapshot(
                state=self._build_trading_state_locked(include_per_instrument=False),
                replay_index=replay_index,
                source_path=source_path,
                mode=self._mode,
            )

    def restore_snapshot(self, snapshot: EngineSnapshot):
        """Restore engine state from a previously captured snapshot."""
        with self._lock:
            if snapshot.mode != self._mode:
                raise ValueError(
                    f"Snapshot mode mismatch. Engine is {self._mode}, snapshot is {snapshot.mode}"
                )

            if self._mode == "replay":
                if not self._replay_provider:
                    raise ValueError(
                        "Engine is in replay mode but has no provider to restore to"
                    )

                current_path = getattr(self._replay_provider, "file_path", None)
                if snapshot.source_path and snapshot.source_path != current_path:
                    raise ValueError(
                        f"Snapshot source mismatch. Expected {current_path}, got {snapshot.source_path}"
                    )

            ts_ms = snapshot.state.timestamp_ms or int(snapshot.state.timestamp * 1000)

            if snapshot.state.history_points:
                history_points = list(snapshot.state.history_points)
            else:
                # Older snapshots may not have history_points; reconstruct them.
                count = len(snapshot.state.history)
                history_points = [
                    HistoryPoint(
                        timestamp_ms=ts_ms - (count - 1 - i) * 1000,
                        price=p,
                    )
                    for i, p in enumerate(snapshot.state.history)
                ]

            self._rs.price = snapshot.state.price
            self._rs.timestamp_ms = ts_ms
            self._rs.history = list(snapshot.state.history)
            self._rs.history_points = history_points
            self._rs.ohlc_points = list(snapshot.state.ohlc_points)
            self._rs.open = snapshot.state.open if snapshot.state.open is not None else snapshot.state.price
            self._rs.high = snapshot.state.high if snapshot.state.high is not None else snapshot.state.price
            self._rs.low = snapshot.state.low if snapshot.state.low is not None else snapshot.state.price
            self._rs.open_time_ms = snapshot.state.open_time_ms if snapshot.state.open_time_ms is not None else ts_ms

            if self._replay_provider:
                if hasattr(self._replay_provider, "current_index"):
                    self._replay_provider.current_index = snapshot.replay_index
                    self._is_exhausted = self._replay_provider.is_exhausted()

            logging.info(
                f"Restored snapshot (mode: {self._mode}, index: {snapshot.replay_index})"
            )

    def get_event_log(self) -> list[ReplayEvent]:
        with self._lock:
            return list(self._event_log)

    @property
    def is_exhausted(self) -> bool:
        with self._lock:
            return self._is_exhausted

    @property
    def jquants_loader_base_dir(self) -> str | None:
        return str(self._jquants_loader.base_dir) if self._jquants_loader else None

    @property
    def mode(self) -> str:
        return self._mode
