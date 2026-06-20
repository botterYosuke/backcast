"""C#↔Python cell-synthesis seam (#81 / ADR-0013 Decision 3 / findings 0050).

In the cell-as-floating-window model each editor window holds one raw cell *body*; the C#
notebook aggregate (`MarimoNotebookDocument`) owns the ordered cells and serialises them to a
single `.py`. C# never reimplements def/ref/return analysis ([[ttwr-parity-first]]) — it calls
marimo's native `generate_filecontents` (synthesise) and `load_app` (decompose) through this
module. `PythonnetMarimoSynthesizer` invokes these two functions across pythonnet under the
single Python owner's GIL (ADR-0009); the marshalling is trivial because the boundary is just
JSON strings.

The seam carries **body + name + config** (not body-only): cell *names* (`def _config()`) and
configs are preserved opaquely so re-saving a named notebook (#76's `v19_morning_cell.py`) does
not collapse `def _config()` -> `def _()`. S1 does not *edit* names/configs (name UI is a later
slice); it captures them on decompose and writes them back on synthesise — `synthesize(decompose(py))`
is byte-idempotent for named files too (proven by the golden gate, the round-trip the bodies-only
signature got wrong, findings 0050).

`decompose_json` returns ``None`` on a broken / non-marimo `.py` (fail-soft): the aggregate keeps
the live buffer untouched and shows a notice rather than wiping it (findings 0044). `synthesize_json`
never raises on a malformed body — marimo's `safe_serialize_cell` emits an unparsable-cell marker.
"""
from __future__ import annotations

import json
import os
import tempfile
from typing import Any, Optional


def synthesize_json(cells_json: str) -> str:
    """Synthesise one canonical marimo ``.py`` from an ordered list of cells.

    ``cells_json`` is a JSON array of ``{"body": str, "name": str, "config": {...}}`` in cell
    order (= the ``.py`` cell order, the authoritative ordering). Returns the file text
    (``__generated_with`` version line + ``@app.cell`` defs + ``app.run()`` footer). Never raises
    on a malformed body.
    """
    from marimo._ast.cell import CellConfig
    from marimo._ast.codegen import generate_filecontents

    cells = json.loads(cells_json) if cells_json else []
    codes = [str(c.get("body", "")) for c in cells]
    names = [str(c.get("name") or "_") for c in cells]
    configs = [CellConfig.from_dict(c.get("config") or {}) for c in cells]
    return generate_filecontents(codes=codes, names=names, cell_configs=configs, config=None)


def load_app_from_text(py: str) -> Any:
    """Parse marimo ``.py`` SOURCE TEXT into an App, or ``None`` when it is not a loadable marimo app.

    ``load_app`` takes a path, so the text is written to a temp file (the proven golden path) and
    removed. Shared by ``decompose_json`` (Open) and the #95 Phase 2 notebook_session (per-cell RUN),
    so the temp-file dance lives in ONE place. marimo is imported lazily here (this module stays
    marimo-free at load time).
    """
    from marimo._ast.load import load_app

    fd, path = tempfile.mkstemp(suffix=".py")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="") as f:
            f.write(py or "")
        try:
            return load_app(path)
        except Exception:
            return None
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def decompose_json(py: str) -> Optional[str]:
    """Decompose a marimo ``.py`` into an ordered list of cells (the Open direction).

    Returns a JSON array of ``{"body", "name", "config"}`` in ``.py`` cell order, or ``None`` when
    the source is not a loadable marimo app (broken syntax / not a marimo file) — the fail-soft
    signal the aggregate turns into "keep the buffer + notice" (findings 0044).
    """
    app = load_app_from_text(py)
    if app is None:
        return None

    cm = app._cell_manager
    codes = list(cm.codes())
    names = list(cm.names())
    configs = list(cm.configs())
    cells: list[dict[str, Any]] = [
        {"body": codes[i], "name": names[i], "config": configs[i].asdict()}
        for i in range(len(codes))
    ]
    return json.dumps(cells)
