"""engine.kernel.stepper — the per-bar Replay state machine (#95 Phase 3).

``KernelStepper`` is the extract of the per-bar loop that used to live inline in
``KernelRunner.run()`` (#24). Splitting "drive one bar" out of "drive every bar" lets the
notebook ``bt`` handle (``engine.strategy_runtime.backtester.Backtester``) consume the SAME
state machine as a public API to step bar-by-bar (``bt.step()``) or stream
(``bt.replay()``), without reaching into ``KernelRunner`` private state (#95 findings 0072
Q1). ``KernelRunner.run()`` is now a thin wrapper that loads bars and drives the stepper to
the end — so the #24 golden stays byte-identical (ADR-0006): the wrapper, ``bt.replay()``
and ``bt.step()`` all hit the same three primitives in the same order.

Bar lifecycle (#95 Q2, γ — auto-close-on-next-step + an idempotent explicit hook):

    handle = stepper.open_next_bar()   # auto-closes the previous bar if still open
    # caller runs the body: Strategy.on_bar (wrapper) or the bt cell body
    stepper.close_current_bar()        # idempotent; the Phase 2 per-cell-RUN finally calls it
    result = stepper.finalize()        # idempotent; valid only after END / STOPPED

Per-bar event order (findings 0008 §2, byte-identical to the imperative oracle):

    push_bar(N) → on_bar(N) [may submit] → denials → fill accepted MARKET @ bar N close →
        on_order(fill) → push_order → portfolio.apply → push_portfolio
    → last_prices[N] → equity point (post-fill cash) → on_equity
    → post-trade rail → (next bar | finalize: on_stop → push_run_complete)

Nautilus-free: importing this module loads no Rust core (gated by the Mono teardown test)
and no marimo (gated by test_strategy_runtime_offline).
"""
from __future__ import annotations

import enum
from dataclasses import dataclass
from typing import Optional

from engine.kernel.broker import ReplayBroker
from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import (
    Order,
    OrderDenied,
    OrderEngine,
    OrderSide,
    OrderStatus,
    signed_qty_to_side,
)
from engine.kernel.portfolio import Portfolio, PortfolioSnapshot
from engine.kernel.risk import RiskEngine
from engine.kernel.sink import EventSink
from engine.kernel.strategy import Strategy
from engine.live.safety_rails import KIND_NO_REFERENCE_PRICE, RailViolation, SafetyRails


@dataclass
class RunResult:
    success: bool
    bars: int
    fills: int
    final_cash: float
    final_equity: float
    realized_pnl: float
    error: str = ""
    stopped_reason: str = ""  # set when a post-trade rail (or stop_event) halted the run early


class StepEvent(enum.Enum):
    """What ``open_next_bar()`` did.

    A domain reason (which rail halted, a stop request) is carried separately in
    ``StepHandle.reason`` / ``RunResult.stopped_reason`` — folding ``RAILS_HALTED`` into this
    enum would mix control flow with domain reason (#95 Q2).
    """

    BAR_OPEN = "bar_open"   # a bar was opened (push_bar done); run the body, submits are valid
    END = "end"             # every bar processed; finalize done; idempotent thereafter
    STOPPED = "stopped"     # halted early by a rail or stop_event; finalize done; reason set


@dataclass(frozen=True)
class StepHandle:
    event: StepEvent
    bar: Optional[Bar] = None    # non-None only for BAR_OPEN
    reason: str = ""             # STOPPED reason (rail violation.kind, or "stopped")


