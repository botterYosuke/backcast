"""v19 production scorer resolver gate (#76 follow-up / findings 0046 R1–R5, AC1/fail-loud).

Drives the REAL production dispatch (``_select_replay_strategy``) over a v19 marimo cell file
whose sidecar carries a ``scorer`` spec. The resolver loads the on-disk artifacts (universe /
adv / prev_close JSON + a joblib-dumped stub model) and builds ``services`` / ``constants`` that
it hands to MarimoStrategy. Mount-free (synthetic bars, no DuckDB) but exercises the real
resolver + real ``joblib.load``: the dispatched marimo-v19 must match the imperative twin
(same stub model + same artifacts). A negative case pins the scenario/universe fail-loud (R3).
"""
from __future__ import annotations

import json
import os
import shutil
import sys
from datetime import datetime
from types import MappingProxyType
from zoneinfo import ZoneInfo

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

pytest.importorskip("marimo", reason="defensive: marimo is a prod dep since ADR-0012")
joblib = pytest.importorskip("joblib", reason="v19 scorer artifacts are joblib-dumped")

import engine.kernel.runner as runner_mod  # noqa: E402
from engine._backend_impl import _select_replay_strategy  # noqa: E402
from engine.kernel.duckdb_bars import Bar, merge_bars_by_ts  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.strategy_runtime.marimo_strategy import MarimoStrategy  # noqa: E402
from strategies.v19 import v19_core  # noqa: E402
from strategies.v19.v19_morning import V19MorningStrategy  # noqa: E402

pytestmark = pytest.mark.marimo

_JST = ZoneInfo("Asia/Tokyo")
_REAL_CELL = os.path.join(_PY, "strategies", "v19", "v19_morning_cell.py")

_UNIVERSE = ["7203.TSE", "6758.TSE", "9984.TSE", "1306.TSE"]
_RS_REF = "1306.TSE"
_IDX = {iid: i for i, iid in enumerate(_UNIVERSE)}
_INITIAL_CASH = 260_000.0
_OPEN_MIN = 9 * 60 + 55
_GAP_TARGET = {"9984.TSE": 0.10, "7203.TSE": 0.05, "6758.TSE": 0.01}
_PREV_CLOSE = {iid: (1000.0 + _IDX[iid]) / (1.0 + g) for iid, g in _GAP_TARGET.items()}
_ADV_BASELINE = {iid: 1_000_000.0 for iid in _GAP_TARGET}
_GAP_COL = v19_core._FEATURES.index("gap")


class _StubGapModel:
    """Picklable deterministic scorer (ranks by the z-scored gap column) — joblib-dumpable so
    the resolver's lazy scorer loads it for real."""

    def predict(self, X):
        return [float(row[_GAP_COL]) for row in X]


def _close(iid: str, minute: int) -> float:
    return 1000.0 + _IDX[iid] * 1.0 + (minute - _OPEN_MIN) * 0.01


def _ts(y, mo, d, hh, mm) -> int:
    return int(datetime(y, mo, d, hh, mm, 59, 999999, tzinfo=_JST).timestamp() * 1_000_000_000)


def _synthetic_bars():
    bars = []
    for (y, m, d) in [(2025, 1, 6), (2025, 1, 7)]:
        for (hh, mm) in [(9, 55), (9, 56), (9, 57), (9, 58), (9, 59), (10, 0), (10, 30), (14, 55)]:
            minute = hh * 60 + mm
            for iid in _UNIVERSE:
                c = _close(iid, minute)
                bars.append(Bar(iid, _ts(y, m, d, hh, mm), open=c, high=c, low=c, close=c, volume=1000.0))
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
        data_root="/unused", instrument_ids=list(_UNIVERSE), granularity="Minute",
        start="2025-01-06", end="2025-01-07", initial_cash=_INITIAL_CASH,
        strategy=strategy, sink=sink,
    ).run()
    return result, sink


def _write_v19_cell_with_scorer(tmp_path, *, universe_instruments=None):
    """Lay down a v19 marimo cell + its sidecar (scenario + scorer spec) + artifacts in
    tmp_path: a joblib stub model, a universe.json ({instruments, rs_ref}), adv/prev_close JSON.
    ``universe_instruments`` overrides the artifact universe to force the fail-loud mismatch."""
    cell = tmp_path / "v19_cell.py"
    shutil.copyfile(_REAL_CELL, cell)

    joblib.dump(_StubGapModel(), tmp_path / "model.joblib")
    (tmp_path / "universe.json").write_text(
        json.dumps({"instruments": universe_instruments or _UNIVERSE, "rs_ref": _RS_REF}),
        encoding="utf-8",
    )
    (tmp_path / "adv.json").write_text(json.dumps(_ADV_BASELINE), encoding="utf-8")
    (tmp_path / "prev_close.json").write_text(json.dumps(_PREV_CLOSE), encoding="utf-8")

    (tmp_path / "v19_cell.json").write_text(
        json.dumps({
            "scenario": {
                "schema_version": 3, "instruments": _UNIVERSE,
                "start": "2025-01-06", "end": "2025-01-07",
                "granularity": "Minute", "initial_cash": int(_INITIAL_CASH),
            },
            "scorer": {
                "kind": "v19", "model_path": "model.joblib", "universe_path": "universe.json",
                "adv_path": "adv.json", "prev_close_path": "prev_close.json",
            },
        }),
        encoding="utf-8",
    )
    return cell


