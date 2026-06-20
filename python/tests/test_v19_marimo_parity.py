"""v19 marimo↔imperative deterministic parity gate (#76 / findings 0046 T5/T7/R2).

The marimo cell-DAG port (``v19_morning_cell.py``) self-loads its scorer artifacts from a
``V19_ARTIFACTS_DIR``-overridable directory (``MEMORY.md``: scorer is no longer a sidecar
field; the cell owns its scoring inputs). This gate writes a STUB universe + adv + prev_close
+ joblib-dumped stub model to a tmp dir, points ``V19_ARTIFACTS_DIR`` at it, and asserts the
cell produces byte-identical order/fill/equity vs the REAL imperative ``V19MorningStrategy``
seeded with the SAME stub model + artifacts.

NON-VACUOUS w.r.t. adv/prev_close (R2): the stub ranks by the **gap** feature (a function of
prev_close), and the gap-rank is deliberately NOT the universe order — so if the marimo cell
dropped prev_close, the picks would change and parity would break. A sensitivity litmus proves
it (imperative with empty prev_close picks a different set), and a feature guard proves
rel_turnover (adv) and gap (prev_close) are non-zero.

Subsumed sibling tests (formerly in test_v19_production_resolver.py): AC2 — the SHIPPED
``v19_morning_cell.py`` cell self-loads its real artifacts and the real joblib HistGB model
scores real-shaped feature rows. AC3 — a missing artifact surfaces as a LOAD-class error at
``MarimoStrategy.on_start`` (cold compile), not a silently-empty score.
"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime
from types import MappingProxyType
from zoneinfo import ZoneInfo

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")
joblib = pytest.importorskip("joblib", reason="v19 cell loads its scorer model via joblib")

from marimo._ast.load import load_app  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar, merge_bars_by_ts  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402
from strategies.v19 import v19_core  # noqa: E402
from strategies.v19.v19_morning import V19MorningStrategy  # noqa: E402

pytestmark = pytest.mark.marimo

_JST = ZoneInfo("Asia/Tokyo")
_CELL = os.path.join(_PY, "strategies", "v19", "v19_morning_cell.py")

# Universe ORDER (7203, 6758, 9984) + rs-ref (1306, never traded). The gap-rank below is
# 9984 > 7203 > 6758 — deliberately NOT the universe order, so a dropped prev_close (gap→0,
# ties broken by universe order) changes the picks.
_UNIVERSE = ("7203.TSE", "6758.TSE", "9984.TSE", "1306.TSE")
_RS_REF = "1306.TSE"
_IDX = {iid: i for i, iid in enumerate(_UNIVERSE)}
_INITIAL_CASH = 260_000.0

# Morning closes ≈ 1000 (so the three picks have near-equal notionals → a clean cash gate),
# with a tiny per-instrument offset for non-degenerate features and a +0.01/min drift.
_OPEN_MIN = 9 * 60 + 55  # 09:55 JST


def _close(iid: str, minute: int) -> float:
    return 1000.0 + _IDX[iid] * 1.0 + (minute - _OPEN_MIN) * 0.01


# prev_close chosen so gap = o0/prev_close - 1 ranks 9984(0.10) > 7203(0.05) > 6758(0.01).
# o0 = the 09:55 close = 1000 + idx.
_GAP_TARGET = {"9984.TSE": 0.10, "7203.TSE": 0.05, "6758.TSE": 0.01}
_PREV_CLOSE = MappingProxyType(
    {iid: (1000.0 + _IDX[iid]) / (1.0 + g) for iid, g in _GAP_TARGET.items()}
)
# Non-zero ADV so rel_turnover is a real number (does not affect the gap-rank).
_ADV_BASELINE = MappingProxyType({iid: 1_000_000.0 for iid in _GAP_TARGET})

_GAP_COL = v19_core._FEATURES.index("gap")  # the stub ranks by this z-scored column


class _StubModel:
    """Picklable deterministic scorer: rank by the z-scored ``gap`` column (a function of
    prev_close). gap depends on prev_close, so this makes parity SENSITIVE to whether
    prev_close was threaded into the features on both paths. Defined at module level so joblib
    can pickle it (the cell loads via joblib.load)."""

    def predict(self, X):
        return [float(row[_GAP_COL]) for row in X]


def _write_stub_artifacts(tmp_path) -> None:
    """Lay down the four artifacts the cell's ``_artifacts`` reads: universe.json, adv.json,
    prev_close.json, and a joblib-dumped stub model. Filenames match the cell's hardcoded
    names (``v19_live_*``)."""
    joblib.dump(_StubModel(), tmp_path / "v19_live_model_o3histgb_10h00.joblib")
    (tmp_path / "v19_live_universe.json").write_text(
        json.dumps({"instruments": list(_UNIVERSE), "rs_ref": _RS_REF}),
        encoding="utf-8",
    )
    (tmp_path / "v19_live_adv_baseline.json").write_text(
        json.dumps(dict(_ADV_BASELINE)), encoding="utf-8"
    )
    (tmp_path / "v19_live_prev_close.json").write_text(
        json.dumps(dict(_PREV_CLOSE)), encoding="utf-8"
    )


def _ts(y: int, mo: int, d: int, hh: int, mm: int) -> int:
    return int(datetime(y, mo, d, hh, mm, 59, 999999, tzinfo=_JST).timestamp() * 1_000_000_000)


def _bar(iid: str, ts: int, close: float) -> Bar:
    return Bar(iid, ts, open=close, high=close, low=close, close=close, volume=1000.0)


def _synthetic_bars():
    bars = []
    for (y, m, d) in [(2025, 1, 6), (2025, 1, 7)]:
        for (hh, mm) in [(9, 55), (9, 56), (9, 57), (9, 58), (9, 59), (10, 0), (10, 30), (14, 55)]:
            minute = hh * 60 + mm
            for iid in _UNIVERSE:
                bars.append(_bar(iid, _ts(y, m, d, hh, mm), _close(iid, minute)))
    return merge_bars_by_ts([bars])


class _RecSink:
    def __init__(self):
        self.fills = []
        self.equities = []

    def push_bar(self, bar):
        pass

    def push_order(self, fill):
        self.fills.append((fill.instrument_id, fill.side, fill.last_qty, fill.last_px))

    def push_portfolio(self, pf):
        pass

    def on_equity(self, ts_ms, equity, cash):
        self.equities.append((equity, cash))

    def push_run_complete(self, run_id, summary):
        pass


def _run(strategy, monkeypatch):
    sink = _RecSink()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(_synthetic_bars()))
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=list(_UNIVERSE),
        granularity="Minute",
        start="2025-01-06",
        end="2025-01-07",
        initial_cash=_INITIAL_CASH,
        strategy=strategy,
        sink=sink,
    ).run()
    return result, sink


def _make_imperative(stub, *, adv=_ADV_BASELINE, prev_close=_PREV_CLOSE):
    """The REAL V19MorningStrategy with on_start stubbed (no artifact/model I/O) and the stub
    model + artifacts injected — the parity oracle. Default ctor knobs match the cell's."""
    strat = V19MorningStrategy(instrument_id=_UNIVERSE[0])

    def fake_on_start() -> None:
        strat._instruments = list(_UNIVERSE)
        strat._rs_ref = _RS_REF
        strat._adv_baseline = dict(adv)
        strat._prev_close = dict(prev_close)
        strat._model = stub

    strat.on_start = fake_on_start  # type: ignore[method-assign]
    return strat


