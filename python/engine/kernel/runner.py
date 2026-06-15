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
from engine.kernel.portfolio import Portfolio
from engine.kernel.risk import RiskEngine
from engine.kernel.sink import EventSink
from engine.kernel.strategy import Strategy
from engine.live.safety_rails import RailViolation, SafetyRails

# Total wall-clock budget for the per-bar animation throttle (#49 review #3). The effective
# per-bar sleep is min(bar_interval_sec, _ANIM_BUDGET_SEC / n_bars), so total throttle time
# never exceeds this regardless of bar count.
_ANIM_BUDGET_SEC = 2.0


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
        # 現在 bar の参照価格（close）。on_bar 前に runner が更新し、submit_market が建玉上限の
        # 約定後時価評価に使う（MARKET は当該 bar close で約定するため close が参照価格・#25 review）。
        self.reference_price: float | None = None

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
        violation: RailViolation | None = self._engine.submit(
            order,
            net_signed_qty=self._portfolio.net_signed_qty(instrument_id),
            reference_price=self.reference_price,  # 当該 bar close（建玉上限の時価評価・#25 review）
            order_notional_jpy=0.0,  # MARKET: price unknown at submit (matches live path)
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
                    ts_event_ns=0,
                )
            )
        else:
            self.pending.append(order)

    def log(self, message: str) -> None:  # tracer: logging is a no-op sink
        pass


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

        # Budget-bound the per-bar animation throttle (#49 review #3): a fixed 10ms/bar would
        # make a long Minute run sleep for minutes. Cap the TOTAL animation time so cadence
        # scales down with bar count (Daily 68 bars → full 10ms each; a year of Minute bars →
        # near-instant per bar, still releasing the GIL each bar for the poll thread).
        effective_interval = self._bar_interval_sec
        if self._bar_interval_sec > 0 and bars:
            effective_interval = min(self._bar_interval_sec, _ANIM_BUDGET_SEC / len(bars))

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

            # Pause/resume gate (host-owned). Blocks here when the DataEngine clears the event
            # (PauseReplay); resume sets it. (Stop is the separate stop_event above.)
            if self._run_event is not None:
                self._run_event.wait()

            self._sink.push_bar(bar)

            # MARKET は当該 bar close で約定するので、submit 時の参照価格 = この bar の close。
            self._ctx.reference_price = bar.close
            self._strategy.on_bar(bar)

            # Risk-denied orders surface to on_order but never fill or touch the sink.
            for denied in self._ctx.denials:
                self._strategy.on_order(denied)
            self._ctx.denials.clear()

            # Accepted MARKET orders fill at this bar's close, in submission order.
            pending = self._ctx.pending
            self._ctx.pending = []
            for order in pending:
                if order.status is not OrderStatus.ACCEPTED:
                    continue
                fill = self._broker.fill_market(order, bar)
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
            if effective_interval > 0:
                _time.sleep(effective_interval)

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
