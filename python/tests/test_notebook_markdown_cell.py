"""#179 gate: the [m] Add Markdown cell renders, and bare ``mo`` resolves via the shared import cell.

The [m] button (``NotebookCellCoordinator.AddMarkdownCell``) seeds a markdown cell whose body is the
bare-``mo`` ``mo.md(...)`` template (findings 0126 D2/D5 — 本家 parity, NOT a cell-local ``_mo``),
after idempotently ensuring ONE shared ``import marimo as mo`` cell. The load-bearing correctness claim
(findings 0126 D3) is that pressing ▶ on the markdown cell runs its STALE upstream ancestor — the import
cell — FIRST (``IncrementalNotebookSession`` autorun = pressed + stale ancestors + reactive descendants),
so the bare ``mo`` resolves WITHOUT a ``NameError`` on the very first press.

This drives the SAME per-cell RUN seam as ``test_rich_output_sample`` (``IncrementalNotebookSession``),
using the EXACT seed strings the C# ships (kept in sync with
``Assets/Scripts/StrategyEditor/NotebookCellCoordinator.cs`` ``MoImportBody`` / ``MarkdownSeedBody`` and
findings 0126 §D5). The C# side (AFK ``StrategyEditorNotebookE2ERunner`` STRATEGY-66) pins the import
idempotency + windowing; this pins the RUNTIME semantics the C# can't reach headlessly.

  uv run python -m pytest tests/test_notebook_markdown_cell.py

delete-the-production-logic litmus: ``test_litmus_bare_mo_without_import_cell_nameerrors`` proves the
GREEN assert is non-vacuous — drop the shared import cell (the exact thing EnsureMoImportCell guarantees)
and the bare-``mo`` seed raises ``NameError`` (findings 0126 D3 RED).
"""
from __future__ import annotations

import pytest

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.notebook_session import IncrementalNotebookSession  # noqa: E402

pytestmark = pytest.mark.marimo

# The exact bodies the [m] button ships — MUST match NotebookCellCoordinator.MoImportBody /
# MarkdownSeedBody (findings 0126 §D5). column-0, no trailing newline (the canonical cell-body form).
MO_IMPORT_BODY = "import marimo as mo"
MARKDOWN_SEED_BODY = 'mo.md(r"""\n# 見出し\n\n本文をここに書く。\n""")'


def _ran_by_id(res: dict) -> dict[str, dict]:
    return {r["cell_id"]: r for r in res["ran"]}


def test_markdown_seed_with_shared_import_renders_without_nameerror():
    """GREEN: press the md cell; its stale upstream import cell runs first → bare ``mo`` resolves →
    the cell crosses as ``text/markdown`` with the heading intact (findings 0126 D3)."""
    imp = {"cell_id": "imp", "code": MO_IMPORT_BODY}
    md = {"cell_id": "md", "code": MARKDOWN_SEED_BODY}
    s = IncrementalNotebookSession()
    try:
        res = s.run_pressed([imp, md], "md")
        assert res["ok"], res
        # autorun ran the pressed md cell AND its stale ancestor (the import) — not just the md cell.
        ran = _ran_by_id(res)
        assert "imp" in ran, f"the stale import ancestor did not autorun: {res}"
        assert ran["md"]["ok"], f"md cell errored (NameError on bare mo?): {ran['md']}"
        assert ran["md"]["mimetype"] == "text/markdown", ran["md"]
        assert "見出し" in ran["md"]["data"], ran["md"]   # the heading text survives rendering
    finally:
        s.close()


def test_second_markdown_cell_reuses_the_same_import():
    """Two markdown cells sharing ONE import cell both render — mirrors [m] pressed twice (the import
    is ensured once; a 2nd ``import marimo as mo`` cell would be a marimo MultipleDefinitionError)."""
    imp = {"cell_id": "imp", "code": MO_IMPORT_BODY}
    md1 = {"cell_id": "md1", "code": MARKDOWN_SEED_BODY}
    md2 = {"cell_id": "md2", "code": 'mo.md(r"""\n## 2枚目\n""")'}
    s = IncrementalNotebookSession()
    try:
        assert s.run_pressed([imp, md1, md2], "md1")["ok"]
        res = s.run_pressed([imp, md1, md2], "md2")
        assert res["ok"], res
        assert _ran_by_id(res)["md2"]["mimetype"] == "text/markdown", res
    finally:
        s.close()


def test_litmus_bare_mo_without_import_cell_nameerrors():
    """RED litmus (findings 0126 D3): drop the shared import cell and the bare-``mo`` seed raises
    ``NameError`` — proving the GREEN assert above is non-vacuous and EnsureMoImportCell is load-bearing."""
    md = {"cell_id": "md", "code": MARKDOWN_SEED_BODY}
    s = IncrementalNotebookSession()
    try:
        res = s.run_pressed([md], "md")
        md_ran = _ran_by_id(res).get("md")
        assert md_ran is not None and not md_ran["ok"], f"bare mo unexpectedly succeeded without the import: {res}"
        blob = (md_ran.get("output") or "") + (md_ran.get("data") or "") + str(md_ran)
        assert "NameError" in blob or "'mo'" in blob, f"expected a NameError about mo, got: {md_ran}"
    finally:
        s.close()
