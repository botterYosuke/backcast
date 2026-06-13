"""S2-spike — live asyncio-loop cross-thread marshal gate (issue #7).

Step 2's threading seam is NOT the same as S0's (#2). S0 proved a *threaded
backtest* (bounded, one run) loads + runs under Unity Mono + pythonnet with a C#
worker holding ``Py.GIL()`` while the main thread stays GIL-free. It did NOT
exercise the live seam: a long-lived asyncio event loop owned by the engine on a
Python daemon thread, into which the host worker marshals work via
``run_coroutine_threadsafe(coro, loop).result(timeout)``. That marshal — and the
``.result()`` internal GIL hand-off (worker releases the GIL inside
``Condition.wait`` so the loop thread can acquire it, run the coro, then the
worker re-acquires) — is the core unknown this spike gates, BEFORE any venue
connection (issue #7 AC: "venue 実接続なし / throwaway spike").

This module is a *self-contained seam model* (粒度1): it reproduces the exact
marshal mechanism of production WITHOUT importing the venue stack, so a Mono
failure points squarely at the asyncio-marshal layer rather than at venue
wiring / LiveRunner / Nautilus kernel. The SAME public methods are driven by:
  * this module's ``run_smoke()`` under plain CPython (pre-isolation — proves the
    loop/thread/marshal logic + the assertions themselves are sound), and
  * ``Assets/Editor/S2SpikeLiveLoopProbe.cs`` under Unity Mono (the verdict).
A Mono-only failure with this CPython smoke passing isolates the fault to the
pythonnet/Mono GIL seam (mirrors the S1 CPython-smoke + Mono-probe split, #9).

Production correspondence (pinned here so the spike does not drift from prod):
  * loop ownership            -> engine/live/live_orchestrator.py:164 (_ensure_live_loop:
                                 new_event_loop() -> run_forever() on a daemon thread).
  * host-facing marshal       -> engine/live/engine_controller.py:583 / :614
                                 (run_coroutine_threadsafe(_cancel/_stop, loop).result(timeout=6/10)).
  * graceful cancel = ADR-0001 decision 6 (best-effort取消 of resting venue orders on
    NORMAL shutdown). The cancel goes through the SAME marshal, so if AC(a) fails
    under Mono, decision 6 is UNREACHABLE on this path (broker指値 が残る) — that is a
    SAFETY finding, not "live is slow" (escalate per issue #7 判定).

GREEN criterion is **prompt completion, NOT "no-hang"** (issue #7 AC, explicit):
``.result(timeout)`` raises ``TimeoutError`` under GIL starvation instead of
hanging forever, so a broken system where every call times out also "doesn't
hang". We therefore assert elapsed ≈ the coro's intrinsic cost, well below the
timeout boundary, over N calls (a system that succeeds once is not GREEN — AC
targets a long-lived loop's *continuous* GIL ping-pong).

Self-failing gate (mirrors python/spike/s0_backtest.py / s1_adapter_smoke.py):
  [S2-SPIKE LIVE LOOP PASS] calls=N median=..s ticks=N order=ACK..  -> exit 0
  any band/median/timeout/cancel/order mismatch                     -> exit 1
"""

from __future__ import annotations

import asyncio
import statistics
import sys
import threading
import time

# ---------------------------------------------------------------------------
# Pins / thresholds (issue #7 grill-with-docs, owner-confirmed 2026-06-13).
# timeout = safety net (no-hang -> TimeoutError); per-call upper = starvation
# detector; median = steady-state; lower bound + work counter = no-op false-green
# killer. See docs/findings/0005-s2-spike-live-loop.md §AC(a).
# ---------------------------------------------------------------------------

BUSY_S = 0.010            # ~10ms of GIL-holding real Python work (perf_counter-bounded)
SLEEP_S = 0.050           # await asyncio.sleep -> yields to the loop (models venue I/O wait)
ORDER_BUSY_S = BUSY_S     # same intrinsic as _work_coro so the order shares the AC(a) band
ORDER_SLEEP_S = SLEEP_S
MARSHAL_TIMEOUT_S = 5.0   # generous: the no-hang->TimeoutError mechanism, NOT what PASS leans on
CANCEL_TIMEOUT_S = 6.0    # mirrors engine_controller.py:583
STOP_TIMEOUT_S = 10.0     # mirrors engine_controller.py:614

