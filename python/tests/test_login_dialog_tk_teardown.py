"""#133 regression — login-dialog tkinter is torn down on its *creating* thread.

Root cause (native crash, Unity process dies):
``Tcl_AsyncDelete: async handler deleted by the wrong thread``. The venue-login
dialog (``run_dialog``) and the headless probe (``_try_create_tk``) build a Tcl
interpreter (``Tk()``) on a dedicated login thread. tkinter ``Tk`` / widget /
``StringVar`` objects form reference cycles, so after ``root.destroy()`` the
underlying ``_tkinter.tkapp`` is reclaimed by the *cyclic* GC — which can fire on
any later thread (e.g. ``PickerInstrumentFetch`` holding the GIL during
``list_instruments``). Finalizing the interpreter off its creating thread trips
``Tcl_AsyncDelete`` and crashes the process.

Fix: ``_login_dialog.teardown_tk`` destroys the root and forces ``gc.collect()`` on
the creating thread; ``run_dialog`` confines every tkinter object to an inner frame
and collects again the moment that frame is released. This module pins the
discipline: a cyclic Tk graph built on thread A must be finalized on thread A, so a
later sweep on thread B touches nothing tkinter-shaped.

These tests need a real Tcl display; on a headless host (``Tk()`` raises) they SKIP,
which is exactly the no-regression contract of AC#3 (the dialog path still degrades
to NO_DISPLAY_AVAILABLE without a display — pinned in test_inproc_prompt_login.py).
"""
from __future__ import annotations

import gc
import threading
import weakref

import pytest


def _display_available() -> bool:
    """True iff a real Tcl interpreter can be created here (False = headless)."""
    try:
        import tkinter
        r = tkinter.Tk()
        r.destroy()
        return True
    except Exception:
        return False


@pytest.fixture(autouse=True)
def _isolate_tk_default_root():
    """Clear tkinter's module-global default root before/after each test.

    These tests create Tk roots on *worker* threads; a leftover (possibly already
    destroyed) ``tkinter._default_root`` from one test can otherwise make the next
    test's display probe spuriously fail/SKIP (observed: TKTEARDOWN-02 SKIPping right
    after TKTEARDOWN-01 ran). The tests here own their roots explicitly, so resetting
    the default-root global between them is safe."""
    import tkinter
    tkinter._default_root = None
    yield
    tkinter._default_root = None


@pytest.mark.scenario("TKTEARDOWN-01")
def test_dialog_tk_graph_is_finalized_on_the_creating_thread():
    """A cyclic Tk graph built + torn down on thread A is finalized on thread A — so a
    subsequent gc.collect() on the main thread (thread B) finalizes nothing (no
    cross-thread Tcl_AsyncDelete).

    RED→GREEN litmus: drop the post-frame ``gc.collect()`` inside ``_creating_thread``
    (mirroring ``run_dialog``'s trailing collect / ``_run``'s defensive collect) and the
    cycle survives thread A → it is reclaimed by the main-thread sweep → the finalize
    thread becomes thread B → this test FAILs.
    """
    if not _display_available():
        pytest.skip("no Tcl display (headless host) — teardown path is display-bound")

    import tkinter
    from engine.exchanges._login_dialog import teardown_tk

    finalized_on: dict[str, int] = {}
    created_on: dict[str, int] = {}

    def _build_fake_dialog():
        """Reproduce the exact shape run_dialog leaves behind: a Tk root + StringVar +
        a widget whose command-closure captures the root, plus a hard reference cycle
        that refcounting alone cannot break (so only the cyclic GC can free it)."""
        root = tkinter.Tk()
        root.withdraw()
        var = tkinter.StringVar(master=root, value="secret")
        holder: dict = {}

        def _command():  # closure captures root + var → widget ⇄ closure ⇄ root cycle
            holder["root"] = root
            holder["var"] = var

        tkinter.Button(root, text="OK", command=_command)
        _command()
        holder["self"] = holder  # hard cycle: survives refcount, needs cyclic GC

        # Record which thread eventually finalizes the interpreter-bearing objects.
        weakref.finalize(root, lambda: finalized_on.setdefault("root", threading.get_ident()))
        weakref.finalize(var, lambda: finalized_on.setdefault("var", threading.get_ident()))

        teardown_tk(root)  # destroy on this thread (root/var still frame-referenced here)
        # local frame (root/var/holder + closures) released when this function returns

    def _creating_thread():
        created_on["id"] = threading.get_ident()
        _build_fake_dialog()
        gc.collect()  # decisive sweep on the CREATING thread (mirrors run_dialog / _run)

    t = threading.Thread(target=_creating_thread, name="venue-login-dialog-fake")
    t.start()
    t.join()

    # Everything was already reclaimed on the creating thread → nothing survives.
    main_ident = threading.get_ident()
    gc.collect()  # the "PickerInstrumentFetch on another thread" sweep

    assert finalized_on.get("root") == created_on["id"], (
        "Tk root finalized on the wrong thread → Tcl_AsyncDelete crash vector is open"
    )
    assert finalized_on.get("var") == created_on["id"], (
        "StringVar finalized on the wrong thread → cross-thread Tcl teardown"
    )
    assert main_ident not in (finalized_on.get("root"), finalized_on.get("var")), (
        "a tkinter object survived to be finalized on the main (non-creating) thread"
    )


