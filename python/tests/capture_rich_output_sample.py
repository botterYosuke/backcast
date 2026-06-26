"""capture_rich_output_sample — (re)generate the #165 rich-output fixture from the SoT sample.

EXPLICIT-RUN ONLY (mirrors capture_scenario_inline_golden.py / the golden doctrine #24). The
fixture is recorded from the canonical per-cell RUN path — ``IncrementalNotebookSession`` over the
REAL sample notebook ``docs/samples/code/07_rich_output.py`` — so it is the genuine ``(mimetype,
data)`` marimo produces, never a hand-authored synthetic payload. The committed fixture is then a
frozen artefact both legs pin to:

  - Python gate (test_rich_output_sample.py): re-running the sample reproduces the fixture for the
    DETERMINISTIC cells (markdown / table byte-equal, ui byte-equal after stripping its per-render
    ``random-id`` UUID) and STRUCTURALLY for the chart (a valid ``data:image/png;base64,`` PNG —
    matplotlib bytes vary by version/platform so byte-equality would be brittle, findings 0123 §3).
  - AFK gate (StrategyEditorNotebookE2ERunner Section31, STRATEGY-63/64): each fixture payload is
    fed to the REAL ``StrategyEditorView.SetOutput`` and asserted to route to the right pane.

``test_rich_output_sample.py`` never writes; only this script does. Updating the fixture is a
reviewed event — inspect the diff before committing.

    python -m tests.capture_rich_output_sample
"""
from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine.strategy_runtime.cell_synthesis import load_app_from_text  # noqa: E402
from engine.strategy_runtime.notebook_session import (  # noqa: E402
    IncrementalNotebookSession,
    text_projection,
)

# repo root = the Unity project root (parent of python/), so the sample + fixture are repo-relative
# and both the pytest (here) and the C# runner (Unity) resolve the same files.
_REPO_ROOT = os.path.dirname(_PYTHON_ROOT)

SAMPLE_REL = "docs/samples/code/07_rich_output.py"
FIXTURE_REL = "Assets/Tests/E2E/Editor/Fixtures/RichOutputSample.json"

SAMPLE_PATH = os.path.join(_REPO_ROOT, SAMPLE_REL)
FIXTURE_PATH = os.path.join(_REPO_ROOT, FIXTURE_REL)

# The four cells of the sample, in order. Names are stable handles the fixture/test/runner share —
# they are NOT marimo cell ids (the sample's cells are addressed positionally).
CELL_NAMES = ("markdown", "table", "chart", "ui")

# mo.ui widgets carry a per-render ``random-id`` UUID (a new value every press); everything else in
# the payload is deterministic. The fixture/test normalise it away so freshness stays byte-exact on
# the stable markup (label/start/stop/value) without chasing the UUID.
_RANDOM_ID_RE = re.compile(r"random-id='[^']*'")


def normalize_volatile(name: str, data: str) -> str:
    """Strip the only non-deterministic token (mo.ui's ``random-id`` UUID) for stable comparison.

    The placeholder is angle-bracket-free on purpose: ``text_projection`` strips ``<...>`` tags, so a
    ``<placeholder>`` token would survive as broken markup and pollute the ui projection.
    """
    if name == "ui":
        return _RANDOM_ID_RE.sub("random-id='NORMALIZED'", data)
    return data


def _run_cell(code: str) -> dict:
    """Run one cell body in a fresh session (each sample cell is self-contained, ``_``-local imports)."""
    s = IncrementalNotebookSession()
    try:
        res = s.run_pressed([{"cell_id": "c", "code": code}], "c")
        if not res["ok"]:
            raise RuntimeError(f"cell run failed: {res}")
        return res["ran"][0]
    finally:
        s.close()


def run_sample_cells() -> list[dict]:
    """Load the real sample, run each cell via per-cell RUN, and collect its rich output.

    Returns one ``{name, mimetype, data, ok}`` per cell, in cell order. The ``data`` is the LIVE
    payload (volatile token intact); callers normalise for comparison/storage. ``build`` is the sole
    author of the (normalised) ``projection`` — computing it here too would be discarded work, since
    every consumer either overwrites it (``build``) or recomputes from ``mimetype``/``data``.
    """
    src = Path(SAMPLE_PATH).read_text(encoding="utf-8")
    app = load_app_from_text(src)
    if app is None:
        raise RuntimeError(f"{SAMPLE_REL} is not a loadable marimo notebook")
    codes = list(app._cell_manager.codes())
    if len(codes) != len(CELL_NAMES):
        raise RuntimeError(f"expected {len(CELL_NAMES)} cells, sample has {len(codes)}")
    out = []
    for name, code in zip(CELL_NAMES, codes):
        r = _run_cell(code)
        out.append(
            {
                "name": name,
                "mimetype": r["mimetype"],
                "data": r["data"],
                "ok": r["ok"],
            }
        )
    return out


def build() -> dict:
    cells = run_sample_cells()
    # Store the volatile token normalised so the committed fixture is stable across captures (the
    # ui payload would otherwise churn its UUID on every run, polluting the diff).
    for c in cells:
        c["data"] = normalize_volatile(c["name"], c["data"])
        c["projection"] = text_projection(c["mimetype"], c["data"])
    return {"schema_version": 1, "sample": SAMPLE_REL, "cells": cells}


def main() -> int:
    fixture = build()
    os.makedirs(os.path.dirname(FIXTURE_PATH), exist_ok=True)
    with open(FIXTURE_PATH, "w", encoding="utf-8") as fh:
        json.dump(fixture, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print(f"[CAPTURE RICH OUTPUT SAMPLE] wrote {FIXTURE_PATH}")
    for c in fixture["cells"]:
        print(f"  {c['name']:9s} {c['mimetype']:16s} data={len(c['data'])}B projection={c['projection'][:40]!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
