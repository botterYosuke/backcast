"""Shared helpers for the in-process venue-login tkinter dialogs (#122).

The dialog runs on a dedicated thread; the dispatcher
(``LiveLoopManager._handle_prompt_login``) signals a ``threading.Event`` on
timeout/teardown. Each dialog polls that event via ``root.after`` (Tk-thread-safe)
and, when set, closes itself. The *decision* of what result to record on that
close is factored here so it is unit-testable without a real Tk display
(findings 0093 §D1-限界 / M4).
"""

from __future__ import annotations


def teardown_tk(root) -> None:
    """Destroy a Tk *root* on the CALLING (creating) thread — half of the #133 fix.

    Tcl/Tk is not thread-safe. The Tcl interpreter registers an async handler on the
    thread that created it; finalizing the interpreter on any *other* thread trips
    ``Tcl_AsyncDelete("async handler deleted by the wrong thread")`` → ``Tcl_Panic`` and
    the whole process (Unity) dies. So the interpreter must be destroyed, and its cyclic
    garbage collected, on the creating thread.

    This function does only the *destroy* (idempotent — the dialog callbacks usually
    called ``root.destroy()`` already). It deliberately does **not** ``gc.collect()``
    here: ``root`` is a live local of this frame (and the caller's), so the
    ``Tk`` ⇄ widget ⇄ command-closure / ``StringVar``-trace reference cycle is still
    reachable — a collect at this point cannot reclaim it. The cycle only becomes
    unreachable garbage once the caller's tkinter-holding frame is popped, so the
    *decisive* same-thread sweep lives at the caller's post-frame collect — the
    ``run_dialog`` wrapper's ``gc.collect()`` and ``LiveLoopManager._run``'s
    ``finally: gc.collect()`` — not here. (Gate: TKTEARDOWN-03 drives the real
    ``run_dialog`` and asserts its root is finalized on the creating thread.)
    """
    try:
        root.destroy()  # idempotent: usually already destroyed by the dialog callback
    except Exception:
        pass


def apply_cancel_timeout(result: dict) -> None:
    """Mark *result* as a cancelled-by-timeout login.

    Records the distinct ``LOGIN_TIMEOUT`` error_code (not the generic
    ``VENUE_LOGIN_FAILED``) and clears any in-memory token. ``result`` is the
    dialog's mutable result dict; a ``token`` key is only present on the kabu path.
    """
    result["success"] = False
    result["error_code"] = "LOGIN_TIMEOUT"
    if "token" in result:
        result["token"] = None
