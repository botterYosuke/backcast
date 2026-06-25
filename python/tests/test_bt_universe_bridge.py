"""ADR-0031 S1 (#141) — bt.universe.* ↔ C# InstrumentRegistry bridge (Python half).

The C#/Unity half (drain → InstrumentRegistry mutate → chart spawn/despawn) is gated by the
AFK runner ``UniverseBridgeE2ERunner`` (Python-FREE). This module gates the Python contract:

  - ``bt.universe.add/remove/clear`` enqueue edit ops on the engine (write channel, D2);
  - ``bt.universe.list()`` reads the registry mirror the host pushes back — Python keeps NO own
    SoT (D2): an enqueued-but-not-yet-applied edit does NOT change ``list()``;
  - the round-trip (enqueue → host drains+applies+pushes → list reflects) is faithful;
  - validation + fail-closed (no bridge / no scenario);
  - parity: a run that never calls ``bt.universe.*`` produces ZERO edits (#24 golden untouched —
    the KernelStepper run path is byte-identical, ADR-0031 D-consequences).

The ``_FakeRegistryHost`` mirrors the C# main-thread loop and ``InstrumentRegistry`` semantics
(order-preserving, first-occurrence-wins dedup, idempotent remove/clear). RED→GREEN: deleting
the engine ``enqueue_universe_edit`` append (write channel) or the ``push_universe_ids`` mirror
(read channel) makes the round-trip assertions fail.
"""
from __future__ import annotations

import os

import pytest

from engine.core import DataEngine
from engine.strategy_runtime.backtester import (
    Backtester,
    NoScenarioBacktester,
    _UniverseHandle,
)
from engine.strategy_runtime.universe_bridge import EngineUniverseBridge


class _FakeRegistryHost:
    """Stands in for the C# main-thread drain → InstrumentRegistry apply → push loop.

    Mirrors ``InstrumentRegistry`` (Assets/Scripts/ScenarioStartup/InstrumentRegistry.cs):
    order-preserving, first-occurrence-wins dedup on Add, idempotent Remove/clear. After applying
    a drained batch it pushes the resulting Ids back so ``bt.universe.list()`` reads the SoT.
    """

    def __init__(self, engine: DataEngine) -> None:
        self._engine = engine
        self.ids: list[str] = []

    def pump(self) -> None:
        for edit in self._engine.drain_universe_edits():
            op, iid = edit["op"], edit["id"]
            if op == "add":
                if iid not in self.ids:
                    self.ids.append(iid)
            elif op == "remove":
                if iid in self.ids:
                    self.ids.remove(iid)
            elif op == "clear":
                self.ids = []
        self._engine.push_universe_ids(self.ids)


def _make_bt(engine: DataEngine) -> Backtester:
    """A Backtester wired to the real engine universe channels, with a stub bar source (this
    module never drives bars — it gates the universe bridge only)."""
    return Backtester(object(), universe_bridge=EngineUniverseBridge(engine))


# ---------------------------------------------------------------------------
# write channel — bt.universe.* enqueues ops on the engine
# ---------------------------------------------------------------------------

@pytest.mark.scenario("BTUNIV-01")
def test_add_remove_clear_enqueue_edits() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)

    bt.universe.add("7203.TSE")
    bt.universe.remove("9984.TSE")
    bt.universe.clear()

    edits = engine.drain_universe_edits()
    assert edits == [
        {"op": "add", "id": "7203.TSE"},
        {"op": "remove", "id": "9984.TSE"},
        {"op": "clear", "id": ""},
    ]
    # drain is destructive — a second drain is empty.
    assert engine.drain_universe_edits() == []


# ---------------------------------------------------------------------------
# round-trip — host drains+applies+pushes, list() reflects the registry
# ---------------------------------------------------------------------------

@pytest.mark.scenario("BTUNIV-02")
def test_add_roundtrip_reflected_in_list() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    host = _FakeRegistryHost(engine)

    bt.universe.add("7203.TSE")
    bt.universe.add("9984.TSE")
    host.pump()  # C# main thread applies + pushes the mirror back

    assert bt.universe.list() == ["7203.TSE", "9984.TSE"]
    assert host.ids == ["7203.TSE", "9984.TSE"]


