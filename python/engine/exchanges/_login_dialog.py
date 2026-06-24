"""Shared helpers for the in-process venue-login tkinter dialogs (#122).

The dialog runs on a dedicated thread; the dispatcher
(``LiveLoopManager._handle_prompt_login``) signals a ``threading.Event`` on
timeout/teardown. Each dialog polls that event via ``root.after`` (Tk-thread-safe)
and, when set, closes itself. The *decision* of what result to record on that
close is factored here so it is unit-testable without a real Tk display
(findings 0093 §D1-限界 / M4).
"""

from __future__ import annotations


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
