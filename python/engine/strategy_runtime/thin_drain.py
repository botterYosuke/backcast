"""Host-owned thin-drain strategy runtime (#76 S1 / findings 0046).

The #76 spike proved that a marimo reactive cell-DAG can drive a strategy, but the
documented ``AppKernelRunner.run`` per-bar drain costs ~4.5ms/bar (100% marimo
``Kernel.run`` orchestration: entry-point disk scan + graph mutation + lint +
topo-sort + execution-context install — none of which a STATIC cell-DAG needs per
bar). The redesign grill (findings 0046, D1–D5) settled the runtime that removes
that cost:

  COLD (once)  ``CompiledStrategy.compile`` stands up a headless marimo kernel, runs
               every cell once, then precomputes — by CALLING marimo's own
               ``_find_cells_for_state`` + ``Runner.compute_cells_to_run`` (never
               mirroring them) — the topo-ordered list of cells a host-driven state
               update must re-run. The executor is built once.
  HOT (per bar) ``StrategyRuntime.drain`` writes the host-driven states and calls
               ``executor.execute_cell(cell, glbls, graph)`` (= ``exec(body);
               eval(last_expr)``) over that frozen list. No marimo orchestration.
               Measured at native speed (~0.6–3µs/bar, ~0.04–0.18s/50k).

Design decisions this module realizes (findings 0046):
  D1  bar-internal intermediates flow as cell return variables (static def→ref
      edges); bar-crossing feedback + host inputs are ``mo.state``.
  D2  the dirty-set/topo is computed by calling marimo's functions, not by hand.
  D5  the host DECLARES which states are driver (root) states — auto-detecting all
      ``mo.state`` would pull cell-written feedback into the roots and break the
      contract. Drivers are declared here by getter name; the setter is recovered
      from the state object (``State._set_value``).

Scope (S1+S2): the host-owned per-bar primitive and the static precompute. NOT in
scope (later slices): the structural fail-closed guard (D3, S3), injected cell-facing
globals (S4), cold↔hot live-edit transition (S5), and wiring into ``KernelRunner``
(S6). marimo is a spike-only dependency until the S3 ADR promotes it, so NOTHING on
the production runtime import path imports this module — it is dormant and exercised
only by the spike-group gates (see ``tests/test_strategy_runtime_thin_drain.py`` and
``tests/test_strategy_runtime_offline.py``).
"""

from __future__ import annotations

import contextlib
import dataclasses
from typing import TYPE_CHECKING, Any, Iterator, Sequence

# marimo is spike-only (not in [project.dependencies]); importing this module
# therefore requires the spike group. The runtime seam never imports it — proven by
# tests/test_strategy_runtime_offline.py.
from marimo._ast.app_config import _AppConfig
from marimo._config.config import DEFAULT_CONFIG
from marimo._messaging.print_override import print_override
from marimo._messaging.streams import (
    ThreadSafeStderr,
    ThreadSafeStdin,
    ThreadSafeStdout,
    ThreadSafeStream,
)
from marimo._runtime import patches
from marimo._runtime.commands import AppMetadata
from marimo._runtime.context import teardown_context
from marimo._runtime.context.kernel_context import initialize_kernel_context
from marimo._runtime.executor import ExecutionConfig, get_executor
from marimo._runtime.input_override import input_override
from marimo._runtime.marimo_pdb import MarimoPdb
from marimo._runtime.runner.cell_runner import Runner
from marimo._runtime.runner.hooks import create_default_hooks
from marimo._runtime.runtime import Kernel
from marimo._session.model import SessionMode

if TYPE_CHECKING:
    from marimo._ast.app import App
    from marimo._messaging.types import KernelMessage

# The host writes driver states from outside any cell, so the setter-cell id never
# matches a real cell — this is the sentinel marimo itself uses for external setter
# calls (runtime.py register_state_update), so self-loop pruning is skipped and every
# cell referencing the state is returned.
_EXTERNAL = "__external__"


