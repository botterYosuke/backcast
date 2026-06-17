"""Shared synthetic bar source + minimal strategy/sink for the #76 AC1/AC2 legs.

A single deterministic generator feeds BOTH the direct-on_bar leg and the embed
(reactive) leg, so the two paths see byte-identical input. The close MOVES every
bar on purpose: a constant (or repeating) close would let marimo's change-elision
skip the downstream cell, which would make AC1 (perf) and AC2 (reactivity) collapse
into the same false premise. Real fixtures top out at ~68 bars (J-Quants daily),
far short of the 50k the gate needs, hence synthetic.
"""

from __future__ import annotations

from engine.kernel.duckdb_bars import Bar

INSTRUMENT = "9999.SPIKE"
_BASE_TS_NS = 1_700_000_000_000_000_000  # arbitrary fixed epoch-ns anchor
_DAY_NS = 86_400_000_000_000


def make_bars(n: int, *, base: float = 1000.0) -> list[Bar]:
    """n synthetic daily bars. close changes every bar (sawtooth on a drift) so no
    two consecutive bars share a close — defeats reactive change-elision."""
    bars: list[Bar] = []
    for i in range(n):
        # Drift + sawtooth: strictly varies bar-to-bar, bounded, deterministic.
        close = base + (i % 97) * 0.5 + i * 0.01
        open_ = close - 0.3
        high = close + 0.7
        low = close - 0.7
        bars.append(
            Bar(
                instrument_id=INSTRUMENT,
                ts_event_ns=_BASE_TS_NS + i * _DAY_NS,
                open=open_,
                high=high,
                low=low,
                close=close,
                volume=1000.0 + i,
            )
        )
    return bars


def strategy_value(close: float) -> float:
    """The one strategy computation, identical in both legs (direct call and cell
    body). Deliberately trivial: the bench isolates the *dispatch mechanism*
    overhead, so the strategy body must not dominate either leg."""
    return 2.0 * close + 1.0


class MinimalStrategy:
    """Strategy that only computes `strategy_value` per bar — no orders, so the
    KernelRunner loop reduces to bar-stream + on_bar + (empty) fills/equity, the
    cleanest real baseline for the direct-on_bar path."""

    def __init__(self) -> None:
        # The one piece of representative per-bar work the direct leg measures —
        # mirrors the embed cell body `result = 2*close + 1`. Written every bar so
        # the dispatch comparison is apples-to-apples (not an empty call).
        self.last_value: float = 0.0

    def register(self, ctx) -> None:  # noqa: ANN001
        self._ctx = ctx

    def on_start(self) -> None:
        pass

    def on_bar(self, bar: Bar) -> None:
        self.last_value = strategy_value(bar.close)

    def on_order(self, event) -> None:  # noqa: ANN001
        pass

    def on_stop(self) -> None:
        pass


class NoopSink:
    """push-target that duck-types RustBacktestSink but discards everything, so the
    baseline measures the kernel loop rather than sink serialization."""

    def push_bar(self, bar) -> None:  # noqa: ANN001
        pass

    def push_order(self, order) -> None:  # noqa: ANN001
        pass

    def push_portfolio(self, portfolio) -> None:  # noqa: ANN001
        pass

    def on_equity(self, ts_ms, equity, cash) -> None:  # noqa: ANN001
        pass

    def push_run_complete(self, error, summary) -> None:  # noqa: ANN001
        pass
