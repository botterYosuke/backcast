"""engine.strategy_runtime.live_cell_runtime — materialize a marimo cell as a live run (#112 S3).

ADR-0025 D4: when the editor runs/materializes a strategy for Auto, the file MUST be a marimo
notebook. ``build_live_marimo_loader`` returns the loader the orchestrator injects into the
``StrategyRegistry`` (validation) and ``LiveStrategyHost`` (materialize) — replacing the kernel
imperative loader (``strategy_loader.load(base_cls=Strategy)``) on the editor live path. It:

  * builds the marimo App (``load_app`` — the SAME ``app is None`` test the Replay path uses at
    ``_backend_impl._select_replay_strategy``) and, if that fails, raises ``StrategyLoadError``
    with ``error_code = NOT_A_MARIMO_NOTEBOOK`` (no imperative dispatch — D4's "唯一の道");
  * returns ``(app, scenario, bridge_factory)`` where ``bridge_factory(instrument_id=..., **params)``
    builds a ``LiveCellBridge`` (S2) whose ``cell_runner`` drives the marimo cell DAG on the
    worker thread with the live ``bt`` injected.

The cell runs via the SAME per-cell-RUN machinery as Replay (``IncrementalNotebookSession``):
the cell's ``for bar in bt.replay()`` loop runs ONCE and streams forever (D1 imperative model —
NOT the reactive ``MarimoStrategy``/``thin_drain`` drain, which ADR-0025 D1 rejected). A fresh
session is built ON the worker thread, so marimo's thread-local ``RuntimeContext`` (#102) lives
on the thread that drives the loop.

The imperative ``strategy_loader.load`` stays for pytest / #24 golden / the parity oracle's
DIRECT callers (they pass ``base_cls=KernelStrategy`` and never touch this editor loader).

Marimo is imported lazily (inside the loader / cell_runner) so importing this module is cheap and
pulls no marimo at package-import time.
"""
from __future__ import annotations

import ast
import logging
from pathlib import Path
from typing import Any, Callable, Optional

from engine.kernel.live.cell_bridge import LiveCellBridge
from engine.strategy_runtime.strategy_loader import StrategyLoadError

log = logging.getLogger(__name__)

# ADR-0025 D4 dedicated error code → UI 文言「marimo notebook ではありません」.
NOT_A_MARIMO_NOTEBOOK = "NOT_A_MARIMO_NOTEBOOK"


def _drives_replay(body: str) -> bool:
    """True iff the cell body calls ``bt.replay(...)`` (AST — a literal/comment match doesn't count;
    mirrors ``_backend_impl.run_cell``'s ``_drives``)."""
    try:
        tree = ast.parse(body)
    except SyntaxError:
        return False
    return any(
        isinstance(n, ast.Attribute)
        and n.attr == "replay"
        and isinstance(n.value, ast.Name)
        and n.value.id == "bt"
        for n in ast.walk(tree)
    )


def _make_cell_runner(app: Any, original_path: Path) -> Callable[[Any], None]:
    """Build the worker-thread cell runner: inject the live ``bt`` and drive the cell DAG once.

    Reuses ``IncrementalNotebookSession.run_pressed`` — the tested per-cell-RUN seam — so the live
    cell runs byte-for-byte the way the Replay per-cell RUN does, only with a live ``bt``.
    """

    def cell_runner(bt: Any) -> None:
        # Lazy — keeps the module marimo-free until a live cell actually runs (on the worker thread).
        from engine.strategy_runtime.notebook_session import IncrementalNotebookSession

        bodies = list(app._cell_manager.codes())
        cells = [{"cell_id": f"c{i}", "code": b} for i, b in enumerate(bodies)]
        pressed_index = next((i for i, b in enumerate(bodies) if _drives_replay(b)), None)
        if pressed_index is None:
            raise StrategyLoadError(
                f"no bt.replay() cell to drive live in {original_path}",
                error_code=NOT_A_MARIMO_NOTEBOOK,
            )
        pressed_id = f"c{pressed_index}"

        # A fresh session ON THIS (worker) thread → its HeadlessKernel + thread-local
        # RuntimeContext are built and driven on the same thread (#102 / findings 0080). It MUST be
        # closed on this same thread when the loop ends, or the per-thread RuntimeContext leaks and
        # makes later marimo tests flaky ("RuntimeContext already initialized" — findings 0080).
        session = IncrementalNotebookSession()
        inject = {"bt": bt, "__file__": str(original_path)}
        try:
            result = session.run_pressed(cells, pressed_id, inject=inject)
        finally:
            session.close()  # teardown_context() on the worker thread (no context leak)

        # Fail-loud (nautilus "on_start propagates"): a compile/registration failure, or the driving
        # cell not running / raising, must surface as a worker error (→ on_start → attach failure),
        # NOT a silently-idle run. A clean stop ends the loop via the sentinel = StopIteration, which
        # the cell's ``for`` swallows, so the pressed cell ran ok.
        if not result.get("ok", False):
            raise StrategyLoadError(
                f"live cell run failed: {result.get('error')}",
                error_code=NOT_A_MARIMO_NOTEBOOK,
            )
        ran = {row["cell_id"]: row for row in result.get("ran", [])}
        pressed_row = ran.get(pressed_id)
        if pressed_row is None:
            raise StrategyLoadError(
                f"live driving cell {pressed_id} did not run (upstream cell error?): "
                f"{result.get('error')}",
                error_code=NOT_A_MARIMO_NOTEBOOK,
            )
        if not pressed_row.get("ok", True):
            # The error text lives in the cell's console (stderr segments) / data — NOT an "output"
            # key (IncrementalNotebookSession ran-rows are {cell_id, mimetype, data, console, ok}).
            detail = pressed_row.get("console") or pressed_row.get("data") or result.get("error")
            raise RuntimeError(f"live cell {pressed_id} raised: {detail}")

    return cell_runner


