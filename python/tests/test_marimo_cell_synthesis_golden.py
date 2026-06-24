"""Cell-synthesis golden gate (#81 / ADR-0013 / findings 0050) — layer 3, the C#↔Python
drift guard for the cell-as-floating-window model.

In that model each editor window holds one raw cell *body*; the notebook aggregate
(`MarimoNotebookDocument`) synthesises the single `.py` via marimo's native
`generate_filecontents` and Open decomposes it via `load_app`. C# never reimplements
def/ref/return analysis (ADR-0013 Decision 3, [[ttwr-parity-first]]). This gate promotes the
two throwaway spikes (`python/spike/marimo_cell_synthesis*.py`) into a permanent gate so the
seam stays frozen GREEN and the future C# `IMarimoSynthesizer` (fake in layer 1, real
pythonnet in layer 2) can bind to the SAME golden bytes:

  1. **form**: `generate_filecontents(bodies)` byte-matches the checked-in canonical fixture
     (`__generated_with` version line masked, so marimo upgrades don't churn it);
  2. **decompose**: `load_app` recovers the original bodies (wrapper hidden);
  3. **round-trip**: bodies -> .py -> bodies -> .py is byte-idempotent;
  4. **structure**: host APIs + the cross-cell DAG edge become hidden args / returns;
  5. **exec parity**: the synthesised arg-form .py runs order-for-order with the imperative
     twin under the real KernelRunner + MarimoStrategy adapter (host-seeding intact).
"""
from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")

from marimo._ast.cell import CellConfig  # noqa: E402
from marimo._ast.codegen import generate_filecontents  # noqa: E402
from marimo._ast.load import load_app  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402

pytestmark = pytest.mark.marimo

IID = "7203.T"
GOLDEN = Path(__file__).parent / "fixtures" / "marimo_cell_synthesis" / "two_cell_dag.py"

# Two raw cell bodies as two C# windows would hold them — NO @app.cell / def _ / return.
# Cell 2 reads `qty` from cell 1 (a real cross-cell DAG edge); get_bar/submit_market are
# host APIs that generate_filecontents promotes from free refs to hidden args.
_BODIES = [
    "bar = get_bar()  # noqa: F821\n"
    "qty = (1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)) * 10.0",
    "submit_market(qty)  # noqa: F821",
]
_NAMES = ["_", "_"]
_CONFIGS = [CellConfig(), CellConfig()]


def _synth(codes, names=None, configs=None) -> str:
    """Synthesise the canonical .py from raw cell bodies (the aggregate's Save direction)."""
    codes = list(codes)
    return generate_filecontents(
        codes=codes,
        names=list(names) if names is not None else ["_"] * len(codes),
        cell_configs=list(configs) if configs is not None else [CellConfig() for _ in codes],
        config=None,
    )


def _mask(text: str) -> str:
    """Drop the marimo version baked into `__generated_with` so the golden is version-independent."""
    return re.sub(r'__generated_with = "[^"]*"', '__generated_with = "MASKED"', text)


# ── 1. synthesis form: matches the checked-in golden (version-masked) ──


def test_synthesize_matches_golden():
    assert _mask(_synth(_BODIES, _NAMES, _CONFIGS)) == _mask(GOLDEN.read_text(encoding="utf-8"))


def test_canonical_form_has_version_line_and_run_guard():
    """The new on-disk canonical form (generate_filecontents) carries a __generated_with line
    and an app.run() footer — the documented divergence from backcast's older footer-less form
    (ADR-0013 Decision 3 / findings 0050). Pinned so a form regression is loud."""
    py = _synth(_BODIES, _NAMES, _CONFIGS)
    assert "__generated_with = " in py
    assert 'if __name__ == "__main__":' in py and "app.run()" in py


# ── 2/3. decompose recovers the bodies; round-trip is byte-idempotent ──


def test_decompose_recovers_bodies(tmp_path):
    p = tmp_path / "nb.py"
    p.write_text(_synth(_BODIES, _NAMES, _CONFIGS), encoding="utf-8", newline="")
    app = load_app(str(p))
    assert app is not None
    codes = list(app._cell_manager.codes())
    assert len(codes) == 2
    assert [c.strip() for c in codes] == [b.strip() for b in _BODIES]