def _make_imperative():
    strat = V19MorningStrategy(instrument_id=_UNIVERSE[0])

    def fake_on_start() -> None:
        strat._instruments = list(_UNIVERSE)
        strat._rs_ref = _RS_REF
        strat._adv_baseline = dict(_ADV_BASELINE)
        strat._prev_close = dict(_PREV_CLOSE)
        strat._model = _StubGapModel()

    strat.on_start = fake_on_start  # type: ignore[method-assign]
    return strat


def _bought(sink):
    return {f[0] for f in sink.fills if f[1] is OrderSide.BUY}


def test_dispatch_resolves_scorer_spec_and_marimo_matches_imperative(tmp_path, monkeypatch):
    """AC1: production dispatch resolves the sidecar scorer spec → builds services/constants via
    the real resolver + real joblib.load → the dispatched marimo-v19 matches the imperative twin
    (same stub model + artifacts), mount-free. If the resolver dropped any artifact the gap-rank
    picks would diverge (the parity gate's sensitivity carries over)."""
    cell = _write_v19_cell_with_scorer(tmp_path)

    scenario, factory, label = _select_replay_strategy(str(cell))
    assert label == "marimo:v19_cell"
    marimo_strat = factory(_UNIVERSE[0])
    assert isinstance(marimo_strat, MarimoStrategy)

    mar_result, mar_sink = _run(marimo_strat, monkeypatch)
    marimo_strat.close()
    imp_result, imp_sink = _run(_make_imperative(), monkeypatch)

    # fixture guard: biting cash gate (2 of 3) + multi-iid + daily round-trips.
    assert _bought(imp_sink) == {"9984.TSE", "7203.TSE"}
    assert imp_result.fills == 8

    assert mar_sink.fills == imp_sink.fills
    assert mar_sink.equities == imp_sink.equities
    assert (mar_result.fills, mar_result.final_cash, mar_result.realized_pnl) == (
        imp_result.fills, imp_result.final_cash, imp_result.realized_pnl,
    )


def test_real_v19_sidecar_resolves_and_real_model_scores(monkeypatch):
    """AC2 (mount-free, skip-if-model-unloadable): the SHIPPED v19_morning_cell.json sidecar
    resolves against the real artifacts, and the real joblib HistGB model loads + scores real-
    shaped feature rows. No DuckDB needed — this exercises the production resolver + real model
    without a full replay (the full real-data replay is mount-gated separately)."""
    from engine.strategy_runtime.scenario import load_scenario
    from engine.strategy_runtime.scorer_bindings import load_scorer_bindings

    real_cell = os.path.join(_PY, "strategies", "v19", "v19_morning_cell.py")
    scenario = load_scenario(__import__("pathlib").Path(real_cell))
    services, constants = load_scorer_bindings(real_cell, scenario)

    # constants come from the real universe artifact (R3): 50-instrument tuple, rs-ref present,
    # adv/prev_close non-empty.
    assert isinstance(constants["V19_UNIVERSE"], tuple) and len(constants["V19_UNIVERSE"]) >= 10
    assert constants["V19_RS_REF"] == "1306.TSE"
    assert len(constants["V19_ADV_BASELINE"]) > 0 and len(constants["V19_PREV_CLOSE"]) > 0

    # Build real-shaped rows for a few scored instruments and score with the REAL model.
    scored = [i for i in constants["V19_UNIVERSE"] if i != constants["V19_RS_REF"]][:3]
    snaps = {
        iid: [
            {"open": 1000.0 + j, "high": 1001.0 + j, "low": 999.0 + j,
             "close": 1000.5 + j, "volume": 5000.0}
            for j in range(5)
        ]
        for iid in scored
    }
    rows = v19_core.build_rows(
        snaps, constants["V19_UNIVERSE"], constants["V19_RS_REF"],
        adv_baseline=constants["V19_ADV_BASELINE"], prev_close=constants["V19_PREV_CLOSE"],
    )
    assert set(rows) == set(scored)
    # Decide skip on the MODEL LOAD alone (sklearn 1.8.x unpickle, findings 0029); a bug in the
    # scoring path itself must NOT be swallowed as a skip, so scoring runs outside the try.
    model_path = os.path.join(_PY, "strategies", "v19", "artifacts",
                              "v19_live_model_o3histgb_10h00.joblib")
    try:
        joblib.load(model_path)
    except Exception as exc:  # noqa: BLE001 — unpickle needs sklearn 1.8.x
        pytest.skip(f"real v19 model not loadable in this env: {exc!r}")
    scores = services["score_v19_rows"](rows)  # real model + predict (bugs here DO fail)
    assert set(scores) == set(scored)
    assert all(isinstance(s, float) for s in scores.values())


def test_resolver_fail_loud_on_missing_artifact_maps_to_load_error(tmp_path):
    """A scorer spec whose artifact is missing must raise a LOAD-class error (ValueError), not a
    bare FileNotFoundError — the dispatch maps FileNotFoundError to STRATEGY_FILE_NOT_FOUND (as if
    the .py were missing), which is misleading when the .py exists but an artifact is absent."""
    cell = _write_v19_cell_with_scorer(tmp_path)
    (tmp_path / "universe.json").unlink()  # remove a required artifact
    with pytest.raises(ValueError, match="artifact not found"):
        _select_replay_strategy(str(cell))


def test_resolver_fail_loud_on_scenario_universe_mismatch(tmp_path):
    """R3: a scoring-universe instrument with no bars in the run would silently shift the
    ranking, so the resolver fail-louds when the scorer's universe_path instruments differ from
    the scenario instruments."""
    cell = _write_v19_cell_with_scorer(
        tmp_path, universe_instruments=["7203.TSE", "6758.TSE", "9984.TSE", "9999.TSE"]
    )
    with pytest.raises(ValueError, match="universe"):
        _select_replay_strategy(str(cell))
