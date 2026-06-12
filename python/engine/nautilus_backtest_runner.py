"""NautilusBacktestRunner — BacktestEngine streaming runner for GUI (issue #68).

Delegates to replay_runner.run() via _GuiSink adapter, which:
  - accumulates equity_curve via on_equity()
  - exposes bar/order/position subscriptions via get_extra_subscriptions()
  - computes summary stats and calls rust_sink.push_run_complete() on on_complete()

Public API:
    NautilusBacktestRunner(*, catalog_path, strategy_file, instruments,
                           start_date, end_date, granularity,
                           initial_cash, rust_sink,
                           pause_event=None, step_event=None, speed_ref=None)
    .run() -> {"success": bool, "run_id": str, "error": str}
"""
from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

log = logging.getLogger(__name__)


class NautilusBacktestRunner:
    """Runs a strategy via BacktestEngine and streams bars to RustBacktestSink.

    Instruments / date range from the caller override the SCENARIO embedded in the
    strategy file (issue #68: Instrument Registry wins over SCENARIO, ADR-0007).
    """

    def __init__(
        self,
        *,
        catalog_path: str,
        strategy_file: str,
        instruments: list[str],
        start_date: str = "",
        end_date: str = "",
        granularity: str = "Daily",
        initial_cash: float = 10_000_000.0,
        rust_sink: Any,
        pause_event: Any = None,
        step_event: Any = None,
        speed_ref: Any = None,
        original_path: str | None = None,
    ) -> None:
        self._catalog_path = catalog_path
        self._strategy_file = strategy_file
        self._instruments = instruments
        self._start_date = start_date
        self._end_date = end_date
        self._granularity = granularity
        self._initial_cash = int(initial_cash)
        self._rust_sink = rust_sink
        self._pause_event = pause_event
        self._step_event = step_event
        self._speed_ref = speed_ref
        self._original_path: Path | None = Path(original_path) if original_path else None

    def run(self) -> dict:
        """Execute the backtest synchronously.

        Returns {"success": bool, "run_id": str, "error": str}.
        Bars stream to rust_sink.push_bar() as they are processed.
        push_run_complete() is called once on success.
        """
        from engine.strategy_runtime import strategy_loader
        from engine.strategy_runtime.catalog_data_loader import (
            bar_type_for_instrument,
            load_bars_for_scenario,
            normalize_granularity,
        )
        from engine.live.gui_bridge_actor import GuiBridgeActor
        from engine.strategy_runtime.replay_runner import run as _replay_run
        from engine.strategy_runtime.summary import equity_curve_stats

        # --- Load strategy file -----------------------------------------------
        try:
            _module, scenario, strategy_cls = strategy_loader.load(
                self._strategy_file, original_path=self._original_path
            )
        except Exception as exc:
            return {"success": False, "run_id": "", "error": f"strategy load failed: {exc}"}

        # Instrument Registry overrides SCENARIO (ADR-0007)
        if self._instruments:
            scenario["instruments"] = list(self._instruments)
        if self._start_date:
            scenario["start"] = self._start_date
        if self._end_date:
            scenario["end"] = self._end_date

        try:
            granularity = normalize_granularity(self._granularity)
        except ValueError as exc:
            return {"success": False, "run_id": "", "error": str(exc)}

        # --- Load bars from catalog --------------------------------------------
        try:
            bars_by_instrument = load_bars_for_scenario(self._catalog_path, scenario)
        except Exception as exc:
            return {"success": False, "run_id": "", "error": f"catalog load failed: {exc}"}

        # --- Build GuiBridgeActor and GUI sink adapter -------------------------
        instruments = list(scenario["instruments"])
        bridge = GuiBridgeActor(
            self._rust_sink,
            instrument_id=instruments[0] if instruments else "",
            pause_event=self._pause_event,
            step_event=self._step_event,
            speed_ref=self._speed_ref,
        )

        rust_sink = self._rust_sink
        equity_events: list[dict] = []

        class _GuiSink:
            """ReplaySink adapter for GUI runner.

            - on_equity: accumulates equity_curve for summary stats.
            - on_fill: no-op (GUI uses events.order subscription, not events.fills).
            - on_complete: computes stats + calls rust_sink.push_run_complete().
            - get_extra_subscriptions: returns bar/order/position handlers.
            """

            def on_equity(self, event: dict) -> None:
                equity_events.append(event)

            def on_fill(self, event: dict) -> None:
                pass

            def on_complete(self, engine: Any) -> None:
                from nautilus_trader.model.enums import OrderStatus as _OrderStatus

                fills_count = sum(
                    1
                    for o in engine.kernel.cache.orders_closed()
                    if o.status in (_OrderStatus.FILLED, _OrderStatus.PARTIALLY_FILLED)
                )
                equity_curve = [e["equity"] for e in equity_events]
                stats = equity_curve_stats(equity_curve)
                summary = json.dumps(
                    {
                        "fills_count": fills_count,
                        "equity_points": len(equity_curve),
                        "max_drawdown": stats["max_drawdown"],
                        "sharpe": stats["sharpe"],
                        "sortino": stats["sortino"],
                    }
                )
                log.info(
                    "[NautilusBacktestRunner] complete: bars=%d summary=%s",
                    len(equity_events),
                    summary,
                )
                rust_sink.push_run_complete("", summary)

            def get_extra_subscriptions(
                self,
                *,
                engine: Any,
                instruments: list[str],
                granularity: str,
                strategy_id_str: str,
                cache: Any,
                venue_str: str,
            ) -> dict[str, Any]:
                subs: dict[str, Any] = {}
                bar_handler = bridge.make_bar_handler()
                for symbol in instruments:
                    bar_type_str = bar_type_for_instrument(symbol, granularity)
                    subs[f"data.bars.{bar_type_str}"] = bar_handler
                subs[f"events.order.{strategy_id_str}"] = bridge.make_order_handler()
                subs[f"events.position.{strategy_id_str}"] = bridge.make_position_handler(
                    cache=cache, venue_str=venue_str
                )
                return subs

        sink = _GuiSink()

        log.info(
            "[NautilusBacktestRunner] start: instruments=%r granularity=%r",
            instruments,
            granularity,
        )

        try:
            _replay_run(
                strategy_cls=strategy_cls,
                scenario=scenario,
                bars_by_instrument=bars_by_instrument,
                sink=sink,
                instruments_override=self._instruments or None,
                run_event=None,  # pause/step gate lives in GuiBridgeActor.make_bar_handler()
            )
            return {"success": True, "run_id": "", "error": ""}
        except Exception as exc:
            log.error("[NautilusBacktestRunner] run failed: %s", exc, exc_info=True)
            return {"success": False, "run_id": "", "error": str(exc)}