def test_round_trip_byte_idempotent(tmp_path):
    gen1 = _synth(_BODIES, _NAMES, _CONFIGS)
    p = tmp_path / "nb.py"
    p.write_text(gen1, encoding="utf-8", newline="")  # byte-faithful LF, so the .py->bodies disk hop is honest
    app = load_app(str(p))
    gen2 = _synth(app._cell_manager.codes(), app._cell_manager.names(), app._cell_manager.configs())
    assert gen2 == gen1, "bodies -> .py -> bodies -> .py is not byte-idempotent"


# ── 4. structure: host APIs + cross-cell edge become hidden args / returns ──


def test_host_apis_and_dag_edge_become_args():
    py = _synth(_BODIES, _NAMES, _CONFIGS)
    # cell 1 reads the host API get_bar -> arg; defines qty read by cell 2 -> returned.
    assert "def _(get_bar):" in py
    assert "return (qty,)" in py
    # cell 2 reads qty (DAG edge) + the host API submit_market -> both args.
    assert "def _(qty, submit_market):" in py


# ── 5. exec parity: the synthesised arg-form .py runs == the imperative twin ──


class _ImperativeTwin(Strategy):
    """Same band logic as _BODIES, expressed in the imperative contract."""

    def on_bar(self, bar) -> None:
        qty = (1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)) * 10.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)
        elif qty < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(qty))


class _RecSink:
    def __init__(self) -> None:
        self.fills: list[tuple] = []
        self.equities: list[tuple] = []

    def push_bar(self, bar) -> None:
        pass

    def push_order(self, fill) -> None:
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))

    def push_portfolio(self, pf) -> None:
        pass

    def on_equity(self, ts_ms: int, equity: float, cash: float) -> None:
        self.equities.append((equity, cash))

    def push_run_complete(self, run_id, summary) -> None:
        pass


def _bars():
    closes = [1000.0 + (i % 97) * 0.5 + i * 0.01 for i in range(300)]
    closes = [980.0 if i % 5 == 0 else (1000.0 if i % 5 == 1 else c) for i, c in enumerate(closes)]
    return [
        Bar(instrument_id=IID, ts_event_ns=1_000 + i, open=c, high=c, low=c, close=c, volume=100.0)
        for i, c in enumerate(closes)
    ]


def _run(strategy, monkeypatch):
    sink = _RecSink()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: _bars())
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=[IID],
        start="2024-01-01",
        end="2024-12-31",
        initial_cash=10_000_000.0,
        strategy=strategy,
        sink=sink,
    ).run()
    return result, sink


def test_synthesized_py_runs_with_parity(tmp_path, monkeypatch):
    """The synthesised arg-form .py (host APIs as args, split across two cells) runs under the
    real KernelRunner + MarimoStrategy adapter and matches the imperative twin order-for-order
    — proving the generate_filecontents output is execution-faithful, not just byte-faithful."""
    p = tmp_path / "strat.py"
    p.write_text(_synth(_BODIES, _NAMES, _CONFIGS), encoding="utf-8", newline="")

    marimo_strat = MarimoStrategy(app=load_app(str(p)), strategy_id="m", instrument_id=IID)
    m_result, m_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()
    t_result, t_sink = _run(_ImperativeTwin(strategy_id="t", instrument_id=IID), monkeypatch)

    # fixture guard: the series must place real orders on both sides, bounded.
    sides = {o[1] for o in t_sink.fills}
    assert sides == {OrderSide.BUY, OrderSide.SELL} and 0 < len(t_sink.fills) < 300

    assert m_sink.fills == t_sink.fills
    assert m_sink.equities == t_sink.equities
    assert (m_result.fills, m_result.final_cash, m_result.realized_pnl) == (
        t_result.fills,
        t_result.final_cash,
        t_result.realized_pnl,
    )


# ── 6. entry-point seam: synthesize_json/decompose_json carry body+name+config ──
#
# The C# PythonnetMarimoSynthesizer binds to THESE two functions (not generate_filecontents
# directly) — JSON in, JSON/None out (findings 0050). The contract the bodies-only signature got
# wrong: a NAMED notebook (#76 v19's `def _config()`) must round-trip byte-identically, names and
# configs preserved opaquely. A bodies-only seam would collapse `def _config()` -> `def _()`.


