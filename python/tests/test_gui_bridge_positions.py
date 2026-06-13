"""Regression gate (#9-12 review, Medium 1): the positions panel must go FLAT
after a position closes.

THE BUG: GuiBridgeActor.make_position_handler used cache.positions() — which
returns BOTH open AND closed positions. After the spike SELL closes 8918.TSE,
the cache still holds the closed position at qty=0, so the final push_portfolio
snapshot carried a phantom `8918.TSE qty=0` row that never left the positions
panel (matches the HITL log).

THE FIX: use cache.positions_open() so a closed position drops out and the
terminal snapshot is FLAT (empty positions list).

Pure-Python + a fake cache/sink — no Nautilus backtest, no Unity, deterministic.
Runnable directly (`python tests/test_gui_bridge_positions.py`) or via pytest.
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.live.gui_bridge_actor import GuiBridgeActor  # noqa: E402


class _FakeQty:
    def __init__(self, v: float) -> None:
        self._v = v

    def as_double(self) -> float:
        return self._v


class _FakePosition:
    """A CLOSED 8918.TSE position (qty=0), mirroring the HITL log."""

    def __init__(self) -> None:
        self.instrument_id = "8918.TSE"
        self.quantity = _FakeQty(0.0)
        self.avg_px_open = 100.0


class _FakeCache:
    """positions() exposes the closed (qty=0) position like Nautilus does;
    positions_open() filters it out (the correct source for a live panel)."""

    def __init__(self) -> None:
        self._closed = _FakePosition()

    def account_for_venue(self, venue):  # noqa: ANN001 - duck-typed
        return None  # -> equity/buying_power fall back to 0 (handler tolerates)

    def positions(self):
        return [self._closed]

    def positions_open(self):
        return []  # the closed position is no longer open


class _FakeSink:
    def __init__(self) -> None:
        self.portfolios: list[dict] = []

    def push_portfolio(self, payload: str) -> None:
        self.portfolios.append(json.loads(payload))


def _run_handler_once() -> dict:
    sink = _FakeSink()
    bridge = GuiBridgeActor(sink, instrument_id="8918.TSE")
    handler = bridge.make_position_handler(cache=_FakeCache(), venue_str="SIM")
    handler(object())  # a PositionClosed-like event; payload is cache-driven
    assert sink.portfolios, "handler pushed no portfolio snapshot"
    return sink.portfolios[-1]


def test_closed_position_drops_out_of_snapshot() -> None:
    snapshot = _run_handler_once()
    positions = snapshot["positions"]
    assert positions == [], (
        "positions panel still carries a closed position — expected FLAT, got "
        f"{positions!r}. make_position_handler must use cache.positions_open()."
    )


if __name__ == "__main__":
    try:
        test_closed_position_drops_out_of_snapshot()
    except AssertionError as exc:
        print(f"[GUI BRIDGE POSITIONS FAIL] {exc}")
        sys.exit(1)
    print("[GUI BRIDGE POSITIONS PASS] closed position drops out -> snapshot is flat")
