"""#112 S2 gates — LiveCellBridge / LiveCellBackend rendezvous (ADR-0025 D2).

Pins the A-1 lock-step rendezvous that lets the SAME marimo cell run under Auto:

  * rendezvous ORDER — bars reach the worker in order, the cell body runs between them, and the
    buffered intents drain AFTER the body (never interleaved with the next bar);
  * R1 — submits are buffered worker-locally and flushed as one bundle at the bar boundary,
    enqueued ON THE LIVE LOOP (so ``driver._intents`` stays a single-thread structure);
  * R2 — ``bt.portfolio()`` is marshalled to the live loop (never read on the worker thread);
  * R3 — a submit targets the CURRENT drive bar's instrument;
  * D5 teardown — an empty queue is idle (the worker blocks, the stream does NOT end); ONLY the
    stop sentinel ends ``bt.replay()``; the caller joins the worker after stop.

No marimo, no venue: the cell body is a plain Python ``cell_runner`` and the live execution seam
is a recording fake ``ctx`` that records which OS thread each call lands on. The real
``KernelLiveDriver`` ``_consume`` → ``drive_bar`` → ``_drain`` order is exercised end-to-end in
the S6 parity gate (``test_v19_cell_auto_parity``); here we drive ``drive_bar`` directly with the
same "await drive_bar(bar) then drain" shape the driver uses.
"""
from __future__ import annotations

import asyncio
import sys
import threading
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.live.cell_bridge import _STOP, LiveCellBridge  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402


def _bar(iid: str, ts_ms: int, close: float) -> Bar:
    ts = ts_ms * 1_000_000
    return Bar(
        instrument_id=iid, ts_event_ns=ts, open=close, high=close, low=close,
        close=close, volume=1.0,
    )


class _LiveLoop:
    """A real asyncio loop on a daemon thread = the production live-loop thread."""

    def __init__(self) -> None:
        self.loop = asyncio.new_event_loop()
        self.thread = threading.Thread(target=self._run, name="live-loop", daemon=True)
        self.thread.start()
        self.thread_id = self.run(self._tid())

    async def _tid(self) -> int:
        return threading.get_ident()

    def _run(self) -> None:
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def run(self, coro):
        """Run a coroutine on the loop, block the caller until it completes."""
        return asyncio.run_coroutine_threadsafe(coro, self.loop).result(timeout=10)

    def call(self, fn, *args):
        """Run a sync fn on the loop thread, block the caller until it returns."""
        async def _c():
            return fn(*args)
        return self.run(_c())

    def close(self) -> None:
        self.loop.call_soon_threadsafe(self.loop.stop)
        self.thread.join(timeout=5)
        self.loop.close()


class _Snapshot:
    def __init__(self) -> None:
        self.positions: dict = {}
        self.buying_power = 15000.0


class _RecordingCtx:
    """Stand-in for ``driver.ctx`` (``_Ctx``). Records the OS thread of every call so the gate
    can prove enqueue (R1) and snapshot reads (R2) happen on the live loop, not the worker."""

    def __init__(self) -> None:
        self.submits: list[tuple] = []          # (thread_id, instrument_id, side, quantity)
        self.snapshot_threads: list[int] = []
        self._snapshot = _Snapshot()

    def submit_market(self, *, strategy_id, instrument_id, side, quantity) -> None:
        self.submits.append((threading.get_ident(), instrument_id, side, quantity))

    def portfolio_snapshot(self, instrument_id=None):
        self.snapshot_threads.append(threading.get_ident())
        return self._snapshot

    def buying_power(self) -> float:
        return self._snapshot.buying_power


def _start_bridge(live: _LiveLoop, cell_runner, *, strategy_id="S-1") -> tuple:
    bridge = LiveCellBridge(cell_runner=cell_runner, strategy_id=strategy_id)
    ctx = _RecordingCtx()
    bridge.register(ctx)
    live.call(bridge.on_start)  # runs on the live loop, like driver.run_on_start
    return bridge, ctx


def _drive(live: _LiveLoop, bridge, bar: Bar) -> None:
    """Mimic the driver's ``_consume``: await drive_bar(bar) (the rendezvous), then the buffered
    intents are already enqueued via ctx — the real driver does ``await _drain()`` here."""
    live.run(bridge.drive_bar(bar))


@pytest.fixture()
def live():
    lp = _LiveLoop()
    yield lp
    lp.close()


# ── rendezvous order + R1 batching + R3 instrument routing ────────────────────


def test_rendezvous_order_and_batched_submit(live) -> None:
    # Each bar submits TWICE; the bundle must flush together, after the body, on the live loop,
    # in order, never interleaved with the next bar.
    def runner(bt):
        for bar in bt.replay():
            bt.submit_market(100)   # BUY 100
            bt.submit_market(-40)   # SELL 40 — same bar, buffered together (R1)

    bridge, ctx = _start_bridge(live, runner)

    bars = [_bar("7203.TSE", 1000, 11.0), _bar("6758.TSE", 2000, 22.0),
            _bar("7203.TSE", 3000, 33.0)]
    for b in bars:
        _drive(live, bridge, b)

    # 3 bars × 2 submits, in bar order, each pair targeting that bar's instrument (R3).
    assert [(iid, side, qty) for (_tid, iid, side, qty) in ctx.submits] == [
        ("7203.TSE", OrderSide.BUY, 100.0), ("7203.TSE", OrderSide.SELL, 40.0),
        ("6758.TSE", OrderSide.BUY, 100.0), ("6758.TSE", OrderSide.SELL, 40.0),
        ("7203.TSE", OrderSide.BUY, 100.0), ("7203.TSE", OrderSide.SELL, 40.0),
    ]
    # R1: every enqueue happened on the live-loop thread, NOT the worker.
    assert {tid for (tid, *_rest) in ctx.submits} == {live.thread_id}

    live.call(bridge.on_stop)
    bridge.join_worker()


