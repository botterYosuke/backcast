"""engine.kernel.runner — EventLoop wiring for the Backcast Execution Kernel (#24).

Drives a Replay run deterministically and pushes the existing sink contract, golden-
comparable to the Nautilus oracle. Per-bar event order (findings 0008 §2, record-from-
oracle):

    push_bar(N) → on_bar(N) [may submit] → fill accepted MARKET @ bar N close →
        on_order(fill) → push_order → portfolio.apply → push_portfolio
    → record equity point (post-fill cash)

then on_stop and push_run_complete. Risk-denied orders never fill; their denial is
delivered to on_order (no sink push — the oracle sink only carries FILLED orders).

Nautilus-free: importing this module loads no Rust core (gated by the Mono teardown test).
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from engine.kernel.broker import ReplayBroker
from engine.kernel.duckdb_bars import Bar, load_universe_bars
from engine.kernel.orders import (
    Order,
    OrderDenied,
    OrderEngine,
    OrderSide,
    OrderStatus,
)
from engine.kernel.portfolio import Portfolio, PortfolioSnapshot
from engine.kernel.risk import RiskEngine
from engine.kernel.sink import EventSink
from engine.kernel.strategy import Strategy
from engine.live.safety_rails import KIND_NO_REFERENCE_PRICE, RailViolation, SafetyRails

# Poll cadence for the paused gate (#30): while PAUSED (run_event cleared) the loop waits in
# short slices so it can wake promptly on a step pulse OR a stop signal. 50ms is imperceptible
# to a human watching a paused chart and costs negligible CPU.
_PAUSE_POLL_SEC = 0.05


@dataclass
class RunResult:
    success: bool
    bars: int
    fills: int
    final_cash: float
    final_equity: float
    realized_pnl: float
    error: str = ""
    stopped_reason: str = ""  # set when a post-trade rail halted the run early


class _Context:
    """StrategyContext implementation: turns submit_market into an OrderEngine submit.

    Accepted orders are queued for the current bar; the runner fills them at bar close.
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


