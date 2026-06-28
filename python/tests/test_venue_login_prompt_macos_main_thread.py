"""macOS regression — venue-login prompt dialog must not touch tkinter/Cocoa
off the main thread (findings 0130).

Symptom (owner, this terminal): clicking Settings ▸ "Connect Tachibana (Demo)"
crashes the Unity app on macOS.

Root cause (empirically pinned, findings 0130):

    Settings button ▸ BackcastWorkspaceRoot.OnVenueConnect
      ▸ WorkspaceEngineHost.VenueLogin  (spawns a "WorkspaceVenueLogin" worker thread)
      ▸ Py.GIL ▸ venue_login("TACHIBANA","prompt","demo")
      ▸ _attempt("prompt") ▸ asyncio.run_coroutine_threadsafe(_handle_prompt_login, live_loop)
      ▸ _handle_prompt_login ▸ loop.run_in_executor(executor, _run)   # "venue-login-dialog" thread
      ▸ _run ▸ _try_create_tk() ▸ tkinter.Tk()

``tkinter.Tk()`` initializes a Cocoa ``NSWindow``, which AppKit requires on the
MAIN thread. Constructed on the dedicated ``venue-login-dialog`` executor thread
it raises ``NSInternalInconsistencyException`` ("NSWindow drag regions should
only be invalidated on the Main Thread!") → ``libc++abi`` abort (SIGABRT, process
exit 134). That abort is an Objective-C abort, NOT a Python exception, so the
``except Exception`` guards in ``_try_create_tk`` / ``_handle_prompt_login``
cannot catch it — the whole host process (Unity) dies.

The #122 in-process dialog architecture (findings 0093) was validated on Windows,
where Tk on a secondary thread is legal; macOS Cocoa is not. Every existing
prompt-login test monkeypatches ``_try_create_tk`` to ``lambda: True``, so none of
them ever runs the real Tk probe off-main — this is the death-angle they all miss.

Why this gate asserts the *cause*, not the *symptom*: the abort is a Cocoa
main-thread UB and is NONDETERMINISTIC (observed both exit 134 and exit 0 from the
same code across runs), so a "process aborts" gate would be flaky. The
deterministic, method-independent invariant is: on macOS the prompt-login path
must never construct ``tkinter.Tk()`` on a non-main thread. Both candidate fixes
satisfy it — (a) marshal the dialog to the main thread, or (b) refuse off-main and
degrade to NO_DISPLAY_AVAILABLE — so the gate does not prejudge the fix.

xfail(strict) on darwin: lands RED without reddening the suite; once a fix makes
Tk main-thread-only (or absent), the test XPASSes and strict-xfail hard-fails,
forcing removal of the marker so this becomes an enforcing gate. Skipped off
darwin (Tk-off-main is legal there; the invariant is macOS-specific).
"""
from __future__ import annotations

import asyncio
import sys
import threading

import pytest

from engine.live.live_orchestrator import LiveLoopManager
from engine.exchanges import tachibana_login_flow


def _run_on_selector_loop(coro):
    """Run *coro* on a fresh SelectorEventLoop, on THIS (main) thread — mirrors how
    the live loop awaits ``_handle_prompt_login`` (the inner ``run_in_executor``
    still hops the dialog work onto a separate worker thread, which is the point)."""
    loop = asyncio.SelectorEventLoop()
    try:
        return loop.run_until_complete(coro)
    finally:
        loop.close()


@pytest.mark.skipif(
    sys.platform != "darwin",
    reason="Cocoa main-thread requirement is macOS-specific; Tk off-main is legal elsewhere",
)
@pytest.mark.xfail(
    strict=True,
    reason="findings 0130: _handle_prompt_login runs _try_create_tk()/tkinter.Tk() on a "
    "run_in_executor worker thread; on macOS Cocoa requires the main thread → process "
    "abort. Fix must marshal Tk to the main thread or refuse off-main (NO_DISPLAY).",
)
def test_prompt_login_never_creates_tk_off_main_thread(monkeypatch):
    main = threading.main_thread()
    tk_construction_threads: list[threading.Thread] = []

    class _FakeTk:
        # Records the constructing thread instead of building a real Cocoa window,
        # so the test is deterministic (no real NSWindow → no flaky abort) and
        # observes ONLY the invariant under test: which thread Tk() runs on.
        def __init__(self, *args, **kwargs):
            tk_construction_threads.append(threading.current_thread())

        def withdraw(self):
            pass

        def destroy(self):
            pass

    monkeypatch.setattr("tkinter.Tk", _FakeTk)
    # The display-bound human-input dialog is downstream of the crash point; stub it
    # to a no-op so the test exercises only the Tk-probe seam.
    monkeypatch.setattr(
        tachibana_login_flow,
        "run_dialog",
        lambda env_hint, cancel_event=None: {"success": True, "error_code": ""},
    )

    mgr = LiveLoopManager.__new__(LiveLoopManager)  # _handle_prompt_login touches no instance state
    result = _run_on_selector_loop(mgr._handle_prompt_login("TACHIBANA", "demo"))

    # Anti-vacuity: the prompt-login coroutine must have run to completion (3-tuple),
    # otherwise an empty thread list would pass for the wrong reason.
    assert result is not None and len(result) == 3, f"path did not complete: {result!r}"

    off_main = [t for t in tk_construction_threads if t is not main]
    assert not off_main, (
        "tkinter.Tk() was constructed on non-main thread(s) "
        f"{[t.name for t in off_main]} — on macOS this aborts the host process "
        "(Cocoa NSWindow main-thread requirement)"
    )
