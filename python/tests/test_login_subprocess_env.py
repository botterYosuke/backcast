"""#23 Windows HITL fix — `_login_subprocess_env` が venv site-packages を伝播する。

embedded Python（Unity/pythonnet）では login_dialog_runner subprocess は base CPython で
起動され（`sys.executable` が host exe）、venv の third-party deps（httpx 等）を持たない。
それらは embedded interpreter の `sys.path`（host 注入の VenvSite）に在るが子へ継承されない
ため、PYTHONPATH に明示伝播しないと `ModuleNotFoundError: httpx` → LOGIN_SUBPROCESS_CRASHED。
"""
from __future__ import annotations

import os
import sys

from engine._backend_impl import PYTHON_SRC_ROOT, _login_subprocess_env


def test_login_env_propagates_site_packages(monkeypatch) -> None:
    fake_site = os.path.join("X:", "fake", ".venv", "Lib", "site-packages")
    monkeypatch.setattr(sys, "path", [*sys.path, fake_site])
    env = _login_subprocess_env()
    parts = env["PYTHONPATH"].split(os.pathsep)
    assert str(PYTHON_SRC_ROOT) in parts   # `import engine`
    assert fake_site in parts              # venv site-packages（httpx 等）が子へ届く


def test_login_env_src_root_first_and_deduped(monkeypatch) -> None:
    # 既存 PYTHONPATH に src_root が重複していても 1 度だけ・先頭。
    monkeypatch.setenv("PYTHONPATH", str(PYTHON_SRC_ROOT))
    env = _login_subprocess_env()
    parts = env["PYTHONPATH"].split(os.pathsep)
    assert parts[0] == str(PYTHON_SRC_ROOT)
    assert len(parts) == len(set(parts))   # 重複なし
