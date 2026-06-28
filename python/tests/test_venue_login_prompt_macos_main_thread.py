"""macOS crash, structurally fixed — the venue-login path no longer creates any GUI
in Python (findings 0130 → 0131 / #181 / ADR-0040).

Original symptom (owner, macOS): clicking Settings ▸ "Connect Tachibana (Demo)"
crashed the Unity app. Root cause (findings 0130): the prompt-login path ran
``tkinter.Tk()`` on the off-main ``venue-login-dialog`` executor thread, violating
Cocoa's NSWindow main-thread requirement → ``libc++abi`` abort (SIGABRT). That abort
is an Objective-C abort, not a Python exception, so the host (Unity) died.

The fix is structural: #181 moves the login GUI to Unity (uGUI modal) and makes
Python authenticate *headless* (``submit_venue_login`` → ``venue_login_headless``).
Python never imports or constructs ``tkinter`` for login anymore, so the off-main
``Tk()`` — and therefore the macOS abort — cannot occur on any platform.

This was the ``@pytest.mark.xfail(strict=True)`` RED gate of findings 0130. With the
fix landed it is now an **enforcing** gate (xfail removed): it fails if any login-path
module reintroduces a tkinter dependency. It is cross-platform — "the login path does
not import tkinter" is a universal invariant, not a macOS-only one (re-adding a
tkinter dialog would re-open the macOS crash vector specifically).
"""
from __future__ import annotations

import inspect

from engine.live import live_orchestrator
from engine.exchanges import (
    venue_login_headless,
    tachibana_login_form_state,
    kabusapi_login_form_state,
)


# Modules on the venue-login path. None may import tkinter: the login GUI now lives
# in Unity (uGUI modal), Python only authenticates headless.
_LOGIN_PATH_MODULES = (
    live_orchestrator,
    venue_login_headless,
    tachibana_login_form_state,
    kabusapi_login_form_state,
)


def test_login_path_modules_do_not_reference_tkinter():
    offenders = []
    for mod in _LOGIN_PATH_MODULES:
        src = inspect.getsource(mod)
        # Scan for real import statements, not the word "tkinter" in prose/docstrings.
        if "import tkinter" in src or "from tkinter" in src or "import _login_dialog" in src:
            offenders.append(mod.__name__)
    assert not offenders, (
        "findings 0130/0131: a login-path module reintroduced a tkinter dependency, "
        "re-opening the macOS off-main Tk() crash vector: " + ", ".join(offenders)
    )


def test_retired_in_proc_dialog_seam_is_gone():
    # The #122 in-proc tkinter dialog seam (_handle_prompt_login / _try_create_tk) is
    # removed — there is no longer any code that could run Tk() on a worker thread.
    from engine.live.live_orchestrator import LiveLoopManager

    assert not hasattr(LiveLoopManager, "_handle_prompt_login")
    assert not hasattr(live_orchestrator, "_try_create_tk")


def test_retired_tkinter_login_flow_modules_are_deleted():
    # The tkinter dialog modules themselves are gone (full deletion, not unwiring).
    import importlib

    for name in (
        "engine.exchanges.tachibana_login_flow",
        "engine.exchanges.kabusapi_login_flow",
        "engine.exchanges._login_dialog",
    ):
        try:
            importlib.import_module(name)
        except ModuleNotFoundError:
            continue
        raise AssertionError(f"{name} should have been deleted (#181/ADR-0040)")
