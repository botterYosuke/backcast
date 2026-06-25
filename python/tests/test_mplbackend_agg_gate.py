"""#134 regression gate — MPLBACKEND=Agg keeps matplotlib off tkinter/Tcl.

The shipped runtime sets ``MPLBACKEND=Agg`` in
``PythonRuntimeLocator.ConfigureBeforeInitialize`` (before ``PythonEngine.Initialize()``).
If that env is removed, matplotlib auto-resolves an *interactive* backend (TkAgg →
tkinter/Tcl). When anything imports matplotlib on a background thread — e.g.
``PickerInstrumentFetch`` → ``InvokeListInstruments`` running on a worker — Tcl is
touched off its creating thread and ``Tcl_Panic`` crashes Unity (a native crash that
AFK batchmode probes cannot reliably catch). This gate pins that env's effect.

Backend resolution is process-global, so each assertion runs in a fresh subprocess
with ``MPLBACKEND`` controlled explicitly. Companion fix: #133 (login-dialog tkinter
teardown). See docs/findings/0107.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import textwrap

import pytest


# Resolve the backend, then force it to actually load (figure() triggers the backend
# import) and report whether tkinter/_tkinter got pulled in. Errors creating a window
# (headless) are swallowed: the question is only whether the *module* tkinter imported.
_PROBE = textwrap.dedent(
    """
    import json, sys
    import matplotlib
    backend = matplotlib.get_backend()
    try:
        import matplotlib.pyplot as plt
        fig = plt.figure()
        plt.close(fig)
    except Exception as exc:  # window creation may fail headless; the import already happened
        backend = backend + "|figure-error:" + type(exc).__name__
    has_tk = ("tkinter" in sys.modules) or ("_tkinter" in sys.modules)
    print(json.dumps({"backend": backend, "has_tk": has_tk}))
    """
)


def _run_probe(mplbackend: str | None) -> dict:
    env = dict(os.environ)
    env.pop("MPLBACKEND", None)
    if mplbackend is not None:
        env["MPLBACKEND"] = mplbackend
    proc = subprocess.run(
        [sys.executable, "-c", _PROBE],
        capture_output=True,
        text=True,
        env=env,
    )
    assert proc.returncode == 0, f"probe crashed:\nSTDOUT={proc.stdout}\nSTDERR={proc.stderr}"
    return json.loads(proc.stdout.strip().splitlines()[-1])


@pytest.mark.scenario("MPLBACKEND-01")
def test_mplbackend_agg_does_not_import_tkinter():
    """Under the production env (MPLBACKEND=Agg), importing + loading matplotlib resolves
    to the non-interactive Agg backend and never pulls in tkinter/_tkinter — so the
    background-thread Tcl_Panic vector stays closed."""
    pytest.importorskip("matplotlib")
    result = _run_probe("Agg")
    assert result["backend"].lower().startswith("agg"), result
    assert result["has_tk"] is False, (
        "matplotlib imported tkinter under MPLBACKEND=Agg — the GUI-backend block leaked"
    )


def test_litmus_tkagg_pulls_in_tkinter():
    """Non-vacuity (delete-the-production-logic litmus): if the shipped Agg env were
    removed and an interactive backend resolved, matplotlib *would* import tkinter — so
    the gate above is meaningful. Forcing MPLBACKEND=TkAgg must pull tkinter in.

    Skipped only when tkinter itself is unavailable in this interpreter (then the whole
    Tcl_Panic vector is moot here)."""
    pytest.importorskip("matplotlib")
    pytest.importorskip("tkinter")
    result = _run_probe("TkAgg")
    assert result["has_tk"] is True, (
        f"TkAgg did not import tkinter — litmus not exercising the real vector: {result}"
    )
