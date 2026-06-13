"""spike.kernel_golden.subprocess_util — one nautilus-free child-process spawner (#24).

The golden gates run the oracle / kernel / purity check in fresh subprocesses (process
isolation — findings 0008 §4). This centralises the identical env/cwd/capture wiring so a
change to the child-process contract (PYTHONPATH precedence, determinism env, etc.) is a
one-line edit instead of four. Each caller keeps its own timeout and return-code handling.
"""
from __future__ import annotations

import os
import subprocess
import sys

from spike.kernel_golden.scenario import PYTHON_ROOT


def run_python(args: list[str], *, timeout: float) -> subprocess.CompletedProcess:
    """Run ``python <args>`` from PYTHON_ROOT with engine.*/spike.* importable.

    `args` is the full argument list after the interpreter, so both ``["-m", module]``
    and ``["-c", source]`` forms work. The caller inspects returncode/stdout/stderr.
    """
    env = dict(os.environ)
    env["PYTHONPATH"] = PYTHON_ROOT + os.pathsep + env.get("PYTHONPATH", "")
    return subprocess.run(
        [sys.executable, *args],
        cwd=PYTHON_ROOT,
        env=env,
        capture_output=True,
        text=True,
        timeout=timeout,
    )
