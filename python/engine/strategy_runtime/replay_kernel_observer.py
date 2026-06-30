"""engine.strategy_runtime.replay_kernel_observer — kernel → production Replay seam (#49).

方針: **ADR-0006**（J-Quants DuckDB 直読み・nautilus runtime 完全退役）。本書の正本は
`docs/findings/0019-replay-duckdb-kernel-cutover.md`。

The production Replay observer for `KernelRunner` (issue #49). It duck-types the kernel's
EventSink surface (`push_bar` / `push_order` / `push_portfolio` / `push_run_complete`) plus the
`#49`-added `on_equity` hook, but instead of emitting the RustBacktestSink JSON contract it
drives the **existing #29 production seam** unchanged:

  - `push_bar(bar)`   → `engine.apply_replay_event(KlineUpdate)`  (reducer → GetState polling)
  - `on_equity(...)`  → `run_buffer.write_equity(...)`           (per-bar cash, post-fill)
  - `push_order(fill)`→ `run_buffer.write_fill(...)`            (→ compute_portfolio → get_portfolio)
  - `push_run_complete` → no-op (the caller owns RunBuffer.finish() + finalize derivation).

#65 — running portfolio snapshot (正本: `docs/findings/0044-replay-panel-real-data.md`): the three
account hooks *additionally* maintain a live `self._snapshot` and atomic-swap the completed dict
into `engine.last_portfolio`, so `get_portfolio` returns live values *during* the run (not just the
post-run `compute_portfolio` snapshot). The RunBuffer writes above are kept verbatim so the golden
#24 event stream stays byte-identical — this seam only adds the in-memory snapshot:
  - `push_portfolio(portfolio)` → positions (`open_positions()`, qty int-rounded, `unrealized_pnl=0`)
    + cash/buying_power (`portfolio.cash`) + `realized_pnl` (`portfolio.realized_pnl`). Equity is NOT
    touched here (`Portfolio.equity == cash`, not MTM); `on_equity` owns equity.
  - `on_equity(ts, equity, cash)` → equity (MTM) + cash/buying_power, and derives
    `unrealized_pnl = (equity − cash) − Σ(qty×avg_px)`. Positions held (push owns them).
The published dict matches `compute_portfolio`'s union so the finalize snapshot converges without a
visible jump, and `get_portfolio` reads one source (`last_portfolio`) for both live and final.

This lives in the adapter layer on purpose: the kernel (`engine.kernel.*`) stays
host-independent and never imports `apply_replay_event` / `RunBuffer` / `_backend_impl`
(D4 import-purity — the DuckDB→kernel runtime loads no `nautilus_trader`).

No primary-skip: with the DuckDB cutover `load_replay_data` no longer primes bar 0 (it resets
the reducer clock to 0 instead), so every bar is streamed **exactly once** — the chart's
`ohlc_points` count equals the streamed primary-bar count. See findings 0019 §exactly-once.
"""
from __future__ import annotations

from typing import Any

from engine.reducer import KlineUpdate


