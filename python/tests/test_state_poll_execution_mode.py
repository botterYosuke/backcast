"""#39 Slice 3 (footer UI) D1: poll JSON must carry the authoritative execution_mode.

The footer is poll-canonical for the current mode (display reconciles to engine
truth). That requires `get_state_json` to report `mode_manager.current_mode`, not
the TradingState field default. ModeManager is the authority for the live mode
(it lives in the live layer, separate from the replay DataEngine), so the poll
serializer must inject the mode it already reads.
"""
from __future__ import annotations

import json

from engine.core import DataEngine
from engine.live.state_machine import VenueStateMachine
from engine.mode_manager import ModeManager
from engine._backend_impl import DataEngineBackend


def _backend() -> tuple[DataEngineBackend, ModeManager]:
    eng = DataEngine()
    venue_sm = VenueStateMachine()
    eng.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=eng)
    eng.attach_mode_manager(mode_manager)
    backend = DataEngineBackend(engine=eng, mode_manager=mode_manager, venue_sm=venue_sm)
    return backend, mode_manager


def test_get_state_json_reports_replay_by_default() -> None:
    backend, _ = _backend()
    assert json.loads(backend.get_state_json())["execution_mode"] == "Replay"


def test_get_state_json_reflects_current_execution_mode() -> None:
    """When the authoritative mode is LiveAuto, the poll JSON must report LiveAuto
    (so the footer can overwrite its optimistic display with the engine truth)."""
    backend, mode_manager = _backend()
    mode_manager.current_mode = "LiveAuto"
    assert json.loads(backend.get_state_json())["execution_mode"] == "LiveAuto"