N_CALLS = 10              # marshal N times while tick-push runs (one success ≠ GREEN)
ELAPSED_LOWER_S = 0.04    # < intrinsic ~0.06s; below this => no-op fast path (false green)
ELAPSED_UPPER_S = 1.0     # << 5s timeout => prompt, not GIL-starvation limping under the timeout
MEDIAN_UPPER_S = 0.25     # steady-state prompt-ness (excludes nothing; cold call counts too)

RESTING_ORDERS = 3        # mock resting venue orders that decision-6 cancel must drain to 0
TICK_INTERVAL_S = 0.005   # sub-thread tick-push cadence (models venue WS thread -> loop)

LOOP_THREAD_NAME = "s2spike-live-loop"
TICK_THREAD_NAME = "s2spike-tick-pump"


class S2SpikeError(RuntimeError):
    """Raised by the S2-spike gate on any mismatch (exit 1)."""


# ---------------------------------------------------------------------------
# Self-contained seam model — daemon live loop + run_coroutine_threadsafe marshal.
# Public methods return ONLY primitives / dicts of primitives so the C# (pythonnet)
# probe can read them via PyObject.As<long/double/string/bool>() without ever
# handing a PyObject back across the boundary (mirrors S0/S1 discipline).
# ---------------------------------------------------------------------------


