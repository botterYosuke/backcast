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
import inspect
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
