"""Automated HITL verification for the #76 portfolio-driver slice (real data, no eyeballing).

Drives the marimo target-position strategy AND an imperative twin (sizing off the same
pre-fill ctx.portfolio_snapshot().position) through the REAL production KernelRunner on REAL
1306.TSE 2024 Daily bars, then asserts:

  C1 dispatch detects the .py as marimo
  C2 marimo == imperative twin, byte-identical (orders / fills / equity / cash / realized) —
     the rigorous correctness + NO-LOOK-AHEAD proof: the twin reads the PRE-FILL position, so
     a leaked post-fill read in get_portfolio() would diverge
  C3 per-fill CASH arithmetic: cash_after == cash_before ∓ qty*px
  C4 realized pnl == sum of closed round-trips
  C5 the fixture actually exercises both BUY and SELL (guards a vacuous pass)

Exit 0 = all PASS. Run:  uv run python hitl/verify_hitl.py
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "python"))

from engine._backend_impl import _select_replay_strategy  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402

STRATEGY_PY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "strat_target_position.py")
DATA_ROOT = os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT", "S:/jp")


class RecSink:
    """Records the per-fill order line + equity track + post-fill cash for parity."""

    def __init__(self, iid: str) -> None:
        self.iid = iid
        self.fills: list[tuple] = []
        self.equities: list[tuple] = []
        self.cash_track: list[float] = []
        self._pending = None

    def push_bar(self, bar) -> None:
        pass

    def push_order(self, fill) -> None:
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))
        self._pending = fill

    def push_portfolio(self, pf) -> None:
        self.cash_track.append(pf.cash)

    def on_equity(self, ts_ms, equity, cash) -> None:
        self.equities.append((equity, cash))

    def push_run_complete(self, run_id, summary) -> None:
        pass


class TargetPositionTwin(Strategy):
    """Imperative twin of strat_target_position.py — sizes the delta off the SAME pre-fill
    position read through ctx.portfolio_snapshot (the no-look-ahead reference)."""

    def on_bar(self, bar) -> None:
        pos = self.portfolio_snapshot().position
        target = 10.0 if bar.close > 2800.0 else (0.0 if bar.close < 2600.0 else pos)
        delta = target - pos
        if delta > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, delta)
        elif delta < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(delta))


def _run(strategy, scenario, iid):
    sink = RecSink(iid)
    try:
        result = KernelRunner(
            data_root=DATA_ROOT,
            instrument_ids=list(scenario["instruments"]),
            granularity=scenario["granularity"],
            start=scenario["start"],
            end=scenario["end"],
            initial_cash=scenario["initial_cash"],
            strategy=strategy,
            sink=sink,
        ).run()
    finally:
        getattr(strategy, "close", lambda: None)()
    return result, sink


def main() -> int:
    checks: list[tuple[str, bool, str]] = []

    # C1 — production dispatch detects marimo
    scenario, factory, label = _select_replay_strategy(STRATEGY_PY)
    iid = scenario["instruments"][0]
    checks.append(("C1 marimo dispatch", label.startswith("marimo:"), f"label={label!r}"))

    # Run marimo + imperative twin on the SAME real bars
    m_result, m = _run(factory(iid), scenario, iid)
    t_result, t = _run(
        TargetPositionTwin(strategy_id="twin", instrument_id=iid), scenario, iid
    )

    # C5 — fixture exercises both sides (guard vacuous parity)
    sides = {f[1] for f in t.fills}
    checks.append(
        ("C5 fixture has BUY+SELL", sides == {OrderSide.BUY, OrderSide.SELL} and 0 < len(t.fills),
         f"sides={sorted(s.value for s in sides)} n={len(t.fills)}")
    )

    # C2 — marimo == imperative twin, byte-identical (correctness + no-look-ahead)
    parity = (
        m.fills == t.fills
        and m.equities == t.equities
        and m.cash_track == t.cash_track
        and (m_result.fills, m_result.final_cash, m_result.realized_pnl)
        == (t_result.fills, t_result.final_cash, t_result.realized_pnl)
    )
    checks.append(
        ("C2 marimo == imperative twin (no-look-ahead)", parity,
         f"marimo fills={m_result.fills} cash={m_result.final_cash:,.0f} "
         f"realized={m_result.realized_pnl:,.0f}; twin fills={t_result.fills}")
    )

    # C3 — per-fill cash arithmetic on the marimo run
    cash = float(scenario["initial_cash"])
    c3_ok = True
    for (instr, side, qty, px), cash_after in zip(m.fills, m.cash_track):
        cash += (-qty * px) if side is OrderSide.BUY else (qty * px)
        if abs(cash - cash_after) > 1e-6:
            c3_ok = False
            break
    checks.append(("C3 per-fill cash arithmetic", c3_ok, "cash_after == cash_before -/+ qty*px"))

    # C4 — realized pnl == sum of closed round-trips (single netting position)
    pos = 0.0
    avg = 0.0
    realized = 0.0
    for (instr, side, qty, px) in m.fills:
        signed = qty if side is OrderSide.BUY else -qty
        if pos != 0.0 and (pos > 0) != (signed > 0):
            reduce_qty = min(abs(signed), abs(pos))
            realized += reduce_qty * ((px - avg) if pos > 0 else (avg - px))
        new = pos + signed
        if pos == 0.0:
            avg = px
        elif (pos > 0) == (signed > 0):
            avg = (pos * avg + signed * px) / new
        pos = new
    checks.append(
        ("C4 realized pnl == closed round-trips",
         abs(realized - m_result.realized_pnl) < 1e-6,
         f"recomputed={realized:,.0f} kernel={m_result.realized_pnl:,.0f}")
    )

    # Report
    print(f"data: {iid} {scenario['granularity']} {scenario['start']}..{scenario['end']} "
          f"({m_result.bars} bars, {m_result.fills} fills)\n")
    all_ok = True
    for name, ok, detail in checks:
        all_ok &= ok
        print(f"  [{'PASS' if ok else 'FAIL'}] {name:48} {detail}")
    print(f"\n{'ALL PASS' if all_ok else 'FAILED'}")
    return 0 if all_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
