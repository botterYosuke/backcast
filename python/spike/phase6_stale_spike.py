"""Throwaway spike (#95 Phase 6 / findings 0075 P6-1): prove the marimo incremental
stale mechanism works against the REAL installed marimo before rewriting NotebookSession.

Validates the load-bearing claim of P6-1 (stale = marimo-faithful, rebuild-on-change retired):

  1. Register cells STALE without running (via Kernel._maybe_register_cell(stale=True)).
  2. A press (bare Runner, roots={pressed}, autorun) runs pressed + STALE ANCESTORS + descendants.
  3. Editing a cell re-registers it (same id, new code) and propagates stale DOWNSTREAM.
  4. Globals PERSIST across edits (no kernel rebuild) — the Phase 2 landmine dissolves.

Run: python/.venv/Scripts/python.exe python/spike/phase6_stale_spike.py
PASS prints [PHASE6-STALE PASS]; any AssertionError is a RED that re-opens the P6-1 seam choice.
"""
from __future__ import annotations

import asyncio
import sys
from typing import Any

from marimo._runtime.runner.cell_runner import Runner
from marimo._runtime.runner.hooks import create_default_hooks

from engine.strategy_runtime.thin_drain import HeadlessKernel


def _await(x: Any) -> Any:
    return asyncio.run(x) if asyncio.iscoroutine(x) else x


def _register(k: Any, cell_id: str, code: str, *, stale: bool) -> None:
    """Diff-register one cell by stable id, marking it stale (no execution)."""
    k._maybe_register_cell(cell_id, code, stale=stale)


def _run_pressed(k: Any, pressed: str) -> dict[str, Any]:
    """Bare-Runner press: roots={pressed}, autorun → pressed + stale ancestors + descendants.

    Returns {cell_id: success}. Clears stale on every cell that ran (the bare Runner does not).
    """
    recorded: dict[str, Any] = {}

    def _record(cell: Any, _ctx: Any, rr: Any) -> None:
        recorded[cell.cell_id] = rr

    hooks = create_default_hooks()
    hooks.add_post_execution(_record)
    runner = Runner(
        roots={pressed},
        graph=k.graph,
        glbls=k.globals,
        debugger=k.debugger,
        hooks=hooks,
        execution_mode="autorun",
        excluded_cells=set(k.errors),
        execution_context=k._install_execution_context,
    )
    _await(runner.run_all())
    for cid in recorded:
        k.graph.cells[cid].set_stale(stale=False, broadcast=False)
    return {cid: rr.success() for cid, rr in recorded.items()}


def main() -> None:
    host = HeadlessKernel()
    try:
        k = host.k
        # 1. Register three cells STALE without running. b depends on a; c is independent.
        _register(k, "a", "a = 1", stale=True)
        _register(k, "b", "b = a + 1", stale=True)
        _register(k, "c", "c = 10", stale=True)
        assert k.graph.get_stale() == {"a", "b", "c"}, k.graph.get_stale()
        assert "a" not in k.globals, "stale registration must NOT execute"

        # 2. Press c (independent): runs only c + clears its stale. a/b stay stale.
        ran = _run_pressed(k, "c")
        assert ran == {"c": True}, ran
        assert k.globals.get("c") == 10
        assert k.graph.get_stale() == {"a", "b"}, k.graph.get_stale()
        assert "a" not in k.globals, "pressing c must not run upstream a"

        # 3. Press b: pulls in STALE ANCESTOR a (b reads a), runs a then b.
        ran = _run_pressed(k, "b")
        assert set(ran) == {"a", "b"}, ran
        assert k.globals.get("a") == 1 and k.globals.get("b") == 2
        assert k.graph.get_stale() == set(), k.graph.get_stale()

        # 4. EDIT a (a=1 -> a=5) via re-register: same id, new code. Downstream b goes stale.
        _register(k, "a", "a = 5", stale=True)
        k.graph.set_stale({"a"}, prune_imports=True)  # propagate to descendants (b)
        assert "b" in k.graph.get_stale(), "editing a must stale its downstream b"
        # Globals PERSIST (c from the independent press is still there — no rebuild wiped it).
        assert k.globals.get("c") == 10, "globals must survive an edit (no kernel rebuild)"

        # 5. Press b again: a (edited, stale) reruns then b — b reflects a=5.
        ran = _run_pressed(k, "b")
        assert set(ran) >= {"a", "b"}, ran
        assert k.globals.get("a") == 5 and k.globals.get("b") == 6, (k.globals.get("a"), k.globals.get("b"))
        assert k.graph.get_stale() == set(), k.graph.get_stale()

        print("[PHASE6-STALE PASS] register-stale / press-runs-stale-ancestors / "
              "edit-propagates-downstream / globals-persist all hold against real marimo")
    finally:
        host.teardown()


if __name__ == "__main__":
    sys.exit(main())
