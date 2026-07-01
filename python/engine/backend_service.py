"""BackendService — issue #68 Slice 11 (完全実装).

BackendService が DataEngineBackend を内部で生成・隠蔽する。
InprocLiveServer はこのクラスだけを import すればよい。
"""
from __future__ import annotations

import json
import logging
from typing import Optional


def _order_event_to_dict(ev, strategy_id: str) -> dict:
    """OrderEventData → plain dict for Rust extraction (proto 非依存)。"""
    return {
        "order_id": ev.order_id,
        "venue_order_id": ev.venue_order_id,
        "client_order_id": ev.client_order_id,
        "status": ev.status,
        "filled_qty": ev.filled_qty,
        "avg_price": ev.avg_price,
        "ts_ms": ev.ts_ms,
        "strategy_id": strategy_id,
        "symbol": ev.symbol,
        "side": ev.side,
        "qty": ev.qty,
        "price": ev.price,
    }


from ._backend_impl import DataEngineBackend


def _call_ack(srv_method, *args, **kwargs) -> dict:
    """Call a DataEngineBackend method that returns CommandAck and convert to plain dict."""
    try:
        ack = srv_method(*args, **kwargs)
        return {"success": ack.success, "error_code": ack.error_code}
    except RuntimeError as exc:
        return {"success": False, "error_code": "INPROC_ABORT", "detail": str(exc)}
    except Exception:
        logging.exception("[backend_service] %s failed", getattr(srv_method, "__name__", str(srv_method)))
        return {"success": False, "error_code": "INPROC_ERROR"}