def test_portfolio_read_is_marshalled_to_live_loop(live) -> None:
    # R2: bt.portfolio() inside the body must be serviced on the live loop, never on the worker.
    worker_tids: list[int] = []

    def runner(bt):
        for bar in bt.replay():
            worker_tids.append(threading.get_ident())  # the body itself runs on the worker
            snap = bt.portfolio()
            assert snap.buying_power == 15000.0          # venue-authoritative value flows through
            bt.submit_market(10)

    bridge, ctx = _start_bridge(live, runner)
    for b in [_bar("7203.TSE", 1000, 11.0), _bar("7203.TSE", 2000, 12.0)]:
        _drive(live, bridge, b)

    assert len(ctx.snapshot_threads) == 2
    assert set(ctx.snapshot_threads) == {live.thread_id}     # reads marshalled to the live loop
    assert worker_tids and live.thread_id not in worker_tids  # body ran off the live loop

    live.call(bridge.on_stop)
    bridge.join_worker()


# ── D5 teardown: empty queue is idle, only the sentinel ends the stream ───────


def test_empty_queue_is_idle_then_sentinel_ends_stream(live) -> None:
    finished = threading.Event()

    def runner(bt):
        for bar in bt.replay():
            bt.submit_market(5)
        finished.set()  # only reached when bt.replay() raises StopIteration (sentinel)

    bridge, ctx = _start_bridge(live, runner)
    _drive(live, bridge, _bar("7203.TSE", 1000, 11.0))

    # No sentinel yet: the worker is blocked in get() (idle, 場引け), the stream has NOT ended.
    assert not finished.wait(timeout=0.2)
    assert bridge._worker.is_alive()
    assert len(ctx.submits) == 1  # only the one driven bar produced a submit

    # Sentinel ends it; the caller joins the worker AFTER stop (D5-R).
    live.call(bridge.on_stop)
    bridge.join_worker()
    assert finished.is_set()
    assert not bridge._worker.is_alive()


def test_sentinel_unblocks_a_worker_idle_between_bars(live) -> None:
    # on_stop must unblock a worker parked in get() even with no bar in flight.
    def runner(bt):
        for bar in bt.replay():
            pass

    bridge, _ctx = _start_bridge(live, runner)
    # Drive nothing — worker is parked at the very first get(). Sentinel must still end it.
    live.call(bridge.on_stop)
    bridge.join_worker(timeout=5)
    assert not bridge._worker.is_alive()


# ── on_start precedes data / fail-loud ────────────────────────────────────────


def test_cell_error_before_replay_fails_on_start(live) -> None:
    def runner(bt):
        raise RuntimeError("boom before replay")

    bridge = LiveCellBridge(cell_runner=runner, strategy_id="S-err")
    bridge.register(_RecordingCtx())
    with pytest.raises(RuntimeError, match="boom before replay"):
        live.call(bridge.on_start)


def test_cell_returns_without_replay_fails_on_start(live) -> None:
    # A cell that never iterates bt.replay() must FAIL the attach (fail-loud), not report a
    # successful run with a dead worker (which would then deadlock the live loop on bar 0).
    def runner(bt):
        return  # never enters the live loop

    bridge = LiveCellBridge(cell_runner=runner, strategy_id="S-noreplay")
    bridge.register(_RecordingCtx())
    with pytest.raises(RuntimeError, match="did not reach bt.replay"):
        live.call(bridge.on_start)


def test_cell_error_mid_loop_raises_not_deadlock(live) -> None:
    # A cell crash on a LATER bar must surface as an exception from drive_bar (so the driver fails
    # the run — #25 finding 5) and must NOT hang the live loop waiting for a completion the dead
    # worker will never signal.
    def runner(bt):
        n = 0
        for bar in bt.replay():
            n += 1
            if n == 2:
                raise RuntimeError("boom on bar 2")

    bridge, _ctx = _start_bridge(live, runner)
    _drive(live, bridge, _bar("7203.TSE", 1000, 11.0))  # bar 1 — fine
    with pytest.raises(RuntimeError, match="boom on bar 2"):
        _drive(live, bridge, _bar("7203.TSE", 2000, 12.0))  # bar 2 — cell raises; drive_bar re-raises
    bridge.join_worker()
    assert not bridge._worker.is_alive()


def test_submit_outside_open_bar_fails_closed(live) -> None:
    # Mirrors the Replay contract: submit with no open bar raises (the cell calls it inside the
    # loop, so this guards a misuse path).
    captured: dict = {}

    def runner(bt):
        captured["bt"] = bt
        for bar in bt.replay():
            pass

    bridge, _ctx = _start_bridge(live, runner)
    live.call(bridge.on_stop)
    bridge.join_worker()
    with pytest.raises(ValueError, match="requires an open bar"):
        captured["bt"].submit_market(100)
