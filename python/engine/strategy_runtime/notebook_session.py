"""Phase 2 (#95) 土台: per-cell RUN over a persistent in-proc marimo kernel.

ADR-0016 D2 / findings 0070 F1 / findings 0071. The "土台" layer is pure reactive
computation — the engine is NOT connected (no ``bt``, no ``submit_market``, no per-bar
drain). Pressing a cell's RUN button runs THAT cell as root plus its reactive downstream,
in DAG order, and the output text of each cell that ran is returned to its window.

The seam is the marimo ``Runner`` interactive path (findings 0070 F1): NOT a reuse of
``thin_drain.HeadlessKernel`` alone (a kernel stand-up has no single-cell reactive run and
no per-cell output capture), and NOT an HTTP server or a hand-rolled DAG engine (F2 — marimo
runs in-process, ADR-0001). We reuse ``HeadlessKernel`` ONLY for the thread-local marimo
RuntimeContext, then drive ``marimo._runtime.runner.cell_runner.Runner`` directly so a press
forces ``{pressed} + reactive descendants`` (``compute_cells_to_run(..., "autorun")``) and a
``post_execution`` hook captures each cell's ``RunResult`` (last-expr value + ``mo.output``).

Lifecycle (findings 0070 F5 / 0071 P2-2): marimo Kernel/RuntimeContext is THREAD-LOCAL, so a
session is built AND run on the SAME thread (the host's notebook-run worker thread). The
source is the LIVE editor content (owner HITL 2026-06-20: run the unsaved buffer, no Save
required) — so the session REBUILDS when the source changes (teardown + cold-run the new
cells) and REUSES (globals persist) when it is unchanged.

This module top-imports marimo (a prod dep since ADR-0012), so the runtime seam that reaches
it (``_backend_impl.run_cell``) must LAZY-import it — proven by tests/test_strategy_runtime_offline.py.
"""
from __future__ import annotations

import asyncio
import html as _html
import inspect
import re as _re
import threading
from typing import Any

# marimo is a prod dependency (ADR-0012); top-importing it here is fine because the runtime
# seam imports THIS module lazily (offline import-purity, tests/test_strategy_runtime_offline.py).
from marimo._runtime.commands import ExecuteCellCommand
from marimo._runtime.runner.cell_runner import Runner
from marimo._runtime.runner.hooks import create_default_hooks

from engine.strategy_runtime.cell_synthesis import load_app_from_text
from engine.strategy_runtime.thin_drain import HeadlessKernel


def _await(maybe_coro: Any) -> Any:
    """Run ``maybe_coro`` to completion whether it is a coroutine or already a value.

    ``Kernel.run`` is synchronous; ``Runner.run_all`` is a coroutine. Both are driven on the
    session's owning thread, so a fresh event loop per call is correct (and cheap relative to
    a cell run). No loop is running on this thread (the host worker is not an asyncio thread).
    """
    if inspect.isawaitable(maybe_coro):
        return asyncio.run(maybe_coro)
    return maybe_coro


def _output_text(run_result: Any) -> str:
    """The Phase 2 plain-text repr of a cell's output (rich rendering is Phase 6).

    Precedence: a raised exception → its text; an explicit ``mo.output`` → the stacked Html's
    markup text; otherwise the last-expression value → ``repr`` (clean plain text for the common
    土台 case of computing numbers/dicts/frames — marimo's ``try_format`` would HTML-wrap it).
    """
    if not run_result.success():
        exc = run_result.exception
        return f"{type(exc).__name__}: {exc}" if exc is not None else "error"
    acc = getattr(run_result, "accumulated_output", None)
    if acc:  # CellOutputList is truthy only when the cell published via mo.output
        html = acc.stack()
        if html is not None:
            return html.text
    value = run_result.output
    return "" if value is None else repr(value)


