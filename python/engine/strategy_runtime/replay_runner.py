"""engine.strategy_runtime.replay_runner — unified BacktestEngine streaming runner (issue #190).

Single module that owns BacktestEngine lifecycle + streaming loop.
Output is injected via ReplaySink adapter interface, eliminating the duplication
between nautilus_backtest_runner and engine_runner.

Public API:
    ReplaySink      — core adapter Protocol (on_equity / on_fill / on_complete)
    run(...)        — streaming loop entry point
"""
from __future__ import annotations

import logging
import threading
from typing import Any, Protocol, runtime_checkable

log = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Sink adapter interface
# ---------------------------------------------------------------------------


@runtime_checkable
class ReplaySink(Protocol):
    """Adapter interface for BacktestEngine streaming replay.

    on_equity / on_fill are called from internal msgbus subscriptions.
    on_complete is called once after the streaming loop ends.

    For bar / order / position events, optionally implement::

        def get_extra_subscriptions(
            self, *, engine, instruments, granularity,
            strategy_id_str, cache, venue_str,
        ) -> dict[str, Any]:
            ...

    The runner duck-types this hook; returning a non-empty dict causes the
    runner to subscribe each (topic, handler) pair to the engine's msgbus
    before the streaming loop starts.
    """

    def on_equity(self, event: dict) -> None: ...  # noqa: E704
    def on_fill(self, event: dict) -> None: ...  # noqa: E704
    def on_complete(self, engine: Any) -> None: ...  # noqa: E704


# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------


