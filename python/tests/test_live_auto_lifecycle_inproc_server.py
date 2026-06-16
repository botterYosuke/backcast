"""Engine-side gate for the #39→#59 host's LiveAuto RPC sequence (Step 3/4).

WorkspaceEngineHost marshals the footer's LiveAuto onto the live-configured InprocLiveServer with
exactly this call sequence: venue_login(MOCK) → set_execution_mode(LiveAuto) →
register_live_strategy → start_live_strategy → (run is live) → stop_live_strategy →
set_execution_mode(Replay). This test drives that sequence through the SAME InprocLiveServer surface
the host calls and asserts each step succeeds and the poll reflects the mode transitions.

Step 1 (test_live_configured_server_replay_intact) proved the Replay half on the unified server; this
proves the Live half. Together they verify decision 1 (one persistent live-configured server runs
both). A configured MockVenueAdapter is injected via the adapter factory so no real venue is needed.
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_STRATEGY = os.path.join(
    _PYTHON_ROOT, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"
)
IID = "8918.TSE"


def test_live_auto_lifecycle_on_live_configured_server(tmp_path, monkeypatch) -> None:
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer
    from engine.live.mock_adapter import MockVenueAdapter
    import engine.live.live_adapter_factory as laf

    # Inject a configured mock so InprocLiveServer(de, "MOCK") connects to a venue with an account
    # (InprocLiveServer builds its factory from the venue string; patch it before construction).
    mock = MockVenueAdapter()
    mock.set_account_snapshot(cash=10_000_000.0, buying_power=10_000_000.0, positions=())
    monkeypatch.setattr(laf, "build_live_adapter_factory", lambda venue: (lambda env_hint=None: mock))

    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.set_rust_event_sink(lambda *a, **k: None)   # live-config: sink wired (no-op stub)
    server = InprocLiveServer(eng, "MOCK")
    try:
        # connect → LiveAuto (the mode precondition requires a connected venue)
        assert server.venue_login("MOCK", "env", "")["success"]
        assert server.set_execution_mode("LiveAuto")["success"]
        assert json.loads(server.get_state_json())["execution_mode"] == "LiveAuto"

        # register → start (the host's 2-stage StartLiveAuto)
        reg = server.register_live_strategy(_STRATEGY, "")
        assert reg["success"], reg
        start = server.start_live_strategy(reg["strategy_id"], IID, "MOCK")
        assert start["success"], start
        run_id = start["run_id"]
        assert run_id, "start_live_strategy returned no run_id"

        # the run is live and the engine still reports LiveAuto
        assert json.loads(server.get_state_json())["execution_mode"] == "LiveAuto"

        # stop → switch back to Replay (the D2 stop-then-switch, in sequence)
        assert server.stop_live_strategy(run_id)["success"]
        assert server.set_execution_mode("Replay")["success"]
        assert json.loads(server.get_state_json())["execution_mode"] == "Replay"
    finally:
        server.close()


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