# ---------------------------------------------------------------------------
# Headless marimo kernel stand-up (graduated from the #76 spike context_standup).
#
# Brings a marimo KernelRuntimeContext up in-process, headless (no ASGI server, no
# websocket, no browser). It is marimo's own MockedKernel test recipe MINUS the
# ``from marimo._server.utils import initialize_mimetypes`` import the fixture does at
# module top — dropped on purpose so we do not ADD a ``marimo._server`` import (the F1
# adjudication: strict module-purity was replaced by the two real invariants — ADR-0001
# orphan-free + Mono native-teardown-absence). The context is thread-local: construct
# on the SAME thread that compiles and drains.
# ---------------------------------------------------------------------------


@dataclasses.dataclass
class _SilentStream(ThreadSafeStream):
    """Captures kernel ops without a real pipe/queue (mirrors conftest._MockStream)."""

    cell_id: "int | None" = None
    input_queue: None = None
    pipe: None = None
    redirect_console: bool = False
    messages: "list[KernelMessage]" = dataclasses.field(default_factory=list)

    def write(self, data: "KernelMessage") -> None:
        self.messages.append(data)


class _SilentStdout(ThreadSafeStdout):
    def __init__(self, stream: _SilentStream) -> None:
        super().__init__(stream)
        self.messages: list[str] = []

    def _write_with_mimetype(self, data: str, mimetype) -> int:  # noqa: ANN001
        del mimetype
        self.messages.append(data)
        return len(data)


class _SilentStderr(ThreadSafeStderr):
    def __init__(self, stream: _SilentStream) -> None:
        super().__init__(stream)
        self.messages: list[str] = []

    def _write_with_mimetype(self, data: str, mimetype) -> int:  # noqa: ANN001
        del mimetype
        self.messages.append(data)
        return len(data)


class _SilentStdin(ThreadSafeStdin):
    def __init__(self, stream: _SilentStream) -> None:
        super().__init__(stream)

    def _readline_with_prompt(self, prompt: str = "", password: bool = False) -> str:
        del password
        return prompt


@dataclasses.dataclass
class HeadlessKernel:
    """A live marimo Kernel + initialized thread-local RuntimeContext, headless.

    Construct on the SAME thread that will later compile and drain. Call ``teardown()``
    when done (the runtime is a context manager via ``CompiledStrategy.session``).
    """

    session_mode: SessionMode = SessionMode.EDIT

    def __post_init__(self) -> None:
        import sys

        self.stream = _SilentStream()
        self.stdout = _SilentStdout(self.stream)
        self.stderr = _SilentStderr(self.stream)
        self.stdin = _SilentStdin(self.stream)
        self._main = sys.modules["__main__"]
        module = patches.patch_main_module(
            file=None,
            input_override=input_override,
            print_override=print_override,
        )
        self.k = Kernel(
            stream=self.stream,
            stdout=self.stdout,
            stderr=self.stderr,
            stdin=self.stdin,
            cell_configs={},
            user_config=DEFAULT_CONFIG,
            app_metadata=AppMetadata(
                query_params={},
                filename=None,
                cli_args={},
                argv=None,
                app_config=_AppConfig(),
            ),
            debugger_override=MarimoPdb(stdout=self.stdout, stdin=self.stdin),
            enqueue_control_request=lambda _: None,
            module=module,
            hooks=create_default_hooks(),
        )
        initialize_kernel_context(
            kernel=self.k,
            stream=self.stream,  # type: ignore[arg-type]
            stdout=self.stdout,  # type: ignore[arg-type]
            stderr=self.stderr,  # type: ignore[arg-type]
            virtual_files_supported=True,
            mode=self.session_mode,
        )

    def teardown(self) -> None:
        import sys

        teardown_context()
        try:
            self.stdout._watcher.stop()
            self.stderr._watcher.stop()
        except Exception:
            pass
        if getattr(self.k, "module_watcher", None) is not None:
            self.k.module_watcher.stop()
        sys.modules["__main__"] = self._main


# ---------------------------------------------------------------------------
# CompiledStrategy (cold) + StrategyRuntime (hot)
# ---------------------------------------------------------------------------


@dataclasses.dataclass(frozen=True)
class CompiledStrategy:
    """The frozen result of the cold compile: everything the hot drain needs.

    The hot path touches only ``executor`` / ``hot_cells`` / ``graph`` / ``glbls`` /
    ``setters`` — no marimo orchestration. ``hot_cell_ids`` mirrors ``hot_cells`` for
    introspection (the public ids accessor).
    """

    executor: Any
    graph: Any
    glbls: dict[str, Any]
    hot_cells: tuple[Any, ...]
    hot_cell_ids: tuple[str, ...]
    setters: dict[str, Any]


