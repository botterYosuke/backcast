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
    """An absolute root is kept verbatim (not resolved under the repo).

    Uses this machine's real `BACKCAST_JQUANTS_DUCKDB_ROOT` from `.env` (loaded into the
    process env at `engine.paths` import) so the absolute path is OS-appropriate: a hardcoded
    Mac `/Volumes/...` is drive-relative on Windows (`is_absolute()` is False there) and would
    be repo-resolved, failing this assertion. Skip-if-absent matches the module's per-machine
    `.env` contract (external-storage paths differ per machine; ADR-0006 / `.env.example`).
    """
    import pytest

    configured = os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT")
    if not configured or not Path(configured).is_absolute():
        pytest.skip("BACKCAST_JQUANTS_DUCKDB_ROOT not set to an absolute path in .env")
    monkeypatch.setattr(os, "environ", {"BACKCAST_JQUANTS_DUCKDB_ROOT": configured})
    assert paths.jquants_duckdb_root() == Path(configured)


def test_jquants_duckdb_root_lazy_reread_after_runtime_update(monkeypatch) -> None:
    """#137 S4 (findings 0107 D4): a RUNTIME os.environ update takes effect on the NEXT call — no restart.

    This is the engine-boundary contract the Settings「Data」injection relies on: the Unity host writes
    os.environ["BACKCAST_JQUANTS_DUCKDB_ROOT"] when the field commits, and the next Replay's
    jquants_duckdb_root() must read the new value (it lazy-reads os.environ.get every call, never caches).
    """
    monkeypatch.setattr(os, "environ", {})
    assert paths.jquants_duckdb_root() is None                 # unset → no override

    os.environ["BACKCAST_JQUANTS_DUCKDB_ROOT"] = "/first/root"  # simulate the host's first injection
    assert paths.jquants_duckdb_root() == Path("/first/root")

    os.environ["BACKCAST_JQUANTS_DUCKDB_ROOT"] = "/second/root"  # the owner edits the field again
    assert paths.jquants_duckdb_root() == Path("/second/root")  # next call reflects it (no restart — D4)


def test_jquants_listed_info_path_resolves_under_injected_root(monkeypatch, tmp_path) -> None:
    """#137 S4: jquants_listed_info_path() resolves `<root>/listed_info.duckdb` from os.environ when present.

    Mirrors DUCKROOT-04's C# end-to-end assertion (host inject → os.environ → engine resolves the real file),
    and the validator contract (a root WITHOUT listed_info.duckdb → None, so the field surfaces a red error).
    """
    monkeypatch.setattr(os, "environ", {"BACKCAST_JQUANTS_DUCKDB_ROOT": str(tmp_path)})
    assert paths.jquants_listed_info_path() is None            # root set but file missing → None (validator errors)

    (tmp_path / "listed_info.duckdb").write_text("")
    assert paths.jquants_listed_info_path() == tmp_path / "listed_info.duckdb"


def test_db_path_rejects_unconfigured_root() -> None:
    """An empty/unset root must fail loudly, not resolve to a relative cwd path (#48 review)."""
    import pytest

    from engine.kernel.duckdb_bars import db_path

    for bad in ("", "   ", "."):
        with pytest.raises(ValueError, match="not configured"):
            db_path(bad, "8918.TSE", "Daily")
    # A real absolute root still builds the expected path.
    assert db_path("/data/jp", "8918.TSE", "Minute") == Path("/data/jp/stocks_minute/8918.duckdb")


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-q"]))