def _make_marimo():
    """The marimo cell-DAG port — self-loads the stub model + artifacts from
    ``V19_ARTIFACTS_DIR`` (the caller sets the env var before run)."""
    return MarimoStrategy(
        app=load_app(_CELL),
        strategy_id="strat-marimo",
        instrument_id=_UNIVERSE[0],
    )


def _bought(sink):
    return {f[0] for f in sink.fills if f[1] is OrderSide.BUY}


def test_v19_marimo_parity_deterministic(tmp_path, monkeypatch):
    _write_stub_artifacts(tmp_path)
    monkeypatch.setenv("V19_ARTIFACTS_DIR", str(tmp_path))

    stub = _StubModel()
    imp_result, imp_sink = _run(_make_imperative(stub), monkeypatch)

    marimo_strat = _make_marimo()
    mar_result, mar_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()

    # ---- fixture guards (non-vacuous): multi-iid, daily reset, biting cash gate ----
    buys = [f for f in imp_sink.fills if f[1] is OrderSide.BUY]
    sells = [f for f in imp_sink.fills if f[1] is OrderSide.SELL]
    # 2 days × 2 affordable gap-ranked picks = 4 BUYs (the 3rd is trimmed by the ¥260k cash
    # gate), each flattened at 14:55 = 4 SELLs (daily round-trips prove the reset).
    assert len(buys) == 4, imp_sink.fills
    assert len(sells) == 4, imp_sink.fills
    # gap-rank top-2 = 9984, 7203; 6758 is trimmed (cash bite), 1306 is rs-ref (never traded).
    assert _bought(imp_sink) == {"9984.TSE", "7203.TSE"}, _bought(imp_sink)
    assert imp_result.fills == 8

    # ---- the parity claim: marimo == imperative, order/fill/equity ----
    assert mar_sink.fills == imp_sink.fills
    assert mar_sink.equities == imp_sink.equities
    assert (mar_result.fills, mar_result.final_cash, mar_result.realized_pnl) == (
        imp_result.fills,
        imp_result.final_cash,
        imp_result.realized_pnl,
    )
    assert mar_result.realized_pnl == pytest.approx(mar_result.final_cash - _INITIAL_CASH)


