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
  - `push_portfolio` / `push_run_complete` → no-op (last_portfolio is derived after the run
    from the RunBuffer fills+equity by `compute_portfolio`).

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

    # --- chart (reducer → GetState polling) ----------------------------------
    def push_bar(self, bar: Any) -> None:
        ts_ms = bar.ts_event_ns // 1_000_000
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

    # --- per-bar equity (RunBuffer; #49-added KernelRunner hook) ---------------
    def on_equity(self, ts_event_ms: int, equity: float, cash: float) -> None:
        # equity = mark-to-market (cash + open-position value); cash = realized cash. Both
        # recorded so compute_portfolio reports equity (MTM) and cash/buying_power separately
        # (#49 review #2). Mirrors how live venues expose account value vs 余力/cash.
        self._buf.write_equity(
            {"ts_event_ms": ts_event_ms, "equity": equity, "cash": cash}
        )

    # --- intentionally inert in production ------------------------------------
    def push_portfolio(self, portfolio: Any) -> None:
        # No-op: get_portfolio reads last_portfolio, computed once after the run from the
        # RunBuffer fills+equity (compute_portfolio). Per-fill pushes would be discarded.
        pass

    def push_run_complete(self, run_id: str, summary: Any) -> None:
        # No-op: the caller owns RunBuffer.finish() + summary/portfolio derivation.
        pass