def _make_bridge_factory(app: Any, original_path: Path) -> Callable[..., LiveCellBridge]:
    def bridge_factory(*, instrument_id: str = "", **_params: Any) -> LiveCellBridge:
        # controller.attach calls strategy_cls(instrument_id=..., **params) then sets .id; the cell
        # gets its instruments/params from the cell body + scenario, so extra kwargs are ignored.
        return LiveCellBridge(cell_runner=_make_cell_runner(app, original_path))

    bridge_factory.__name__ = "LiveCellBridgeFactory"
    return bridge_factory


def build_live_marimo_loader() -> Callable[..., tuple]:
    """Return the editor live loader: ``(path, *, original_path=None, base_cls=None) ->
    (app, scenario, bridge_factory)``. Non-marimo (``load_app`` → None) raises
    ``NOT_A_MARIMO_NOTEBOOK``; broken syntax propagates as ``SyntaxError`` (D4)."""

    def load_live(
        path: "str | Path",
        *,
        original_path: Optional[Path] = None,
        base_cls: Any = None,  # accepted for loader-signature parity; marimo has no subclass
    ) -> tuple:
        p = Path(path)
        if not p.exists():
            raise FileNotFoundError(f"strategy file not found: {p}")

        from marimo._ast.load import load_app  # lazy: marimo only on the live materialize path

        # D4 "唯一の道": a non-marimo file is NOT a notebook. Across marimo versions that surfaces as
        # ``load_app`` returning None (empty/comment-only) OR raising ``NonMarimoPythonScriptError``
        # (a plain .py script) OR ``MarimoFileError`` (a file that doesn't define a valid marimo app);
        # all mean the same → NOT_A_MARIMO_NOTEBOOK. Broken SYNTAX (``SyntaxError``) is a DIFFERENT
        # failure and propagates raw (not caught here).
        _not_marimo: tuple = ()
        for _mod, _name in (
            ("marimo._ast.parse", "NonMarimoPythonScriptError"),
            ("marimo._ast.errors", "MarimoFileError"),
        ):
            try:
                _exc = getattr(__import__(_mod, fromlist=[_name]), _name, None)
            except Exception:  # noqa: BLE001 — this marimo version lacks that exception
                _exc = None
            if isinstance(_exc, type) and issubclass(_exc, BaseException):
                _not_marimo += (_exc,)
        try:
            app = load_app(str(p))
        except _not_marimo as exc:  # type: ignore[misc]  # empty tuple → catches nothing
            raise StrategyLoadError(
                f"not a marimo notebook: {p}", error_code=NOT_A_MARIMO_NOTEBOOK
            ) from exc
        if app is None:
            raise StrategyLoadError(
                f"not a marimo notebook: {p}", error_code=NOT_A_MARIMO_NOTEBOOK
            )

        from engine.strategy_runtime.scenario import load_scenario

        scenario = load_scenario(p)
        anchor = original_path if original_path is not None else p
        return app, scenario, _make_bridge_factory(app, anchor)

    return load_live
