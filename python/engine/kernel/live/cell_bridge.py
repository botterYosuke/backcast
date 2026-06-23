"""engine.kernel.live.cell_bridge — drive a marimo strategy cell as a live Strategy (#112 S2).

ADR-0025 D1/D2: the SAME cell that runs under Replay (``for bar in bt.replay(): ...``) runs
under Auto by swapping ``bt``'s two seams. This module supplies the Auto seams:

* ``LiveCellBackend`` — ``bt``'s (BarSource, ExecutionSeam) for Auto. It runs on the notebook
  **worker thread** (marimo's ``RuntimeContext`` is OS-thread-local — #102), pulls one
  venue-confirmed bar at a time from the rendezvous, buffers ``submit_market`` worker-locally
  (R1), and marshals ``portfolio`` reads to the live loop (R2).
* ``LiveCellBridge`` — the Strategy-duck object ``KernelLiveDriver`` drives on the **live
  loop**. It owns the worker thread and the **A-1 lock-step rendezvous** (D2): the live
  consumer hands one bar to the worker (``drive_bar``), waits for the worker to finish that
  bar, and only then lets the driver ``_drain`` the buffered intents to the venue. This
  reconstructs the industry-standard single-thread event order (data → handler → submit →
  settle) across the thread split marimo forces.

The bridge reaches the live execution seam through the ``StrategyContext`` the controller
hands it in ``register`` (``driver.ctx`` = ``_Ctx``: ``submit_market`` / ``buying_power`` /
``portfolio_snapshot``). So intent enqueue (R1) and portfolio snapshot construction (R2) both
happen on the live loop, keeping ``driver._intents`` and the portfolio on the live-loop thread
exactly as the imperative path does. The driver/controller stay generic — the only driver
change is the surgical ``_consume`` hook that ``await``s ``drive_bar`` when the strategy is a
bridge (a plain ``Strategy`` has no ``drive_bar`` and is byte-identical).

Lifetime (D5): the live ``bt.replay()`` ends ONLY on the stop sentinel (empty queue ≠
StopIteration — the worker blocks = idle, which is how 場引け is expressed). Teardown order
(D5-R, nautilus self-deadlock contract): the live loop never block-joins the worker; the
caller joins it AFTER ``driver.stop`` returns (``join_worker``).

Nautilus-free and marimo-free: importing this module pulls neither (``Backtester`` is
marimo-free; the cell runner that touches marimo is injected by the materialize layer, S3/S5).
"""
from __future__ import annotations

import asyncio
import logging
import queue
import threading
from typing import Callable, Optional

from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import signed_qty_to_side
from engine.kernel.stepper import RunResult, StepEvent, StepHandle
from engine.strategy_runtime.backtester import Backtester

log = logging.getLogger(__name__)

# How long ``on_start`` waits for the worker to reach the first ``bt.replay()`` block before
# declaring the attach failed (nautilus "on_start precedes data": the worker must be ready to
# receive bar 0 before the consumer starts). Generous — running the cell DAG can lazy-load.
_WORKER_READY_TIMEOUT_S = 30.0
# How long a worker-side portfolio marshal (R2) waits for the live loop to return the snapshot.
# The live loop is responsive while ``await``-ing ``drive_bar``, so this resolves in μs; the
# timeout only guards against a torn-down loop.
_MARSHAL_TIMEOUT_S = 10.0


class _StopSentinel:
    """Singleton put on the rendezvous queue to end the live ``bt.replay()`` (D5)."""


_STOP = _StopSentinel()