class ReplayKernelObserver:
    """Bridges KernelRunner's per-event callbacks to the #29 production Replay seam."""

    def __init__(self, *, engine: Any, run_buffer: Any) -> None:
        self._engine = engine
        self._buf = run_buffer
        # #65 running snapshot accumulators (published as a fresh dict via _publish_snapshot).
        self._positions: list[dict] = []
        self._orders: list[dict] = []
        self._cash: float = 0.0
        self._equity: float = 0.0
        self._realized: float = 0.0
        self._unrealized: float = 0.0
        # #185 (findings 0134): the replay clock for the Run Result time line — the latest streamed
        # primary bar's time (ms). Set in push_bar (the bar-open hook, which fires BEFORE any
        # fill/equity publish in that bar — stepper._open_bar), so every published snapshot carries
        # the bar currently under consideration. 0 until the first bar streams.
        self._clock_ms: int = 0

    # --- chart (reducer → GetState polling) ----------------------------------
    def push_bar(self, bar: Any) -> None:
        ts_ms = bar.ts_event_ns // 1_000_000
        # #185: advance the replay clock to the bar currently under consideration. push_bar runs
        # at bar-open, before this bar's fills (push_portfolio) / equity (on_equity) publish — so the
        # next published snapshot reflects THIS bar's time, even in an observation-only (pass) loop.
        self._clock_ms = ts_ms
        # Build KlineUpdate directly from the kernel Bar's plain floats. (The nautilus
        # `bar_to_kline_update` expects Price objects with `.as_double()` and would pull the
        # Rust core — avoided here to keep the runtime nautilus-free.)
        self._engine.apply_replay_event(
            KlineUpdate(
                timestamp_ms=ts_ms,
                open_time_ms=ts_ms,
                open=bar.open,
                high=bar.high,
                low=bar.low,
                close=bar.close,
                instrument_id=bar.instrument_id,
                volume=bar.volume,
            )
        )

    # --- fills (RunBuffer → compute_portfolio → get_portfolio) ----------------
    def push_order(self, fill: Any) -> None:
        # Field shape matches the legacy nautilus path's RunBuffer fill record
        # (run_buffer_reader.Fill: instrument_id / side∈{BUY,SELL} / qty>0 / price>0 / ts_event_ms).
        self._buf.write_fill(
            {
                "instrument_id": fill.instrument_id,
                "side": fill.side.value,  # OrderSide.value == "BUY" / "SELL"
                "qty": str(fill.last_qty),
                "price": str(fill.last_px),
                "ts_event_ms": fill.ts_event_ns // 1_000_000,
            }
        )
        # #65: grow the running Orders panel source. Replay is MARKET-immediate, so every fill is a
        # FILLED row (no resting orders) — shape matches compute_portfolio.orders / PortfolioOrderInfo.
        self._orders.append(
            {
                "symbol": fill.instrument_id,
                "side": fill.side.value,
                "qty": float(fill.last_qty),
                "price": float(fill.last_px),
                "status": "FILLED",
                "ts_ms": fill.ts_event_ns // 1_000_000,
            }
        )
        self._publish_snapshot()

    # --- per-bar equity (RunBuffer; #49-added KernelRunner hook) ---------------
    def on_equity(self, ts_event_ms: int, equity: float, cash: float) -> None:
        # equity = mark-to-market (cash + open-position value); cash = realized cash. Both
        # recorded so compute_portfolio reports equity (MTM) and cash/buying_power separately
        # (#49 review #2). Mirrors how live venues expose account value vs 余力/cash.
        self._buf.write_equity(
            {"ts_event_ms": ts_event_ms, "equity": equity, "cash": cash}
        )
        # #65: on_equity owns equity (MTM) + cash. Derive unrealized from cost basis:
        # unrealized = market value − cost = (equity − cash) − Σ(qty×avg_px). NOTE (equity − cash)
        # alone is the position market value, not the P&L (§4-b). Positions are held (push owns them).
        self._equity = equity
        self._cash = cash
        cost_basis = sum(p["qty"] * p["avg_price"] for p in self._positions)
        self._unrealized = (equity - cash) - cost_basis
        self._publish_snapshot()

    # --- running portfolio snapshot (#65) -------------------------------------
    def push_portfolio(self, portfolio: Any) -> None:
        # #65: positions + cash/buying_power + realized from the kernel Portfolio. unrealized_pnl is
        # 0.0 here to match the finalize snapshot (strategy_runtime/portfolio.py hardcodes 0.0); the
        # running running-view含み is carried on the snapshot top-level (_unrealized via on_equity).
        # qty is int-rounded so it equals _net_positions' int(round()) — no 100.0→100 finalize jump.
        self._positions = [
            {
                "symbol": p.instrument_id,
                "qty": int(round(p.quantity)),
                "avg_price": p.avg_px,
                "unrealized_pnl": 0.0,
            }
            for p in portfolio.open_positions()
        ]
        self._cash = portfolio.cash
        self._realized = portfolio.realized_pnl
        self._publish_snapshot()

    def push_run_complete(self, run_id: str, summary: Any) -> None:
        # No-op: the caller owns RunBuffer.finish() + summary/portfolio derivation.
        pass

    def _publish_snapshot(self) -> None:
        # Atomic ref swap (build a fresh dict, never mutate in place) so a concurrent get_portfolio
        # poll reads either the old or the new complete snapshot — never a half-updated one. Keys
        # match compute_portfolio's union (+realized/unrealized) so get_portfolio has one read path.
        self._engine.last_portfolio = {
            "buying_power": self._cash,
            "cash": self._cash,
            "equity": self._equity,
            "positions": list(self._positions),
            "orders": list(self._orders),
            "realized_pnl": self._realized,
            "unrealized_pnl": self._unrealized,
            # #185 (findings 0134): replay clock (latest streamed bar ts) for the Run Result time line.
            "clock_ms": self._clock_ms,
        }