@pytest.mark.scenario("TKTEARDOWN-02")
def test_try_create_tk_tears_down_its_probe_root(monkeypatch):
    """``_try_create_tk`` hands its probe ``Tk()`` to ``teardown_tk`` (destroy +
    same-thread collect), so the headless-detection probe never leaks an interpreter.

    RED→GREEN litmus: remove the ``teardown_tk(root)`` call in ``_try_create_tk`` and
    ``calls`` stays empty → this test FAILs. The probe is imported at call time, so
    patching ``_login_dialog.teardown_tk`` is observed by the orchestrator.
    """
    if not _display_available():
        pytest.skip("no Tcl display (headless host) — probe returns False, nothing to tear down")

    from engine.exchanges import _login_dialog
    from engine.live import live_orchestrator

    calls: list = []
    real = _login_dialog.teardown_tk

    def _spy(root):
        calls.append(root)
        real(root)  # still actually tear it down on this thread

    monkeypatch.setattr(_login_dialog, "teardown_tk", _spy)

    assert live_orchestrator._try_create_tk() is True
    assert len(calls) == 1, "the probe root was not handed to teardown_tk (leak risk)"


@pytest.mark.scenario("TKTEARDOWN-03")
@pytest.mark.parametrize("venue", ["kabu", "tachibana"])
def test_real_run_dialog_finalizes_its_tk_on_the_creating_thread(monkeypatch, venue):
    """The REAL ``run_dialog`` (not a hand-built analog) leaves no Tk object alive for a
    later cross-thread sweep: the root it creates on worker-thread A is finalized on A.

    This is the gate on the *actual shipped fix line* — the ``gc.collect()`` in the
    ``run_dialog`` wrapper (kabusapi_login_flow / tachibana_login_flow). The dialog is
    driven headlessly-on-display via a pre-set ``cancel_event``: ``_poll_cancel`` fires
    at +200ms, sees the event set, ``root.destroy()`` → ``mainloop()`` returns
    (LOGIN_TIMEOUT) — no station, credentials, or auth thread needed. Looped ×3 to also
    cover the "repeated login attempts don't accumulate cross-thread teardown" criterion
    (the AFK analog of KABU-TCL-HITL-01's "踏み続けても落ちない").

    RED→GREEN litmus: delete the wrapper's ``gc.collect()`` and the dialog's Tk cycle is
    NOT reclaimed on thread A (``teardown_tk`` only destroys; the impl frame is popped but
    nothing collects on A). The final root then survives thread A and is finalized by the
    main-thread (thread B) ``gc.collect()`` below → ``main_ident in finalized_on`` → FAIL.
    Unlike TKTEARDOWN-01 (which supplies its own decisive collect and so does not exercise
    the production line), removing the wrapper collect makes THIS test red.
    """
    if not _display_available():
        pytest.skip("no Tcl display (headless host) — run_dialog teardown path is display-bound")

    import tkinter

    if venue == "kabu":
        from engine.exchanges import kabusapi_login_flow as flow
        # Avoid any kabuStation network probe before mainloop — irrelevant to teardown.
        monkeypatch.setattr(flow, "probe_station", lambda *a, **k: False)
    else:
        from engine.exchanges import tachibana_login_flow as flow

    real_tk = tkinter.Tk
    finalized_on: list[int] = []
    created_count = {"n": 0}

    def _recording_tk(*a, **k):
        r = real_tk(*a, **k)  # a genuine Tk root — the dialog works normally
        created_count["n"] += 1
        # Record the thread that finalizes this exact root. Deliberately keep NO strong
        # ref to ``r`` here: in production nothing outside run_dialog's frame holds the
        # root, so the wrapper's gc.collect() is the only thing that can reclaim it. A
        # strong ref in the test would mask that — letting the test's own teardown free
        # the root and hiding a missing wrapper collect.
        weakref.finalize(r, lambda: finalized_on.append(threading.get_ident()))
        return r

    monkeypatch.setattr(tkinter, "Tk", _recording_tk)

    worker_ident: dict[str, int] = {}

    def _drive():
        worker_ident["id"] = threading.get_ident()
        for _ in range(3):
            ev = threading.Event()
            ev.set()  # dialog self-closes via _poll_cancel at +200ms
            res = flow.run_dialog(env_hint="verify", cancel_event=ev)
            assert res["success"] is False
            assert res["error_code"] == "LOGIN_TIMEOUT", res

    t = threading.Thread(target=_drive, name="venue-login-dialog-fake")
    t.start()
    t.join(timeout=20)
    assert not t.is_alive(), "run_dialog did not self-close on a pre-set cancel_event (hang)"

    main_ident = threading.get_ident()
    gc.collect()  # the "PickerInstrumentFetch on another thread" sweep on thread B

    assert created_count["n"] == 3, "run_dialog did not create the expected 3 Tk roots"
    assert len(finalized_on) == 3, (
        f"expected 3 dialog roots finalized, got {len(finalized_on)} — a Tk root may still be alive"
    )
    assert main_ident not in finalized_on, (
        "a run_dialog Tk root was finalized on the main (non-creating) thread → the "
        "Tcl_AsyncDelete crash vector is open (the wrapper gc.collect() is not reclaiming it)"
    )
    assert all(ident == worker_ident["id"] for ident in finalized_on), (
        f"a run_dialog Tk root was finalized off its creating thread "
        f"{worker_ident['id']}: {finalized_on}"
    )


def test_try_create_tk_returns_false_without_a_display(monkeypatch):
    """AC#3 no-regression: when ``Tk()`` raises (headless), the probe returns False and
    never reaches teardown — the dialog dispatcher then yields NO_DISPLAY_AVAILABLE
    (pinned in test_inproc_prompt_login.py). Simulated by forcing ``tkinter.Tk`` to raise."""
    import tkinter

    from engine.live import live_orchestrator

    def _boom(*_a, **_k):
        raise RuntimeError("no display name and no $DISPLAY environment variable")

    monkeypatch.setattr(tkinter, "Tk", _boom)
    assert live_orchestrator._try_create_tk() is False