class LiveLoopSeam:
    """Models engine-owned live loop ownership + host marshal (venue-free).

    The host (CPython smoke main, or a C# worker under Py.GIL()) calls
    ``marshal_work`` / ``marshal_order`` / ``graceful_stop``; each blocks the
    CALLING thread on ``Future.result()`` while the daemon loop thread runs the
    coroutine — exactly the cross-thread GIL hand-off this spike gates.
    """

    def __init__(self, resting_orders: int = RESTING_ORDERS,
                 tick_interval_s: float = TICK_INTERVAL_S) -> None:
        self._loop: asyncio.AbstractEventLoop | None = None
        self._thread: threading.Thread | None = None
        self._tick_thread: threading.Thread | None = None
        self._tick_stop = threading.Event()
        self._tick_count = 0          # bumped on the loop thread via call_soon_threadsafe
        self._resting = int(resting_orders)
        self._cancel_ran = False
        self._stopped = False
        self._order_seq = 0
        self._tick_interval_s = float(tick_interval_s)

    # --- loop ownership (mirror live_orchestrator.py:164 _ensure_live_loop) ---

    def start(self) -> bool:
        """Spawn the daemon loop thread (run_forever) + the tick-push sub-thread.

        Returns True once the loop is running. The loop thread is a *daemon*
        (ADR-0001 d3: orphan-free — it cannot block process exit when the host
        dies), matching production's "phase8-live-loop" daemon.
        """
        loop = asyncio.new_event_loop()

        def run_loop() -> None:
            asyncio.set_event_loop(loop)
            loop.run_forever()

        self._loop = loop
        self._thread = threading.Thread(target=run_loop, name=LOOP_THREAD_NAME, daemon=True)
        self._thread.start()

        # Wait until the loop is actually running before any marshal is scheduled.
        deadline = time.perf_counter() + 5.0
        while not loop.is_running():
            if time.perf_counter() >= deadline:
                raise S2SpikeError("live loop did not reach run_forever() within 5s")
            time.sleep(0.001)

        self._start_tick_pump()
        return True

    def _start_tick_pump(self) -> None:
        """Continuous sub-thread tick push (models the venue WS thread -> loop).

        Each tick is marshalled onto the live loop via call_soon_threadsafe, so
        the loop stays busy with cross-thread work WHILE the host marshals
        .result() calls — proving the loop is not starved by the host's waits.
        """
        def pump() -> None:
            while not self._tick_stop.is_set():
                loop = self._loop
                if loop is None:
                    break
                try:
                    loop.call_soon_threadsafe(self._on_tick)
                except RuntimeError:
                    break  # loop stopped/closed
                time.sleep(self._tick_interval_s)

        self._tick_thread = threading.Thread(target=pump, name=TICK_THREAD_NAME, daemon=True)
        self._tick_thread.start()

    def _on_tick(self) -> None:
        # Runs on the loop thread; int += is atomic under the GIL.
        self._tick_count += 1

    def tick_count(self) -> int:
        return int(self._tick_count)

    # --- host-facing marshal (mirror engine_controller.py:583/614) ---

    async def _work_coro(self) -> int:
        """Real GIL-holding work + an awaited yield. Returns a work counter > 0.

        The busy-loop holds the GIL for ~BUSY_S (perf_counter-bounded, not a fixed
        iteration count) so the loop thread provably occupies the GIL for a
        MEASURABLE time while the host worker blocks in .result() — exposing the
        GIL ping-pong (``async def f(): return 1`` returns too fast to expose it,
        issue #7 AC). The returned counter proves the coro actually executed (a
        no-op fast path returning a canned value would yield counter == 0).
        """
        counter = 0
        end = time.perf_counter() + BUSY_S
        while time.perf_counter() < end:
            counter += 1
        await asyncio.sleep(SLEEP_S)
        return counter

    def marshal_work(self, timeout: float = MARSHAL_TIMEOUT_S) -> int:
        """Host worker -> live loop, blocking on .result (the gated marshal)."""
        loop = self._require_loop()
        fut = asyncio.run_coroutine_threadsafe(self._work_coro(), loop)
        return int(fut.result(timeout=timeout))

    async def _order_coro(self, n: int) -> str:
        end = time.perf_counter() + ORDER_BUSY_S
        while time.perf_counter() < end:
            pass
        await asyncio.sleep(ORDER_SLEEP_S)
        return f"ACK:{n}"

    def marshal_order(self, timeout: float = MARSHAL_TIMEOUT_S) -> str:
        """Concurrent order-issue call — same marshal, asserted on the same band.

        Called from a SEPARATE worker thread than marshal_work so two callers
        contend for the GIL + the single loop at once (issue #7 AC: "並行して
        注文発行コールを呼んでも (a) が成立し prompt に返る").
        """
        loop = self._require_loop()
        self._order_seq += 1
        n = self._order_seq
        fut = asyncio.run_coroutine_threadsafe(self._order_coro(n), loop)
        return str(fut.result(timeout=timeout))

    # --- shutdown order = ADR-0001 decision 6 (mirror engine_controller.py:583/614) ---

    async def _cancel_resting_orders(self) -> None:
        """decision 6: best-effort cancel of resting venue orders, via the marshal."""
        await asyncio.sleep(0.020)
        self._resting = 0
        self._cancel_ran = True

    async def _stop(self) -> None:
        await asyncio.sleep(0.010)
        self._stopped = True

    def graceful_stop(self, cancel_timeout: float = CANCEL_TIMEOUT_S,
                       stop_timeout: float = STOP_TIMEOUT_S) -> dict:
        """(1) cancel coroutine (decision 6) THEN (2) stop — same marshal as prod.

        Run by the host WORKER (the .result waits block the worker, GIL released
        internally so the loop runs). The host's MAIN thread must only join the
        worker — never block in Python — to avoid an ANR-kill during host quit
        (issue #7 AC(b)). Returns the post-conditions as primitives.
        """
        loop = self._require_loop()
        asyncio.run_coroutine_threadsafe(self._cancel_resting_orders(), loop).result(timeout=cancel_timeout)
        asyncio.run_coroutine_threadsafe(self._stop(), loop).result(timeout=stop_timeout)
        return {
            "cancel_ran": bool(self._cancel_ran),
            "resting": int(self._resting),
            "stopped": bool(self._stopped),
        }

    def teardown_loop(self, join_timeout: float = STOP_TIMEOUT_S) -> bool:
        """Stop the loop + join the daemon thread, THEN close the loop.

        AC(b) ordering: this MUST run only AFTER graceful_stop has completed and
        the worker has been joined (host main calls it last). Returns True if the
        loop thread terminated.
        """
        self._tick_stop.set()
        loop = self._loop
        if loop is not None and loop.is_running():
            try:
                loop.call_soon_threadsafe(loop.stop)
            except RuntimeError:
                pass  # already stopped/closed by a concurrent path
        if self._thread is not None:
            self._thread.join(timeout=join_timeout)
        if self._tick_thread is not None:
            self._tick_thread.join(timeout=1.0)
        # close() only once the loop has actually stopped — closing a still-running
        # loop raises (e.g. the daemon thread did not honor stop within join_timeout).
        if loop is not None and not loop.is_running():
            try:
                loop.close()
            except Exception:
                pass
        return not (self._thread is not None and self._thread.is_alive())

    def _require_loop(self) -> asyncio.AbstractEventLoop:
        if self._loop is None:
            raise S2SpikeError("seam not started (call start() first)")
        return self._loop


