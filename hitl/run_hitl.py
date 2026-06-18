"""HITL driver for the #76 portfolio-driver slice.

Runs a marimo target-position strategy that reads get_portfolio().position through the REAL
production dispatch (_select_replay_strategy → MarimoStrategy adapter → _Context.portfolio_
snapshot → thin-drain subset) against REAL J-Quants DuckDB bars (S:\\jp). Mirrors
_backend_impl._start_engine_duckdb minus the UI observer; KernelRunner + _Context are the
production objects, so this exercises the new code end-to-end on real data.

Run:  uv run python hitl/run_hitl.py     (from the repo root, or via `! ` in the session)
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "python"))

from engine._backend_impl import _select_replay_strategy  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402

STRATEGY_PY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "strat_target_position.py")
DATA_ROOT = os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT", "S:/jp")


class HITLSink:
    """Records one row per fill: the bar that triggered it + the post-fill position/cash."""

    def __init__(self, iid: str) -> None:
        self.iid = iid
        self.rows: list[dict] = []
        self.summary: dict = {}
        self._bar = None
        self._pending = None

    def push_bar(self, bar) -> None:
        self._bar = bar

    def push_order(self, fill) -> None:
        self._pending = fill

    def push_portfolio(self, pf) -> None:
        f = self._pending
        self.rows.append(
            {
                "date": _date(self._bar.ts_event_ns),
                "close": self._bar.close,
                "side": f.side.value,
                "qty": f.last_qty,
                "px": f.last_px,
                "position": pf.net_signed_qty(self.iid),  # post-fill holdings
                "cash": pf.cash,
            }
        )

    def on_equity(self, ts_ms, equity, cash) -> None:
        pass

    def push_run_complete(self, run_id, summary) -> None:
        self.summary = summary


def _date(ts_ns: int) -> str:
    import datetime

    return datetime.datetime.fromtimestamp(ts_ns / 1e9, datetime.timezone.utc).strftime(
        "%Y-%m-%d"
    )


def main() -> int:
    print(f"strategy file : {STRATEGY_PY}")
    print(f"data root     : {DATA_ROOT}\n")

    # 1) PRODUCTION DISPATCH — marimo detect (AST) + sidecar scenario, no UI.
    scenario, factory, label = _select_replay_strategy(STRATEGY_PY)
    print(f"[dispatch] detected label = {label!r}")
    assert label.startswith("marimo:"), f"expected marimo dispatch, got {label!r}"
    iid = scenario["instruments"][0]
    print(f"[dispatch] scenario       = {iid} {scenario['granularity']} "
          f"{scenario['start']}..{scenario['end']} cash={scenario['initial_cash']:,}\n")

    # 2) REAL run through the production KernelRunner + _Context (the adapter reads
    #    ctx.portfolio_snapshot each bar at on_bar entry = pre-fill = no-look-ahead).
    strat = factory(iid)
    sink = HITLSink(iid)
    try:
        result = KernelRunner(
            data_root=DATA_ROOT,
            instrument_ids=list(scenario["instruments"]),
            granularity=scenario["granularity"],
            start=scenario["start"],
            end=scenario["end"],
            initial_cash=scenario["initial_cash"],
            strategy=strat,
            sink=sink,
        ).run()
    finally:
        getattr(strat, "close", lambda: None)()  # S6a teardown ownership

    # 3) HITL report — eyeball the trade log + summary.
    print(f"{'date':12} {'close':>8} {'side':>4} {'qty':>5} {'fill_px':>9} "
          f"{'->position':>10} {'cash':>14}")
    print("-" * 72)
    for r in sink.rows:
        print(f"{r['date']:12} {r['close']:>8.1f} {r['side']:>4} {r['qty']:>5.0f} "
              f"{r['px']:>9.1f} {r['position']:>10.0f} {r['cash']:>14,.0f}")

    final_position = sink.rows[-1]["position"] if sink.rows else 0.0
    print("\n[run result]")
    print(f"  success        : {result.success}")
    print(f"  bars           : {result.bars}")
    print(f"  fills          : {result.fills}")
    print(f"  final position : {final_position:.0f} unit(s) of {strat.instrument_id}")
    print(f"  final cash     : {result.final_cash:,.0f}")
    print(f"  final equity   : {result.final_equity:,.0f} (cash-basis golden view)")
    print(f"  realized pnl   : {result.realized_pnl:,.0f}")
    print(f"  summary        : {sink.summary}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
