"""Unit gate (#49): load_replay_data resolves the ADR-0006 DuckDB direct-read path.

Pins the LOADED-transition contract for the DuckDB branch:
  - root resolved from ctor arg OR .env (BACKCAST_JQUANTS_DUCKDB_ROOT), ctor wins;
  - DuckDB takes PRECEDENCE over an explicitly-passed catalog (no nautilus fallback);
  - NO prime: the reducer clock is reset to 0 so historical bars aren't stale-dropped;
  - a missing per-symbol file fails at LOAD (panel feedback);
  - stop/force-stop clears the root so a later catalog load isn't misrouted.

Uses an empty .duckdb file (load_replay_data only checks existence — it does not open it).
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.core import DataEngine  # noqa: E402


def _touch_daily(root, symbol="8918") -> None:
    d = root / "stocks_daily"
    d.mkdir(parents=True, exist_ok=True)
    (d / f"{symbol}.duckdb").write_bytes(b"")  # existence-only check


def test_ctor_root_loads_duckdb_branch_without_prime(tmp_path) -> None:
    _touch_daily(tmp_path)
    eng = DataEngine(duckdb_root=str(tmp_path))
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")

    assert ok and err is None
    assert eng.replay_duckdb_root == str(tmp_path)
    assert eng.replay_state == "LOADED"
    assert eng._replay_primary_id == "8918.TSE"
    assert eng._mode == "replay"
    # No prime of bar data: nothing accumulated yet (bar 0 streams exactly once at run time).
    assert eng._rs.ohlc_points == []
    # But a valid "warming-up" placeholder so the pre-stream poll satisfies TradingState
    # (price>0 & timestamp>0); timestamp_ms=1 is below any historical bar so nothing stale-drops.
    assert eng._rs.timestamp_ms == 1
    assert eng._rs.price > 0


def test_loaded_state_is_pollable_before_streaming(tmp_path) -> None:
    """Regression: a LOADED DuckDB replay must build a valid TradingState BEFORE any bar
    streams (the UI poll thread hits this window). A price=0/timestamp=0 reset raised
    '2 validation errors for TradingState (price/timestamp > 0)' on every early poll."""
    _touch_daily(tmp_path)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    st = eng.get_current_state()  # must NOT raise pydantic ValidationError
    assert st.price > 0 and st.timestamp_ms > 0
    assert st.ohlc_points == []  # warming-up: no candle rendered yet


def test_env_root_resolves_when_no_ctor_arg(tmp_path, monkeypatch) -> None:
    _touch_daily(tmp_path)
    monkeypatch.setenv("BACKCAST_JQUANTS_DUCKDB_ROOT", str(tmp_path))
    eng = DataEngine()  # no ctor arg → falls back to .env
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok and eng.replay_duckdb_root == str(tmp_path)


def test_duckdb_takes_precedence_over_catalog(tmp_path) -> None:
    """A vestigial catalog arg is ignored when a DuckDB root is configured (no nautilus)."""
    _touch_daily(tmp_path)
    eng = DataEngine(nautilus_catalog_path="/some/catalog", duckdb_root=str(tmp_path))
    ok, _ = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert ok and eng.replay_duckdb_root == str(tmp_path)
    # The catalog branch was not taken → no providers built.
    assert eng._replay_provider is None and eng._replay_providers == {}


def test_missing_file_fails_at_load(tmp_path) -> None:
    eng = DataEngine(duckdb_root=str(tmp_path))  # no file created
    ok, err = eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    assert not ok and err and "not found" in err
    assert eng.replay_duckdb_root is None


def test_force_stop_clears_root(tmp_path) -> None:
    _touch_daily(tmp_path)
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.load_replay_data(["8918.TSE"], "2024-10-01", "2025-01-10", "Daily")
    eng.start()  # LOADED → RUNNING
    eng.force_stop_replay()
    assert eng.replay_duckdb_root is None


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
