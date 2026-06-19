"""Leg A — Python-side staleness guard for the #66 inline-SCENARIO golden (findings 0043 §2).

Asserts the canonical run-time SoT (engine.strategy_runtime.scenario.load_scenario) still
reproduces the committed golden for every fixture. If a fixture's SCENARIO is edited without
re-running capture_scenario_inline_golden.py, this fails — keeping the golden honest so Leg B
(the C# ScenarioStartupE2ERunner) pins the C# parser to a TRUE oracle, not a stale one.

Pairs with the C# reproduce leg: ScenarioInlineReader.Read(fixture) == committed golden.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

import pytest

from engine.strategy_runtime.scenario import load_scenario
from tests.capture_scenario_inline_golden import GOLDEN_PATH, project

_REPO_ROOT = os.path.dirname(_PYTHON_ROOT)


def _load_golden() -> dict:
    with open(GOLDEN_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def _golden_fixtures_for_params() -> list:
    """Fixtures for parametrize, evaluated at COLLECTION time. A missing / partial golden must
    NOT crash collection with an opaque traceback — return [] so test_golden_exists_and_nonempty
    owns the actionable "run capture" message instead."""
    try:
        return _load_golden().get("fixtures", [])
    except (FileNotFoundError, json.JSONDecodeError):
        return []


def test_golden_exists_and_nonempty():
    golden = _load_golden()
    assert golden.get("fixtures"), "golden has no fixtures — run `python -m tests.capture_scenario_inline_golden`"


def test_fixtures_have_no_sidecar():
    """The 2-leg gate pins BOTH legs to the inline .py SCENARIO. If a sidecar <strategy>.json with a
    'scenario' key were added next to a fixture, load_scenario (Leg A) would silently switch to the
    JSON while the C# reader (Leg B) keeps reading the inline .py — the legs would diverge with no
    test catching it. Guard the inline-source invariant (findings 0043 §2 / #24 doctrine)."""
    for entry in _load_golden().get("fixtures", []):
        sidecar = Path(_REPO_ROOT) / entry["path"]
        sidecar = sidecar.with_name(sidecar.stem + ".json")
        assert not sidecar.exists(), (
            f"{sidecar} exists — it would shadow the inline SCENARIO and split the 2-leg golden"
        )


@pytest.mark.parametrize("entry", _golden_fixtures_for_params(), ids=lambda e: e["path"])
def test_load_scenario_matches_committed_golden(entry):
    abs_path = os.path.join(_REPO_ROOT, entry["path"])
    actual = project(load_scenario(Path(abs_path)))
    assert actual == entry["scenario"], (
        f"load_scenario({entry['path']}) drifted from the committed golden; "
        f"re-run `python -m tests.capture_scenario_inline_golden` and review the diff"
    )
