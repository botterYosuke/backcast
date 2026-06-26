"""LIVEUNIV-01..05 — a LiveAuto strategy cell that edits the universe MID-STREAM, gated against the
real prod kabu capture replayed through the live data pipeline (#141-145 / ADR-0031 + findings 0117).

WHY THIS GATE EXISTS — the seam no existing gate covers:
  * BTUNIV-* gates ``bt.universe.*`` enqueue/list against a FAKE registry; UNISUB-* gates the C#
    Changed→subscribe/unsubscribe wiring against a MOCK venue with no real market data; the kabu
    replay spikes (kabu_replay_multi) drive the codec→aggregator→reducer with a STATIC universe.
  * NOBODY gated: a strategy cell that ADDS/REMOVES symbols WHILE real prod kabu frames flow
    through the live aggregation pipeline. That is exactly where a "live add/remove breaks the
    chart/feed" bug would live (frame for an unsubscribed symbol, _aggregators mutated during
    _partial_push, reducer per_id orphaning).

WHAT IT DRIVES (faithful LiveAuto, ADR-0025 + ADR-0031 S4/S5):
  build_live_marimo_loader(universe_bridge=EngineUniverseBridge(engine)) → a real marimo cell
  (spike/fixtures/strategies/universe_churn_cell.py) driven through KernelLiveEngineController →
  driver → LiveCellBridge worker → ``for bar in bt.replay(): bt.universe.add/remove(...)``. The
  cell's edits enqueue on the engine universe channel; a PUMP plays the C# host role — it drains
  ``engine.drain_universe_edits()`` and applies each op to ``runner.subscribe/unsubscribe`` +
  ``engine.push_universe_ids`` (mirroring DriveUniverseBridge → InstrumentRegistry.Changed →
  LiveSubscriptionCoordinator). Market data is the REAL prod kabu capture (findings 0117) decoded
  by the production ``KabuPushFrameProcessor`` and injected into a subscribe-gated MockVenueAdapter.

NON-VACUITY: the MockVenueAdapter is subscribe-gated (inject_tick drops events for unsubscribed
ids), so ANY recorded market data for an id structurally proves it was subscribed at that moment.
  LIVEUNIV-01 (ADD): 9984/285A — absent from the run's initial universe — produce live data only
    because the cell's mid-stream ``bt.universe.add`` was drained into a real subscribe.
  LIVEUNIV-02 (REMOVE): after the churn, a freshly injected trade for a removed id (8306, 9984) is
    DROPPED (no recorded TradesUpdate), while a survivor (7203, 285A) still flows — proving remove
    unsubscribed at the venue. (Floors 01: a no-op remove would let removed ids keep flowing → RED.)
  LIVEUNIV-03: final ``runner.subscribed_ids()`` AND the engine registry mirror both == {7203,285A}.
  LIVEUNIV-04: no crash under churn — the worker exits clean (no cell error), the run finalizes, and
    _partial_push survives _aggregators mutation; clean teardown (worker joined, no fd corruption #112).
  LIVEUNIV-05: reducer per_id consistency — the recorded closed klines populate per_id_ohlc_points
    for the symbols that survived, with no orphan series for a removed-before-any-bar id.

LITMUS (delete-the-production-logic): no-op the pump's subscribe → LIVEUNIV-01 RED (added ids never
flow). No-op the pump's unsubscribe → LIVEUNIV-02 RED (removed ids keep flowing). Mock is venue-free
and deterministic — replaying the committed capture is reproducible.

Run: cd python && uv run pytest tests/test_kabu_live_universe_churn.py -v
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402
from engine.exchanges.kabusapi_ws_codec import KabuPushFrameProcessor  # noqa: E402
from engine.kernel.live.controller import KernelLiveEngineController  # noqa: E402
from engine.live.adapter import KlineUpdate, TradesUpdate  # noqa: E402
from engine.live.live_runner import LiveRunner  # noqa: E402
from engine.live.mock_adapter import MockVenueAdapter  # noqa: E402
from engine.live.reducer_bridge import live_kline_to_reducer_kline  # noqa: E402
from engine.reducer import ReducerState, apply_event  # noqa: E402
from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader  # noqa: E402
from engine.strategy_runtime.universe_bridge import EngineUniverseBridge  # noqa: E402

_HERE = os.path.dirname(os.path.abspath(__file__))
_CELL = os.path.join(_HERE, "..", "spike", "fixtures", "strategies", "universe_churn_cell.py")
# The user-named raw prod capture (findings 0117 §再生成). Fall back to the committed lightweight
# fixture so the gate runs in CI where the 14MB raw is .gitignored.
_RAW = os.path.join(_HERE, "..", "spike", "captures", "kabu_mock_20260626T013342Z.json")
_FIXTURE = os.path.join(_HERE, "fixtures", "kabu_live_mock_4sym.json")

_INITIAL = ["7203.TSE", "8306.TSE"]   # the run's universe = cell-driving symbols
_ADDED = ["9984.TSE", "285A.TSE"]     # added mid-stream (data/chart subs; do NOT drive the cell)
_FINAL = {"7203.TSE", "285A.TSE"}     # after: +9984+285A −8306 −9984
_MINUTE_NS = 60 * 1_000_000_000


def _capture_path() -> str:
    return _RAW if os.path.exists(_RAW) else _FIXTURE


@dataclass
class Churn:
    """Observations from one full kabu-mock live-churn replay (built once, asserted per-id)."""

    capture: str
    worker_error: object
    reached_running: bool
    detached: bool
    converged: bool
    worker_alive: bool                      # the cell worker still alive after detach (orphan)
    bg_errors: list                         # exceptions raised inside the pump/recorder loop tasks
    final_subscribed: set
    mirror_ids: set
    applied_ops: list                       # [(op, id)] the pump drained from the cell
    data_ids_during: set                    # ids that produced ANY market data during the run
    probe_after: dict                       # id -> bool: a fresh trade injected post-churn flowed
    closed_klines: list = field(default_factory=list)   # recorded closed KlineUpdate [(iid, ts)]


def _run_churn() -> Churn:
    capture = _capture_path()
    frames = json.load(open(capture, encoding="utf-8"))["frames"]
    # bare kabu code (frame["Symbol"]) → full instrument_id used by the live pipeline.
    bare_to_full = {iid.split(".")[0]: iid for iid in (_INITIAL + _ADDED)}
    procs = {bare: KabuPushFrameProcessor(symbol=bare) for bare in bare_to_full}

    loop = asyncio.new_event_loop()
    thread = threading.Thread(target=loop.run_forever, name="churn-loop", daemon=True)
    thread.start()

    def run(coro, timeout=10.0):
        return asyncio.run_coroutine_threadsafe(coro, loop).result(timeout)

    engine = DataEngine()
    bridge = EngineUniverseBridge(engine)
    _app, scenario, bridge_factory = build_live_marimo_loader(universe_bridge=bridge)(_CELL)
    assert scenario["instruments"] == _INITIAL

    adapter = MockVenueAdapter()
    runner = LiveRunner(adapter, interval_ns=_MINUTE_NS, partial_push_interval_s=0.2)
    runner._loop = loop
    controller = KernelLiveEngineController(
        loop_provider=lambda: loop,
        adapter_provider=lambda: adapter,
        runner_provider=lambda: runner,
        on_order_event=lambda ev, sid: None,
    )

    # Background-task errors (recorder/pump run as fire-and-forget loop tasks; a swallowed exception
    # there would silently degrade the gate — a dead recorder makes data asserts vacuous). Captured
    # and asserted empty in LIVEUNIV-04.
    bg_errors: list = []

    # --- recorder: an independent bus subscription that captures every published event -----------
    rec_events: list = []  # (kind, iid, ts_ns, is_closed)
    closed_kl: list = []   # the REAL closed KlineUpdate objects (full OHLC) the pipeline produced

    async def _recorder(sub):
        try:
            async for evt in sub:
                if isinstance(evt, TradesUpdate):
                    rec_events.append(("trades", evt.instrument_id, evt.ts_ns, None))
                elif isinstance(evt, KlineUpdate):
                    rec_events.append(("kline", evt.instrument_id, evt.ts_ns, evt.is_closed))
                    if evt.is_closed:
                        closed_kl.append(evt)
        except Exception as exc:  # noqa: BLE001 — surface a dead recorder instead of silent vacuity
            bg_errors.append(("recorder", repr(exc)))

    # --- pump: the C# host role — drain cell universe edits → subscribe/unsubscribe + mirror ------
    pump_registry: set = set(_INITIAL)   # the InstrumentRegistry SoT the host seeds at session start
    applied_ops: list = []
    pump_stop = threading.Event()

    async def _pump_loop():
        engine.push_universe_ids(sorted(pump_registry))   # initial mirror (bt.universe.list seed)
        while not pump_stop.is_set():
            edits = engine.drain_universe_edits()
            changed = False
            for e in edits:
                op, iid = e.get("op"), e.get("id") or ""
                applied_ops.append((op, iid))
                if op == "add":
                    if iid not in pump_registry:
                        pump_registry.add(iid)            # registry mutate BEFORE subscribe (D3)
                        await runner.subscribe(iid)
                        changed = True
                elif op == "remove":
                    if iid in pump_registry:
                        pump_registry.discard(iid)
                        await runner.unsubscribe(iid)
                        changed = True
                elif op == "clear":
                    for dead in list(pump_registry):
                        await runner.unsubscribe(dead)
                    pump_registry.clear()
                    changed = True
            if changed:
                engine.push_universe_ids(sorted(pump_registry))
            await asyncio.sleep(0.005)

    async def _pump():
        try:
            await _pump_loop()
        except Exception as exc:  # noqa: BLE001 — surface a dead pump instead of indirect misdiagnosis
            bg_errors.append(("pump", repr(exc)))

    worker_error = None
    reached_running = False
    detached = False
    try:
        run(adapter.login(None))
        adapter.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
        run(runner.start())
        sub = run(_coro_subscribe_bus(runner))
        asyncio.run_coroutine_threadsafe(_recorder(sub), loop)
        asyncio.run_coroutine_threadsafe(_pump(), loop)

        controller.attach(
            strategy_cls=bridge_factory,
            scenario=scenario,
            instrument_id=_INITIAL[0],
            venue="TSE",
            params={},
            nautilus_strategy_id="LIVE-univ-churn",
            session=object(),
        )
        reached_running = controller._driver is not None

        # --- replay the capture: decode each prod frame → trade → inject (subscribe-gated) --------
        injected = 0
        for rec in frames:
            f = rec["frame"]
            bare = str(f.get("Symbol"))
            if bare not in procs:
                continue
            trade, _ = procs[bare].process(f)
            if trade is None:
                continue
            ev = TradesUpdate(
                kind="trades", instrument_id=bare_to_full[bare], ts_ns=trade["ts_ns"],
                price=trade["price"], size=trade["size"], aggressor_side=trade["aggressor_side"],
            )
            loop.call_soon_threadsafe(adapter.inject_tick, ev)
            injected += 1
            if injected % 20 == 0:
                time.sleep(0.004)   # let the loop drain: consume → aggregate → drive cell → pump

        # Soft-converge on the final membership the cell's edits imply. We do NOT hard-fail here:
        # a broken remove/add chain must surface as a granular per-id RED (LIVEUNIV-02/03), not a
        # fixture error — so we poll up to a bound, then proceed and let the asserts judge.
        converged = _poll(lambda: runner.subscribed_ids() == _FINAL, timeout=8.0)
        time.sleep(0.2)   # let the last pushes/partials settle into the recorder

        data_ids_during = {iid for (_k, iid, _t, _c) in rec_events}

        # --- LIVEUNIV-02 probe: inject ONE fresh trade per symbol; only subscribed ids may flow ---
        # future_ts is strictly disjoint from every replay ts (max+10min), so a recorded event at
        # future_ts can ONLY be a probe trade — never a real one.
        base = len(rec_events)
        future_ts = max((t for (_k, _i, t, _c) in rec_events if t), default=0) + 10 * _MINUTE_NS
        for iid in (_INITIAL + _ADDED):
            loop.call_soon_threadsafe(
                adapter.inject_tick,
                TradesUpdate(kind="trades", instrument_id=iid, ts_ns=future_ts,
                             price=100.0, size=1.0, aggressor_side="buy"),
            )
        # Wait until BOTH survivors' probe trades are recorded (deterministic witness that the whole
        # injected batch has drained), rather than a fixed sleep — so a slow loop is a wait, not a
        # false RED. Removed ids are never recorded regardless, so this can't false-PASS them.
        _poll(lambda: {"7203.TSE", "285A.TSE"}
              <= {iid for (_k, iid, t, _c) in rec_events[base:] if t == future_ts}, timeout=5.0)
        probed = {iid for (_k, iid, t, _c) in rec_events[base:] if t == future_ts}
        probe_after = {iid: (iid in probed) for iid in (_INITIAL + _ADDED)}

        bridge_obj = controller._driver._strategy if controller._driver else None
        worker_error = bridge_obj._worker_error if bridge_obj else None
        final_subscribed = runner.subscribed_ids()
        mirror_ids = set(engine.get_universe_ids())
        closed = list(closed_kl)   # the real closed KlineUpdate objects (full OHLC)

        controller.cancel_inflight_orders(nautilus_strategy_id="LIVE-univ-churn")
        controller.detach(nautilus_strategy_id="LIVE-univ-churn")  # stops driver + joins the worker
        # detach nulls _driver unconditionally, so assert the WORKER actually terminated (no orphan).
        worker = bridge_obj._worker if bridge_obj else None
        worker_alive = bool(worker and worker.is_alive())
        detached = controller._driver is None and not worker_alive
    finally:
        pump_stop.set()
        try:
            run(runner.aclose())
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)
        thread.join(timeout=2.0)

    return Churn(
        capture=os.path.basename(capture),
        worker_error=worker_error,
        reached_running=reached_running,
        detached=detached,
        converged=converged,
        worker_alive=worker_alive,
        bg_errors=list(bg_errors),
        final_subscribed=final_subscribed,
        mirror_ids=mirror_ids,
        applied_ops=applied_ops,
        data_ids_during=data_ids_during,
        probe_after=probe_after,
        closed_klines=closed,
    )


async def _coro_subscribe_bus(runner):
    # bus.subscribe() registers the queue synchronously; must run on the loop thread.
    return runner.bus.subscribe()


def _poll(predicate, timeout=8.0) -> bool:
    """Poll until predicate is true or timeout; return whether it became true (never raises).

    Predicate exceptions are swallowed as 'not yet': reading ``runner.subscribed_ids()`` (==
    ``set(self._aggregators.keys())``) on this thread while the pump mutates ``_aggregators`` on the
    loop thread can raise ``RuntimeError: dictionary changed size during iteration``. That transient
    must not abort the poll — we just retry on the next tick.
    """
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if predicate():
                return True
        except Exception:  # noqa: BLE001 — cross-thread dict-resize transient; retry next tick
            pass
        time.sleep(0.02)
    return False


@pytest.fixture(scope="module")
def churn() -> Churn:
    return _run_churn()


@pytest.mark.scenario("LIVEUNIV-01")
def test_midstream_add_brings_symbols_into_live_feed(churn: Churn) -> None:
    """ADD: 9984/285A — absent from the initial universe — flow live ONLY via bt.universe.add."""
    assert ("add", "9984.TSE") in churn.applied_ops
    assert ("add", "285A.TSE") in churn.applied_ops
    for iid in _ADDED:
        # subscribe-gated adapter ⇒ any recorded data proves the mid-stream subscribe took effect.
        assert iid in churn.data_ids_during, (
            f"{iid} was added mid-stream but produced NO live data — add→subscribe broken "
            f"(capture={churn.capture}, data ids={sorted(churn.data_ids_during)})"
        )


@pytest.mark.scenario("LIVEUNIV-02")
def test_remove_unsubscribes_added_and_initial_symbols(churn: Churn) -> None:
    """REMOVE: post-churn a fresh trade for a removed id is dropped; a survivor still flows."""
    assert ("remove", "8306.TSE") in churn.applied_ops  # an initial cell-driving symbol
    assert ("remove", "9984.TSE") in churn.applied_ops  # a previously-added symbol
    # 8306 was definitely subscribed at attach; 9984 must have been subscribed mid-stream FIRST
    # (else "False after" would pass vacuously for a never-subscribed id). Pin both were live.
    assert "8306.TSE" in churn.data_ids_during, "8306 never flowed — can't prove its unsubscribe"
    assert "9984.TSE" in churn.data_ids_during, "9984 never flowed — its remove probe is vacuous"
    assert churn.probe_after["8306.TSE"] is False, "removed initial symbol still flowed after churn"
    assert churn.probe_after["9984.TSE"] is False, "removed added symbol still flowed after churn"
    assert churn.probe_after["7203.TSE"] is True, "survivor 7203 stopped flowing (over-unsubscribe)"
    assert churn.probe_after["285A.TSE"] is True, "survivor 285A stopped flowing (over-unsubscribe)"


@pytest.mark.scenario("LIVEUNIV-03")
def test_final_membership_consistent_runner_and_mirror(churn: Churn) -> None:
    """Final venue subscription set AND the engine registry mirror both == {7203, 285A}."""
    assert churn.converged, f"membership never converged to {_FINAL} (got {churn.final_subscribed})"
    assert churn.final_subscribed == _FINAL, f"runner subscribed {churn.final_subscribed} != {_FINAL}"
    assert churn.mirror_ids == _FINAL, f"engine mirror {churn.mirror_ids} != {_FINAL}"


@pytest.mark.scenario("LIVEUNIV-04")
def test_no_crash_clean_teardown_under_churn(churn: Churn) -> None:
    """No cell error under churn; attach reached RUNNING; detach tore down cleanly (no orphan worker)."""
    assert churn.reached_running, "controller never reached RUNNING (attach failed)"
    assert churn.worker_error is None, f"cell worker raised under churn: {churn.worker_error!r}"
    assert churn.bg_errors == [], f"pump/recorder loop task raised under churn: {churn.bg_errors}"
    assert not churn.worker_alive, "cell worker still alive after detach (orphan thread / hung join)"
    assert churn.detached, "controller did not detach cleanly (driver not nulled or worker orphaned)"


@pytest.mark.scenario("LIVEUNIV-05")
def test_reducer_per_id_consistency(churn: Churn) -> None:
    """The REAL closed klines the live pipeline produced under churn, fed through the production
    reducer, route to per-instrument series correctly — no symbol merged into another, none orphaned.
    Non-tautological: the reducer is invoked with a PRIMARY of 7203, so a reducer that mis-routed all
    bars to ``primary`` (instead of each kline's own instrument_id) would collapse the multi-symbol
    series and break the per-id count match below."""
    assert churn.closed_klines, "no closed bars recorded under churn — pipeline produced nothing to reduce"

    state = ReducerState(timestamp_ms=0, price=0.0)
    primary = "7203.TSE"
    recorded_counts: dict = {}
    for kl in churn.closed_klines:
        recorded_counts[kl.instrument_id] = recorded_counts.get(kl.instrument_id, 0) + 1
        apply_event(state, live_kline_to_reducer_kline(kl), primary)   # real OHLC, real ids

    closed_ids = set(recorded_counts)
    # Multi-symbol churn must have produced closed bars for more than one symbol (else a single-id
    # degenerate run could pass the routing check vacuously).
    assert len(closed_ids) >= 2, f"only one symbol closed a bar ({closed_ids}) — churn not exercised"
    # per_id keys are EXACTLY the symbols that closed a bar: nothing merged away, nothing orphaned.
    assert set(state.per_id_ohlc_points) == closed_ids, (
        f"per_id keys {set(state.per_id_ohlc_points)} != closed-bar ids {closed_ids} "
        f"(reducer mis-routed or orphaned a symbol's series)"
    )
    # each symbol's series length matches the count of closed bars it actually produced (routing).
    for iid, n in recorded_counts.items():
        got = len(state.per_id_ohlc_points.get(iid, []))
        assert got == n, f"{iid}: per_id series has {got} points, recorded {n} closed bars (mis-routed)"