def test_prev_close_changes_the_picks_so_the_parity_is_not_vacuous(monkeypatch):
    """Sensitivity litmus (delete-the-production-logic): with prev_close the gap-rank buys
    {9984, 7203}; with EMPTY prev_close (gap→0, ties broken by universe order) the imperative
    twin buys a DIFFERENT set. So prev_close demonstrably drives the outcome — the parity gate
    above is not vacuous w.r.t. the R2 adv/prev_close threading."""
    stub = _StubModel()
    _r1, with_pc = _run(_make_imperative(stub), monkeypatch)
    _r2, no_pc = _run(_make_imperative(stub, prev_close={}), monkeypatch)
    # With prev_close the gap-rank buys {9984, 7203}; with empty prev_close (gap→0, ties broken
    # by universe order) it buys {7203, 6758} — a concretely different set, so prev_close
    # demonstrably drives the picks (the parity gate is not vacuous w.r.t. R2).
    assert _bought(with_pc) == {"9984.TSE", "7203.TSE"}
    assert _bought(no_pc) == {"7203.TSE", "6758.TSE"}


def test_adv_and_prev_close_reach_nonzero_features():
    """Feature guard: the injected adv_baseline / prev_close actually flow into build_rows and
    produce non-zero rel_turnover / gap (so the constants are not silently dropped)."""
    snaps = {
        iid: [
            {"open": _close(iid, m), "high": _close(iid, m), "low": _close(iid, m),
             "close": _close(iid, m), "volume": 1000.0}
            for m in range(_OPEN_MIN, _OPEN_MIN + 5)
        ]
        for iid in _UNIVERSE
    }
    rows = v19_core.build_rows(
        snaps, _UNIVERSE, _RS_REF, adv_baseline=_ADV_BASELINE, prev_close=_PREV_CLOSE
    )
    # Every scored instrument (all of _GAP_TARGET) must carry non-zero rel_turnover (adv) and
    # gap (prev_close) — so neither constant is silently dropped for any instrument.
    assert set(_GAP_TARGET) <= set(rows)
    for iid in _GAP_TARGET:
        assert rows[iid]["rel_turnover"] != 0.0, iid
        assert rows[iid]["gap"] != 0.0, iid


# ---------------------------------------------------------------------------
# Subsumed from the deleted test_v19_production_resolver.py
# ---------------------------------------------------------------------------


