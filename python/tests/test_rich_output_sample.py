"""#165 gate: the shipping rich-output sample notebook produces real rich payloads per cell.

Drives the REAL ``docs/samples/code/07_rich_output.py`` through ``IncrementalNotebookSession`` (the
per-cell RUN path) and asserts each of its 4 cells crosses with the marimo-native ``(mimetype,
data)`` + ``text_projection`` the C# renderers switch on, AND that the committed fixture
``Assets/Tests/E2E/Editor/Fixtures/RichOutputSample.json`` (captured by
``tests/capture_rich_output_sample.py``) is still fresh:

  - markdown / table → byte-equal (deterministic),
  - ui              → byte-equal after normalising mo.ui's per-render ``random-id`` UUID,
  - chart           → STRUCTURAL only (a valid ``data:image/png;base64,`` PNG — matplotlib bytes
                      vary by version/platform, so byte-equality would be brittle; findings 0123 §3).

The fixture this test pins is the SAME file the AFK gate (StrategyEditorNotebookE2ERunner Section31,
STRATEGY-63/64) feeds to the real ``StrategyEditorView.SetOutput`` — so one capture keeps both legs
honest. This test never writes; re-capture with ``python -m tests.capture_rich_output_sample``.

  uv run python -m pytest tests/test_rich_output_sample.py

delete-the-production-logic litmus: ``test_litmus_plainified_cell_loses_its_rich_mimetype`` proves the
mimetype asserts are non-vacuous (plain-ifying a cell drops it out of its rich bucket → the per-cell
assert would go RED).
"""
from __future__ import annotations

import base64
import json
from pathlib import Path

import pytest

pytest.importorskip("marimo", reason="marimo is a prod dep since ADR-0012")

from engine.strategy_runtime.notebook_session import (  # noqa: E402
    IncrementalNotebookSession,
    text_projection,
)
from tests.capture_rich_output_sample import (  # noqa: E402
    CELL_NAMES,
    FIXTURE_PATH,
    SAMPLE_PATH,
    normalize_volatile,
    run_sample_cells,
)

pytestmark = pytest.mark.marimo

_PNG_MAGIC = bytes.fromhex("89504e470d0a1a0a")


@pytest.fixture(scope="module")
def cells() -> dict[str, dict]:
    """Run the real sample once; index its per-cell rich output by stable cell name."""
    return {c["name"]: c for c in run_sample_cells()}


@pytest.fixture(scope="module")
def fixture() -> dict:
    return json.loads(Path(FIXTURE_PATH).read_text(encoding="utf-8"))


# ---- the sample is a valid, 4-cell marimo notebook ----


def test_sample_is_valid_marimo_app_with_four_named_cells():
    from engine.strategy_runtime.cell_synthesis import load_app_from_text

    app = load_app_from_text(Path(SAMPLE_PATH).read_text(encoding="utf-8"))
    assert app is not None, "07_rich_output.py is not a loadable marimo App"
    assert len(list(app._cell_manager.codes())) == len(CELL_NAMES) == 4


# ---- per-cell rich payload (the live run) ----


def test_markdown_cell_is_markdown_with_heading_and_clean_projection(cells):
    c = cells["markdown"]
    assert c["ok"] and c["mimetype"] == "text/markdown", c
    assert "リッチ output デモ" in c["data"]          # the heading survives
    assert "<strong>" in c["data"]                     # bold rendered as HTML markup
    proj = text_projection(c["mimetype"], c["data"])
    assert "リッチ output デモ" in proj                 # tag-stripped text view keeps the heading
    assert "<" not in proj                              # …with every tag removed


def test_table_cell_is_html_table_with_data_values(cells):
    c = cells["table"]
    assert c["ok"] and c["mimetype"] == "text/html", c
    assert "<table" in c["data"]                        # a real DataFrame table
    assert "銘柄" in c["data"] and "7203.TSE" in c["data"]   # header + a value
    assert "7203.TSE" in text_projection(c["mimetype"], c["data"])  # value survives tag-strip


