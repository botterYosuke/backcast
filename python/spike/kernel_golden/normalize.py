"""spike.kernel_golden.normalize — sink stream → normalized golden contract (#24).

Nautilus-free (imported by the kernel subprocess that must stay Rust-core-free).
Turns a captured sink event stream into the normalized contract used as the golden
(findings 0008 §4): the semantic 6 items, NOT the raw JSON. Volatile, impl-specific
identifiers (client_order_id / venue_order_id / strategy_id) are dropped so a kernel
run and the Nautilus oracle compare on meaning, not on Nautilus' ID formatting.

Floats here are bit-reproducible across oracle and kernel (same parquet raw decode,
same `equity_curve_stats` on an identical equity list — findings 0008 §2), so exact
value equality is used rather than tolerance comparison.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1

# A captured event is a tuple: ("bar", payload) / ("order", payload) /
# ("portfolio", payload) / ("run_complete", run_id, summary).
CapturedEvent = tuple


class CaptureSink:
    """Duck-types RustBacktestSink, recording every push in order."""

    def __init__(self) -> None:
        self.events: list[CapturedEvent] = []

    def push_bar(self, payload: str) -> None:
        self.events.append(("bar", json.loads(payload)))

    def push_order(self, payload: str) -> None:
        self.events.append(("order", json.loads(payload)))

    def push_portfolio(self, payload: str) -> None:
        self.events.append(("portfolio", json.loads(payload)))

    def push_run_complete(self, run_id: str, summary: str) -> None:
        self.events.append(("run_complete", run_id, json.loads(summary)))

    def push_run_failed(self, *args: Any) -> None:
        self.events.append(("run_failed", list(args)))


def _order_semantic(payload: dict) -> dict:
    """Order fields that are part of the contract (drop volatile ids)."""
    return {
        "symbol": payload["symbol"],
        "side": payload["side"],
        "status": payload["status"],
        "qty": payload["qty"],
        "price": payload["price"],
        "timestamp_ms": payload["timestamp_ms"],
    }


# Derived float stats (sharpe/sortino/max_drawdown) come from the same
# `equity_curve_stats`, so they are bit-reproducible on one machine but can differ in the
# last ULP across platforms/BLAS. Round them so the golden survives a re-host without
# masking a real regression (gross changes survive rounding). Raw prices/cash/qty are
# exact (parquet raw decode / integer arithmetic) and are NOT rounded.
_STATS_DECIMALS = 9


def _round_summary(summary: dict | None) -> dict | None:
    if summary is None:
        return None
    return {
        k: (round(v, _STATS_DECIMALS) if isinstance(v, float) else v)
        for k, v in summary.items()
    }


def normalize(events: list[CapturedEvent], *, initial_cash: float) -> dict:
    """Build the normalized golden contract from a captured sink stream."""
    sequence = [e[0] for e in events]
    orders = [_order_semantic(e[1]) for e in events if e[0] == "order"]
    portfolios = [e[1] for e in events if e[0] == "portfolio"]
    bars = [e[1] for e in events if e[0] == "bar"]
    run_completes = [e for e in events if e[0] == "run_complete"]

    # Final account from the terminal portfolio snapshot (or initial if no trades).
    if portfolios:
        last = portfolios[-1]
        final_cash = last["buying_power"]
        final_equity = last["equity"]
    else:
        final_cash = final_equity = float(initial_cash)

    run_summary = run_completes[-1][2] if run_completes else None

    return {
        "schema_version": SCHEMA_VERSION,
        "contract": {
            "sink_event_sequence": sequence,
            "bar_count": len(bars),
            "order_states": [{"side": o["side"], "status": o["status"]} for o in orders],
            "fills": [
                {
                    "side": o["side"],
                    "qty": o["qty"],
                    "price": o["price"],
                    "timestamp_ms": o["timestamp_ms"],
                    "symbol": o["symbol"],
                }
                for o in orders
            ],
            # Full portfolio payloads (cash/equity + positions) so the INTERMEDIATE
            # held-position cash debit is a checked value — not just the flat terminal
            # state, which would make the cash/PnL dimension trivially satisfiable.
            "portfolio_snapshots": [
                {
                    "buying_power": p["buying_power"],
                    "equity": p["equity"],
                    "positions": p["positions"],
                }
                for p in portfolios
            ],
            "realized_pnl": final_cash - float(initial_cash),
            "final_account": {"cash": final_cash, "equity": final_equity},
            "run_summary": _round_summary(run_summary),
        },
    }


def canonical_json(obj: Any) -> str:
    """Stable JSON text for byte-comparison / hashing."""
    return json.dumps(obj, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()