def _maybe_matplotlib_png(value: Any) -> "tuple[str, str] | None":
    """Render a matplotlib Figure/Axes to a SELF-CONTAINED ``image/png`` data URL (P6-3).

    marimo's own mpl formatter only activates via an import hook AND, once registered, swaps every
    library's static repr for an opinionated *interactive* formatter that serialises empty without
    the web frontend.  We sidestep both: detect a Figure (or an Axes' Figure) by type — only when
    matplotlib is already imported (a cell imported it), so this stays lazy — and ``savefig`` to a
    base64 PNG the C# side decodes straight to a Texture2D.  Returns ``None`` for non-figures."""
    import sys as _sys

    mpl = _sys.modules.get("matplotlib")
    if mpl is None:  # no cell imported matplotlib → not a figure (stay lazy, never import it here)
        return None
    from matplotlib.axes import Axes
    from matplotlib.figure import Figure

    fig = value if isinstance(value, Figure) else (value.figure if isinstance(value, Axes) else None)
    if fig is None:
        return None
    import base64
    import io

    buf = io.BytesIO()
    fig.savefig(buf, format="png", bbox_inches="tight")
    b64 = base64.b64encode(buf.getvalue()).decode("ascii")
    return "image/png", f"data:image/png;base64,{b64}"


def _format_output(run_result: Any) -> "tuple[str, str]":
    """The Phase 6 rich output of a cell as ``(mimetype, data)`` (findings 0075 P6-2).

    Uses marimo's own ``try_format`` so a value crosses with its REAL mimetype: a DataFrame →
    ``text/html`` table, ``mo.md(...)`` → ``text/markdown`` (rendered HTML), an image / matplotlib
    figure → ``image/png`` (a ``data:`` URL) or ``application/vnd.marimo+mimebundle``, a plain value
    → marimo's ``<pre>`` HTML.  An explicit ``mo.output`` publish takes precedence (its stacked
    Html).  A raised cell → its exception text as ``text/plain``.  The C# side renders images as
    textures and HTML/markdown via an HTML→TMP subset; ``text_projection`` is the interim text view.
    """
    if not run_result.success():
        exc = run_result.exception
        return "text/plain", (f"{type(exc).__name__}: {exc}" if exc is not None else "error")
    acc = getattr(run_result, "accumulated_output", None)
    if acc:  # an explicit mo.output.replace/append — the stacked Html is the cell's output
        html = acc.stack()
        if html is not None:
            return "text/html", html.text
    value = run_result.output
    if value is None:
        return "text/plain", ""
    png = _maybe_matplotlib_png(value)  # the named charting producer → self-contained image/png
    if png is not None:
        return png
    from marimo._output.formatting import try_format

    formatted = try_format(value)
    data = formatted.data if isinstance(formatted.data, str) else str(formatted.data)
    return str(formatted.mimetype), data


_TAG_RE = _re.compile(r"<[^>]+>")


def text_projection(mimetype: str, data: str) -> str:
    """An interim plain-text view of a rich output for the current C# Text renderer.

    Phase 6 Slice 5 adds native image/markdown/table renderers keyed on ``mimetype``; until then a
    cell window still shows text, so: an image → a short ``[image/png]`` placeholder; HTML/markdown
    → its tags stripped and entities unescaped (so ``<pre>20</pre>`` reads as ``20`` and a table
    reads as its cell text); ``text/plain`` → as-is.  Kept as the fallback for unsupported types.
    """
    if mimetype.startswith("image/") or "mimebundle" in mimetype:
        return f"[{mimetype}]"
    if mimetype in ("text/html", "text/markdown"):
        return _html.unescape(_TAG_RE.sub("", data)).strip()
    return data


