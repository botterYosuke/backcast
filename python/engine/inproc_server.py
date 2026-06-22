"""InprocLiveServer — Phase 4 / issue #64.

Thin façade over BackendService that lets Rust call live Python methods
directly via PyO3, bypassing TCP round-trips via InprocTransport.

Design decisions:
- BackendService owns DataEngineBackend internally.
- All return values are plain Python dicts so PyO3 can extract them without
  proto imports on the Rust side.
- get_state_json() delegates to BackendService.get_state_json() so live
  mode returns price-cache / depth-cache enriched state.
"""
from __future__ import annotations

import logging
from typing import Optional


class InprocLiveServer:
    """Direct-call façade over BackendService for in-process Rust dispatch."""

    def __init__(self, data_engine, live_venue_id: Optional[str] = None):
        from .backend_service import BackendService
        from .live.state_machine import VenueStateMachine
        from .mode_manager import ModeManager

        venue_sm = VenueStateMachine()
        # get_state_json() の poll は data_engine.state_machine / .mode_manager から
        # venue_state / execution_mode を導出する。login/logout/set_execution_mode が
        # 変更するこの venue_sm / mode_manager を data_engine にも共有しないと、poll が
        # 常に DISCONNECTED / Replay を返し、フッターの Auto 切替・Disconnect が UI に
        # 反映されない。mode_manager は precondition で venue_sm.current を見るため、
        # 必ず *この* venue_sm に束ねる（古い mode_manager の再利用は venue_sm 不一致で
        # set_execution_mode が EXECUTION_MODE_PRECONDITION を誤発火する）。
        data_engine.state_machine = venue_sm
        mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=data_engine)
        data_engine.attach_mode_manager(mode_manager)

        factory = None
        if live_venue_id:
            try:
                from .live.live_adapter_factory import build_live_adapter_factory
                factory = build_live_adapter_factory(live_venue_id)
            except Exception:
                logging.warning(
                    "[inproc] live_adapter_factory build failed for venue_id=%r",
                    live_venue_id,
                    exc_info=True,
                )

        self._svc = BackendService(
            engine=data_engine,
            mode_manager=mode_manager,
            venue_sm=venue_sm,
            live_adapter_factory=factory,
            live_venue_id=live_venue_id,
        )
        # #30: the footer transport drives these directly on the DataEngine (state-mutating
        # control, distinct from the run orchestration in BackendService.start_engine).
        self._engine = data_engine


    # ------------------------------------------------------------------
    # State polling
    # ------------------------------------------------------------------

    def get_state_json(self) -> str:
        """Return JSON from BackendService.get_state_json() (includes live prices/depth)."""
        return self._svc.get_state_json()

    # ------------------------------------------------------------------
    # Venue lifecycle
    # ------------------------------------------------------------------

    def venue_login(
        self,
        venue_id: str,
        credentials_source: str,
        environment_hint: Optional[str],
    ) -> dict:
        return self._svc.venue_login(venue_id, credentials_source, environment_hint)

    def venue_logout(self) -> dict:
        return self._svc.venue_logout()

    # ------------------------------------------------------------------
    # Execution mode
    # ------------------------------------------------------------------

    def set_execution_mode(self, mode: str) -> dict:
        return self._svc.set_execution_mode(mode)

    # ------------------------------------------------------------------
    # Instruments
    # ------------------------------------------------------------------

    def list_instruments(self, source: str, end_date: str = "") -> dict:
        return self._svc.list_instruments(source, end_date)

    def list_all_listed_symbols(self, end_date: str) -> dict:
        return self._svc.list_all_listed_symbols(end_date)

    # ------------------------------------------------------------------
    # Market data subscriptions
    # ------------------------------------------------------------------

    def subscribe_market_data(self, instrument_id: str) -> dict:
        return self._svc.subscribe_market_data(instrument_id)

    def subscribe_market_data_batch(self, instrument_ids) -> dict:
        return self._svc.subscribe_market_data_batch(instrument_ids)

    def unsubscribe_market_data(self, instrument_id: str) -> dict:
        return self._svc.unsubscribe_market_data(instrument_id)

    # ------------------------------------------------------------------
    # Orders
    # ------------------------------------------------------------------

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
    ) -> dict:
        return self._svc.place_order(
            venue=venue,
            instrument_id=instrument_id,
            side=side,
            qty=qty,
            price=price,
            order_type=order_type,
            time_in_force=time_in_force,
            second_secret=second_secret,
            idempotency_key=idempotency_key,
        )

    def cancel_order(
        self,
        venue: str,
        order_id: str,
        second_secret: Optional[str],
    ) -> dict:
        return self._svc.cancel_order(venue=venue, order_id=order_id, second_secret=second_secret)

    def modify_order(
        self,
        venue: str,
        client_order_id: str,
        new_qty: Optional[float],
        new_price: Optional[float],
        second_secret: Optional[str],
    ) -> dict:
        return self._svc.modify_order(
            venue=venue,
            client_order_id=client_order_id,
            new_qty=new_qty,
            new_price=new_price,
            second_secret=second_secret,
        )

    def get_orders(self, venue: str) -> dict:
        return self._svc.get_orders(venue)

    def submit_secret(self, request_id: str, secret: str) -> dict:
        return self._svc.submit_secret(request_id, secret)

    def force_account_snapshot(self) -> dict:
        return self._svc.force_account_snapshot()

    # ------------------------------------------------------------------
    # Live strategy lifecycle
    # ------------------------------------------------------------------

    def register_live_strategy(self, strategy_file: str, original_path: str = "") -> dict:
        return self._svc.register_live_strategy(strategy_file, original_path=original_path)

    def start_live_strategy(
        self,
        strategy_id: str,
        instrument_id: str,
        venue: str,
        safety_limits_dict: Optional[dict] = None,
    ) -> dict:
        return self._svc.start_live_strategy(
            strategy_id=strategy_id,
            instrument_id=instrument_id,
            venue=venue,
            safety_limits_dict=safety_limits_dict,
        )

    def stop_live_strategy(self, run_id: str) -> dict:
        return self._svc.stop_live_strategy(run_id)

    def pause_live_strategy(self, run_id: str) -> dict:
        return self._svc.pause_live_strategy(run_id)

    def resume_live_strategy(self, run_id: str) -> dict:
        return self._svc.resume_live_strategy(run_id)

    # ------------------------------------------------------------------
    # Strategy engine run (used by RunStrategy command)
    # ------------------------------------------------------------------

    def start_engine(self, cfg: dict) -> dict:
        """Delegate to BackendService.start_engine() for strategy backtest runs."""
        return self._svc.start_engine(cfg)

    def run_cell(self, source: str, pressed_index: int, scenario_json: str = None) -> str:
        """#95 Phase 2/4: per-cell RUN. ``scenario_json`` (Phase 4) is the committed scenario used
        to build a ``bt`` handle when the notebook drives a backtest. Returns a JSON string
        ``{"ok","ran":[{"index","output","ok"}...],"error","run_summary"?}``."""
        return self._svc.run_cell(source, pressed_index, scenario_json)

    def notebook_restage(self, source: str) -> str:
        """#95 Phase 6 Slice 4: edit-time stale projection (diff-register the live source WITHOUT
        running). Returns a JSON string ``{"stale":[indices], "error"}``."""
        return self._svc.notebook_restage(source)

    def get_portfolio(self) -> dict:
        return self._svc.get_portfolio()

    def get_portfolio_json(self) -> str:
        # #65: Replay poll lane (LiveRpcLanes) reads this; C# DecodePortfolio wants a JSON string.
        return self._svc.get_portfolio_json()

    def get_run_summary_json(self) -> str:
        """#100 Slice ① (findings 0077): Replay poll lane source for the RunResult full-stats
        tile.  Mirrors get_portfolio_json — honest-empty (``""``) until a run finalizes."""
        return self._svc.get_run_summary_json()

    def clear_run_view(self) -> dict:
        """#100 Slice ① (findings 0077): document-boundary reset (File→New / File→Open).  Clears
        both ``last_portfolio`` and ``last_run_summary`` so the 4 Replay tiles drop to honest-empty."""
        return self._svc.clear_run_view()

    # ------------------------------------------------------------------
    # Replay transport — RETIRED (#76 S6b-β); only force_stop (teardown) survives
    # ------------------------------------------------------------------
    #
    # The #30 footer transport (play / pause / step / speed) was the only caller of the
    # pause_replay / resume_replay / step_replay / set_replay_speed RPCs. ADR-0012's reactive
    # execution model supersedes the transport affordance (a reactive drain completes near-
    # instantly — 0.3s/50k — so scrubbing is obsolete), and the footer was removed with it. Those
    # four user-transport RPCs are retired from the production surface.
    #
    # force_stop_replay SURVIVES as run-lifecycle TEARDOWN (NOT a user transport control): the C#
    # host's Stop() teardown calls it to unblock the launcher's synchronous start_engine before
    # closing the server, and _backend_impl calls the engine method directly on run completion /
    # abort. It maps the engine (ok, err) tuple to the {success, error_message} dict CallTransport
    # consumes (#76 S6b-β: kept by role — teardown, not transport).

    def force_stop_replay(self) -> dict:
        ok, err = self._engine.force_stop_replay()
        if ok:
            return {"success": True, "error_code": "", "error_message": ""}
        return {"success": False, "error_code": "TEARDOWN_FAILED", "error_message": err or ""}

    def close(self) -> None:
        """Tear down the underlying live server (loop/runner/account-sync).

        Phase 4 / issue #64 finding #6: the InProc worker drops this façade
        when its command channel closes, but the wrapped BackendService's
        live loop thread + runner/account-sync survive. close() must stop them.
        """
        try:
            self._svc.teardown()
        except Exception:
            logging.exception("[inproc] close: teardown failed")