class LiveCellBackend:
    """``bt``'s (BarSource, ExecutionSeam) for Auto — runs on the worker thread.

    Mirrors ``KernelStepper``'s cell-facing contract method-for-method so the SAME ``Backtester``
    drives it unchanged: ``open_next_bar`` blocks on the rendezvous instead of pulling a
    historical bar; ``submit_market`` buffers to the open bar's instrument (R3) instead of
    filling at close; ``portfolio_snapshot`` marshals to the live loop (R2).
    """

    def __init__(self, bridge: "LiveCellBridge") -> None:
        self._bridge = bridge
        # Worker-local buffer (R1): (side, quantity, instrument_id) bundles flushed at the bar
        # boundary in ONE call_soon_threadsafe hop. Never crosses threads as a mutable handle.
        self._pending: list[tuple] = []
        self._current_bar: Optional[Bar] = None
        self._open = False
        self._bars_seen = 0
        self._terminal: Optional[StepHandle] = None  # set once the stop sentinel is drawn

    # -- BarSource ------------------------------------------------------------

    def open_next_bar(self) -> StepHandle:
        """Block until the live consumer hands over the next venue bar, or finalize on the stop
        sentinel. Flushing the previous bar (signalling completion to the live loop) happens
        first, mirroring ``KernelStepper``'s auto-close-on-next-step so the lock-step holds."""
        if self._terminal is not None:
            return self._terminal
        if self._open:
            self.close_current_bar()  # flush the just-finished bar before blocking for the next
        item = self._bridge._await_next_bar()  # blocks on queue.get(); sets ready on first call
        if item is _STOP:
            # D5: empty queue is idle (we just keep blocking); ONLY the sentinel ends the stream.
            self._terminal = StepHandle(StepEvent.STOPPED, bar=None, reason="stopped")
            return self._terminal
        bar: Bar = item
        self._current_bar = bar
        self._open = True
        self._bars_seen += 1
        return StepHandle(StepEvent.BAR_OPEN, bar=bar)

    def close_current_bar(self) -> None:
        """Signal the live loop that the current bar's body is done, handing it the buffered
        intent bundle (R1). Idempotent — a no-op when no bar is open (so the Phase-2 per-cell
        ``finally``'s ``_close_open_bar`` is safe here too)."""
        if not self._open:
            return
        self._open = False
        bundle = self._pending
        self._pending = []
        self._bridge._signal_bar_done(bundle)

    def finalize(self) -> RunResult:
        """The terminal ``RunResult`` ``bt.replay()`` reads when the sentinel ends the stream.
        Live has no equity curve / sim fills of its own (the venue is authoritative), so this is
        a thin record of bars driven — the host does not finalize a live run into a summary."""
        return RunResult(
            success=True,
            bars=self._bars_seen,
            fills=0,
            final_cash=0.0,
            final_equity=0.0,
            realized_pnl=0.0,
            stopped_reason="stopped",
        )

    def current_or_last_bar(self) -> Optional[Bar]:
        return self._current_bar

    def set_pacing(self, bars_per_second: Optional[float]) -> None:
        # Live pacing is venue-driven (bars arrive when the venue closes them). The cell's
        # ``bars_per_second`` arg is meaningless in Auto — accepted and ignored (no sleep).
        return None

    # -- ExecutionSeam --------------------------------------------------------

    def submit_market(self, qty: float) -> None:
        """Buffer a signed-delta MARKET order to the OPEN bar's instrument (R3). Fail-closed
        outside an open bar, mirroring the Replay contract. The bundle ships at the bar boundary
        (R1) — nothing crosses to the live loop here."""
        if not self._open or self._current_bar is None:
            raise ValueError(
                "bt.submit_market() requires an open bar — call it inside a bt.replay() body "
                "(no bar is open before the first bar or after the stream ends)"
            )
        resolved = signed_qty_to_side(qty)  # NaN/inf → ValueError; 0/-0.0 → None (no-op)
        if resolved is None:
            return
        side, quantity = resolved
        self._pending.append((side, quantity, self._current_bar.instrument_id))

    def portfolio_snapshot(self):
        """Read the live portfolio for the current bar's instrument, marshalled to the live loop
        (R2) so the worker never touches the portfolio dict concurrently with async venue fills
        (``apply_venue_async_event``). ``buying_power`` is the venue-authoritative value."""
        iid = self._current_bar.instrument_id if self._current_bar is not None else None
        return self._bridge._read_portfolio(iid)