class _ConsoleCapture:
    """Per-press, per-cell stdout/stderr capture (#102 Slice 1, findings 0079).

    The cell-attribution seam is marimo's own ``ThreadSafeStream.cell_id``: ``redirect_streams``
    sets it to the executing cell's id when entering ``_install_execution_context`` and restores it
    on exit (`redirect_streams.py:71`).  Each ``_append(cell_id, stream, text)`` lands in that cell's
    segment list — adjacent same-stream writes COLLAPSE into one entry (marimo cell.ts:133 /
    collapseConsoleOutputs.tsx parity — findings 0076 D1), and a stream switch (stdout→stderr or
    vice versa) opens a new segment so arrival order is preserved.

    The capture itself is plain data — no thread state, no stdout swap.  The wiring that delivers a
    cell's ``sys.stdout.write`` here is ``_BridgedConsoleHook`` (installed once per kernel), which
    binds the active capture during a press and unbinds after.
    """

    def __init__(self) -> None:
        # cell_id → list of (stream, list[str chunks]).  Same-stream writes append to the chunk
        # list rather than concatenating strings, so a tight ``for i in range(N): print(i)`` cell
        # stays O(N) instead of O(N²) (Python strings are immutable; ``buf += chunk`` reallocates).
        # ``get`` joins each chunk list ONCE when the press finalises.
        self._cells: dict[str, list[tuple[str, list[str]]]] = {}

    def _append(self, cell_id: "str | None", stream: str, text: str) -> None:
        if not cell_id or not text:
            return
        segments = self._cells.setdefault(str(cell_id), [])
        if segments and segments[-1][0] == stream:
            segments[-1][1].append(text)   # marimo-parity collapse: same stream extends the chunk list
        else:
            segments.append((stream, [text]))

    def reset_cell(self, cell_id: str) -> None:
        """Press-scoped clear for one cell: when the runner is about to execute it, drop any
        segments a stale-ancestor re-run in THIS press already accumulated (other cells untouched)."""
        self._cells[cell_id] = []

    def get(self, cell_id: str) -> list[dict[str, str]]:
        """Finalise the cell's segments to ``[{stream, text}, ...]``; joins each chunk list once."""
        return [
            {"stream": stream, "text": "".join(chunks)}
            for stream, chunks in self._cells.get(cell_id, [])
        ]


class _BridgedConsoleHook:
    """Bridges the HeadlessKernel's ``_SilentStdout``/``_SilentStderr`` into a press-scoped capture.

    The ``HeadlessKernel`` (`thin_drain.py:152`) hands real marimo ``Stdout``/``Stderr`` instances
    to its ``Kernel`` AND to ``initialize_kernel_context``, so the ``redirect_streams`` taken by
    ``_install_execution_context`` lands in the EDIT-MODE path (`redirect_streams.py:91`): it swaps
    ``sys.stdout``/``sys.stderr`` to those kernel-owned instances for the duration of the cell.
    Hooking at the marimo pre/post boundary would lose the writes — by the time ``pre`` fires, the
    swap hasn't happened; by the time ``post`` fires, it's already been undone.  The reliable seam
    is ``Stdout._write_with_mimetype`` on the kernel's stdout itself (the bottom of `Stdout.write`
    in `marimo/_messaging/types.py:48`); cell ``print`` calls it directly with ``data`` already a
    ``str``.

    We install ONE bridge per kernel: ``__init__`` replaces the two ``_write_with_mimetype`` methods
    with bound shims that route to ``self._capture`` when one is bound (i.e. during a press) and are
    a no-op otherwise.  The originals (``_SilentStdout._write_with_mimetype``) appended to a
    per-instance ``messages: list[str]`` that no production code reads (only the standalone
    thin_drain unit tests inspect it) and that would otherwise grow unbounded across a long-lived
    kernel — a ``for i in range(10_000): print(i)`` cell would leak 10k strings into the silent
    queue per press for nobody.  We deliberately do NOT forward, both to avoid that growth and so
    that uninstrumented kernel writes (rare but possible from extension calls) silently drop.

    Cell attribution comes from ``host.stream.cell_id``, which marimo's redirect_streams pins to the
    cell currently in ``_install_execution_context`` (`redirect_streams.py:71`).
    """

    def __init__(self, host: "HeadlessKernel") -> None:
        self._host = host
        self._capture: "_ConsoleCapture | None" = None
        host.stdout._write_with_mimetype = self._stdout_write  # type: ignore[method-assign]
        host.stderr._write_with_mimetype = self._stderr_write  # type: ignore[method-assign]

    def _current_cell_id(self) -> "str | None":
        cid = getattr(self._host.stream, "cell_id", None)
        return str(cid) if cid is not None else None

    def _stdout_write(self, data: str, mimetype: Any) -> int:
        if self._capture is not None and isinstance(data, str):
            self._capture._append(self._current_cell_id(), "stdout", data)
        return len(data) if isinstance(data, str) else 0

    def _stderr_write(self, data: str, mimetype: Any) -> int:
        if self._capture is not None and isinstance(data, str):
            self._capture._append(self._current_cell_id(), "stderr", data)
        return len(data) if isinstance(data, str) else 0

    def begin(self, capture: _ConsoleCapture) -> None:
        self._capture = capture

    def end(self) -> None:
        self._capture = None


