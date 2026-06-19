"""capture_scenario_inline_golden — (re)generate the #66 inline-SCENARIO golden from the SoT.

EXPLICIT-RUN ONLY (mirrors spike/kernel_golden/capture_golden.py). The golden is recorded
from the canonical run-time path — engine.strategy_runtime.scenario.load_scenario — never
computed from the C# reader's assumptions (findings 0043 §2, golden doctrine #24). The
committed golden is then a frozen fixture both legs pin to:
  - Leg A (test_scenario_inline_golden.py): load_scenario(fixture) == committed golden
    (Python-side staleness guard),
  - Leg B (C# ScenarioStartupE2ERunner): ScenarioInlineReader.Read(fixture) == committed golden
    (cross-language faithfulness of the C# parser).

`test_scenario_inline_golden.py` never writes; only this script does. Updating the golden is
a reviewed event — inspect the diff before committing.

    python -m tests.capture_scenario_inline_golden
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine.strategy_runtime.scenario import load_scenario  # noqa: E402

# repo root = the Unity project root (parent of python/), so `path` entries are repo-relative
# and both the pytest (here) and the C# probe (Unity) resolve the same files.
_REPO_ROOT = os.path.dirname(_PYTHON_ROOT)

GOLDEN_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "golden", "scenario_inline_golden.json")

# The fixtures in the golden set — chosen to cover the literal subset the C# reader supports
# (double + single quotes, underscore ints, single + multi instrument lists, nested siblings).
FIXTURES: list[str] = [
    "python/spike/fixtures/strategies/kernel_spike_buy_sell.py",
    "python/spike/fixtures/strategies/scenario_inline_subset.py",
]

# The 5 panel-owned keys ScenarioSnapshot carries (the C# reader projects exactly these).
_PROJECTION_KEYS = ("start", "end", "granularity", "initial_cash", "instruments")


def project(scenario: dict) -> dict:
    """Project a load_scenario() result down to the 5 panel-owned keys (ScenarioSnapshot shape)."""
    return {k: scenario[k] for k in _PROJECTION_KEYS if k in scenario}


def build() -> dict:
    fixtures = []
    for rel in FIXTURES:
        abs_path = os.path.join(_REPO_ROOT, rel)
        scenario = load_scenario(Path(abs_path))
        fixtures.append({"path": rel, "scenario": project(scenario)})
    return {"schema_version": 1, "fixtures": fixtures}


def main() -> int:
    golden = build()
    os.makedirs(os.path.dirname(GOLDEN_PATH), exist_ok=True)
    with open(GOLDEN_PATH, "w", encoding="utf-8") as fh:
        json.dump(golden, fh, ensure_ascii=False, indent=2, sort_keys=True)
        fh.write("\n")
    print(f"[CAPTURE SCENARIO INLINE GOLDEN] wrote {GOLDEN_PATH}")
    for entry in golden["fixtures"]:
        print(f"  {entry['path']}: {json.dumps(entry['scenario'], ensure_ascii=False)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