def run(
    *,
    strategy_cls,
    scenario: dict,
    bars_by_instrument: dict,
    sink: ReplaySink,
    instruments_override: list[str] | None = None,
    strategy_init_kwargs: dict | None = None,
    run_event: threading.Event | None = None,
    bar_interval_sec: float = 0.0,
) -> None:
    """BacktestEngine streaming runner — 1 bar at a time.

    Parameters
    ----------
    strategy_cls:
        nautilus_trader.trading.strategy.Strategy subclass.
    scenario:
        SCENARIO dict. Uses granularity / initial_cash / account_type.
        instruments are taken from instruments_override if provided,
        otherwise from scenario["instruments"] / scenario["instrument"].
    bars_by_instrument:
        {InstrumentId: list[Bar]} from catalog_data_loader or synthetic bars.
    sink:
        ReplaySink adapter. Receives on_equity / on_fill / on_complete.
        Optionally implements get_extra_subscriptions() for bar/order/position.
    instruments_override:
        When provided, replaces scenario instruments. Implements ADR-0007:
        Instrument Registry wins over SCENARIO. Caller intent is explicit.
    strategy_init_kwargs:
        Passed as strategy_cls(**kwargs). None → {}.
    run_event:
        threading.Event; run_event.wait() is called before each bar.
        None means unthrottled. Owned by this module (not the adapter).
    bar_interval_sec:
        Wallclock sleep in seconds after each bar. 0 = disabled.
    """
    import time as _time

    from nautilus_trader.backtest.engine import BacktestEngine
    from nautilus_trader.config import BacktestEngineConfig, LoggingConfig
    from nautilus_trader.model.currencies import JPY
    from nautilus_trader.model.enums import AccountType, OmsType
    from nautilus_trader.model.identifiers import InstrumentId, Venue
    from nautilus_trader.model.objects import Money

    from engine.strategy_runtime.catalog_data_loader import (
        bar_type_for_instrument,
        instruments_from_scenario,
        merge_bars_by_ts,
        normalize_granularity,
    )
    from engine.strategy_runtime.instrument_factory import make_equity_instrument
    from engine.strategy_runtime.scenario import ScenarioValidationError

    kwargs = strategy_init_kwargs or {}
    granularity = normalize_granularity(scenario["granularity"])

    instruments = (
        list(instruments_override)
        if instruments_override is not None
        else instruments_from_scenario(scenario)
    )
    initial_cash = int(scenario.get("initial_cash", 10_000_000))

    _account_type_map = {"CASH": AccountType.CASH, "MARGIN": AccountType.MARGIN}
    raw_account_type = scenario.get("account_type", "CASH")
    if not isinstance(raw_account_type, str):
        raise ScenarioValidationError(
            f"SCENARIO['account_type'] must be str, "
            f"got {type(raw_account_type).__name__}"
        )
    if raw_account_type not in _account_type_map:
        raise ScenarioValidationError(
            f"SCENARIO['account_type'] must be one of "
            f"{sorted(_account_type_map)}, got {raw_account_type!r}"
        )
    account_type = _account_type_map[raw_account_type]

    venue_str = InstrumentId.from_str(instruments[0]).venue.value
    venue_obj = Venue(venue_str)

    cfg = BacktestEngineConfig(
        trader_id="REPLAYRUNNER-001",
        logging=LoggingConfig(bypass_logging=True),
    )
    assert cfg.cache.database is None, "nautilus persistence must be disabled"

    engine = BacktestEngine(config=cfg)
    all_subscriptions: list[tuple[str, Any]] = []

    try:
        engine.add_venue(
            venue=venue_obj,
            oms_type=OmsType.NETTING,
            account_type=account_type,
            base_currency=JPY,
            starting_balances=[Money(initial_cash, JPY)],
        )

        for symbol in instruments:
            ticker = InstrumentId.from_str(symbol).symbol.value
            engine.add_instrument(make_equity_instrument(ticker, venue_str))

        strategy = strategy_cls(**kwargs)
        engine.add_strategy(strategy)
        strategy_id_str = str(strategy.id)

        # ── Fill subscriptions → sink.on_fill ─────────────────────────────────
        def _make_fill_handler(iid_str: str):
            def _on_fill(event) -> None:
                try:
                    record: dict = {
                        "instrument_id": iid_str,
                        "side": event.order_side.name,
                        "qty": str(event.last_qty),
                        "price": str(event.last_px),
                        "ts_event_ms": event.ts_event // 1_000_000,
                    }
                    commission_raw = getattr(event, "commission", None)
                    if commission_raw is not None:
                        try:
                            record["commission"] = str(commission_raw.as_decimal())
                        except AttributeError:
                            record["commission"] = str(commission_raw)
                    try:
                        order = engine.cache.order(event.client_order_id)
                        tags = getattr(order, "tags", None) if order is not None else None
                        if tags:
                            record["tags"] = list(tags)
                    except Exception:
                        pass
                    sink.on_fill(record)
                except Exception:
                    log.warning(
                        "[replay_runner] on_fill failed: instrument=%r",
                        iid_str,
                        exc_info=True,
                    )

            return _on_fill

        for symbol in instruments:
            handler = _make_fill_handler(symbol)
            topic = f"events.fills.{symbol}"
            engine.kernel.msgbus.subscribe(topic=topic, handler=handler)
            all_subscriptions.append((topic, handler))

        # ── Bar subscriptions → sink.on_equity ────────────────────────────────
        # normalize_granularity() already raises ValueError for unknown values, so
        # granularity here is always "Daily" or "Minute" — no runtime guard needed.
        def _on_bar_equity(bar) -> None:
            try:
                account = engine.kernel.portfolio.account(venue_obj)
                if account is not None:
                    balance = account.balance_total(JPY)
                    if balance is not None:
                        equity = float(str(balance.as_decimal()))
                    else:
                        equity = float(initial_cash)
                else:
                    equity = float(initial_cash)
                sink.on_equity(
                    {
                        "ts_event_ms": bar.ts_event // 1_000_000,
                        "equity": equity,
                    }
                )
            except Exception:
                log.warning("[replay_runner] on_equity failed", exc_info=True)

        for symbol in instruments:
            bar_type_str = bar_type_for_instrument(symbol, granularity)
            topic = f"data.bars.{bar_type_str}"
            engine.kernel.msgbus.subscribe(topic=topic, handler=_on_bar_equity)
            all_subscriptions.append((topic, _on_bar_equity))

        # ── Extra subscriptions from sink (bar/order/position hooks) ──────────
        extra_sub_getter = getattr(sink, "get_extra_subscriptions", None)
        if extra_sub_getter is not None:
            extra: dict[str, Any] = extra_sub_getter(
                engine=engine,
                instruments=instruments,
                granularity=granularity,
                strategy_id_str=strategy_id_str,
                cache=engine.kernel.cache,
                venue_str=venue_str,
            ) or {}
            for topic, handler in extra.items():
                if handler is not None:
                    engine.kernel.msgbus.subscribe(topic=topic, handler=handler)
                    all_subscriptions.append((topic, handler))

        # ── Streaming loop: 1 bar at a time ──────────────────────────────────
        items = merge_bars_by_ts(bars_by_instrument)
        log.info(
            "[replay_runner] start: instruments=%r granularity=%r bars=%d",
            instruments,
            granularity,
            len(items),
        )

        for item in items:
            if run_event is not None:
                run_event.wait()
            engine.add_data([item])
            engine.run(streaming=True)
            engine.clear_data()
            if bar_interval_sec > 0:
                _time.sleep(bar_interval_sec)

        log.info("[replay_runner] complete: bars=%d", len(items))
        sink.on_complete(engine)

    finally:
        for topic, handler in all_subscriptions:
            try:
                engine.kernel.msgbus.unsubscribe(topic=topic, handler=handler)
            except Exception:
                pass
        try:
            engine.dispose()
        except Exception:
            pass