class NotebookSession:
    """A persistent in-proc marimo kernel for one notebook, on its owning thread.

    Use ``run_pressed(source, index)`` per RUN press; call ``close()`` to tear the kernel down
    (config re-commit / notebook close — Phase 3 ``bt`` lifecycle reuses this seam).
    """

    def __init__(self) -> None:
        self._source: "str | None" = None
        self._host: "HeadlessKernel | None" = None
        self._cids: list[str] = []
        self._owner_thread: "int | None" = None  # the one thread allowed to drive this session

    @property
    def cell_count(self) -> int:
        return len(self._cids)

    def run_pressed(
        self, source: str, pressed_index: int, inject: "dict[str, Any] | None" = None
    ) -> dict[str, Any]:
        """Run the pressed cell + reactive downstream over the LIVE source; return per-cell text.

        Returns ``{"ok": bool, "ran": [{"index", "output", "ok"}...], "error": str | None}``.
        ``ran`` is in cell order and contains EXACTLY the cells that ran (pressed + descendants),
        so an upstream-independent cell is structurally absent (AC: it does not recompute).

        ``inject`` (#95 Phase 4) is a dict of host free refs merged into the cell globals — the
        ``bt`` handle that lets a cell drive a real backtest (ADR-0016 D4). It is injected BEFORE
        the press run; while the notebook is (re)built (cold-run), an injected ``bt`` is DISARMED
        so building/editing the notebook never starts a backtest (P4-1). Injected refs persist in
        globals for the kernel's lifetime; a fresh ``bt`` re-injected on the next press replaces it.
        """
        # marimo's Kernel/RuntimeContext is thread-local: the session must be driven from ONE thread
        # (the host's notebook-run worker — findings 0071 P2-2). Fail-closed if a second thread ever
        # reaches here, instead of silently corrupting the thread-local context.
        tid = threading.get_ident()
        if self._owner_thread is None:
            self._owner_thread = tid
        elif tid != self._owner_thread:
            return {
                "ok": False,
                "ran": [],
                "error": "NotebookSession driven from a second thread (marimo kernel is thread-local)",
            }
        if self._host is None or source != self._source:
            try:
                self._rebuild(source, inject)
            except Exception as exc:  # malformed source / load failure → fail-soft, kernel torn down
                return {"ok": False, "ran": [], "error": f"{type(exc).__name__}: {exc}"}
        if not (0 <= pressed_index < len(self._cids)):
            return {
                "ok": False,
                "ran": [],
                "error": f"cell index {pressed_index} out of range (0..{len(self._cids) - 1})",
            }
        return self._run(pressed_index, inject)

    def close(self) -> None:
        if self._host is not None:
            self._host.teardown()
        self._host = None
        self._cids = []
        self._source = None

    # ---- internals (all on the owning thread) ----

    @staticmethod
    def _apply_inject(k: Any, inject: "dict[str, Any] | None", *, armed: bool) -> None:
        """Merge the host free refs into the kernel globals, arming/disarming a ``bt`` handle.

        Disarmed for the cold-run graph build (so editing the notebook never drives a backtest),
        armed for the explicit RUN press (#95 P4-1)."""
        if not inject:
            return
        bt = inject.get("bt")
        if bt is not None and hasattr(bt, "arm"):
            bt.arm() if armed else bt.disarm()
        k.globals.update(inject)

    def _rebuild(self, source: str, inject: "dict[str, Any] | None" = None) -> None:
        self.close()
        host = HeadlessKernel()
        try:
            app = load_app_from_text(source)
            if app is None:
                raise ValueError("source is not a loadable marimo notebook")
            cids = list(app._cell_manager.cell_ids())
            codes = list(app._cell_manager.codes())
            # Inject host free refs (bt) BEFORE the cold-run so a cell that references bt resolves
            # the name — but DISARMED, so cold-running it does not start a backtest (P4-1).
            self._apply_inject(host.k, inject, armed=False)
            # Register + cold-run every cell so the graph is built and globals populated; after this
            # no cell is stale, so a press scopes to {pressed} + descendants (not stale ancestors).
            reqs = [ExecuteCellCommand(cell_id=cid, code=code) for cid, code in zip(cids, codes)]
            _await(host.k.run(reqs))
        except Exception:
            host.teardown()
            raise
        self._host = host
        self._cids = cids
        self._source = source

    def _run(self, pressed_index: int, inject: "dict[str, Any] | None" = None) -> dict[str, Any]:
        k = self._host.k  # type: ignore[union-attr]
        # Re-inject (a fresh bt per press replaces the prior one) and ARM for the explicit press.
        self._apply_inject(k, inject, armed=True)
        pressed = self._cids[pressed_index]

        recorded: dict[str, Any] = {}

        def _record(cell: Any, _ctx: Any, run_result: Any) -> None:
            recorded[cell.cell_id] = run_result

        hooks = create_default_hooks()
        hooks.add_post_execution(_record)
        runner = Runner(
            roots={pressed},
            graph=k.graph,
            glbls=k.globals,
            debugger=k.debugger,
            hooks=hooks,
            execution_mode="autorun",  # pressed + reactive descendants (forced, not stale-gated)
            excluded_cells=set(k.errors),
            execution_context=k._install_execution_context,  # publishes mo.output → captured
        )
        _await(runner.run_all())

        index_of = {cid: i for i, cid in enumerate(self._cids)}
        ran = [
            {"index": index_of[cid], "output": _output_text(rr), "ok": rr.success()}
            for cid, rr in recorded.items()
            if cid in index_of  # only cells the C# side can map back to a window (skip any setup cell)
        ]
        ran.sort(key=lambda r: r["index"])
        return {"ok": True, "ran": ran, "error": None}


