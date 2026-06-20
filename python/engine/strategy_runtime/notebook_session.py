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

    @property
    def cell_count(self) -> int:
        return len(self._order)

    def close(self) -> None:
        if self._host is not None:
            self._host.teardown()
        self._host = None
        self._codes = {}
        self._order = []

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
        return self._host

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

        # Registrations: new or code-changed cells become stale (+ downstream via set_stale).
        for cid in new_ids:
            code = new_codes[cid]
            if self._codes.get(cid) == code and cid in k.graph.cells:
                continue  # unchanged — keep its current (clean or stale) state
            k._maybe_register_cell(cid, code, stale=True)
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

        hooks = create_default_hooks()
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
        _await(runner.run_all())

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
            ran.append({"cell_id": cid, "mimetype": mimetype, "data": data, "ok": rr.success()})
        ran.sort(key=lambda r: order_of[r["cell_id"]])
        return {"ok": True, "ran": ran, "stale": self.stale(), "error": None}
