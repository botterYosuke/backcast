"""engine.strategy_runtime.universe_bridge — the production ``bt.universe`` ↔ engine bridge.

ADR-0031 S1 (#141). ``bt.universe.*`` edits the C# ``InstrumentRegistry`` SoT as a
"programmatic user edit" (D2). The cell handle (``Backtester.universe``) is decoupled from the
engine by the ``UniverseBridge`` protocol (defined in ``backtester.py`` so that module stays
marimo-free and import-light); this module supplies the production implementation that talks to
the shared ``DataEngine`` channels:

  - write (Python→C#): ``add`` / ``remove`` / ``clear`` enqueue an edit op on the engine; the
    Unity host drains them on the main thread and applies each to the registry (Changed → chart
    spawn/despawn is free).
  - read  (C#→Python): ``list`` reads the registry mirror the host pushes back — Python keeps no
    own SoT (D2).

Marimo-free and nautilus-free: it only calls plain ``DataEngine`` methods.
"""
from __future__ import annotations

from typing import Any


class EngineUniverseBridge:
    """``bt.universe``'s production backing: route edits/reads through the ``DataEngine`` channels.

    Holds the SAME ``DataEngine`` the run's observer pushes ``last_portfolio`` through, so the
    Unity host (which polls that engine over the in-proc server) drains the edits and pushes the
    registry mirror on the same object.
    """

    def __init__(self, engine: Any) -> None:
        self._engine = engine

    def add(self, instrument_id: str) -> None:
        self._engine.enqueue_universe_edit("add", instrument_id)

    def remove(self, instrument_id: str) -> None:
        self._engine.enqueue_universe_edit("remove", instrument_id)

    def clear(self) -> None:
        self._engine.enqueue_universe_edit("clear")

    def list(self) -> list[str]:
        return self._engine.get_universe_ids()
