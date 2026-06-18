"""Throwaway validator for the user-manual sample strategies (docs/samples/code/*.py).

Loads each marimo sample through the REAL MarimoStrategy + KernelRunner (bars injected,
no DuckDB mount — same recipe as tests/test_marimo_strategy_adapter.py) and asserts the
sample actually compiles and trades as the docs claim. This is the "公開前に実機検証"
gate from the docs plan — run it before publishing, not a permanent CI test.

    .venv/bin/python python/spike/docs_samples_check.py
"""
from __future__ import annotations

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PYROOT = os.path.dirname(HERE)
sys.path.insert(0, PYROOT)

from marimo._ast.load import load_app  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402

SAMPLES = os.path.join(PYROOT, os.pardir, "docs", "samples", "code")
IID = "7203.T"
IID2 = "9984.T"


class _RecSink:
    def __init__(self) -> None:
        self.fills: list[tuple] = []

    def push_bar(self, bar) -> None: ...
    def push_order(self, fill) -> None:
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))
    def push_portfolio(self, pf) -> None: ...
    def on_equity(self, ts_ms, equity, cash) -> None: ...
    def push_run_complete(self, run_id, summary) -> None: ...


def _run(strategy, bars, instrument_ids, initial_cash=10_000_000.0):
    sink = _RecSink()
    runner_mod.load_universe_bars = lambda *a, **k: bars  # direct monkeypatch (no pytest)
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=instrument_ids,
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=initial_cash,
        strategy=strategy,
        sink=sink,
    ).run()
    return result, sink


def _osc_bars(n=300, iid=IID):
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(n)]
    closes = [980.0 if i % 5 == 0 else (1000.0 if i % 5 == 1 else c) for i, c in enumerate(closes)]
    return [Bar(instrument_id=iid, ts_event_ns=1000 + i, open=c, high=c, low=c, close=c, volume=100.0)
            for i, c in enumerate(closes)]


def _trend_bars(n=60, iid=IID):
    # rise then fall, so SMA/momentum cross both ways
    closes = [1000.0 + i * 5.0 for i in range(n // 2)] + [1000.0 + (n - i) * 5.0 for i in range(n // 2)]
    return [Bar(instrument_id=iid, ts_event_ns=1000 + i, open=c, high=c, low=c, close=c, volume=100.0)
            for i, c in enumerate(closes)]


def _two_instrument_bars(n=20):
    bars = []
    for i in range(n):
        bars.append(Bar(instrument_id=IID, ts_event_ns=1000 + i * 2, open=1000.0, high=1000.0,
                        low=1000.0, close=1000.0, volume=100.0))
        bars.append(Bar(instrument_id=IID2, ts_event_ns=1001 + i * 2, open=2000.0, high=2000.0,
                        low=2000.0, close=2000.0, volume=100.0))
    return bars


def _marimo(name):
    return MarimoStrategy(app=load_app(os.path.join(SAMPLES, name)),
                          strategy_id="s", instrument_id=IID)


def _check(name, strategy, bars, instrument_ids, assertion, **kw):
    try:
        result, sink = _run(strategy, bars, instrument_ids, **kw)
    except Exception as e:  # noqa: BLE001
        import traceback
        cause = e.__cause__ or e
        print(f"[FAIL] {name}: RAISED {type(cause).__name__}: {cause}")
        traceback.print_exception(type(cause), cause, cause.__traceback__)
        if hasattr(strategy, "close"):
            strategy.close()
        return False
    if hasattr(strategy, "close"):
        strategy.close()
    ok, detail = assertion(result, sink)
    flag = "PASS" if (result.success and ok) else "FAIL"
    print(f"[{flag}] {name}: success={result.success} fills={len(sink.fills)} {detail}")
    return result.success and ok


def main() -> int:
    results = []

    results.append(_check(
        "00_observe", _marimo("00_observe.py"), _osc_bars(), [IID],
        lambda r, s: (len(s.fills) == 0, "(発注なしを確認)")))

    def both_sides(r, s):
        sides = {f[1] for f in s.fills}
        return (sides == {OrderSide.BUY, OrderSide.SELL}, f"sides={ {x.name for x in sides} }")

    results.append(_check("01_threshold", _marimo("01_threshold.py"), _osc_bars(), [IID], both_sides))
    results.append(_check("02_rebalance", _marimo("02_rebalance.py"), _osc_bars(), [IID], both_sides))

    results.append(_check(
        "03_cash_gate", _marimo("03_cash_gate.py"), _osc_bars(), [IID],
        lambda r, s: (0 < len(s.fills) < 10 and all(f[1] is OrderSide.BUY for f in s.fills),
                      "(現金が尽きて止まる)"),
        initial_cash=5_000.0))

    # both_sides also pins the EXIT (手仕舞い=SELL), not just entry — the docs promise both.
    results.append(_check("04_sma_cross", _marimo("04_sma_cross.py"), _trend_bars(), [IID], both_sides))

    results.append(_check("05_momentum", _marimo("05_momentum.py"), _trend_bars(80), [IID], both_sides))

    def both_instruments(r, s):
        iids = {f[0] for f in s.fills}
        return (iids == {IID, IID2}, f"iids={iids}")

    results.append(_check(
        "06_equal_weight", _marimo("06_equal_weight.py"), _two_instrument_bars(), [IID, IID2],
        both_instruments))

    # imperative appendix: import the module, instantiate, run
    sys.path.insert(0, SAMPLES)
    import importlib.util
    spec = importlib.util.spec_from_file_location("imp99", os.path.join(SAMPLES, "99_imperative.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    imp = mod.ThresholdStrategy(strategy_id="imp", instrument_id=IID)
    results.append(_check("99_imperative", imp, _osc_bars(), [IID], both_sides))

    print(f"\n{sum(results)}/{len(results)} samples PASS")
    return 0 if all(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