@pytest.mark.scenario("BTUNIV-03")
def test_remove_roundtrip_drops_from_list() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    host = _FakeRegistryHost(engine)

    bt.universe.add("7203.TSE")
    bt.universe.add("9984.TSE")
    host.pump()
    bt.universe.remove("7203.TSE")
    host.pump()

    assert bt.universe.list() == ["9984.TSE"]


@pytest.mark.scenario("BTUNIV-04")
def test_clear_roundtrip_empties_list() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    host = _FakeRegistryHost(engine)

    bt.universe.add("7203.TSE")
    bt.universe.add("9984.TSE")
    host.pump()
    bt.universe.clear()
    host.pump()

    assert bt.universe.list() == []


# ---------------------------------------------------------------------------
# D2 — Python keeps NO own SoT: list() is the host mirror, not local intent
# ---------------------------------------------------------------------------

@pytest.mark.scenario("BTUNIV-05")
def test_list_is_host_mirror_not_local_intent() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    host = _FakeRegistryHost(engine)

    # Edit enqueued but the host has NOT pumped yet → list() still reflects the (empty) registry.
    bt.universe.add("7203.TSE")
    assert bt.universe.list() == []  # no own SoT — the edit is pending, not applied

    host.pump()
    assert bt.universe.list() == ["7203.TSE"]

    # A UI edit applied on the host (not via bt) is also visible to list() — the mirror is the SoT.
    host.ids.append("6758.TSE")
    engine.push_universe_ids(host.ids)
    assert bt.universe.list() == ["7203.TSE", "6758.TSE"]


# ---------------------------------------------------------------------------
# validation + fail-closed
# ---------------------------------------------------------------------------

@pytest.mark.scenario("BTUNIV-06")
def test_blank_id_raises() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    for bad in ("", "   ", None, 123):
        with pytest.raises((ValueError, TypeError)):
            bt.universe.add(bad)  # type: ignore[arg-type]
    # nothing was enqueued
    assert engine.drain_universe_edits() == []


def test_id_is_stripped() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    bt.universe.add("  7203.TSE  ")
    assert engine.drain_universe_edits() == [{"op": "add", "id": "7203.TSE"}]


@pytest.mark.scenario("BTUNIV-07")
def test_fail_closed_without_bridge() -> None:
    bt = Backtester(object())  # no universe_bridge wired
    with pytest.raises(RuntimeError):
        bt.universe.add("7203.TSE")
    with pytest.raises(RuntimeError):
        bt.universe.list()


def test_no_scenario_placeholder_universe_guides() -> None:
    bt = NoScenarioBacktester()
    with pytest.raises(RuntimeError, match="commit the startup panel"):
        bt.universe.add("7203.TSE")
    with pytest.raises(RuntimeError, match="commit the startup panel"):
        bt.universe.list()


# ---------------------------------------------------------------------------
# parity — bt.universe.* is the SOLE edit producer (silent when unused → #24 golden untouched)
# ---------------------------------------------------------------------------

@pytest.mark.scenario("BTUNIV-08")
def test_unused_universe_emits_no_edits() -> None:
    engine = DataEngine()
    bt = _make_bt(engine)
    # A run that never touches bt.universe.* enqueues nothing — the registry/run path is unchanged.
    assert bt.universe is not None
    assert isinstance(bt.universe, _UniverseHandle)
    assert engine.drain_universe_edits() == []
    assert engine.get_universe_ids() == []


