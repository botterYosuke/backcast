"""Phase 6 (#95) gate: rich output {mimetype, data} contract (findings 0075 P6-2).

Drives ``IncrementalNotebookSession`` over cells that produce each output class and asserts the
marimo-native ``(mimetype, data)`` the C# renderers switch on, plus the interim ``text_projection``
the current Text path uses until Slice 5's native renderers land. Exercised with marimo-native
producers (mo.md / mo.image) + pandas (a prod dep) — no matplotlib/altair needed for the seam.

  uv run python -m pytest tests/test_notebook_rich_output.py
"""
from __future__ import annotations

import importlib.util

import pytest

_HAS_MPL = importlib.util.find_spec("matplotlib") is not None

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.notebook_session import (  # noqa: E402
    IncrementalNotebookSession,
    text_projection,
)

pytestmark = pytest.mark.marimo

# A 1x1 PNG (smallest valid) for the image path.
_PNG_HEX = (
    "89504e470d0a1a0a0000000d49484452000000010000000108020000009077053b"
    "0000000a49444154789c6360000002000154a24f8c0000000049454e44ae426082"
)


def _one(code: str) -> dict:
    s = IncrementalNotebookSession()
    try:
        res = s.run_pressed([{"cell_id": "c", "code": code}], "c")
        assert res["ok"], res
        return res["ran"][0]
    finally:
        s.close()


def test_plain_value_crosses_as_html_and_projects_to_clean_text():
    r = _one("21 + 21")
    assert r["mimetype"] == "text/html"  # marimo wraps a repr in <pre>
    assert "42" in r["data"]
    assert text_projection(r["mimetype"], r["data"]) == "42"  # interim Text view stays clean


def test_mo_md_is_markdown_mimetype():
    r = _one("import marimo as mo\nmo.md('# Title\\n**bold**')")
    assert r["mimetype"] == "text/markdown", r
    assert "Title" in r["data"]
    assert "Title" in text_projection(r["mimetype"], r["data"])


def test_dataframe_is_html_table():
    r = _one("import pandas as pd\npd.DataFrame({'x': [1, 2], 'y': [3, 4]})")
    assert r["mimetype"] == "text/html", r
    assert "<table" in r["data"]


def test_mo_image_falls_into_html_bucket_via_virtual_file():
    # mo.image(bytes) emits text/html `<img src='@file/...'>` — a marimo VIRTUAL FILE ref, not a
    # self-contained image. It is server-dependent (no @file server in-proc), so it lands in the
    # html bucket; the self-contained, Unity-renderable image path is matplotlib's data: URL
    # (test_matplotlib_figure_is_renderable_image). Documents the boundary, not a feature.
    r = _one(f"import marimo as mo\nmo.image(bytes.fromhex('{_PNG_HEX}'))")
    assert r["mimetype"] == "text/html", r
    assert "<img" in r["data"]


@pytest.mark.skipif(not _HAS_MPL, reason="matplotlib is bundled by P6-3; skip until installed")
def test_matplotlib_figure_is_renderable_image():
    # The named image producer (P6-3): a matplotlib figure crosses as a self-contained data: URL
    # (image/png or application/vnd.marimo+mimebundle) the C# side decodes to a Texture2D.
    r = _one(
        "import matplotlib\nmatplotlib.use('Agg')\n"
        "import matplotlib.pyplot as plt\n"
        "fig, ax = plt.subplots()\nax.plot([0, 1, 2], [0, 1, 4])\nfig"
    )
    assert r["mimetype"].startswith("image/") or "mimebundle" in r["mimetype"], r
    assert "base64," in r["data"], "expected a self-contained data: URL"
    assert text_projection(r["mimetype"], r["data"]).startswith("[")


def test_explicit_mo_output_is_html():
    r = _one("import marimo as mo\nmo.output.replace(mo.md('explicit **out**'))")
    assert r["mimetype"] == "text/html", r
    assert "explicit" in text_projection(r["mimetype"], r["data"])


def test_exception_is_text_plain():
    r = _one("raise ValueError('boom')")
    assert r["ok"] is False
    assert r["mimetype"] == "text/plain", r
    assert "ValueError" in r["data"] and "boom" in r["data"]