# ---------------------------------------------------------------------------
# AC(b) reverse-order negative micro-check (pure asyncio semantics, not Mono):
# proves WHY the ordering matters. Runs on an INDEPENDENT instance.
# ---------------------------------------------------------------------------


def reverse_order_negative_check() -> dict:
    """Demonstrate decision-6 空振り when teardown PRECEDES the cancel.

    Build a fresh seam, CLOSE its loop FIRST (the forbidden order), then attempt
    to schedule the cancel coroutine onto the dead loop: run_coroutine_threadsafe
    must fail IMMEDIATELY (RuntimeError: event loop is closed) — not wait 6s — so
    cancel_ran stays False and resting stays > 0. The orphan coroutine is
    explicitly close()d on failure to avoid a "never awaited" warning.

    Returns {cancel_ran, resting, schedule_failed}; raises S2SpikeError if the
    cancel somehow RAN on the closed loop (which would make the AC(b) ordering
    guard vacuous).
    """
    seam = LiveLoopSeam(resting_orders=RESTING_ORDERS)
    seam.start()
    seam.teardown_loop()  # kill the loop FIRST (forbidden order)

    loop = seam._loop
    assert loop is not None
    coro = seam._cancel_resting_orders()
    scheduled_ok = False
    try:
        fut = asyncio.run_coroutine_threadsafe(coro, loop)
        fut.result(timeout=1.0)
        scheduled_ok = True
    except RuntimeError:
        coro.close()      # expected: loop closed -> schedule fails at once
    except Exception:
        coro.close()
        raise

    if scheduled_ok:
        raise S2SpikeError(
            "reverse-order negative check FAILED: cancel ran on a CLOSED loop "
            "(the AC(b) ordering guard would be vacuous)"
        )
    return {
        "cancel_ran": bool(seam._cancel_ran),
        "resting": int(seam._resting),
        "schedule_failed": True,
    }


# ---------------------------------------------------------------------------
# CPython smoke driver (pre-isolation gate). The C# Mono probe drives the SAME
# public methods with C# worker threads + Stopwatch timing; here the main thread
# does the marshals directly (single CPython interpreter — the GIL-discipline leg
# is a Mono/pythonnet concern verified in S2SpikeLiveLoopProbe.cs, not here).
# ---------------------------------------------------------------------------