@pytest.mark.scenario("BTUNIV-14")
def test_live_cell_bridge_wires_universe_bridge() -> None:
    """ADR-0031 S4/S5: a LiveAuto LiveCellBridge threads the universe bridge so a cell's
    bt.universe.* edits the registry (add→subscribe / remove→unsubscribe). Regression for the gap
    where LiveCellBridge built Backtester with no universe_bridge → bt.universe fail-closed in Auto."""
    from engine.kernel.live.cell_bridge import LiveCellBridge

    engine = DataEngine()
    lcb = LiveCellBridge(cell_runner=lambda bt: None, universe_bridge=EngineUniverseBridge(engine))
    lcb._bt.universe.add("7203.TSE")
    lcb._bt.universe.remove("9984.TSE")
    assert engine.drain_universe_edits() == [
        {"op": "add", "id": "7203.TSE"},
        {"op": "remove", "id": "9984.TSE"},
    ]
    # LiveAuto's bt has NO mid-stream data join (venue feeds data — S2 is Replay-only): the backend
    # exposes no join_instrument, so the handle must not try to call it.
    assert not hasattr(lcb._backend, "join_instrument")


def test_live_cell_bridge_without_bridge_fail_closed() -> None:
    from engine.kernel.live.cell_bridge import LiveCellBridge

    lcb = LiveCellBridge(cell_runner=lambda bt: None)
    with pytest.raises(RuntimeError):
        lcb._bt.universe.add("7203.TSE")


# Real marimo fixture used by the loader-threading gate (mirrors test_marimo_live_guard).
_MARIMO_FIXTURE = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "spike", "fixtures", "strategies", "kernel_spike_buy_sell_cell.py",
)


@pytest.mark.scenario("BTUNIV-15")
def test_loader_threads_universe_bridge_end_to_end() -> None:
    """ADR-0031 S4/S5 — the LiveAuto bridge plumbing at its ACTUAL bug site. The prior HIGH bug
    (findings 0113 §Review F1) lived in the loader→factory→bridge chain, NOT the LiveCellBridge
    ctor (which BTUNIV-14 already pins). This drives the real ``build_live_marimo_loader(
    universe_bridge=)`` → factory → ``LiveCellBridge`` → ``Backtester`` and asserts a cell's
    ``bt.universe.add`` reaches the engine. Litmus: drop the ``universe_bridge`` param threading in
    either ``build_live_marimo_loader`` OR ``_make_bridge_factory`` → this goes RED (BTUNIV-14 stays
    green, so it alone does not protect the fix)."""
    from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader

    engine = DataEngine()
    _app, _scenario, factory = build_live_marimo_loader(
        universe_bridge=EngineUniverseBridge(engine)
    )(_MARIMO_FIXTURE)
    bridge = factory(instrument_id="8918.TSE")
    bridge._bt.universe.add("8918.TSE")
    assert engine.drain_universe_edits() == [{"op": "add", "id": "8918.TSE"}]


def test_loader_without_universe_bridge_fail_closed_end_to_end() -> None:
    """The default (no ``universe_bridge``) must still fail-closed through the WHOLE chain — proves
    the BTUNIV-15 positive isn't vacuously green from an unrelated default."""
    from engine.strategy_runtime.live_cell_runtime import build_live_marimo_loader

    _app, _scenario, factory = build_live_marimo_loader()(_MARIMO_FIXTURE)
    bridge = factory(instrument_id="8918.TSE")
    with pytest.raises(RuntimeError):
        bridge._bt.universe.add("8918.TSE")


def test_inproc_drain_and_push_json_layer() -> None:
    """The InprocLiveServer JSON layer (PyO3 boundary) round-trips edits and ids as strings."""
    import json

    from engine.inproc_server import InprocLiveServer

    engine = DataEngine()
    srv = InprocLiveServer(engine)

    engine.enqueue_universe_edit("add", "7203.TSE")
    engine.enqueue_universe_edit("clear")
    drained = json.loads(srv.drain_universe_edits())
    assert drained == [{"op": "add", "id": "7203.TSE"}, {"op": "clear", "id": ""}]

    ack = srv.push_universe_ids(json.dumps(["7203.TSE", "9984.TSE"]))
    assert ack["success"] is True
    assert engine.get_universe_ids() == ["7203.TSE", "9984.TSE"]

    # Malformed JSON is rejected, not crashed.
    assert srv.push_universe_ids("{not json")["success"] is False


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
