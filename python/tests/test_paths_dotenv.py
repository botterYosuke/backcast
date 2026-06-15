"""engine.paths .env loader + DuckDB-root resolution (#48 review).

Pins the per-machine config contract introduced in #48: `.env` fills only UNSET keys (real
process env wins), quotes are stripped, comments/blank/no-`=` lines are ignored, and
`jquants_duckdb_root()` returns None when unset / resolves relative values under the repo.
No real-data dependency — always runs.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PYTHON_ROOT)

from engine import paths


def test_apply_dotenv_fills_unset_key(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {})
    paths._apply_dotenv("BACKCAST_X=value")
    assert os.environ["BACKCAST_X"] == "value"


def test_apply_dotenv_process_env_wins(monkeypatch) -> None:
    """The real process env must win over .env (override=False / setdefault)."""
    monkeypatch.setattr(os, "environ", {"BACKCAST_X": "real"})
    paths._apply_dotenv("BACKCAST_X=from_dotenv")
    assert os.environ["BACKCAST_X"] == "real"


def test_apply_dotenv_strips_quotes(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {})
    paths._apply_dotenv('DQ="double"\nSQ=\'single\'')
    assert os.environ["DQ"] == "double"
    assert os.environ["SQ"] == "single"


def test_apply_dotenv_ignores_comments_blank_and_no_eq(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {})
    paths._apply_dotenv("# a comment\n\n   \nNOEQ_LINE\nGOOD=ok")
    assert "NOEQ_LINE" not in os.environ
    assert os.environ == {"GOOD": "ok"}


def test_jquants_duckdb_root_none_when_unset(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {})
    assert paths.jquants_duckdb_root() is None


def test_jquants_duckdb_root_relative_resolves_under_repo(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {"BACKCAST_JQUANTS_DUCKDB_ROOT": "data/jp"})
    assert paths.jquants_duckdb_root() == paths.REPO_ROOT / "data" / "jp"


def test_jquants_duckdb_root_absolute_kept(monkeypatch) -> None:
    monkeypatch.setattr(os, "environ", {"BACKCAST_JQUANTS_DUCKDB_ROOT": "/Volumes/StockData/jp"})
    assert paths.jquants_duckdb_root() == Path("/Volumes/StockData/jp")


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