def run_smoke() -> str:
    """Drive the seam through the full AC(a)+(b) gate. Returns the PASS line."""
    seam = LiveLoopSeam(resting_orders=RESTING_ORDERS)
    seam.start()

    # AC(a): a concurrent order-issue call runs on its own worker WHILE the main
    # thread does N marshal_work calls and the tick-pump sub-thread pushes ticks.
    order_box: dict = {}

    def order_worker() -> None:
        t0 = time.perf_counter()
        try:
            ack = seam.marshal_order(timeout=MARSHAL_TIMEOUT_S)
            order_box["elapsed"] = time.perf_counter() - t0
            order_box["ack"] = ack
        except Exception as exc:  # noqa: BLE001 — surfaced as a gate failure below
            order_box["error"] = repr(exc)

    ot = threading.Thread(target=order_worker, name="s2spike-order-worker")
    ot.start()

    elapseds: list[float] = []
    for i in range(N_CALLS):
        t0 = time.perf_counter()
        counter = seam.marshal_work(timeout=MARSHAL_TIMEOUT_S)  # raises TimeoutError on starvation
        dt = time.perf_counter() - t0
        elapseds.append(dt)
        if counter <= 0:
            raise S2SpikeError(
                f"[S2-SPIKE LIVE LOOP FAIL] marshal_work #{i} work counter={counter} "
                "(coro did not execute — no-op fast path?)"
            )

    ot.join(timeout=STOP_TIMEOUT_S)

    # --- AC(a) assertions ---
    if "error" in order_box:
        raise S2SpikeError(f"[S2-SPIKE LIVE LOOP FAIL] concurrent order marshal: {order_box['error']}")
    ack = order_box.get("ack")
    if not (isinstance(ack, str) and ack.startswith("ACK:")):
        raise S2SpikeError(f"[S2-SPIKE LIVE LOOP FAIL] order result != expected ACK:* (got {ack!r})")
    order_elapsed = order_box.get("elapsed", float("inf"))
    if not (ELAPSED_LOWER_S <= order_elapsed < ELAPSED_UPPER_S):
        raise S2SpikeError(
            f"[S2-SPIKE LIVE LOOP FAIL] order elapsed={order_elapsed:.4f}s "
            f"outside [{ELAPSED_LOWER_S}, {ELAPSED_UPPER_S})s"
        )

    out_of_band = [(i, dt) for i, dt in enumerate(elapseds)
                   if not (ELAPSED_LOWER_S <= dt < ELAPSED_UPPER_S)]
    if out_of_band:
        raise S2SpikeError(
            f"[S2-SPIKE LIVE LOOP FAIL] {len(out_of_band)} call(s) outside "
            f"[{ELAPSED_LOWER_S}, {ELAPSED_UPPER_S})s: "
            + ", ".join(f"#{i}={dt:.4f}s" for i, dt in out_of_band[:3])
        )

    median = statistics.median(elapseds)
    if median >= MEDIAN_UPPER_S:
        raise S2SpikeError(
            f"[S2-SPIKE LIVE LOOP FAIL] median elapsed={median:.4f}s >= {MEDIAN_UPPER_S}s "
            "(not steady-state prompt — GIL hand-off limping)"
        )

    ticks = seam.tick_count()
    if ticks <= 0:
        raise S2SpikeError(
            "[S2-SPIKE LIVE LOOP FAIL] tick count=0 — sub-thread tick push never "
            "reached the loop (loop starved by the host's .result waits?)"
        )

    # --- AC(b) positive: graceful_stop (cancel -> stop) THEN teardown ---
    gs = seam.graceful_stop()
    if not gs["cancel_ran"]:
        raise S2SpikeError(
            "[S2-SPIKE LIVE LOOP FAIL] decision-6 cancel did NOT run through the marshal "
            "(cancel_ran=False) — broker resting orders would survive"
        )
    if gs["resting"] != 0:
        raise S2SpikeError(f"[S2-SPIKE LIVE LOOP FAIL] resting orders not drained (resting={gs['resting']})")
    if not gs["stopped"]:
        raise S2SpikeError("[S2-SPIKE LIVE LOOP FAIL] stop coroutine did not complete (stopped=False)")

    if not seam.teardown_loop():
        raise S2SpikeError("[S2-SPIKE LIVE LOOP FAIL] loop thread did not terminate on teardown")

    # --- AC(b) negative: reverse order on an independent instance ---
    neg = reverse_order_negative_check()
    if neg["cancel_ran"] or neg["resting"] <= 0 or not neg["schedule_failed"]:
        raise S2SpikeError(
            f"[S2-SPIKE LIVE LOOP FAIL] reverse-order negative check did not behave: {neg!r}"
        )

    return (
        f"[S2-SPIKE LIVE LOOP PASS] calls={N_CALLS} median={median:.4f}s "
        f"band=[{min(elapseds):.4f},{max(elapseds):.4f}]s ticks={ticks} order={ack} "
        f"cancel_ran=True resting=0 reverse_order_guarded"
    )


def main() -> None:
    try:
        line = run_smoke()
    except S2SpikeError as exc:
        print(exc)
        sys.exit(1)
    except Exception as exc:  # noqa: BLE001 — any unexpected error fails the gate
        print(f"[S2-SPIKE LIVE LOOP FAIL] unexpected error: {exc!r}")
        sys.exit(1)
    print(line)
    sys.exit(0)


if __name__ == "__main__":
    main()