class IncrementalNotebookSession:
    """A persistent marimo kernel driven incrementally for faithful per-cell staleness (#95 Phase 6).

    This is the P6-1 replacement for the rebuild-on-change ``NotebookSession`` above. Instead of
    tearing the kernel down and cold-running every cell when the source changes (which makes
    "stale" impossible — a press always runs everything fresh), it keeps ONE kernel for the
    notebook's lifetime and:

      - ``restage(cells)`` diff-registers each cell BY STABLE ID (the C# window/region id). A cell
        whose code changed is re-registered ``stale=True`` and its downstream is marked stale via
        ``graph.set_stale`` — but nothing runs. The current stale set is returned so the C# side can
        badge each window idle/stale (stale appears on EDIT, not on RUN).
      - ``run_pressed(cells, pressed_id)`` restages, then drives a bare ``Runner`` rooted at the
        pressed cell in ``autorun`` mode — which runs the pressed cell + its STALE ANCESTORS +
        reactive descendants (``compute_cells_to_run`` runs stale ancestors). After the run it
        clears stale on every cell that ran (the bare Runner does not), so the remaining stale set
        is exactly the cells still needing a press.

    Globals PERSIST across edits (no rebuild), so an injected ``bt`` handle and step-cache state are
    not destroyed by a keystroke — the Phase 2/4/5 "edit wipes live state" landmine dissolves
    structurally. Proven against real marimo by ``python/spike/phase6_stale_spike.py``.

    Thread-local discipline is identical to ``NotebookSession``: built AND driven on the host's one
    notebook-run worker thread (marimo Kernel/RuntimeContext is thread-local).
    """

    def __init__(self) -> None:
        self._host: "HeadlessKernel | None" = None
        self._codes: dict[str, str] = {}   # cell_id -> last registered code (diff source of truth)
        self._order: list[str] = []          # cell_id order (output ordering + index mapping)
        self._owner_thread: "int | None" = None
        # #102 Slice 1 (findings 0079): the bridge into the kernel's _SilentStdout/_SilentStderr —
        # built once with the kernel, bound to a fresh _ConsoleCapture for the duration of each press.
        self._console_hook: "_BridgedConsoleHook | None" = None
        # findings 0081: cell_id -> marimo registration Error (MarimoSyntaxError/…) for cells that did
        # NOT compile.  An un-compilable cell never enters the dataflow graph, so pressing it must NOT
        # reach _run (the runner KeyErrors on a root outside the graph) — we surface this error to the
        # cell's console instead, mirroring how a RUNTIME error already flows back through marimo.
        self._cell_errors: dict[str, Any] = {}

    @property
    def cell_count(self) -> int:
        return len(self._order)

    def close(self) -> None:
        if self._host is not None:
            self._host.teardown()
        self._host = None
        self._console_hook = None  # the bridge holds a reference to the torn-down kernel's streams
        self._codes = {}
        self._order = []
        self._cell_errors = {}

    def stale(self) -> list[str]:
        """The current stale set (cells needing a press), in cell order — empty when no kernel."""
        if self._host is None:
            return []
        stale = self._host.k.graph.get_stale()
        return [cid for cid in self._order if cid in stale]

    def restage(
        self, cells: "list[dict[str, Any]]", inject: "dict[str, Any] | None" = None
    ) -> dict[str, Any]:
        """Diff-register ``cells`` by stable id; mark changed+downstream stale WITHOUT running.

        ``cells`` is an ordered list of ``{"cell_id": str, "code": str}`` (the C# window order).
        Returns ``{"stale": [cell_id...], "error": str | None}``. Idempotent: re-staging the same
        cells changes nothing (``_maybe_register_cell`` no-ops on identical code).
        """
        guard = self._thread_guard()
        if guard is not None:
            return {"stale": [], "error": guard}
        try:
            with self._kernel_context():  # findings 0080: re-assert context on THIS thread for registration
                self._restage(cells, inject)
        except Exception as exc:  # malformed registration → fail-soft, surface to the notice line
            return {"stale": self.stale(), "error": f"{type(exc).__name__}: {exc}"}
        return {"stale": self.stale(), "error": None}

    def run_pressed(
        self,
        cells: "list[dict[str, Any]]",
        pressed_cell_id: str,
        inject: "dict[str, Any] | None" = None,
    ) -> dict[str, Any]:
        """Restage ``cells``, then run the pressed cell + stale ancestors + reactive descendants.

        Returns ``{"ok", "ran": [{"cell_id", "output", "ok"}...], "stale": [...], "error"}``. ``ran``
        contains exactly the cells that executed (so an upstream-independent cell is structurally
        absent). ``stale`` is what remains after clearing stale on the cells that ran.
        """
        guard = self._thread_guard()
        if guard is not None:
            return {"ok": False, "ran": [], "stale": [], "error": guard}
        # findings 0080: registration (_restage) AND the run (_run) share ONE context-install scope so
        # marimo's get_context() resolves on this run thread even when the kernel was built on another.
        with self._kernel_context():
            try:
                self._restage(cells, inject)
            except Exception as exc:
                return {"ok": False, "ran": [], "stale": self.stale(), "error": f"{type(exc).__name__}: {exc}"}
            if pressed_cell_id not in self._codes:
                return {
                    "ok": False,
                    "ran": [],
                    "stale": self.stale(),
                    "error": f"pressed cell {pressed_cell_id!r} is not registered",
                }
            if pressed_cell_id in self._cell_errors:
                # The cell did not compile (e.g. SyntaxError): it is absent from the dataflow graph, so
                # running it would KeyError in the runner.  Surface the error to ITS console instead
                # (findings 0081) — same destination a runtime traceback already reaches.
                return self._compile_error_result(pressed_cell_id)
            return self._run(pressed_cell_id, inject)

    # ---- internals (all on the owning thread) ----

    def _thread_guard(self) -> "str | None":
        tid = threading.get_ident()
        if self._owner_thread is None:
            self._owner_thread = tid
            return None
        if tid != self._owner_thread:
            return "IncrementalNotebookSession driven from a second thread (marimo kernel is thread-local)"
        return None

    def _ensure_host(self) -> "HeadlessKernel":
        if self._host is None:
            self._host = HeadlessKernel()
            # #102 Slice 1: install the stdout/stderr bridge once per kernel; presses bind their
            # own _ConsoleCapture for the duration of _run.
            self._console_hook = _BridgedConsoleHook(self._host)
        return self._host

    def _kernel_context(self):
        """Pin THIS session's marimo RuntimeContext on the CURRENT thread for the duration (findings 0080).

        marimo's RuntimeContext is OS-thread-local (`_ThreadLocalContext(threading.local)`); the kernel
        may be BUILT on one thread (`_ensure_host` → `initialize_kernel_context`) yet DRIVEN on another —
        the embedded pythonnet + `asyncio.run` notebook lane does not guarantee build-thread == run-thread.
        EVERY operation that touches the kernel must re-assert the context on the run thread, or marimo's
        `get_context()` raises `ContextNotInitializedError`:
          * `_restage` re-registers/compiles cells (marimo publishes through the context) — a CHANGED cell
            on a later press is where this bit (#102: run 1 fresh OK, run 2 after an edit failed); and
          * `_run` drives `run_all`, which reads the context at its prologue via `_should_broadcast_data`.
        `RuntimeContext.install()` is marimo's own re-entrant guard (save/install/restore), used by its
        AppKernelRunner — a no-op for state when build-thread == run-thread (saved and restored to itself).
        """
        return self._ensure_host().runtime_context.install()

    def _restage(self, cells: "list[dict[str, Any]]", inject: "dict[str, Any] | None") -> None:
        host = self._ensure_host()
        k = host.k
        # bt / host free refs must resolve as names when a cell is registered+run; seed them
        # DISARMED (registration never executes, and a later run arms before pressing — P4-1).
        NotebookSession._apply_inject(k, inject, armed=False)

        new_ids = [str(c["cell_id"]) for c in cells]
        new_codes = {str(c["cell_id"]): str(c.get("code", "")) for c in cells}

        # Deletions: cells that left the notebook. Purge their defs from globals so a downstream
        # re-run cannot read a vanished cell's stale value, then stale the orphaned children.
        for cid in self._order:
            if cid in new_codes:
                continue  # retained — keep its code-cache entry so it is not re-registered (re-staled)
            if cid in k.graph.cells:
                cell = k.graph.cells[cid]
                defs = set(getattr(cell, "defs", ()) or ())
                children = k.graph.delete_cell(cid)
                for name in defs:
                    k.globals.pop(name, None)
                if children:
                    k.graph.set_stale(children, prune_imports=True)
            self._codes.pop(cid, None)
            self._cell_errors.pop(cid, None)

        # Registrations: new or code-changed cells become stale (+ downstream via set_stale).
        for cid in new_ids:
            code = new_codes[cid]
            if self._codes.get(cid) == code and cid in k.graph.cells:
                continue  # unchanged — keep its current (clean or stale) state
            # _maybe_register_cell returns (old_children, error); error is non-None when the cell does
            # NOT compile (SyntaxError → MarimoSyntaxError).  Such a cell is absent from the graph, so we
            # remember the error here and short-circuit the press in run_pressed (findings 0081).
            _, err = k._maybe_register_cell(cid, code, stale=True)
            if err is not None:
                self._cell_errors[cid] = err
            else:
                self._cell_errors.pop(cid, None)
                if cid in k.graph.cells:
                    k.graph.set_stale({cid}, prune_imports=True)
            self._codes[cid] = code

        self._order = new_ids

    def _run(self, pressed_cell_id: str, inject: "dict[str, Any] | None") -> dict[str, Any]:
        k = self._host.k  # type: ignore[union-attr]
        NotebookSession._apply_inject(k, inject, armed=True)

        recorded: dict[str, Any] = {}

        def _record(cell: Any, _ctx: Any, run_result: Any) -> None:
            recorded[cell.cell_id] = run_result

        # #102 Slice 1 (findings 0079): per-cell stdout/stderr capture.  A fresh capture per press
        # gives the AC's "console clears each press" semantics naturally; the bridge routes writes
        # by host.stream.cell_id (pinned by redirect_streams to the executing cell) into the
        # right segment list.  A pre-execution hook resets THIS cell's segments — so a stale
        # ancestor that ran earlier in this same press starts the cell run from empty (not from
        # whatever its prior in-press writes left).
        console = _ConsoleCapture()
        assert self._console_hook is not None  # invariant: _ensure_host built it
        self._console_hook.begin(console)

        def _reset_cell(cell: Any, _ctx: Any) -> None:
            console.reset_cell(cell.cell_id)

        hooks = create_default_hooks()
        hooks.add_pre_execution(_reset_cell)
        hooks.add_post_execution(_record)
        runner = Runner(
            roots={pressed_cell_id},
            graph=k.graph,
            glbls=k.globals,
            debugger=k.debugger,
            hooks=hooks,
            execution_mode="autorun",  # pressed + stale ancestors + reactive descendants
            excluded_cells=set(k.errors),
            execution_context=k._install_execution_context,
        )
        # The marimo RuntimeContext this `run_all` reads (via `_should_broadcast_data`) is pinned on the
        # CURRENT thread by the `_kernel_context()` guard in `run_pressed` — _restage AND this run share
        # one install scope (findings 0080).
        try:
            _await(runner.run_all())
        finally:
            self._console_hook.end()

        # The bare Runner does not clear stale; the host does, so the next press's stale set is
        # exactly the cells STILL needing a run (faithful to marimo's post-run stale clear).
        for cid in recorded:
            if cid in k.graph.cells:
                k.graph.cells[cid].set_stale(stale=False, broadcast=False)

        order_of = {cid: i for i, cid in enumerate(self._order)}
        ran = []
        for cid, rr in recorded.items():
            if cid not in order_of:  # only cells the C# side can map back to a window
                continue
            mimetype, data = _format_output(rr)
            ran.append(
                {
                    "cell_id": cid,
                    "mimetype": mimetype,
                    "data": data,
                    "console": console.get(cid),
                    "ok": rr.success(),
                }
            )
        ran.sort(key=lambda r: order_of[r["cell_id"]])
        return {"ok": True, "ran": ran, "stale": self.stale(), "error": None}

    def _compile_error_result(self, cell_id: str) -> dict[str, Any]:
        """Hand back a single ran row that surfaces an un-compilable cell's error to its console.

        The cell is absent from the dataflow graph (it never compiled), so it cannot be run — but the
        press must still produce visible feedback.  We mirror the shape ``_run`` returns for a cell that
        FAILED: top-level ``ok``/``error`` stay clean (this is not a driver failure), the row's ``ok`` is
        ``False``, and the marimo error message rides the ``stderr`` console stream — the SAME place a
        runtime traceback lands, so the editor paints it amber instead of the press dying with a KeyError
        (findings 0081).
        """
        err = self._cell_errors[cell_id]
        msg = getattr(err, "msg", None) or str(err)
        return {
            "ok": True,
            "ran": [
                {
                    "cell_id": cell_id,
                    "mimetype": "text/plain",
                    "data": "",
                    "console": [{"stream": "stderr", "text": msg}],
                    "ok": False,
                }
            ],
            "stale": self.stale(),
            "error": None,
        }
