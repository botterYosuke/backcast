"""Step 1 gate for the #39→#59 integration (re-home the LiveAuto footer into the
Backcast workspace root).

The plan unifies ReplayEngineHost + the live seam onto ONE persistent server: at
startup build a *live-configured* server — `DataEngine` + `set_rust_event_sink` +
`InprocLiveServer(data_engine, venue)` — and run BOTH Replay and Live on it. This
gate pins the load-bearing precondition: the Replay path (load_replay_data /
start_engine / get_state_json poll / replay transport RPCs) must behave EXACTLY as
on the replay-only server (`InprocLiveServer(data_engine)`), and the Replay run must
NOT push to the live event sink. If this is GREEN the unification foundation holds;
if it ever goes RED the integration premise is broken and we stop.

Mirrors test_replay_duckdb_kernel_afk.py's synthetic-DuckDB fixture (WIRING gate; data
faithfulness is #47/#48 on the real mount). Strategy = kernel-native golden twin.
"""
from __future__ import annotations

import datetime
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import duckdb  # noqa: E402

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_STRATEGY = os.path.join(
    _PYTHON_ROOT, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"
)
_N_BARS = 50  # > SELL_AT_BAR (40) so both legs fill


def _build_synthetic_duckdb(root, *, symbol: str = "8918", n: int = _N_BARS) -> None:
    d = os.path.join(str(root), "stocks_daily")
    os.makedirs(d, exist_ok=True)
    con = duckdb.connect(os.path.join(d, f"{symbol}.duckdb"))
    try:
        con.execute(
            "CREATE TABLE stocks_daily ("
            "Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, Low BIGINT, "
            "Close BIGINT, Volume BIGINT)"
        )
        day0 = datetime.date(2024, 10, 1)
        rows = [
            (day0 + datetime.timedelta(days=i), symbol, 1000 + i, 1005 + i, 995 + i, 1002 + i, 1000 + i)
            for i in range(n)
        ]
        con.executemany("INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)", rows)
    finally:
        con.close()


def test_replay_path_intact_on_live_configured_server(tmp_path) -> None:
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer

    _build_synthetic_duckdb(tmp_path)

    # ── live configuration (mirrors ProductionLiveShell.Start): sink wired + venue id ──
    eng = DataEngine(duckdb_root=str(tmp_path))
    sink_calls: list = []
    eng.set_rust_event_sink(lambda *a, **k: sink_calls.append((a, k)))
    server = InprocLiveServer(eng, "MOCK")  # 2-arg live form (replay-only uses 1-arg)

    # poll before any run works; mode_manager defaults to Replay
    assert json.loads(server.get_state_json())["execution_mode"] == "Replay"

    # Replay path: load on the engine, start on the server (exactly as ReplayEngineHost does)
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok, f"load_replay_data failed on live-configured server: {err}"

    res = server.start_engine({"strategy_file": _STRATEGY})
    assert res["success"], f"start_engine failed on live-configured server: {res}"

    # the kernel strategy traded exactly as on the replay-only server (BUY+SELL fills)
    assert eng.last_portfolio is not None
    assert len(eng.last_portfolio["orders"]) == 2, eng.last_portfolio["orders"]
    # exactly-once streaming invariant (no prime / no skip) survives the live config
    assert len(eng._rs.ohlc_points) == _N_BARS
    assert len(eng._rs.per_id_ohlc_points["8918.TSE"]) == _N_BARS

    # poll after the run still reports Replay state
    assert json.loads(server.get_state_json())["execution_mode"] == "Replay"

    # the Replay transport RPCs are wired on the live-configured server (callable, dict shape)
    for rpc in (server.pause_replay, server.resume_replay, server.step_replay):
        assert "success" in rpc()
    assert "success" in server.set_replay_speed(2)

    # CRITICAL: a Replay run must NEVER push to the live event sink (the sink is live-only)
    assert sink_calls == [], f"Replay streaming invoked the live event sink: {sink_calls}"


def test_sequential_reruns_reset_streaming_state_on_persistent_server(tmp_path) -> None:
    """Code-review F6: the unified host (#39→#59) keeps ONE persistent DataEngine/server across runs
    (the old ReplayEngineHost built a fresh one per run). This pins what that change is responsible
    for: a SECOND Replay run (load_replay_data + start_engine on the SAME engine/server) must reset the
    STREAMING / chart state — load_replay_data re-inits the ReducerState (core.py), so run 2 streams a
    FRESH _N_BARS, not 2x accumulated. This is the invariant the persistent-server decision must keep.

    NOTE — pre-existing, NOT caused by this change: the *portfolio* (RunBuffer fills) DOES accumulate
    across re-runs of the SAME strategy, because `make_run_id(strategy_file)` is deterministic so the
    file-backed RunBuffer re-uses the same run_dir and APPENDS. That run_dir is process-global
    (get_run_buffer_base_dir), independent of the DataEngine instance, so the OLD fresh-per-run host hit
    it too. It is a RunBuffer/run_id concern (#30/#49 replay-run), filed separately — this test
    deliberately does NOT assert portfolio reset so as not to mask it as fixed."""
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer

    _build_synthetic_duckdb(tmp_path)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.set_rust_event_sink(lambda *a, **k: None)
    server = InprocLiveServer(eng, "MOCK")

    for run in (1, 2):
        ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
        assert ok, f"run {run}: load_replay_data failed: {err}"
        res = server.start_engine({"strategy_file": _STRATEGY})
        assert res["success"], f"run {run}: start_engine failed (carryover?): {res}"

        # streaming/chart state is fresh each run (load_replay_data re-inits ReducerState) — NOT 2x.
        assert len(eng._rs.ohlc_points) == _N_BARS, f"run {run}: ohlc carryover {len(eng._rs.ohlc_points)}"
        assert len(eng._rs.per_id_ohlc_points["8918.TSE"]) == _N_BARS, f"run {run}: per-id carryover"


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
