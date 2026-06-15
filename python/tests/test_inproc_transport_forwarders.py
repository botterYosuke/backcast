"""Unit gate (#30): InprocLiveServer Replay-transport forwarders.

The #30 footer drives transport via the server (one object for run + control). These forwarders
map the DataEngine (ok, err) tuple to the {success, error_code, error_message} dict shape the C#
side already consumes from start_engine. Pure-Python: no nautilus, no DuckDB mount.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402
from engine.inproc_server import InprocLiveServer  # noqa: E402


def _server():
    return InprocLiveServer(DataEngine())


def test_pause_resume_roundtrip_dict_shape() -> None:
    srv = _server()
    srv._engine._replay_state = "RUNNING"  # as after start_engine

    paused = srv.pause_replay()
    assert paused == {"success": True, "error_code": "", "error_message": ""}
    assert srv._engine.replay_state == "PAUSED"

    resumed = srv.resume_replay()
    assert resumed["success"] is True
    assert srv._engine.replay_state == "RUNNING"


def test_pause_rejected_when_not_running_maps_to_failure_dict() -> None:
    srv = _server()  # IDLE
    res = srv.pause_replay()
    assert res["success"] is False
    assert res["error_code"] == "TRANSPORT_REJECTED"
    assert "only allowed from RUNNING" in res["error_message"]


def test_set_replay_speed_forwarder_stores_and_reports() -> None:
    srv = _server()
    res = srv.set_replay_speed(5)
    assert res["success"] is True
    assert srv._engine.replay_speed_multiplier == 5

    bad = srv.set_replay_speed(0)
    assert bad["success"] is False and bad["error_code"] == "TRANSPORT_REJECTED"


def test_step_forwarder_pulses_step_event_on_kernel_path() -> None:
    srv = _server()
    srv._engine._replay_duckdb_root = "/synthetic/root"
    srv._engine._replay_state = "PAUSED"
    srv._engine._run_event.clear()

    res = srv.step_replay()
    assert res["success"] is True
    assert srv._engine.step_event.is_set()


def test_force_stop_forwarder_returns_idle() -> None:
    srv = _server()
    srv._engine._replay_state = "RUNNING"
    res = srv.force_stop_replay()
    assert res["success"] is True
    assert srv._engine.replay_state == "IDLE"


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
