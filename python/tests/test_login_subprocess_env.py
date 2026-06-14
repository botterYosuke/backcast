"""#23 Windows HITL fix — `_login_subprocess_env` が venv site-packages を伝播する。

embedded Python（Unity/pythonnet）では login_dialog_runner subprocess は base CPython で
起動され（`sys.executable` が host exe）、venv の third-party deps（httpx 等）を持たない。
それらは embedded interpreter の `sys.path`（host 注入の VenvSite）に在るが子へ継承されない
ため、PYTHONPATH に明示伝播しないと `ModuleNotFoundError: httpx` → LOGIN_SUBPROCESS_CRASHED。
"""
from __future__ import annotations

import os
import sys

from engine._backend_impl import (
    PYTHON_SRC_ROOT,
    _login_subprocess_env,
    _resolve_python_executable,
)


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


def test_resolve_python_uses_install_root_when_scripts_empty(monkeypatch) -> None:
    """uv python layout: python.exe は install ROOT 直下、Scripts/ は空。

    pythonnet/PyO3 in-proc では sys.executable は host exe（Unity.exe / backcast.exe）で
    Python ではない。現リゾルバは base_prefix/{Scripts,bin} しか探さないので、uv 導入
    CPython ではそこに python が無く host exe にフォールバック →
    `Unity.exe -m engine.live.login_dialog_runner` → NDJSON 不在 →
    LOGIN_SUBPROCESS_CRASHED。resolve() は root 直下の python.exe を返すべき。
    """
    monkeypatch.delenv("TTWR_PYTHON_BIN", raising=False)
    host_exe = os.path.join("D:\\", "UnityHub", "Editor", "Unity.exe")
    fake_root = os.path.join("C:\\", "uv", "python", "cpython-3.12")
    root_python = os.path.join(fake_root, "python.exe")
    monkeypatch.setattr(sys, "executable", host_exe)
    monkeypatch.setattr(sys, "base_prefix", fake_root)
    monkeypatch.setattr(sys, "prefix", fake_root)

    def fake_isfile(path: str) -> bool:
        # uv layout: root の python.exe だけ存在し、Scripts/ と bin/ は空。
        return path == root_python

    monkeypatch.setattr(os.path, "isfile", fake_isfile)
    assert _resolve_python_executable() == root_python


def test_resolve_python_keeps_real_sys_executable(monkeypatch) -> None:
    """Step 2 退行ガード: 実 Python の sys.executable（venv-activated /
    tachibana out-of-proc）はそのまま返すこと。root hardening は純加算。
    """
    monkeypatch.delenv("TTWR_PYTHON_BIN", raising=False)
    py = os.path.join("C:\\", "venv", "Scripts", "python.exe")
    monkeypatch.setattr(sys, "executable", py)
    assert _resolve_python_executable() == py
