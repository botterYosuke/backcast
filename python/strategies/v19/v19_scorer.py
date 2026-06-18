"""v19 host-side scorer factory — resolves artifacts into marimo bindings (#76 / R4).

Builds the ``(services, constants)`` for a ``{"kind": "v19", ...}`` scorer spec: it loads v19's
artifacts (universe / adv_baseline / prev_close JSON) into host constants and a lazy joblib
scorer service. This is the production analog of ``V19MorningStrategy.on_start`` — the imperative
strategy and the marimo cell thus run off the SAME artifacts, so production parity holds.

Lazy by design (AC3 + v19's deferred-model-load): ``joblib`` is imported and the model is loaded
on the FIRST score call (the entry bar), not at module load or resolve time. The scoring numeric
itself is the shared ``v19_core.score_universe`` — sklearn never enters the marimo cell (T1).
"""
from __future__ import annotations

import json
from pathlib import Path
from types import MappingProxyType
from typing import Tuple

from strategies.v19 import v19_core


def make_v19_scorer_bindings(spec: dict, base_dir, scenario: dict) -> Tuple[dict, dict]:
    """Resolve a v19 scorer spec into ``(services, constants)``.

    ``spec`` carries ``model_path`` / ``universe_path`` / ``adv_path`` / ``prev_close_path``
    relative to ``base_dir`` (the sidecar directory). The universe artifact (``{instruments,
    rs_ref}``) is the scoring universe — the SAME source v19's on_start reads (R3). Fail-loud
    when a scoring instrument (or the rs-ref) has no bars in the scenario, since its snapshots
    would never fill and the ranking would silently shift.
    """
    base = Path(base_dir)

    universe_doc = _read_json(base, spec["universe_path"], "universe")
    instruments = list(universe_doc["instruments"])
    rs_ref = universe_doc.get("rs_ref", "1306.TSE")

    # The SCORED instruments are the universe minus the rs-ref (build_rows skips rs_ref). Each
    # needs bars or its snapshots never fill and the ranking silently shifts → fail-loud. The
    # rs-ref itself is a soft dependency (its absence just makes rs_vs_1306 = 0, matching the
    # imperative path), so it is NOT required here.
    scenario_instruments = set(scenario.get("instruments", []))
    scored = set(instruments) - {rs_ref}
    missing = sorted(scored - scenario_instruments)
    if missing:
        raise ValueError(
            f"v19 scorer universe instrument(s) {missing} have no bars in the scenario — their "
            "snapshots would never fill and the ranking would silently shift. The scenario "
            "instruments must cover the scoring universe."
        )

    adv_baseline = _load_optional_map(base, spec.get("adv_path"))
    prev_close = _load_optional_map(base, spec.get("prev_close_path"))
    model_path = str(base / spec["model_path"])

    # Lazy model: joblib.load on the first score call, then cache. Keeps joblib/sklearn off the
    # resolve path and the module-load path (AC3), mirroring v19's deferred load.
    cache: dict = {}

    def score_v19_rows(rows: dict) -> dict:
        model = cache.get("model")
        if model is None:
            import joblib  # noqa: PLC0415 — heavy/cold import deferred to first scoring

            model = joblib.load(model_path)
            cache["model"] = model
        return v19_core.score_universe(rows, model)

    constants = {
        "V19_UNIVERSE": tuple(instruments),
        "V19_RS_REF": rs_ref,
        "V19_ADV_BASELINE": MappingProxyType(dict(adv_baseline)),
        "V19_PREV_CLOSE": MappingProxyType(dict(prev_close)),
    }
    services = {"score_v19_rows": score_v19_rows}
    return services, constants


def _load_optional_map(base: Path, rel: "str | None") -> dict:
    """Load a ``{iid: float}`` artifact (adv / prev_close); ``{}`` when the path is omitted."""
    if not rel:
        return {}
    return _read_json(base, rel, "artifact")


def _read_json(base: Path, rel: str, what: str) -> dict:
    """Read a required scorer artifact JSON. A missing or malformed artifact is a strategy LOAD
    failure (the .py exists; its config is wrong), so raise ValueError — the dispatch site maps
    that to STRATEGY_LOAD_ERROR. Letting the bare FileNotFoundError escape would be mis-reported
    as STRATEGY_FILE_NOT_FOUND (as if the strategy file itself were missing)."""
    path = base / rel
    if not path.exists():
        raise ValueError(f"v19 scorer {what} artifact not found: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"v19 scorer {what} artifact is not valid JSON: {path}: {exc}") from exc