def _compile(app: "App", drivers: Sequence[str]) -> CompiledStrategy:
    """Cold precompute. Must run inside a HeadlessKernel context, on its thread.

    Runs every cell once (so the state getters/setters land in ``runner.globals`` and
    the graph is built with no stale cells), then asks marimo which cells a host-driven
    state update must re-run, in topological order.
    """
    import asyncio

    runner = app._get_kernel_runner()
    cells = list(app._cell_manager.valid_cells())
    # A stale-free graph + populated globals is the precondition compute_cells_to_run assumes.
    asyncio.run(runner.run({cid for cid, _ in cells}))

    kernel = runner._kernel
    graph = kernel.graph
    glbls = runner.globals

    # mo.state()'s getter IS the State object (State.__call__ returns the value), so
    # globals[name] is the State and State._set_value is its setter. roots are the cells
    # that read a host-driven state — found by calling marimo's own function, never
    # mirroring it (it folds in stale-ancestor union / import-block relatives / override
    # pruning that an approximation would drop).
    setters: dict[str, Any] = {}
    roots: set = set()
    for name in drivers:
        if name not in glbls:
            raise KeyError(f"driver getter {name!r} not found in strategy globals")
        state = glbls[name]
        if not hasattr(state, "_set_value"):
            raise TypeError(
                f"driver getter {name!r} is not an mo.state getter "
                f"(got {type(state).__name__}); declare only host-driven mo.state getters"
            )
        setters[name] = state._set_value
        roots |= kernel._find_cells_for_state(state, _EXTERNAL)

    hot_ids = Runner.compute_cells_to_run(graph, roots, set(), "autorun")
    # Built once so the per-bar drain never re-pays marimo's entry-point scan.
    executor = get_executor(ExecutionConfig())

    return CompiledStrategy(
        executor=executor,
        graph=graph,
        glbls=glbls,
        hot_cells=tuple(graph.cells[cid] for cid in hot_ids),
        hot_cell_ids=tuple(str(cid) for cid in hot_ids),
        setters=setters,
    )


class StrategyRuntime:
    """Host-owned thin drain over a CompiledStrategy.

    Per bar: write the host-driven states, then execute the precomputed hot cells in
    topological order. The static list runs unconditionally every bar — the runtime does
    not consume marimo's stale-set, which is what keeps it orchestration-free. Structural
    fail-closed validation (rejecting an out-of-list cell firing) is a later slice.
    """

    def __init__(self, compiled: CompiledStrategy) -> None:
        self._c = compiled

    @property
    def globals(self) -> dict[str, Any]:
        return self._c.glbls

    @property
    def hot_cell_ids(self) -> tuple[str, ...]:
        return self._c.hot_cell_ids

    def set_driver(self, getter_name: str, value: Any) -> None:
        """Write a host-driven state (e.g. the current bar) via its mo.state setter."""
        self._c.setters[getter_name](value)

    def step(self) -> None:
        """Execute the precomputed hot cells once, in topological order."""
        c = self._c
        for cell in c.hot_cells:
            c.executor.execute_cell(cell, c.glbls, c.graph)

    def drain(self, values: dict[str, Any]) -> None:
        """One bar: set every declared driver from ``values``, then step the cell-DAG."""
        for name, value in values.items():
            self._c.setters[name](value)
        self.step()


@contextlib.contextmanager
def open_runtime(app: "App", *, drivers: Sequence[str]) -> Iterator[StrategyRuntime]:
    """Open a thin-drain runtime for ``app``, owning the headless kernel's lifetime.

    Usage::

        with open_runtime(app, drivers=["get_bar"]) as rt:
            for bar in bars:
                rt.drain({"get_bar": bar.close})
                signal = rt.globals["signal"]

    ``drivers`` are the getter names of the host-driven (root) mo.state — exactly the
    states the host writes between bars (never auto-detect: a cell-written feedback state
    must not become a root). The single try/finally guarantees the thread-local marimo
    context is torn down even when the compile itself raises.
    """
    host = HeadlessKernel()
    try:
        runtime = StrategyRuntime(_compile(app, drivers))
        yield runtime
    finally:
        host.teardown()
