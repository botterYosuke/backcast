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

from engine.kernel.bars import Bar, load_bars
from engine.kernel.broker import ReplayBroker
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
        catalog_path: str | Path,
        instrument_id: str,
        start: str,
        end: str,
        initial_cash: float,
        strategy: Strategy,
        push_target,
        rails: Optional[SafetyRails] = None,
    ) -> None:
        self._catalog_path = catalog_path
        self._instrument_id = instrument_id
        self._start = start
        self._end = end
        self._initial_cash = float(initial_cash)
        self._strategy = strategy
        self._venue = instrument_id.split(".")[-1]
        self._risk = RiskEngine(rails)
        self._order_engine = OrderEngine(risk_engine=self._risk, venue=self._venue)
        self._portfolio = Portfolio(initial_cash=initial_cash)
        self._broker = ReplayBroker(self._order_engine)
        self._sink = EventSink(push_target)
        self._ctx = _Context(order_engine=self._order_engine, portfolio=self._portfolio)

    def run(self) -> RunResult:
        from engine.strategy_runtime.summary import equity_curve_stats

        bars: list[Bar] = load_bars(
            self._catalog_path, self._instrument_id, start=self._start, end=self._end
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

            # Per-bar equity point (post-fill cash), mirroring the oracle's on_equity.
            equity_curve.append(self._portfolio.cash)

            # Post-trade rail on mark-to-market equity (skipped when rails are
            # unconfigured, so the golden tracer is untouched).
            if rails_active:
                last_prices[bar.instrument_id] = bar.close
                violation = self._risk.check_post_trade(
                    equity=self._portfolio.mark_to_market_equity(last_prices),
                    baseline_equity=baseline_equity,
                )
                if violation is not None:
                    stopped_reason = violation.kind
                    break

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