class _Context:
    """StrategyContext implementation: turns submit_market into an OrderEngine submit.

    Accepted orders are queued for the current bar; the stepper fills them at bar close.
    """

    def __init__(self, *, order_engine: OrderEngine, portfolio: Portfolio) -> None:
        self._engine = order_engine
        self._portfolio = portfolio
        self._client_seq = 0
        self.pending: list[Order] = []
        self.denials: list[OrderDenied] = []
        # Latest close by instrument, with the current bar overlaid before on_bar. The
        # order path uses the order instrument's price for pre-trade notional checks and
        # replay fills; unknown prices are denied instead of leaking another symbol's close.
        self.reference_prices: dict[str, float] = {}
        self.ts_event_ns: int = 0

    def submit_market(
        self, *, strategy_id: str, instrument_id: str, side: OrderSide, quantity: float
    ) -> None:
        self._client_seq += 1
        order = Order(
            client_order_id=f"O-{strategy_id}-{self._client_seq}",
            strategy_id=strategy_id,
            instrument_id=instrument_id,
            side=side,
            quantity=quantity,
        )
        reference_price = self.reference_prices.get(instrument_id)
        if reference_price is None:
            self.denials.append(
                OrderDenied(
                    client_order_id=order.client_order_id,
                    strategy_id=strategy_id,
                    instrument_id=instrument_id,
                    side=side,
                    quantity=quantity,
                    kind=KIND_NO_REFERENCE_PRICE,
                    reason=(
                        f"no reference price for {instrument_id}; "
                        "cannot fill MARKET order before market data"
                    ),
                    ts_event_ns=self.ts_event_ns,
                )
            )
            return

        violation: RailViolation | None = self._engine.submit(
            order,
            net_signed_qty=self._portfolio.net_signed_qty(instrument_id),
            reference_price=reference_price,
            order_notional_jpy=reference_price * quantity,
        )
        if violation is not None:
            self.denials.append(
                OrderDenied(
                    client_order_id=order.client_order_id,
                    strategy_id=strategy_id,
                    instrument_id=instrument_id,
                    side=side,
                    quantity=quantity,
                    kind=violation.kind,
                    reason=violation.detail,
                    ts_event_ns=self.ts_event_ns,
                )
            )
        else:
            self.pending.append(order)

    def log(self, message: str) -> None:  # tracer: logging is a no-op sink
        pass

    def buying_power(self) -> float:
        return float(self._portfolio.cash)

    def portfolio_snapshot(self, instrument_id: str | None = None) -> PortfolioSnapshot:
        """Cell-facing read seam (#76): a frozen pre-fill snapshot marked to market at the
        current reference_prices (the bar close orders fill at). Called at on_bar entry —
        before this bar's fill — so the book is end-of-(N-1) = no-look-ahead."""
        return self._portfolio.snapshot(
            self.reference_prices, instrument_id, buying_power=self.buying_power()
        )


