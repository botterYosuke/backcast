"""Unit gate (#49): KernelRunner's opt-in production injection seams.

Issue #49 extends the ONE per-bar execution loop with opt-in seams so the golden #24 path
and the production Replay path share it (anti-divergence): an injectable `sink` observer, a
new per-bar `on_equity` emission, a `run_event` pause gate, and a `bar_interval_sec` throttle.
Defaults must leave the golden EventSink path byte-identical (asserted separately by
test_kernel_subprocess_matches_committed_golden); here we pin the new seams directly.

Pure-Python: load_universe_bars is monkeypatched so no DuckDB mount is needed.
"""
from __future__ import annotations

import os
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402


def _bars(n: int) -> list[Bar]:
    return [
        Bar(
            instrument_id="8918.TSE",
            ts_event_ns=1_700_000_000_000_000_000 + i * 1_000_000_000,
            open=100.0 + i, high=101.0 + i, low=99.0 + i, close=100.0 + i, volume=10.0,
        )
        for i in range(n)
    ]


class _Recorder:
    """Observer surface: records bars + per-bar equity; no fills (the strategy is inert)."""

    def __init__(self) -> None:
        self.bars: list[Bar] = []
        self.equity: list[tuple[int, float]] = []
        self.portfolios = 0
        self.complete = 0

    def push_bar(self, bar) -> None:
        self.bars.append(bar)

    def push_order(self, fill) -> None:  # pragma: no cover - inert strategy
        pass

    def push_portfolio(self, pf) -> None:
        self.portfolios += 1

    def on_equity(self, ts_ms: int, equity: float) -> None:
        self.equity.append((ts_ms, equity))

    def push_run_complete(self, run_id, summary) -> None:
        self.complete += 1


def _make_runner(monkeypatch, *, sink, bars, **kw) -> KernelRunner:
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    return KernelRunner(
        data_root="/unused",
        instrument_ids=["8918.TSE"],
        start="2024-10-01",
        end="2025-01-10",
        initial_cash=10_000_000,
        strategy=Strategy(),  # inert: no orders, so equity == cash every bar
        sink=sink,
        **kw,
    )


def test_on_equity_fires_once_per_bar_with_cash(monkeypatch) -> None:
    sink = _Recorder()
    bars = _bars(5)
    _make_runner(monkeypatch, sink=sink, bars=bars).run()

    assert len(sink.bars) == 5
    # No fills → cash stays at initial; one equity point per bar with the bar's ts.
    assert [ts for ts, _ in sink.equity] == [b.ts_event_ns // 1_000_000 for b in bars]
    assert all(eq == 10_000_000 for _, eq in sink.equity)


def test_run_event_gate_blocks_until_set(monkeypatch) -> None:
    sink = _Recorder()
    ev = threading.Event()  # cleared → first wait() blocks before any bar
    runner = _make_runner(monkeypatch, sink=sink, bars=_bars(3), run_event=ev)

    done = threading.Event()

    def _go():
        runner.run()
        done.set()

    t = threading.Thread(target=_go, daemon=True)
    t.start()
    # Paused: the loop is blocked on run_event.wait() before bar 0.
    assert not done.wait(0.2), "run proceeded while run_event was cleared"
    assert sink.bars == []
    ev.set()  # resume
    assert done.wait(2.0), "run did not finish after run_event was set"
    assert len(sink.bars) == 3


def test_sink_without_on_equity_is_tolerated(monkeypatch) -> None:
    """Golden EventSink has no on_equity — the runner must getattr-guard it (no crash)."""

    class _NoEquitySink:
        def __init__(self) -> None:
            self.bars = 0

        def push_bar(self, bar) -> None:
            self.bars += 1

        def push_order(self, fill) -> None:  # pragma: no cover
            pass

        def push_portfolio(self, pf) -> None:  # pragma: no cover
            pass

        def push_run_complete(self, run_id, summary) -> None:
            pass

    sink = _NoEquitySink()
    result = _make_runner(monkeypatch, sink=sink, bars=_bars(4)).run()
    assert result.success and sink.bars == 4


def test_requires_push_target_or_sink() -> None:
    try:
        KernelRunner(
            data_root="/unused", instrument_ids=["8918.TSE"],
            start="a", end="b", initial_cash=1, strategy=Strategy(),
        )
    except ValueError as exc:
        assert "push_target or sink" in str(exc)
    else:  # pragma: no cover
        raise AssertionError("expected ValueError when neither push_target nor sink given")


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