class KernelRunner:
    """Runs one Replay tracer: load bars → stream → emit sink → run result."""

    def __init__(
        self,
        *,
        data_root: str | Path,
        instrument_id: str | None = None,
        instrument_ids: Optional[list[str]] = None,
        granularity: str = "Daily",
        start: str,
        end: str,
        initial_cash: float,
        strategy: Strategy,
        push_target=None,
        sink=None,
        rails: Optional[SafetyRails] = None,
        run_event=None,
        bar_interval_sec: float = 0.0,
        stop_event=None,
        step_event=None,
        speed_provider=None,
    ) -> None:
        self._data_root = data_root  # J-Quants DuckDB root (ADR-0006); <root>/<table>/<code>.duckdb
        # Single instrument (#47) or a universe (#48); universe is time-order-merged into one
        # stream. instrument_id is kept for back-compat with existing single-instrument callers.
        if instrument_ids is None:
            if instrument_id is None:
                raise ValueError("KernelRunner requires instrument_id or instrument_ids")
            instrument_ids = [instrument_id]
        if not instrument_ids:
            raise ValueError("instrument_ids must be non-empty")
        self._instrument_ids = list(instrument_ids)
        self._granularity = granularity
        self._start = start
        self._end = end
        self._initial_cash = float(initial_cash)
        self._strategy = strategy
        venues = {iid.split(".")[-1] for iid in self._instrument_ids}
        if len(venues) != 1:
            raise ValueError(f"universe must share a single venue, got {sorted(venues)}")
        self._venue = venues.pop()
        self._risk = RiskEngine(rails)
        self._order_engine = OrderEngine(risk_engine=self._risk, venue=self._venue)
        self._portfolio = Portfolio(initial_cash=initial_cash)
        self._broker = ReplayBroker(self._order_engine)
        # Observer injection (#49): golden #24 callers pass `push_target` and get the
        # default EventSink (RustBacktestSink JSON contract). The production Replay seam
        # passes its own `sink` (ReplayKernelObserver → apply_replay_event + RunBuffer) so
        # the SAME per-bar execution loop drives both, anti-divergence by construction.
        # The kernel stays host-independent: it never imports the observer or its deps.
        if sink is not None:
            self._sink = sink
        elif push_target is not None:
            self._sink = EventSink(push_target)
        else:
            raise ValueError("KernelRunner requires either push_target or sink")
        self._ctx = _Context(order_engine=self._order_engine, portfolio=self._portfolio)
        # Control seam (#49): run_event.wait() before each bar (pause/resume/stop, owned by
        # the host's DataEngine), and an optional wallclock throttle that releases the GIL so
        # a polling thread can read the bar-by-bar chart between bars. Both default-off →
        # golden path runs straight through, byte-identical.
        self._run_event = run_event
        self._bar_interval_sec = bar_interval_sec
        # Stop seam (#49 review #5): distinct from run_event (which only pauses: clear=pause,
        # set=run — it can't signal "stop" because a running run also has it set). force_stop
        # sets this; the loop breaks promptly so a long Minute run halts instead of running
        # out. Default None → golden path never breaks early (byte-identical).
        self._stop_event = stop_event
        # Transport seam (#30): step_event advances EXACTLY one bar while PAUSED (run_event
        # cleared) — one pulse → one bar → re-block. speed_provider() is read EACH bar to scale
        # the throttle interval (bar_interval_sec / multiplier), so a speed change takes effect
        # mid-run. Both default None → golden path byte-identical (no step gate, multiplier 1).
        self._step_event = step_event
        self._speed_provider = speed_provider

    def run(self) -> RunResult:
        import time as _time

        from engine.strategy_runtime.summary import equity_curve_stats

        bars: list[Bar] = load_universe_bars(
            self._data_root,
            self._instrument_ids,
            start=self._start,
            end=self._end,
            granularity=self._granularity,
        )

        self._strategy.register(self._ctx)
        self._strategy.on_start()

        equity_curve: list[float] = []
        fills = 0
        # Post-trade baseline = run-start mark-to-market equity (flat → initial cash),
        # fixed BEFORE any bar/fill (the live orchestrator establishes it at run start).
        # Tracer scope: single-instrument, flat start. Multi-instrument / non-flat-start
        # would need run-start MARKET prices per instrument (not the avg_px fallback in
        # mark_to_market_equity), and last_prices fed from every instrument's stream.
        last_prices: dict[str, float] = {}
        baseline_equity = self._portfolio.mark_to_market_equity(last_prices)
        rails_active = self._risk.rails is not None
        stopped_reason = ""

        for bar in bars:
            # Stop gate (#49 review #5): break promptly when the host requests a stop, so a
            # long run halts instead of streaming every remaining bar.
            if self._stop_event is not None and self._stop_event.is_set():
                stopped_reason = "stopped"
                break

            # Pause / step gate (#30, host-owned). When PAUSED (run_event cleared) the loop
            # waits in short slices so it can wake on either resume (run_event set), a single
            # step pulse (step_event → advance EXACTLY this one bar, then re-block), or a stop
            # signal (break out). run_event/stop_event mutation stays in the DataEngine; this
            # gate is purely additive (step_event None → identical to a plain run_event.wait()).
            if self._run_event is not None:
                while not self._run_event.is_set():
                    if self._stop_event is not None and self._stop_event.is_set():
                        break
                    if self._step_event is not None and self._step_event.is_set():
                        self._step_event.clear()  # consume one pulse → let this one bar through
                        break
                    # Paused: sleep a slice (waking immediately on resume via run_event), then
                    # re-check stop/step. Waiting ON run_event — not a bare sleep — means resume
                    # is instant and the loop never busy-spins, including when step_event is None.
                    self._run_event.wait(_PAUSE_POLL_SEC)
                if self._stop_event is not None and self._stop_event.is_set():
                    stopped_reason = "stopped"
                    break

            self._sink.push_bar(bar)

            # MARKET uses the order instrument's latest close. Overlay only the current
            # instrument so single-symbol replay remains fill-at-current-bar-close.
            self._ctx.reference_prices = {**last_prices, bar.instrument_id: bar.close}
            self._ctx.ts_event_ns = bar.ts_event_ns
            self._strategy.on_bar(bar)

            # Risk-denied orders surface to on_order but never fill or touch the sink.
            for denied in self._ctx.denials:
                self._strategy.on_order(denied)
            self._ctx.denials.clear()

            # Accepted MARKET orders fill at the order instrument's latest close, in
            # submission order. For the current bar's instrument this is bar.close.
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
                self._strategy.on_order(fill)
                self._sink.push_order(fill)
                self._sink.push_portfolio(self._portfolio)
                fills += 1

            # Track each instrument's latest close so equity can be marked to market
            # (cash + open-position value), consistent with how live venues report account
            # value. Updated every bar (not just when rails are on).
            last_prices[bar.instrument_id] = bar.close
            mtm_equity = self._portfolio.mark_to_market_equity(last_prices)

            # equity_curve feeds the kernel's RunResult/summary = the FROZEN #24 golden
            # (cash-basis, oracle-matched, immutable per ADR-0006). Left as cash so the golden
            # stays byte-identical.
            equity_curve.append(self._portfolio.cash)
            # Production observer projection (#49 review #2): mark-to-market equity + realized
            # cash, mirrored into RunBuffer per bar → compute_portfolio → get_portfolio. So the
            # UI's equity reflects open-position value (not just cash); cash stays separate.
            # No-op for the golden EventSink. Called unconditionally (declared sink method).
            self._sink.on_equity(
                bar.ts_event_ns // 1_000_000, mtm_equity, self._portfolio.cash
            )

            # Post-trade rail on mark-to-market equity (skipped when rails are
            # unconfigured, so the golden tracer is untouched).
            if rails_active:
                violation = self._risk.check_post_trade(
                    equity=mtm_equity,
                    baseline_equity=baseline_equity,
                )
                if violation is not None:
                    stopped_reason = violation.kind
                    break

            # Wallclock throttle (last in the body, like the legacy replay_runner): releases
            # the GIL between bars so the host's poll thread reads the incrementally-streamed
            # chart (issue #29 bar-by-bar following). Default 0 → golden runs straight through.
            # #30: the interval is read EACH bar as bar_interval_sec / speed_multiplier so a
            # transport speed change takes effect mid-run. The #49-review-#3 total-budget cap is
            # removed — #30 hands rate ownership to the user (findings 0023 §4(D)); a long run at
            # 1x is meant to be watchable (high-speed completion is 50x or stop).
            if self._bar_interval_sec > 0:
                multiplier = self._speed_provider() if self._speed_provider is not None else 1
                if multiplier < 1:
                    multiplier = 1
                _time.sleep(self._bar_interval_sec / multiplier)

        self._strategy.on_stop()

        stats = equity_curve_stats(equity_curve)
        summary = {
            "fills_count": fills,
            "equity_points": len(equity_curve),
            "max_drawdown": stats["max_drawdown"],
            "sharpe": stats["sharpe"],
            "sortino": stats["sortino"],
        }
        self._sink.push_run_complete("", summary)

        return RunResult(
            success=True,
            bars=len(equity_curve),  # bars actually PROCESSED (< len(bars) on an early halt)
            fills=fills,
            final_cash=self._portfolio.cash,
            final_equity=self._portfolio.equity,
            realized_pnl=self._portfolio.realized_pnl,
            stopped_reason=stopped_reason,
        )