class KernelStepper:
    """Bar-by-bar Replay state machine over a pre-loaded bar list (#95 Phase 3).

    Builds the same collaborators ``KernelRunner`` used (RiskEngine / OrderEngine / Portfolio
    / ReplayBroker / _Context / EventSink) and exposes three idempotent-where-it-counts
    primitives — ``open_next_bar`` / ``close_current_bar`` / ``finalize`` — that drive the
    frozen per-bar order. ``strategy=None`` skips the user hooks (register / on_start / on_bar
    / on_stop / on_order) ONLY; fills, denials, sink pushes and portfolio accounting are always
    the stepper's responsibility (#95 Q4) — that is the ``bt`` handle's mode, where the cell
    body plays the on_bar role between ``open_next_bar()`` returning and the next call.

    Single-threaded (#95 Q5 C1/D1): a host worker-thread driver and a busy guard are Phase 4.
    """

    def __init__(
        self,
        *,
        bars: list[Bar],
        instrument_ids: list[str],
        initial_cash: float,
        strategy: Strategy | None = None,
        strategy_id: str = "",
        push_target=None,
        sink=None,
        rails: Optional[SafetyRails] = None,
        bar_interval_sec: float = 0.0,
        stop_event=None,
    ) -> None:
        if not instrument_ids:
            raise ValueError("instrument_ids must be non-empty")
        self._instrument_ids = list(instrument_ids)
        venues = {iid.split(".")[-1] for iid in self._instrument_ids}
        if len(venues) != 1:
            raise ValueError(f"universe must share a single venue, got {sorted(venues)}")
        self._venue = venues.pop()
        self._bars = bars
        self._strategy = strategy
        # When no Strategy drives the run (the bt handle), submits still need a strategy_id
        # for the client_order_id; the host binds it (mirrors cell_api's host-bound id).
        self._strategy_id = strategy.id if strategy is not None else strategy_id
        self._risk = RiskEngine(rails)
        self._order_engine = OrderEngine(risk_engine=self._risk, venue=self._venue)
        self._portfolio = Portfolio(initial_cash=initial_cash)
        self._broker = ReplayBroker(self._order_engine)
        # Observer injection (#49): golden #24 callers pass `push_target` and get the default
        # EventSink (RustBacktestSink JSON contract). The production Replay seam passes its own
        # `sink` (ReplayKernelObserver) so the SAME per-bar loop drives both, anti-divergence by
        # construction. The kernel never imports the observer or its deps.
        if sink is not None:
            self._sink = sink
        elif push_target is not None:
            self._sink = EventSink(push_target)
        else:
            raise ValueError("KernelStepper requires either push_target or sink")
        self._ctx = _Context(order_engine=self._order_engine, portfolio=self._portfolio)
        # Wallclock throttle (#29): KernelRunner forwards its bar_interval_sec; default 0 → the
        # golden path runs straight through, byte-identical. The bt handle leaves it 0 — pacing
        # (bt.replay(bars_per_second=N) → per-bar sleep) is Phase 4 (#95 Q5 A1).
        self._bar_interval_sec = bar_interval_sec
        # Stop seam (#49 review #5 / #95 Q5 B1): the host sets this; open_next_bar checks it so a
        # long run halts instead of streaming every remaining bar. Default None → never breaks
        # early (byte-identical).
        self._stop_event = stop_event

        # Hoisted run state (was method-local in runner.run()). These produce the golden, so
        # extracting them is what makes this "more than a wrap" (#95 findings 0072 Q1).
        self._index = -1
        self._current_bar: Optional[Bar] = None
        self._last_bar: Optional[Bar] = None
        self._bar_open = False
        self._started = False
        self._equity_curve: list[float] = []
        self._fills = 0
        self._last_prices: dict[str, float] = {}
        self._baseline_equity = 0.0
        self._rails_active = self._risk.rails is not None
        self._stopped_reason = ""
        self._terminal: Optional[StepHandle] = None  # set once END / STOPPED is reached
        self._result: Optional[RunResult] = None     # set once finalize runs

    # -- pacing (#95 Phase 4) -------------------------------------------------

    def set_pacing(self, bars_per_second: Optional[float]) -> None:
        """Set the per-bar wallclock throttle from a bars-per-second rate (#95 Phase 4 / D8-D9).

        ``None`` → full speed (no sleep); the GIL is handed off by CPython auto-switch, not an
        explicit floor (findings 0070 F6 — ``bt.replay()`` captures the rate at the START of a
        stream so it is immutable for that run; speed is NOT a live-mutable register). A positive
        rate sets ``1 / bars_per_second`` as the per-bar pacing sleep (the watchable-playback
        feature); a non-positive rate is a caller typo and fails closed."""
        if bars_per_second is None:
            self._bar_interval_sec = 0.0
        elif bars_per_second > 0:
            self._bar_interval_sec = 1.0 / bars_per_second
        else:
            raise ValueError(f"bars_per_second must be positive, got {bars_per_second!r}")

    # -- primitives -----------------------------------------------------------

    def open_next_bar(self) -> StepHandle:
        """Close the open bar (if any), then open and return the next bar — or finalize.

        Auto-close-on-next-step (#95 Q2 γ): the previous bar's denials/fills/equity/rail run
        here, before the next bar opens, so a plain ``while open_next_bar().event is BAR_OPEN``
        loop reproduces the golden order exactly.
        """
        if self._terminal is not None:
            return self._terminal

        self._ensure_started()
        self.close_current_bar()  # idempotent; may set self._stopped_reason via a post-trade rail

        if self._stopped_reason:  # a rail halted the bar we just closed
            return self._to_terminal(StepEvent.STOPPED, self._stopped_reason)
        # END before the stop check: the legacy loop only checked stop_event at the TOP of an
        # iteration that had a bar to process, so an exhausted/empty stream reaches END with
        # stopped_reason="" even if stop_event fired as the last bar settled (faithful parity).
        if self._index + 1 >= len(self._bars):
            return self._to_terminal(StepEvent.END)
        if self._stop_event is not None and self._stop_event.is_set():
            self._stopped_reason = "stopped"
            return self._to_terminal(StepEvent.STOPPED, "stopped")

        self._index += 1
        bar = self._bars[self._index]
        self._open_bar(bar)
        return StepHandle(StepEvent.BAR_OPEN, bar=bar)

    def close_current_bar(self) -> None:
        """Settle the currently-open bar: denials → fills → equity point → post-trade rail.

        Idempotent: a no-op when no bar is open, so the Phase 2 per-cell-RUN ``finally`` can
        call it without knowing whether ``open_next_bar`` already closed it (#95 Q2).
        """
        if not self._bar_open:
            return
        self._bar_open = False
        bar = self._current_bar
        assert bar is not None  # _bar_open implies a current bar

        # Risk-denied orders surface to on_order but never fill or touch the sink.
        for denied in self._ctx.denials:
            if self._strategy is not None:
                self._strategy.on_order(denied)
        self._ctx.denials.clear()

        # Accepted MARKET orders fill at the order instrument's latest close, in submission
        # order. For the current bar's instrument this is bar.close.
        pending = self._ctx.pending
        self._ctx.pending = []
        for order in pending:
            if order.status is not OrderStatus.ACCEPTED:
                continue
            fill = self._broker.fill_market(
                order,
                price=self._ctx.reference_prices[order.instrument_id],
                ts_event_ns=bar.ts_event_ns,
            )
            self._portfolio.apply_fill(fill)
            if self._strategy is not None:
                self._strategy.on_order(fill)
            self._sink.push_order(fill)
            self._sink.push_portfolio(self._portfolio)
            self._fills += 1

        # Track each instrument's latest close so equity can be marked to market (cash +
        # open-position value), consistent with how live venues report account value.
        self._last_prices[bar.instrument_id] = bar.close
        mtm_equity = self._portfolio.mark_to_market_equity(self._last_prices)

        # equity_curve feeds the kernel's RunResult/summary = the FROZEN #24 golden (cash-basis,
        # oracle-matched, immutable per ADR-0006). Left as cash so the golden stays byte-identical.
        self._equity_curve.append(self._portfolio.cash)
        # Production observer projection (#49 review #2): mark-to-market equity + realized cash,
        # mirrored into RunBuffer per bar. No-op for the golden EventSink. Called unconditionally.
        self._sink.on_equity(
            bar.ts_event_ns // 1_000_000, mtm_equity, self._portfolio.cash
        )

        # Post-trade rail on mark-to-market equity (skipped when rails are unconfigured, so the
        # golden tracer is untouched). On a violation we record the reason and skip the throttle
        # sleep — the run is about to halt (matches the legacy break-before-sleep order).
        if self._rails_active:
            violation = self._risk.check_post_trade(
                equity=mtm_equity,
                baseline_equity=self._baseline_equity,
            )
            if violation is not None:
                self._stopped_reason = violation.kind
                return

        # Wallclock throttle (last in the body, like the legacy replay loop): releases the GIL
        # between bars so the host's poll thread reads the incrementally-streamed chart. Default
        # 0 → golden runs straight through. (bt pacing is Phase 4 — #95 Q5 A1.)
        if self._bar_interval_sec > 0:
            import time as _time

            _time.sleep(self._bar_interval_sec)

    def finalize(self) -> RunResult:
        """Return the RunResult. Valid only after END / STOPPED (idempotent thereafter).

        finalize is NOT a resource-cleanup API (the stepper owns no marimo Kernel / DuckDB
        connection — #95 Q6 α teardown = drop the reference); it is the run-result primitive.
        """
        if self._result is None:
            raise RuntimeError(
                "finalize() before the run reached END/STOPPED — drive open_next_bar() until "
                "it returns a non-BAR_OPEN handle first"
            )
        return self._result

    # -- bt handle read / submit seams ---------------------------------------

    def current_or_last_bar(self) -> Optional[Bar]:
        """The bar ``bt.bar()`` reports: the open bar while a bar is open, else the last bar
        opened (None before the first step). Per #95 Q3 the open and just-closed bar are the
        same object, so the most-recently-opened bar covers every lifecycle state."""
        return self._last_bar

    def portfolio_snapshot(self) -> PortfolioSnapshot:
        """The snapshot ``bt.portfolio()`` reports: primary = the current/last bar's instrument
        (None before the first step → aggregate snapshot, position 0, empty positions). The
        ``positions`` mapping keeps multi-instrument observation open (#95 Q3)."""
        primary = self._last_bar.instrument_id if self._last_bar is not None else None
        return self._ctx.portfolio_snapshot(primary)

    def submit_market(self, qty: float) -> None:
        """``bt.submit_market(qty)``: signed delta to the OPEN bar's instrument (#95 Q3 a).

        Fail-closed with ValueError outside an open bar (before the first step, after a close,
        or after END/STOPPED) — there is no implicit instrument to target then (Q3 lock)."""
        if not self._bar_open or self._current_bar is None:
            raise ValueError(
                "bt.submit_market() requires an open bar — call it inside a bt.step() / "
                "bt.replay() body (no bar is open before the first step or after a close)"
            )
        resolved = signed_qty_to_side(qty)  # NaN/inf → ValueError; 0/-0.0 → None (no-op)
        if resolved is None:
            return
        side, quantity = resolved
        self._ctx.submit_market(
            strategy_id=self._strategy_id,
            instrument_id=self._current_bar.instrument_id,
            side=side,
            quantity=quantity,
        )

    # -- internals ------------------------------------------------------------

    def _ensure_started(self) -> None:
        """Register the strategy and fix the post-trade baseline, once, before the first bar.

        Mirrors runner.run()'s pre-loop block: register → on_start → baseline = run-start
        mark-to-market equity (flat → initial cash), fixed before any bar/fill."""
        if self._started:
            return
        self._started = True
        if self._strategy is not None:
            self._strategy.register(self._ctx)
            self._strategy.on_start()
        self._baseline_equity = self._portfolio.mark_to_market_equity(self._last_prices)

    def _open_bar(self, bar: Bar) -> None:
        self._sink.push_bar(bar)
        # MARKET uses the order instrument's latest close. Overlay only the current instrument
        # so single-symbol replay remains fill-at-current-bar-close.
        self._ctx.reference_prices = {**self._last_prices, bar.instrument_id: bar.close}
        self._ctx.ts_event_ns = bar.ts_event_ns
        self._current_bar = bar
        self._last_bar = bar
        self._bar_open = True
        if self._strategy is not None:
            self._strategy.on_bar(bar)

    def _to_terminal(self, event: StepEvent, reason: str = "") -> StepHandle:
        self._finalize_run()
        self._terminal = StepHandle(event, bar=None, reason=reason)
        return self._terminal

    def _finalize_run(self) -> None:
        if self._result is not None:
            return
        from engine.strategy_runtime.summary import equity_curve_stats

        if self._strategy is not None:
            self._strategy.on_stop()

        stats = equity_curve_stats(self._equity_curve)
        summary = {
            "fills_count": self._fills,
            "equity_points": len(self._equity_curve),
            "max_drawdown": stats["max_drawdown"],
            "sharpe": stats["sharpe"],
            "sortino": stats["sortino"],
        }
        self._sink.push_run_complete("", summary)

        self._result = RunResult(
            success=True,
            bars=len(self._equity_curve),  # bars PROCESSED (< len(bars) on an early halt)
            fills=self._fills,
            final_cash=self._portfolio.cash,
            final_equity=self._portfolio.equity,
            realized_pnl=self._portfolio.realized_pnl,
            stopped_reason=self._stopped_reason,
        )