def test_chart_cell_is_self_contained_png_data_url(cells):
    c = cells["chart"]
    assert c["ok"] and c["mimetype"] == "image/png", c
    assert c["data"].startswith("data:image/png;base64,"), c["data"][:40]
    raw = base64.b64decode(c["data"].split("base64,", 1)[1])
    assert raw[:8] == _PNG_MAGIC, "expected a valid PNG payload"
    assert text_projection(c["mimetype"], c["data"]) == "[image/png]"


def test_ui_cell_is_html_slider_fallback_with_empty_projection(cells):
    c = cells["ui"]
    assert c["ok"] and c["mimetype"] == "text/html", c
    # mo.ui is an interactive widget: it folds into the html bucket (Strategy Editor cannot drive
    # interaction — the honest boundary). The stable slider config + label are present…
    assert "<marimo-slider" in c["data"]
    assert "data-start='1'" in c["data"] and "data-stop='10'" in c["data"]
    assert "data-initial-value='5'" in c["data"]
    assert "サンプルスライダー" in c["data"]
    # …but the widget carries no static text content, so the interim text projection is empty.
    assert text_projection(c["mimetype"], c["data"]) == ""


# ---- committed fixture freshness (live == fixture for deterministic cells) ----


def test_fixture_has_the_four_cells_in_order(fixture):
    assert fixture["schema_version"] == 1
    assert [c["name"] for c in fixture["cells"]] == list(CELL_NAMES)


@pytest.mark.parametrize("name", ["markdown", "table", "ui"])
def test_deterministic_cell_matches_committed_fixture(name, cells, fixture):
    fx = next(c for c in fixture["cells"] if c["name"] == name)
    live = cells[name]
    assert live["mimetype"] == fx["mimetype"]
    # ui is byte-equal only after normalising its per-render random-id; md/table normalise to identity.
    assert normalize_volatile(name, live["data"]) == fx["data"], (
        f"{name} payload drifted from the committed fixture — re-run "
        "`python -m tests.capture_rich_output_sample` and review the diff"
    )
    assert fx["projection"] == text_projection(fx["mimetype"], fx["data"])


def test_chart_fixture_is_structurally_a_valid_png_data_url(cells, fixture):
    # matplotlib bytes are not guaranteed stable across versions/platforms, so the chart's freshness
    # is STRUCTURAL: both the live run and the committed fixture must be valid PNG data URLs with the
    # [image/png] projection. (Byte-equality is intentionally NOT asserted; findings 0123 §3.)
    fx = next(c for c in fixture["cells"] if c["name"] == "chart")
    for src, data in (("live", cells["chart"]["data"]), ("fixture", fx["data"])):
        assert data.startswith("data:image/png;base64,"), (src, data[:40])
        assert base64.b64decode(data.split("base64,", 1)[1])[:8] == _PNG_MAGIC, src
    assert fx["mimetype"] == "image/png" and fx["projection"] == "[image/png]"


# ---- litmus: the rich-mimetype asserts are non-vacuous ----


def test_litmus_plainified_cell_loses_its_rich_mimetype():
    # If the markdown cell were plain-ified (mo.md removed → a bare value), it would no longer cross
    # as text/markdown — it lands in marimo's <pre>-wrapped text/html bucket. So the per-cell
    # mimetype asserts above genuinely depend on the sample's rich producers (delete-the-production-
    # logic litmus): plain-ifying a cell flips its bucket and the assertion goes RED.
    s = IncrementalNotebookSession()
    try:
        r = s.run_pressed([{"cell_id": "c", "code": "42"}], "c")["ran"][0]
    finally:
        s.close()
    assert r["mimetype"] != "text/markdown"
    assert r["mimetype"] == "text/html" and "42" in r["data"]