class BackendService:
    """DataEngineBackend を包む薄いラッパー。proto 非依存の plain dict を返す。"""

    def __init__(
        self,
        engine,
        mode_manager=None,
        venue_sm=None,
        live_adapter_factory=None,
        live_venue_id=None,
        engine_controller=None,
    ) -> None:
        self._srv = DataEngineBackend(
            engine=engine,
            mode_manager=mode_manager,
            venue_sm=venue_sm,
            live_adapter_factory=live_adapter_factory,
            live_venue_id=live_venue_id,
            engine_controller=engine_controller,
        )

    # ------------------------------------------------------------------
    # State
    # ------------------------------------------------------------------

    def get_state_json(self) -> str:
        try:
            return self._srv.get_state_json()
        except Exception:
            logging.exception("[backend_service] get_state_json failed; falling back")
            return self._srv.engine.get_current_state().model_dump_json()

    def get_portfolio(self) -> dict:
        try:
            resp = self._srv.get_portfolio()
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "buying_power": 0.0, "cash": 0.0, "equity": 0.0, "positions": [], "orders": [], "realized_pnl": 0.0, "unrealized_pnl": 0.0, "clock_ms": 0, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "buying_power": 0.0, "cash": 0.0, "equity": 0.0, "positions": [], "orders": [], "realized_pnl": 0.0, "unrealized_pnl": 0.0, "clock_ms": 0, "detail": str(exc)}
        return {
            "success": resp.success,
            "buying_power": resp.buying_power,
            "cash": resp.cash,
            "equity": resp.equity,
            "positions": [
                {"symbol": p.symbol, "qty": p.qty, "avg_price": p.avg_price, "unrealized_pnl": p.unrealized_pnl}
                for p in resp.positions
            ],
            "orders": [
                {"symbol": o.symbol, "side": o.side, "qty": o.qty, "price": o.price, "status": o.status, "ts_ms": o.ts_ms}
                for o in resp.orders
            ],
            # #65: RunResult running-view (走行中) realized/unrealized — Python 権威 (§7-c).
            "realized_pnl": resp.realized_pnl,
            "unrealized_pnl": resp.unrealized_pnl,
            # #185 (findings 0134): replay clock (latest/final bar ts) for the Run Result time line.
            "clock_ms": resp.clock_ms,
        }

    def get_portfolio_json(self) -> str:
        """#65: JSON string of get_portfolio() for the Replay poll (symmetric with get_state_json).

        C#'s ReplayPanelDecoder.DecodePortfolio takes a JSON string; get_portfolio returns a dict,
        so the Replay poll lane reads this. Keeps portfolio off the chart's get_state_json (TTWR's
        StateJson/Status 2-channel split — findings 0044 §2/§7-a).

        Honest-empty (findings 0044 §3/§7-b): when no run has produced a portfolio yet
        (`last_portfolio is None` — loaded-but-not-running, or just-cleared at run start), return ""
        so the C# panels show "(no data)" (ShowReplayEmpty) instead of a zero-filled snapshot
        ("bp=0 / flat / o:0"), which would misread as real zero buying power.
        """
        if getattr(self._srv.engine, "last_portfolio", None) is None:
            return ""
        return json.dumps(self.get_portfolio(), ensure_ascii=False)

    def get_run_summary_json(self) -> str:
        """#100 Slice ① (findings 0077): JSON string of the finalized run summary, polled by
        ``LiveRpcLanes`` for the Replay RunResult tile.  Symmetric with ``get_portfolio_json`` —
        Python is the single source (was previously a C#-owned ``_runSummaryJson`` set-at-return,
        which had no per-cell caller after the #95 Phase 6 ``TryStartRun(RunRequest)`` sunset).

        Honest-empty: ``engine.last_run_summary is None`` (run-begin clear before finalize, or
        first-run pre-start) → ``""``.  C# reads this as "running view": counts + realized/unrealized
        from the portfolio poll, NOT the prior run's fills/sharpe/drawdown.
        """
        summary = getattr(self._srv.engine, "last_run_summary", None)
        if summary is None:
            return ""
        return json.dumps(summary, ensure_ascii=False)

    def populate_replay_preview(
        self,
        instrument_id: str,
        start: str = "",
        end: str = "",
        granularity: str = "Daily",
    ) -> dict:
        """#129: thin wrapper — engine.populate_replay_preview owns the contract."""
        try:
            success, error_code, bar_count = self._srv.engine.populate_replay_preview(
                instrument_id=instrument_id,
                start=start or None,
                end=end or None,
                granularity=granularity or "Daily",
            )
            return {"success": success, "error_code": error_code, "bar_count": bar_count}
        except Exception as exc:
            logging.exception("[backend_service] populate_replay_preview failed for %s", instrument_id)
            return {"success": False, "error_code": "INPROC_ERROR", "bar_count": 0, "detail": str(exc)}

    def extend_replay_preview_left(
        self,
        instrument_id: str,
        before_open_time_ms: int,
        granularity: str = "Daily",
    ) -> dict:
        """#188: thin wrapper — engine.extend_replay_preview_left owns the contract."""
        try:
            success, error_code, bar_count = self._srv.engine.extend_replay_preview_left(
                instrument_id=instrument_id,
                before_open_time_ms=before_open_time_ms,
                granularity=granularity or "Daily",
            )
            return {"success": success, "error_code": error_code, "bar_count": bar_count}
        except Exception as exc:
            logging.exception("[backend_service] extend_replay_preview_left failed for %s", instrument_id)
            return {"success": False, "error_code": "INPROC_ERROR", "bar_count": 0, "detail": str(exc)}

    def clear_run_view(self) -> dict:
        """#100 Slice ① (findings 0077): document-boundary reset for File→New / File→Open.

        Clears BOTH ``last_portfolio`` and ``last_run_summary`` so the 4 Replay tiles (buying_power,
        positions, orders, run_result) drop to honest-empty when the user switches strategy
        documents — without this, the prior strategy's last run keeps showing in the new doc's
        tile until that doc's own bt run starts.  Idempotent (already-None → still-None ack).
        """
        try:
            self._srv.engine.last_portfolio = None
            self._srv.engine.last_run_summary = None
            return {"success": True, "error_code": ""}
        except Exception as exc:
            logging.exception("[backend_service] clear_run_view failed")
            return {"success": False, "error_code": "INPROC_ERROR", "detail": str(exc)}

    # ------------------------------------------------------------------
    # Venue lifecycle
    # ------------------------------------------------------------------

    def venue_login(
        self,
        venue_id: str,
        credentials_source: str,
        environment_hint: Optional[str],
    ) -> dict:
        try:
            # #181/ADR-0040: "prompt" is retired; an empty source is an explicit error
            # (orchestrator → INVALID_CREDENTIALS_SOURCE), not a silent dialog default.
            result = self._srv.venue_login(venue_id, credentials_source or "", environment_hint or "")
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "venue_state": "", "instruments_loaded": 0, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "venue_state": "", "instruments_loaded": 0, "detail": str(exc)}
        return {
            "success": result.success,
            "error_code": result.error_code,
            "venue_state": result.venue_state,
            "instruments_loaded": result.instruments_loaded,
        }

    def venue_login_form_init(self, venue_id: str, mode: str) -> dict:
        """#181/ADR-0040: prefill state for the Unity login modal (open / mode-switch)."""
        try:
            return self._srv.venue_login_form_init(venue_id, mode or "")
        except Exception as exc:
            return {"error_code": "INPROC_ERROR", "detail": str(exc)}

    def venue_login_probe_station(self, venue_id: str, mode: str) -> dict:
        """#181/ADR-0040: probe the venue's local station (kabu 本体起動確認)."""
        try:
            return self._srv.venue_login_probe_station(venue_id, mode or "")
        except Exception as exc:
            return {"running": False, "port": 0, "error_code": "INPROC_ERROR", "detail": str(exc)}

    def submit_venue_login(self, venue_id: str, mode: str, fields_json: str, secret: str) -> dict:
        """#181/ADR-0040: modal submit → validate → headless auth → finalize."""
        try:
            return self._srv.submit_venue_login(venue_id, mode or "", fields_json or "", secret or "")
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "status_text": "内部エラー", "allow_retry": True, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "status_text": "内部エラー", "allow_retry": True, "detail": str(exc)}

    def venue_logout(self) -> dict:
        return _call_ack(self._srv.venue_logout)

    # ------------------------------------------------------------------
    # Execution mode
    # ------------------------------------------------------------------

    def set_execution_mode(self, mode: str) -> dict:
        try:
            res = self._srv.set_execution_mode(mode)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "execution_mode": "", "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "execution_mode": "", "detail": str(exc)}
        return {
            "success": res.success,
            "error_code": res.error_code,
            "execution_mode": mode if res.success else "",
        }

    # ------------------------------------------------------------------
    # Instruments
    # ------------------------------------------------------------------

    def list_instruments(self, source: str, end_date: str = "") -> dict:
        try:
            result = self._srv.list_instruments(source, end_date)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "instruments": [], "instrument_ids": [], "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "instruments": [], "instrument_ids": [], "detail": str(exc)}
        return {
            "success": result.success,
            "error_code": result.error_message,
            "instrument_ids": list(result.instrument_ids),
            "instruments": [
                {"id": i.id, "name": i.name, "market": i.market}
                for i in result.instruments
            ],
        }

    def list_all_listed_symbols(self, end_date: str) -> dict:
        try:
            result = self._srv.list_all_listed_symbols(end_date)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "instrument_ids": [], "resolved_end_date": end_date, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "instrument_ids": [], "resolved_end_date": end_date, "detail": str(exc)}
        return {
            "success": result.success,
            "error_code": result.error_message,
            "instrument_ids": list(result.instrument_ids),
            "resolved_end_date": result.resolved_end_date,
        }

    # ------------------------------------------------------------------
    # Market data subscriptions
    # ------------------------------------------------------------------

    def subscribe_market_data(self, instrument_id: str) -> dict:
        return _call_ack(self._srv.subscribe_market_data, instrument_id)

    def subscribe_market_data_batch(self, instrument_ids) -> dict:
        # #107: orchestrator が既に plain dict（success/error_code/results）を返すので素通し。
        try:
            return self._srv.subscribe_market_data_batch(instrument_ids)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "results": [], "detail": str(exc)}
        except Exception:
            logging.exception("[backend_service] subscribe_market_data_batch failed")
            return {"success": False, "error_code": "INPROC_ERROR", "results": []}

    def unsubscribe_market_data(self, instrument_id: str) -> dict:
        return _call_ack(self._srv.unsubscribe_market_data, instrument_id)

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
        try:
            res = self._srv.place_order(
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
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "order_event": None, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "order_event": None, "detail": str(exc)}
        return {
            "success": res.success,
            "error_code": res.error_code,
            "order_event": _order_event_to_dict(res.order_event, res.strategy_id)
            if res.order_event is not None
            else None,
        }

    def cancel_order(
        self,
        venue: str,
        order_id: str,
        second_secret: Optional[str],
    ) -> dict:
        try:
            res = self._srv.cancel_order(
                venue=venue,
                order_id=order_id,
                second_secret=second_secret,
            )
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "order_event": None, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "order_event": None, "detail": str(exc)}
        return {
            "success": res.success,
            "error_code": res.error_code,
            "order_event": _order_event_to_dict(res.order_event, res.strategy_id)
            if res.order_event is not None
            else None,
        }

    def modify_order(
        self,
        venue: str,
        client_order_id: str,
        new_qty: Optional[float],
        new_price: Optional[float],
        second_secret: Optional[str],
    ) -> dict:
        try:
            res = self._srv.modify_order(
                venue=venue,
                order_id=client_order_id,
                new_price=new_price,
                new_qty=new_qty,
                second_secret=second_secret,
            )
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "order_event": None, "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "order_event": None, "detail": str(exc)}
        return {
            "success": res.success,
            "error_code": res.error_code,
            "order_event": _order_event_to_dict(res.order_event, res.strategy_id)
            if res.order_event is not None
            else None,
        }

    def get_orders(self, venue: str) -> dict:
        try:
            resp = self._srv.get_orders(venue=venue)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "orders": [], "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "orders": [], "detail": str(exc)}
        return {
            "success": resp.success,
            "error_code": resp.error_code,
            "orders": [_order_event_to_dict(o, resp.strategy_id) for o in resp.orders],
        }

    def submit_secret(self, request_id: str, secret: str) -> dict:
        return _call_ack(self._srv.submit_secret, request_id, secret)

    def force_account_snapshot(self) -> dict:
        return _call_ack(self._srv.force_account_snapshot)

    # ------------------------------------------------------------------
    # Live strategy lifecycle
    # ------------------------------------------------------------------

    def register_live_strategy(self, strategy_file: str, original_path: str = "") -> dict:
        try:
            resp = self._srv.register_live_strategy(strategy_file, original_path=original_path)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "strategy_id": "", "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "strategy_id": "", "detail": str(exc)}
        return {
            "success": resp.success,
            "error_code": resp.error_code,
            "strategy_id": resp.strategy_id,
            "error_message": resp.error_message if not resp.success else "",
        }

    def start_live_strategy(
        self,
        strategy_id: str,
        instrument_id: str,
        venue: str,
        safety_limits_dict: Optional[dict] = None,
    ) -> dict:
        try:
            resp = self._srv.start_live_strategy(strategy_id, instrument_id, venue, safety_limits_dict)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "run_id": "", "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "run_id": "", "detail": str(exc)}
        return {
            "success": resp.success,
            "error_code": resp.error_code,
            "run_id": resp.run_id if resp.success else "",
            "error_message": resp.error_message if not resp.success else "",
        }

    def stop_live_strategy(self, run_id: str) -> dict:
        return _call_ack(self._srv.stop_live_strategy, run_id)

    def pause_live_strategy(self, run_id: str) -> dict:
        return _call_ack(self._srv.pause_live_strategy, run_id)

    def resume_live_strategy(self, run_id: str) -> dict:
        return _call_ack(self._srv.resume_live_strategy, run_id)

    # ------------------------------------------------------------------
    # Strategy engine run (used by RunStrategy command)
    # ------------------------------------------------------------------

    def start_engine(self, cfg: dict) -> dict:
        """Delegate to DataEngineBackend.start_engine() for strategy backtest runs."""
        strategy_file = cfg.get("strategy_file", "")
        try:
            result = self._srv.start_engine(strategy_file)
        except RuntimeError as exc:
            return {"success": False, "error_code": "INPROC_ABORT", "run_id": "", "summary_json": "", "detail": str(exc)}
        except Exception as exc:
            return {"success": False, "error_code": "INPROC_ERROR", "run_id": "", "summary_json": "", "detail": str(exc)}
        return {
            "success": result.success,
            "error_code": result.error_code if not result.success else "",
            "error_message": result.error_message if not result.success else "",
            "run_id": result.run_id if result.success else "",
            "summary_json": result.summary_json if result.success else "",
        }

    def run_cell(
        self, source: str, pressed_index: int, scenario_json: str = None, strategy_path: str = None
    ) -> str:
        """#95 Phase 2/4: delegate per-cell RUN to DataEngineBackend.run_cell.

        ``scenario_json`` (Phase 4) is the committed startup-panel scenario; when present and the
        notebook drives a backtest the backend builds a ``bt`` handle. ``strategy_path`` (the
        document's canonical on-disk ``.py`` path) is forwarded so the marimo cell globals get the
        right ``__file__`` for cell-adjacent artifact resolution. Returns the backend's JSON string
        verbatim (``{"ok","ran","error","run_summary"?}``)."""
        try:
            return self._srv.run_cell(source, pressed_index, scenario_json, strategy_path)
        except Exception as exc:
            import json as _json

            logging.exception("[backend_service] run_cell failed")
            return _json.dumps({"ok": False, "ran": [], "error": f"{type(exc).__name__}: {exc}"})

    def notebook_restage(self, source: str) -> str:
        """#95 Phase 6 Slice 4: delegate edit-time stale projection to
        DataEngineBackend.notebook_restage.  Returns the backend's JSON string verbatim
        (``{"stale":[indices], "error"}``)."""
        try:
            return self._srv.notebook_restage(source)
        except Exception as exc:
            import json as _json

            logging.exception("[backend_service] notebook_restage failed")
            return _json.dumps({"stale": [], "error": f"{type(exc).__name__}: {exc}"})

    # #50 (ADR-0006): the nautilus BacktestEngine→GUI bridge (#68 start_nautilus_replay +
    # pause/step/resume/set_replay_speed) was retired with nautilus. Production Replay runs
    # through start_engine (DuckDB→kernel); these forwarders had no production caller.

    # ------------------------------------------------------------------
    # Teardown
    # ------------------------------------------------------------------

    def teardown(self) -> None:
        try:
            self._srv._teardown_live_components()
        except Exception:
            logging.exception("[backend_service] teardown: _teardown_live_components failed")
        try:
            self._srv.stop_live_loop(timeout=1.0)
        except Exception:
            logging.exception("[backend_service] teardown: stop_live_loop failed")

    def stop_live_loop(self, timeout: float = 5.0) -> bool:
        # Returns True if the live loop thread joined cleanly (safe to finalize
        # the Python runtime), False if it hung (host must NOT finalize — #22
        # Gap4). Fail closed (False) on error.
        try:
            return self._srv.stop_live_loop(timeout=timeout)
        except Exception:
            logging.exception("[backend_service] stop_live_loop failed")
            return False