def test_entry_point_named_cell_round_trip_is_byte_idempotent():
    from engine.strategy_runtime.cell_synthesis import decompose_json, synthesize_json

    cells = [
        {"body": "x = 1", "name": "_config", "config": {}},
        {"body": "y = x + 1", "name": "_strategy", "config": {"disabled": True}},
    ]
    py1 = synthesize_json(json.dumps(cells))
    # names + configs survive synthesis (the #76 artifact-preservation guard).
    assert "def _config():" in py1
    assert "@app.cell(disabled=True)" in py1 and "def _strategy(x):" in py1

    recovered_json = decompose_json(py1)
    assert recovered_json is not None
    recovered = json.loads(recovered_json)
    assert [c["name"] for c in recovered] == ["_config", "_strategy"]
    assert recovered[1]["config"]["disabled"] is True

    # synthesise(decompose(py)) == py — byte-idempotent for named cells (bodies-only was false here).
    py2 = synthesize_json(recovered_json)
    assert py2 == py1


def test_entry_point_decompose_non_marimo_returns_none():
    # #113: a NON-MARIMO `.py` (loadable Python without `app = marimo.App()`) returns None — the C#
    # aggregate turns that into an explicit NOT_A_MARIMO_NOTEBOOK Open failure (the 1-cell auto-wrap
    # of findings 0054 §D1 is retired). Mirrors the run layer (#112 ADR-0025 D4) so the editor is
    # "marimo or error" at Open time, consistent with run/materialize.
    import pytest

    from engine.strategy_runtime.cell_synthesis import decompose_json

    # an imperative Strategy subclass: valid Python, but not a marimo notebook → None.
    assert decompose_json("class V19MorningStrategy:\n    def on_bar(self, bar):\n        pass\n") is None
    # an empty / comment-only file is also not a notebook → None.
    assert decompose_json("# just a comment\n") is None

    # #113 AC#2: a BROKEN-SYNTAX source raises SyntaxError (a DISTINCT failure) rather than being
    # silently masked as None — the Open layer surfaces it as a clear parse error.
    # LITMUS: revert decompose_json's `raise_syntax_error=True` and this raise turns back into None.
    with pytest.raises(SyntaxError):
        decompose_json("this is (not valid python at all")


def test_entry_point_decompose_for_open_envelope():
    # #113: the C# Open seam reads a STRUCTURED envelope (no PythonException message parsing) so the
    # failure KIND is classified in Python. ok -> cells; not_marimo -> non-marimo/empty; syntax_error
    # -> broken syntax with a detail. The C# aggregate maps these to "<cells>" / "not a marimo
    # notebook" / "syntax error: <detail>" respectively.
    import json

    from engine.strategy_runtime.cell_synthesis import decompose_for_open, synthesize_json

    ok = decompose_for_open(synthesize_json(json.dumps([{"body": "x = 1", "name": "_", "config": {}}])))
    assert ok["status"] == "ok"
    assert json.loads(ok["cells"])[0]["body"] == "x = 1"

    assert decompose_for_open("class Foo:\n    pass\n") == {"status": "not_marimo"}
    assert decompose_for_open("# comment only\n") == {"status": "not_marimo"}

    broken = decompose_for_open("this is (not valid python at all")
    assert broken["status"] == "syntax_error"
    assert broken["detail"]   # carries the parse-error detail for the user


def test_entry_point_new_cell_is_anonymous_default():
    """A freshly added cell carries body="" / name=_ / default config — marimo's own new cell."""
    from engine.strategy_runtime.cell_synthesis import decompose_json, synthesize_json

    py = synthesize_json(json.dumps([{"body": "", "name": "_", "config": {}}]))
    recovered = json.loads(decompose_json(py))
    assert len(recovered) == 1
    assert recovered[0]["name"] == "_"
    assert recovered[0]["body"] == ""


def _write_golden() -> None:
    """Refresh the checked-in golden from live marimo output (run this module as __main__ after a
    deliberate skeleton/marimo change). Keeps the fixture byte-faithful, never hand-edited."""
    GOLDEN.write_text(_synth(_BODIES, _NAMES, _CONFIGS), encoding="utf-8", newline="")
    print(f"wrote {GOLDEN}")


if __name__ == "__main__":
    _write_golden()