class LiveCellBridge:
    """The Strategy-duck object ``KernelLiveDriver`` drives — owns the cell worker + rendezvous.

    Construct with a ``cell_runner``: ``Callable[[Backtester], None]`` that runs the cell body to
    completion (it ends when ``bt.replay()`` raises ``StopIteration`` on the stop sentinel). In
    production the materialize layer (S3/S5) supplies a runner that injects ``bt`` into the
    marimo cell globals and drives the cell DAG on the worker thread; the gates supply a plain
    Python runner. Everything marimo lives behind that callable, keeping this module marimo-free.
    """

    def __init__(
        self,
        *,
        cell_runner: Callable[[Backtester], None],
        strategy_id: str = "",
    ) -> None:
        self._cell_runner = cell_runner
        self.id = strategy_id  # controller overwrites with nautilus_strategy_id after construction
        self._backend = LiveCellBackend(self)
        self._bt = Backtester(self._backend)  # bar_source = execution_seam = the live backend

        # Rendezvous primitives. ``_bar_q`` (thread-safe) carries bars live-loop → worker.
        # ``_completion`` (a loop-bound future) + ``_complete_bar`` carry "bar done + intents"
        # worker → live-loop in one call_soon_threadsafe hop (R1). ``_ready`` gates on_start on
        # the worker reaching the first ``bt.replay()`` block (on_start precedes data).
        self._bar_q: "queue.Queue" = queue.Queue()
        self._ready = threading.Event()
        self._first_get = True
        self._reached_replay = False  # worker set: the cell entered bt.replay() (ready for data)
        self._worker_exited = False   # live loop set: the worker terminated (cleanly or by a cell error)
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._ctx = None  # the live StrategyContext (driver.ctx) captured in register
        self._worker: Optional[threading.Thread] = None
        self._worker_error: Optional[BaseException] = None
        self._completion: Optional[asyncio.Future] = None
        self._stopping = False

    # -- Strategy interface the driver / controller use -----------------------

    def register(self, ctx) -> None:
        """Capture the live execution seam (``driver.ctx`` — ``_Ctx``). The bridge routes
        ``submit_market`` (R1, on the live loop in ``_complete_bar``) and ``portfolio_snapshot``
        (R2) through it, so intents land in ``driver._intents`` and the portfolio is read on the
        live-loop thread, exactly as the imperative path does."""
        self._ctx = ctx

    def on_start(self) -> None:
        """Spawn the worker and block (on the live loop, briefly) until it reaches the first
        ``bt.replay()`` ``next()`` — guaranteeing on_start completes before bar 0 (nautilus
        invariant). A cell that raises before reaching ``replay`` fails the attach (fail-loud,
        like a Strategy raising in on_start)."""
        self._loop = asyncio.get_running_loop()
        self._worker = threading.Thread(
            target=self._run_worker, name=f"cell-worker-{self.id}", daemon=True
        )
        self._worker.start()
        if not self._ready.wait(timeout=_WORKER_READY_TIMEOUT_S):
            raise RuntimeError(
                "cell worker did not reach the first bt.replay() within "
                f"{_WORKER_READY_TIMEOUT_S}s"
            )
        if self._worker_error is not None:
            raise self._worker_error  # cell raised before the first bar — surface as attach failure
        if not self._reached_replay:
            # _ready was set by the worker's exit (not by reaching bt.replay) — the cell returned
            # without entering the live loop, so there is nothing to drive in Auto. Fail the attach
            # (fail-loud) rather than report a successful run with a dead worker.
            raise RuntimeError("cell did not reach bt.replay(); nothing to drive in Auto")

    async def drive_bar(self, bar: Bar) -> None:
        """Live loop: hand ``bar`` to the worker and await its completion. On return the worker's
        buffered intents are already in ``driver._intents`` (enqueued by ``_complete_bar`` on this
        loop) and the driver's ``_consume`` does ``await _drain()`` next — preserving the frozen
        on_bar → drain order across the thread split (D2 A-1 / R1)."""
        # If the worker already exited (a cell crash or an early return), do NOT block on a bar it
        # will never draw. Re-raise a cell error so the driver fails the run — a mid-run cell crash
        # must not be swallowed (the imperative on_bar path upholds the same #25 finding-5 contract).
        if self._worker_exited:
            if self._worker_error is not None and not self._stopping:
                raise self._worker_error
            return
        if self._stopping:
            return
        fut = self._loop.create_future()  # type: ignore[union-attr]
        self._completion = fut
        self._bar_q.put(bar)
        await fut

    def on_stop(self) -> None:
        """Live loop (``driver.stop``): stop accepting bars, put the sentinel to unblock the
        worker's ``get()``, and resolve any in-flight completion so a cancelled ``drive_bar``
        unwinds. Does NOT join the worker — the caller joins after ``driver.stop`` returns
        (D5-R: the live loop must never block-join the worker)."""
        self._stopping = True
        self._bar_q.put(_STOP)
        fut = self._completion
        if fut is not None and not fut.done():
            fut.set_result(None)

    def on_order(self, event) -> None:
        # The cell observes fills through ``bt.portfolio()`` (R2 marshal), not a callback —
        # so the bridge has no on_order reaction. (Async fills mutate the portfolio on the live
        # loop; the next bar's body reads the updated book.)
        return None

    def on_tick(self, evt) -> None:
        # The cell consumes bars (``bt.replay()`` yields bars); ticks don't drive it.
        return None

    # -- worker thread lifecycle ----------------------------------------------

    def _run_worker(self) -> None:
        try:
            self._cell_runner(self._bt)
        except BaseException as exc:  # noqa: BLE001 — surface to on_start / teardown
            # A clean sentinel exit raises StopIteration inside bt.replay() which the cell's
            # ``for`` loop swallows, so reaching here is a real cell error.
            self._worker_error = exc
            log.exception("cell worker raised")
        finally:
            self._ready.set()  # unblock on_start whether we reached replay or failed first
            self._signal_worker_exit()  # unblock any in-flight drive_bar (no live-loop deadlock)

    def join_worker(self, timeout: float = 10.0) -> None:
        """Join the worker — call on the CALLER thread AFTER ``driver.stop`` returns (D5-R). Safe
        because the caller is not the live loop, so the worker's final R2 round-trip (if any) is
        serviced by the live loop instead of deadlocking."""
        worker = self._worker
        if worker is not None:
            worker.join(timeout=timeout)

    # -- rendezvous primitives (worker ⇄ live loop) ---------------------------

    def _await_next_bar(self):
        """Worker: block for the next bar (or the stop sentinel). Signals readiness on the first
        call so ``on_start`` knows the worker reached ``bt.replay()`` (on_start precedes data)."""
        if self._first_get:
            self._first_get = False
            self._reached_replay = True  # distinguishes "reached replay" from "worker exited" in on_start
            self._ready.set()
        return self._bar_q.get()

    def _signal_worker_exit(self) -> None:
        """Worker thread: tell the live loop the worker has terminated (cleanly or by a cell error)."""
        loop = self._loop
        if loop is None:
            return
        try:
            loop.call_soon_threadsafe(self._on_worker_exit)
        except RuntimeError:
            pass  # loop already closed (teardown) — nothing left to unblock

    def _on_worker_exit(self) -> None:
        """Live loop: mark the worker gone and unblock any in-flight ``drive_bar``. A cell error
        surfaces as the future's exception (so the driver fails the run); a clean exit / stop
        resolves with None. Guarded against an already-resolved future (on_stop may have set it)."""
        self._worker_exited = True
        fut = self._completion
        if fut is not None and not fut.done():
            if self._worker_error is not None and not self._stopping:
                fut.set_exception(self._worker_error)
            else:
                fut.set_result(None)

    def _signal_bar_done(self, bundle: list) -> None:
        """Worker: ship the buffered intent bundle to the live loop and resolve the in-flight
        completion in ONE hop (R1). Enqueue itself runs on the live loop (``_complete_bar``)."""
        loop = self._loop
        if loop is None:  # defensive: never reached after on_start
            return
        loop.call_soon_threadsafe(self._complete_bar, bundle)

    def _complete_bar(self, bundle: list) -> None:
        """Live loop: enqueue the worker's buffered submits into the driver (via the captured
        ctx → ``driver._intents``), then resolve ``drive_bar``. ``_consume`` drains right after."""
        ctx = self._ctx
        if ctx is not None:
            for side, quantity, instrument_id in bundle:
                ctx.submit_market(
                    strategy_id=self.id,
                    instrument_id=instrument_id,
                    side=side,
                    quantity=quantity,
                )
        fut = self._completion
        if fut is not None and not fut.done():
            fut.set_result(None)

    def _read_portfolio(self, instrument_id: Optional[str]):
        """Worker: marshal a portfolio-snapshot read to the live loop (R2). The live loop is
        responsive while ``await``-ing ``drive_bar``, so this returns in μs."""
        loop = self._loop
        ctx = self._ctx
        if loop is None or ctx is None:
            raise RuntimeError("bt.portfolio() called before the live bridge was started")

        async def _coro():
            return ctx.portfolio_snapshot(instrument_id)

        return asyncio.run_coroutine_threadsafe(_coro(), loop).result(timeout=_MARSHAL_TIMEOUT_S)