def test_real_shipped_cell_self_loads_and_real_model_scores():
    """The SHIPPED ``v19_morning_cell.py`` self-loads its real artifacts (universe / adv /
    prev_close) and the real joblib HistGB model loads + scores real-shaped feature rows. No
    DuckDB needed — exercises the cell's loader + real model without a full replay. The full
    real-data replay is mount-gated separately."""
    real_cell_dir = os.path.join(_PY, "strategies", "v19")
    real_artifacts_dir = os.path.join(real_cell_dir, "artifacts")
    universe_doc = json.loads(
        open(os.path.join(real_artifacts_dir, "v19_live_universe.json"), encoding="utf-8").read()
    )
    universe = tuple(universe_doc["instruments"])
    rs_ref = universe_doc.get("rs_ref", "1306.TSE")
    adv = json.loads(
        open(os.path.join(real_artifacts_dir, "v19_live_adv_baseline.json"), encoding="utf-8").read()
    )
    prev_close = json.loads(
        open(os.path.join(real_artifacts_dir, "v19_live_prev_close.json"), encoding="utf-8").read()
    )

    # Real artifacts (R3): a 50-instrument universe, rs-ref present, adv/prev_close non-empty.
    assert len(universe) >= 10
    assert rs_ref == "1306.TSE"
    assert len(adv) > 0 and len(prev_close) > 0

    # Build real-shaped rows for a few scored instruments and score with the REAL model.
    scored = [i for i in universe if i != rs_ref][:3]
    snaps = {
        iid: [
            {"open": 1000.0 + j, "high": 1001.0 + j, "low": 999.0 + j,
             "close": 1000.5 + j, "volume": 5000.0}
            for j in range(5)
        ]
        for iid in scored
    }
    rows = v19_core.build_rows(
        snaps, universe, rs_ref, adv_baseline=adv, prev_close=prev_close,
    )
    assert set(rows) == set(scored)
    # Decide skip on the MODEL LOAD alone (sklearn 1.8.x unpickle, findings 0029); a bug in the
    # scoring path itself must NOT be swallowed as a skip, so scoring runs outside the try.
    model_path = os.path.join(real_artifacts_dir, "v19_live_model_o3histgb_10h00.joblib")
    try:
        model = joblib.load(model_path)
    except Exception as exc:  # noqa: BLE001 — unpickle needs sklearn 1.8.x
        pytest.skip(f"real v19 model not loadable in this env: {exc!r}")
    scores = v19_core.score_universe(rows, model)  # real model + predict (bugs here DO fail)
    assert set(scores) == set(scored)
    assert all(isinstance(s, float) for s in scores.values())


def test_cell_fail_loud_on_missing_artifact(tmp_path, monkeypatch):
    """A missing artifact must surface as a HARD failure during the run (cold compile records
    the cell as errored; the first dependent hot-cell run re-raises it), not as a silently-
    empty score. The cell's ``_artifacts`` cell reads the JSON eagerly, so a missing
    ``v19_live_universe.json`` propagates as a ``FileNotFoundError`` — marimo wraps it in a
    ``MarimoRuntimeException`` at the hot-drain boundary, but the original cause is the file-
    not-found, naming the missing artifact. Only the universe artifact is removed —
    adv/prev_close + model are present, so the failure must come from universe.json."""
    from marimo._runtime.exceptions import MarimoRuntimeException

    _write_stub_artifacts(tmp_path)
    (tmp_path / "v19_live_universe.json").unlink()
    monkeypatch.setenv("V19_ARTIFACTS_DIR", str(tmp_path))

    marimo_strat = _make_marimo()
    with pytest.raises(MarimoRuntimeException) as exc_info:
        _run(marimo_strat, monkeypatch)
    # The cell's FileNotFoundError leaves _artifacts in an errored state, so when _strategy
    # later reads V19_UNIVERSE marimo surfaces it as a MarimoMissingRefError — proves the run
    # did NOT silently no-op past the broken loader.
    assert "V19_UNIVERSE" in str(exc_info.value.__cause__ or exc_info.value)
    marimo_strat.close()
