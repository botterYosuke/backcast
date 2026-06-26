"""ADR-0031 S2 (#142) — Replay 走行中追加の mid-stream join (KernelStepper).

bt.universe.add(X) mid-run streams X's bars from the CURRENT replay time to scenario.end into the
remaining stream — added-time-onward (no rewind, no re-play of history), ts-ordered, leaving
existing instruments' per-bar settle untouched. remove stops X's future bars; clear ends the stream.
A run that never edits the universe is byte-identical (#24 golden — the join is the sole mutation).

Pure-Python: bars are injected (a fake bar_loader stands in for duckdb load_bars), so no DuckDB
mount is needed. RED→GREEN: delete the merge in join_instrument → MIDJOIN-01/04 RED (X never
streams); delete the ts filter → MIDJOIN-01 RED (history replays); delete drop_instrument's tail
filter → MIDJOIN-03 RED.
"""
from __future__ import annotations

import pytest

from engine.kernel.duckdb_bars import Bar
from engine.kernel.stepper import KernelStepper, StepEvent
from engine.strategy_runtime.backtester import Backtester
from engine.synth import explicit, linear_grid, synth_bars  # #153: 合成バーを生成器へ集約

_CASH = 10_000_000.0
_BASE = 1_700_000_000_000_000_000
_STEP = 1_000_000_000  # 1s between bars


def _series(iid: str, closes: list[float]) -> list[Bar]:
    # #153: 旧アドホック _bar/_series（open=close, high=close+1, low=close-1, vol=10, 1s grid）を
    # synth_bars + explicit ビルダー（spread=1.0, volume=10.0）+ linear_grid で再現（byte-identical）。
    grid = linear_grid(len(closes), base=_BASE, step=_STEP)
    return synth_bars([iid], path=explicit(closes, spread=1.0, volume=10.0), grid=grid)[iid]


class _NullSink:
    def push_bar(self, bar) -> None: ...
    def push_order(self, fill) -> None: ...
    def push_portfolio(self, pf) -> None: ...
    def on_equity(self, ts_ms, equity, cash) -> None: ...
    def push_run_complete(self, run_id, summary) -> None: ...


def _make_stepper(primary_closes: list[float], *, loader, instrument="7203.TSE") -> KernelStepper:
    return KernelStepper(
        bars=_series(instrument, primary_closes),
        instrument_ids=[instrument],
        initial_cash=_CASH,
        strategy=None,
        strategy_id="S",
        sink=_NullSink(),
        data_root="/unused",       # non-None so join_instrument actually loads
        start="2024-01-01",
        end="2024-12-31",
        granularity="Daily",
        bar_loader=loader,
    )


def _drive(stepper: KernelStepper, *, join_after=None, join_id=None,
           drop_after=None, drop_id=None, clear_after=None) -> list[tuple[str, int]]:
    """Open bars to the terminal; optionally join/drop/clear after the Nth opened bar. Returns the
    opened (instrument_id, ts) sequence."""
    seq: list[tuple[str, int]] = []
    opened = 0
    while True:
        h = stepper.open_next_bar()
        if h.event is not StepEvent.BAR_OPEN:
            break
        seq.append((h.bar.instrument_id, h.bar.ts_event_ns))
        opened += 1
        if join_after is not None and opened == join_after:
            stepper.join_instrument(join_id)
        if drop_after is not None and opened == drop_after:
            stepper.drop_instrument(drop_id)
        if clear_after is not None and opened == clear_after:
            stepper.clear_instruments()
    return seq


_A_CLOSES = [100.0, 101.0, 102.0, 103.0, 104.0]   # primary 7203, ts i=0..4
_B_CLOSES = [200.0, 201.0, 202.0, 203.0, 204.0]   # joined 9984, ts i=0..4 (same grid → interleave)


@pytest.mark.scenario("MIDJOIN-01")
def test_join_streams_only_added_time_onward() -> None:
    b_bars = _series("9984.TSE", _B_CLOSES)
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars))

    # Join AFTER opening the 2nd bar (current ts = _BASE + 1*_STEP). Only B bars with ts > that join.
    seq = _drive(stepper, join_after=2, join_id="9984.TSE")

    join_ts = _BASE + 1 * _STEP
    b_seen = [(iid, ts) for iid, ts in seq if iid == "9984.TSE"]
    # B's history (ts <= join_ts: i=0,1) must NOT replay; its future (i=2,3,4) must stream.
    assert all(ts > join_ts for _, ts in b_seen), f"B replayed history: {b_seen}"
    assert [ts for _, ts in b_seen] == [_BASE + i * _STEP for i in (2, 3, 4)]
    # ts order is monotonic across the merged stream.
    assert [ts for _, ts in seq] == sorted(ts for _, ts in seq)
    # at equal ts the existing instrument precedes the joined one (stable merge).
    assert seq.index(("7203.TSE", _BASE + 2 * _STEP)) < seq.index(("9984.TSE", _BASE + 2 * _STEP))


@pytest.mark.scenario("MIDJOIN-02")
def test_existing_instrument_bars_unaffected_by_join() -> None:
    b_bars = _series("9984.TSE", _B_CLOSES)
    joined = _drive(_make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars)),
                    join_after=2, join_id="9984.TSE")
    control = _drive(_make_stepper(_A_CLOSES, loader=lambda *a, **k: []))

    a_from_join = [(iid, ts) for iid, ts in joined if iid == "7203.TSE"]
    assert a_from_join == control, "the primary's bar sequence changed when a second instrument joined"


