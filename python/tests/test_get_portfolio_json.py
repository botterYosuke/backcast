"""#65 read-path + JSON layer: get_portfolio surfaces realized/unrealized, and get_portfolio_json
emits the keys C#'s ReplayPanelDecoder.DecodePortfolio consumes.

The running snapshot (ReplayKernelObserver, tested in test_replay_kernel_observer) is published into
engine.last_portfolio; these tests pin the two layers that carry it out to C#:
  - DataEngineBackend.get_portfolio  : dict → PortfolioResult (+ realized/unrealized, default 0.0)
  - BackendService.get_portfolio_json: PortfolioResult → dict → JSON string (the Replay poll source)

Constructors for both classes are heavy (full DataEngine / live wiring), so we bypass __init__ via
object.__new__ and inject only the attribute each method reads — these are pure boundary mappers.
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine._backend_impl import DataEngineBackend  # noqa: E402
from engine.backend_service import BackendService  # noqa: E402


class _StubEngine:
    def __init__(self, last_portfolio) -> None:
        self.last_portfolio = last_portfolio


def _backend(last_portfolio) -> DataEngineBackend:
    be = object.__new__(DataEngineBackend)  # bypass heavy __init__
    be.engine = _StubEngine(last_portfolio)
    return be


_RUNNING_SNAPSHOT = {
    "buying_power": 900_000.0,
    "cash": 900_000.0,
    "equity": 1_005_000.0,
    "positions": [
        {"symbol": "8918.TSE", "qty": 100, "avg_price": 1000.0, "unrealized_pnl": 0.0}
    ],
    "orders": [
        {"symbol": "8918.TSE", "side": "BUY", "qty": 100.0, "price": 1000.0,
         "status": "FILLED", "ts_ms": 1_700_000_000_000}
    ],
    "realized_pnl": 0.0,
    "unrealized_pnl": 5_000.0,
}


def test_get_portfolio_surfaces_running_realized_and_unrealized() -> None:
    pf = _backend(_RUNNING_SNAPSHOT).get_portfolio()
    assert pf.realized_pnl == 0.0
    assert pf.unrealized_pnl == 5_000.0
    assert pf.orders and pf.orders[0].symbol == "8918.TSE" and pf.orders[0].status == "FILLED"
    assert pf.positions and pf.positions[0].qty == 100


def test_get_portfolio_defaults_realized_unrealized_for_final_snapshot() -> None:
    # compute_portfolio's post-run dict has no realized/unrealized keys → default 0.0, no KeyError.
    final = {"buying_power": 1.0, "cash": 1.0, "equity": 2.0, "positions": [], "orders": []}
    pf = _backend(final).get_portfolio()
    assert pf.realized_pnl == 0.0 and pf.unrealized_pnl == 0.0


def test_get_portfolio_json_emits_decoder_keys() -> None:
    svc = object.__new__(BackendService)
    svc._srv = _backend(_RUNNING_SNAPSHOT)

    payload = json.loads(svc.get_portfolio_json())

    # keys DecodePortfolio reads (buying_power/equity/positions[symbol,qty,avg_price,unrealized_pnl]).
    assert payload["buying_power"] == 900_000.0
    assert payload["equity"] == 1_005_000.0
    pos = payload["positions"][0]
    assert (pos["symbol"], pos["qty"], pos["avg_price"], pos["unrealized_pnl"]) == (
        "8918.TSE", 100, 1000.0, 0.0
    )
    # #65: orders are now surfaced (PortfolioDto must add the field; was always [] pre-#65).
    assert payload["orders"][0]["status"] == "FILLED"
    # #65: RunResult running-view pnl cells.
    assert payload["realized_pnl"] == 0.0 and payload["unrealized_pnl"] == 5_000.0


def test_get_portfolio_json_empty_when_no_portfolio() -> None:
    # findings 0044 §3/§7-b honest-empty: last_portfolio is None (loaded-but-not-running, or just
    # cleared at run start) → "" so C# shows "(no data)" (ShowReplayEmpty), NOT a zero-filled snapshot
    # that would misread as real zero buying power.
    svc = object.__new__(BackendService)
    svc._srv = _backend(None)
    assert svc.get_portfolio_json() == ""


if __name__ == "__main__":
    test_get_portfolio_surfaces_running_realized_and_unrealized()
    test_get_portfolio_defaults_realized_unrealized_for_final_snapshot()
    test_get_portfolio_json_emits_decoder_keys()
    test_get_portfolio_json_empty_when_no_portfolio()
    print("[GET_PORTFOLIO_JSON PASS] read-path + JSON layer carry the #65 running snapshot")
