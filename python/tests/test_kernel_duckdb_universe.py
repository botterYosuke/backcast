"""DuckDB multi-instrument universe: time-order merge + kernel run-to-completion (#48).

ADR-0006. Extends the #47 daily/single-instrument tracer to a universe read that merges
several instruments into one ts-ascending stream (`merge_bars_by_ts`) and is consumed by
the kernel.

Two concerns, each pinned by the most probative target (grill 2026-06-15, Option 1):

  - Merge tie-stability — DAILY universe [8918, 1301] over a shared window: every bar lands
    on the same 15:30 ts on shared trading days, so ts collisions are maximal. Run BOTH
    [8918,1301] AND [1301,8918] and assert the per-ts ordering FLIPS — proving the merge
    preserves *input (universe) order* on ties (a plain instrument_id sort would not flip,
    so this is the real guard against accidental sorting).
  - Minute × universe × kernel run — MINUTE universe [1301, 1305] on 2024-01-04: the only
    test that exercises #48's whole new path at once. Assert the kernel consumed EVERY bar of
    BOTH instruments (per-instrument on_bar count == each code's bar count), not merely that
    the run finished — so a silent drop of one instrument can't pass as "completed".

Real-data dependency: skipped where the owner's DuckDB root is not mounted (repo convention).
"""
from __future__ import annotations

import os
import sys
from collections import Counter, defaultdict

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

import pytest

from engine.kernel.duckdb_bars import db_path, load_bars, load_universe_bars
from engine.kernel.runner import KernelRunner
from engine.kernel.strategy import Strategy
from spike.kernel_golden import scenario

# Daily tie test: 8918 & 1301 share all 8 trading days in this window (probed) → 8 colliding ts.
_TIE_A, _TIE_B = "8918.TSE", "1301.TSE"
_TIE_START, _TIE_END = "2024-10-01", "2024-10-10"
# Minute universe run: 1301 (59 bars) + 1305 (133 bars) both trade on this day (probed).
_UNI = ["1301.TSE", "1305.TSE"]
_UNI_DAY = "2024-01-04"

_DB_PRESENT = db_path(scenario.DUCKDB_ROOT, _TIE_A, "Daily").exists()

pytestmark = pytest.mark.skipif(
    not _DB_PRESENT, reason=f"J-Quants DuckDB not mounted at {scenario.DUCKDB_ROOT}"
)


def _order_at_shared_ts(universe: list[str]) -> tuple[int, list[str]]:
    """Merge `universe` (daily, shared window) and return (a shared ts, the id order at it)."""
    merged = load_universe_bars(
        scenario.DUCKDB_ROOT, universe, start=_TIE_START, end=_TIE_END, granularity="Daily"
    )
    # merged must be globally ts-ascending.
    assert merged == sorted(merged, key=lambda b: b.ts_event_ns)
    by_ts: dict[int, list[str]] = defaultdict(list)
    for bar in merged:
        by_ts[bar.ts_event_ns].append(bar.instrument_id)
    shared = sorted(ts for ts, ids in by_ts.items() if set(ids) == set(universe))
    assert shared, f"expected a ts shared by both {universe}"
    return shared[0], by_ts[shared[0]]


def test_merge_preserves_universe_order_on_ties() -> None:
    """Same ts → output order follows the INPUT universe order, and FLIPS when it flips."""
    ts_fwd, order_fwd = _order_at_shared_ts([_TIE_A, _TIE_B])
    ts_rev, order_rev = _order_at_shared_ts([_TIE_B, _TIE_A])
    assert ts_fwd == ts_rev  # same calendar instant, both directions
    assert order_fwd == [_TIE_A, _TIE_B], f"forward order should match universe, got {order_fwd}"
    assert order_rev == [_TIE_B, _TIE_A], f"reversed order should match universe, got {order_rev}"
    assert order_fwd == list(reversed(order_rev)), "tie order must follow input, not a fixed sort"


def test_merge_total_count_is_sum_of_instruments() -> None:
    """No bars lost or duplicated in the merge."""
    merged = load_universe_bars(
        scenario.DUCKDB_ROOT, [_TIE_A, _TIE_B], start=_TIE_START, end=_TIE_END, granularity="Daily"
    )
    n_a = len(load_bars(scenario.DUCKDB_ROOT, _TIE_A, start=_TIE_START, end=_TIE_END))
    n_b = len(load_bars(scenario.DUCKDB_ROOT, _TIE_B, start=_TIE_START, end=_TIE_END))
    assert len(merged) == n_a + n_b


class _RecordingStrategy(Strategy):
    """Counts on_bar per instrument so the run can assert BOTH instruments were consumed."""

    def __init__(self, **kwargs) -> None:
        super().__init__(**kwargs)
        self.seen: Counter = Counter()

    def on_bar(self, bar) -> None:
        self.seen[bar.instrument_id] += 1


class _CountingSink:
    """Duck-types the Replay push target; the run is data-only so payloads are ignored."""

    def __init__(self) -> None:
        self.bars = 0

    def push_bar(self, _: str) -> None:
        self.bars += 1

    def push_order(self, _: str) -> None: ...  # noqa: E704

    def push_portfolio(self, _: str) -> None: ...  # noqa: E704

    def push_run_complete(self, *_: object) -> None: ...  # noqa: E704


def test_minute_universe_run_consumes_every_instrument() -> None:
    """AC#2: minute universe merged → kernel run completes AND consumes every bar of both codes."""
    expected = {
        iid: len(load_bars(scenario.DUCKDB_ROOT, iid, start=_UNI_DAY, end=_UNI_DAY, granularity="Minute"))
        for iid in _UNI
    }
    assert all(n > 0 for n in expected.values()), f"fixture invariant: both codes trade {_UNI_DAY}"

    strategy = _RecordingStrategy(strategy_id="uni-test", instrument_id=_UNI[0])
    sink = _CountingSink()
    runner = KernelRunner(
        data_root=scenario.DUCKDB_ROOT,
        instrument_ids=_UNI,
        granularity="Minute",
        start=_UNI_DAY,
        end=_UNI_DAY,
        initial_cash=10_000_000,
        strategy=strategy,
        push_target=sink,
    )
    result = runner.run()

    assert result.success, f"universe run failed: {result.error}"
    # Consumed EVERY bar of EACH instrument (no silent drop hiding behind "completed").
    assert dict(strategy.seen) == expected, f"per-instrument on_bar {dict(strategy.seen)} != {expected}"
    # Bars processed == merged total (sum of both instruments).
    assert result.bars == sum(expected.values())
    assert sink.bars == sum(expected.values()), "sink must receive every merged bar"


if __name__ == "__main__":
    if not _DB_PRESENT:
        print(f"[KERNEL DUCKDB UNIVERSE SKIP] DuckDB not mounted at {scenario.DUCKDB_ROOT}")
        sys.exit(0)
    failures = []
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
            except AssertionError as exc:
                failures.append(f"{name}: {exc}")
            except Exception as exc:  # noqa: BLE001 — surface in standalone run
                failures.append(f"{name}: {type(exc).__name__}: {exc}")
    if failures:
        print("[KERNEL DUCKDB UNIVERSE FAIL]")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("[KERNEL DUCKDB UNIVERSE PASS] daily tie stable=input-order; minute universe run consumes both")