@pytest.mark.scenario("MIDJOIN-03")
def test_drop_stops_future_bars() -> None:
    b_bars = _series("9984.TSE", _B_CLOSES)
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars))
    # join after bar1, then drop after bar3 (some B bars streamed, the rest must stop).
    seq = _drive(stepper, join_after=1, join_id="9984.TSE", drop_after=3, drop_id="9984.TSE")

    b_ts = [ts for iid, ts in seq if iid == "9984.TSE"]
    assert b_ts, "B should have streamed at least one bar before the drop"
    # after the drop (3rd opened bar) no further B bar appears.
    drop_point_index = 3
    after_drop = seq[drop_point_index:]
    assert all(iid != "9984.TSE" for iid, _ in after_drop), f"B kept streaming after drop: {after_drop}"


@pytest.mark.scenario("MIDJOIN-04")
def test_setup_time_add_streams_from_start() -> None:
    b_bars = _series("9984.TSE", _B_CLOSES)
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars))
    # join BEFORE the first open_next_bar (setup-time add) → B streams from the start (all 5 bars).
    stepper.join_instrument("9984.TSE")
    seq = _drive(stepper)

    b_ts = sorted(ts for iid, ts in seq if iid == "9984.TSE")
    assert b_ts == [_BASE + i * _STEP for i in range(5)], f"setup add did not stream B from start: {b_ts}"


@pytest.mark.scenario("MIDJOIN-05")
def test_clear_ends_stream() -> None:
    b_bars = _series("9984.TSE", _B_CLOSES)
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars))
    seq = _drive(stepper, join_after=1, join_id="9984.TSE", clear_after=2)
    # after clear (2nd opened bar) the stream ends — no further bars open.
    assert len(seq) == 2, f"clear did not end the stream: {seq}"


@pytest.mark.scenario("MIDJOIN-06")
def test_unused_universe_is_byte_identical() -> None:
    # A stepper whose universe is never edited streams EXACTLY its initial bars (the join is the
    # sole mutation; #24 golden untouched). A loader that would explode proves it is never called.
    def _boom(*a, **k):
        raise AssertionError("bar_loader must not be called when the universe is never edited")

    seq = _drive(_make_stepper(_A_CLOSES, loader=_boom))
    assert seq == [("7203.TSE", _BASE + i * _STEP) for i in range(5)]


def test_cross_venue_join_rejected() -> None:
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: [])
    with pytest.raises(ValueError, match="cross-venue"):
        stepper.join_instrument("AAPL.NASDAQ")


def test_join_missing_duckdb_is_membership_only() -> None:
    # A missing DuckDB file for X → X is a member (no crash), just no bars (ADR-0031 S1).
    def _missing(*a, **k):
        raise FileNotFoundError("no duckdb for X")

    stepper = _make_stepper(_A_CLOSES, loader=_missing)
    stepper.join_instrument("6758.TSE")
    seq = _drive(stepper)
    assert "6758.TSE" in stepper._instrument_ids
    assert all(iid == "7203.TSE" for iid, _ in seq), "X has no data but should be a member"


class _RecordingBridge:
    def __init__(self): self.ops = []
    def add(self, iid): self.ops.append(("add", iid))
    def remove(self, iid): self.ops.append(("remove", iid))
    def clear(self): self.ops.append(("clear",))
    def list(self): return []


def test_bt_universe_add_drives_midstream_join() -> None:
    """Wiring: bt.universe.add(X) routes through the handle to the stepper's join (full Backtester)."""
    b_bars = _series("9984.TSE", _B_CLOSES)
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: list(b_bars))

    bridge = _RecordingBridge()
    bt = Backtester(stepper, universe_bridge=bridge)

    seq: list[tuple[str, int]] = []
    opened = 0
    for bar in bt.replay():
        seq.append((bar.instrument_id, bar.ts_event_ns))
        opened += 1
        if opened == 2:
            bt.universe.add("9984.TSE")   # registry edit (bridge) + stepper join (bar_source)

    assert ("add", "9984.TSE") in bridge.ops               # registry edit enqueued
    assert any(iid == "9984.TSE" for iid, _ in seq)        # AND its data joined the stream


def test_cross_venue_add_via_handle_leaves_membership_unmutated() -> None:
    """A cross-venue bt.universe.add must raise BEFORE the registry edit. _UniverseHandle.add does
    join_instrument (venue validation) FIRST, then bridge.add — so a rejected cross-venue id never
    reaches the registry (no phantom membership / chart). Litmus: reorder _UniverseHandle.add to
    bridge.add-first and bridge.ops gains a phantom ('add', 'AAPL.NASDAQ') → RED. The direct-stepper
    test_cross_venue_join_rejected does NOT catch that ordering (it bypasses the handle)."""
    stepper = _make_stepper(_A_CLOSES, loader=lambda *a, **k: [])
    bridge = _RecordingBridge()
    bt = Backtester(stepper, universe_bridge=bridge)

    with pytest.raises(ValueError, match="cross-venue"):
        bt.universe.add("AAPL.NASDAQ")
    assert bridge.ops == []                                  # membership never mutated on rejection
    assert "AAPL.NASDAQ" not in stepper._instrument_ids      # nor did the stream pick it up


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
