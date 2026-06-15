from __future__ import annotations
import os
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# `engine` パッケージを含むソースルート（= `<repo>/python`）。`python -m engine...`
# を spawn する子プロセスの PYTHONPATH 構築に使う（in-proc では Rust ホストの
# sys.path 注入が子プロセスへ伝播しないため）。
PYTHON_SRC_ROOT = Path(__file__).resolve().parents[1]


def _apply_dotenv(text: str) -> None:
    """Parse KEY=VALUE lines and fill os.environ for keys that are not already set.

    Dependency-free. The real process environment always wins (`setdefault`), so an explicit
    export or launch-config `envFile` takes precedence over `.env`. Comments (`#`), blank
    lines, and lines without `=` are ignored; surrounding quotes on the value are stripped.
    """
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        if key:
            os.environ.setdefault(key, value.strip().strip('"').strip("'"))


def _load_dotenv_once() -> None:
    """Populate os.environ from the repo-root `.env` (per-machine config; gitignored).

    External-storage paths live in `.env` per `.env.example` ("read from here / process env,
    never hardcoded"). No-op when there is no `.env` on this machine — callers then rely on
    the process env and skip-if-absent downstream.
    """
    try:
        text = (REPO_ROOT / ".env").read_text(encoding="utf-8")
    except OSError:
        return
    _apply_dotenv(text)


_load_dotenv_once()


def resolve_repo_relative(value) -> Path:
    path = Path(value)
    return path if path.is_absolute() else REPO_ROOT / path


def artifacts_root() -> Path:
    return resolve_repo_relative(os.environ.get("ARTIFACTS_PATH", "artifacts"))


# テスト・外部コード向けエイリアス
def artifacts_dir() -> Path:
    return artifacts_root()


def jquants_catalog_path() -> Path:
    return artifacts_root() / "jquants-catalog"


# テスト・外部コード向けエイリアス
def catalog_path() -> Path:
    return jquants_catalog_path()


def instrument_lists_dir() -> Path:
    return artifacts_root() / "instrument-lists"


def listed_symbols_artifact_path(end_date: str) -> Path:
    return instrument_lists_dir() / f"listed-symbols-{end_date}.json"


def jquants_cache_dir() -> Path | None:
    value = os.environ.get("DEV_J_QUANTS_CACHE")
    return Path(value) if value else None


def jquants_duckdb_root() -> Path | None:
    """Owner's J-Quants DuckDB market-data root, from `BACKCAST_JQUANTS_DUCKDB_ROOT` (.env).

    Read from `.env` / process env — never hardcoded, because the external-storage path
    differs per machine (ADR-0006; `.env.example`). Returns None when unset so the real-data
    readers/tests skip-if-absent instead of pointing at a bogus default path.
    """
    value = os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT")
    return resolve_repo_relative(value) if value else None
