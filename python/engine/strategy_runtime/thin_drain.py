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
(S6). marimo is a prod dependency since S3 (ADR-0012), but the runtime seam must still
NOT import this module at module-load (it top-imports marimo): the seam loads it LAZILY,
only when a marimo strategy runs — so this module stays dormant until S6 wires it. The
lazy-import discipline is proven by ``tests/test_strategy_runtime_offline.py``; the runtime
behavior by ``tests/test_strategy_runtime_thin_drain.py``.
"""

from __future__ import annotations

import asyncio
import contextlib
import dataclasses
import sys
from typing import TYPE_CHECKING, Any, Iterator, Sequence

# marimo is a prod dependency since ADR-0012, so importing this module is fine — but the
# runtime seam must not import it at module-load (lazy-import discipline) — proven by
# tests/test_strategy_runtime_offline.py.
from marimo import state as _mo_state  # public mo.state factory (aliased: _compile has a local `state`)
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
from marimo._runtime.context.types import get_context
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


def _inert_action(*_args: Any, **_kwargs: Any) -> None:
    """No-op stand-in for an injected action callable during the cold compile run (S4).

    The cold run executes every cell once; injected actions (``submit_market`` / ``log``) must
    not fire then — only during the hot drain, where ``_compile`` arms the real callables.
    """
    return None


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
        # Best-effort cleanup, but the __main__ restore is non-negotiable: it runs in
        # ``finally`` and every stop is independently guarded, so one failing step can
        # neither skip the restore — a leaked patched __main__ would be captured as the
        # NEXT HeadlessKernel's self._main (cross-session corruption) — nor skip a sibling.
        try:
            with contextlib.suppress(Exception):
                teardown_context()
            for stream in (self.stdout, self.stderr):
                watcher = getattr(stream, "_watcher", None)
                if watcher is not None:
                    with contextlib.suppress(Exception):
                        watcher.stop()
            module_watcher = getattr(self.k, "module_watcher", None)
            if module_watcher is not None:
                with contextlib.suppress(Exception):
                    module_watcher.stop()
        finally:
            sys.modules["__main__"] = self._main


# ---------------------------------------------------------------------------
# CompiledStrategy (cold) + StrategyRuntime (hot)
# ---------------------------------------------------------------------------


def _execute_hot_cell(
    ctx: Any, executor: Any, cell: Any, glbls: dict[str, Any], graph: Any
) -> None:
    """The single hot-path primitive: run one cell INSIDE the kernel's execution context.

    ``with_cell_id`` installs the per-cell execution context (D6, findings 0046 redesign
    追補): with it, ``mo.output`` / ``mo.ui`` / ``mo.state`` behave exactly like a normal
    marimo cell — there is no hot/cold behavioral split for the author. The install is
    sub-microsecond (it builds one ``ExecutionContext`` and swaps an attribute) — NOT what
    made ``Kernel.run`` slow (that was the entry-point scan + graph mutation + lint + topo).

    ``StrategyRuntime.step`` and the D6 production gate route through this one function, so
    the gate can never silently drift from what the runtime does per bar — if production
    ever stopped installing the context, the footgun would return and the gate would go RED.
    """
    with ctx.with_cell_id(cell.cell_id):
        executor.execute_cell(cell, glbls, graph)


@dataclasses.dataclass(frozen=True)
class CompiledStrategy:
    """The frozen result of the cold compile: everything the hot drain needs.

    The hot path touches only ``executor`` / ``hot_cells`` / ``graph`` / ``glbls`` /
    ``setters`` — no marimo orchestration. The hot-cell ids (introspection only) are
    derived from ``hot_cells`` on demand, not stored as a second copy.
    """

    executor: Any
    graph: Any
    glbls: dict[str, Any]
    hot_cells: tuple[Any, ...]
    setters: dict[str, Any]


def _cell_clobbered_names(host_names: "dict[str, Any]", graph: Any) -> list[str]:
    """Host-seed / inject names that a cell ALSO defines — a fail-closed clobber.

    After the cold run a cell's own definition shadows the host's seeded driver State or
    armed action callable, so the host would drive/arm an orphaned binding (a dead driver or
    a silently-shadowed action). Both ``_compile`` guards reject on this set, each with a
    role-specific message.
    """
    cell_defs = {d for cell in graph.cells.values() for d in cell.defs}
    return sorted(set(host_names) & cell_defs)


def _compile(
    app: "App",
    drivers: Sequence[str],
    inject: "dict[str, Any] | None" = None,
    driver_seeds: "dict[str, Any] | None" = None,
) -> CompiledStrategy:
    """Cold precompute. Must run inside a HeadlessKernel context, on its thread.

    Runs every cell once (so the state getters/setters land in ``runner.globals`` and
    the graph is built with no stale cells), then asks marimo which cells a host-driven
    state update must re-run, in topological order.

    Two kinds of host names are seeded into the cell globals BEFORE the cold run, branched
    by role:

      ``inject`` — host ACTION callables (``submit_market`` / ``log``, S4). A cell references
        the name as a free ref — like a builtin. Seeded INERT for the cold run (so the cold
        run, which executes every cell once, does not fire a spurious order before any bar),
        then armed with the real callable for the hot drain.
      ``driver_seeds`` — host-owned driver mo.state, ``{getter_name: initial_value}`` (S6a,
        findings 0046 S6-6). The host OWNS the State (name + object): this builds a real
        ``mo.state(initial)`` here and seeds its getter into globals, so a cell reading
        ``get_bar()`` as a free ref is rooted by identity (``_find_cells_for_state``) and the
        host writes it each bar via ``drain``. This is the canonical authoring style — the
        author writes no ``get_bar, set_bar = mo.state(...)`` boilerplate. (``drivers`` is the
        older author-defined style, where the cell itself defines the mo.state; both resolve
        through the same setter recovery below.)
    """
    runner = app._get_kernel_runner()
    if inject:
        # Seed INERT stubs before the cold run so cells referencing these names resolve, but
        # action side effects DO NOT fire: the cold run executes every cell once (to populate
        # globals + build the graph), so a live ``submit_market`` would place a spurious order
        # before any bar. The real callables are armed only after the cold run, for the hot drain.
        runner.globals.update({name: _inert_action for name in inject})
    if driver_seeds:
        # Build host-owned driver States and seed their getters so free-ref reads resolve in
        # the cold run AND the hot drain. state() registers in the active kernel context
        # (we are inside a HeadlessKernel), so this must run in-context, on its thread.
        for name, initial in driver_seeds.items():
            getter, _setter = _mo_state(initial)
            runner.globals[name] = getter
    cells = list(app._cell_manager.valid_cells())
    # A stale-free graph + populated globals is the precondition compute_cells_to_run assumes.
    asyncio.run(runner.run({cid for cid, _ in cells}))

    kernel = runner._kernel
    graph = kernel.graph
    glbls = runner.globals
    if driver_seeds:
        # Fail-closed (symmetric with inject below): a cell that also DEFINES a host-seeded driver
        # name shadows the seeded State after the cold run, so the host's per-bar write would drive
        # an orphaned State (a dead driver that looks live). Reject — the host owns the canonical
        # driver names; the cell must read the driver as a free ref, not redefine it.
        seed_clobbered = _cell_clobbered_names(driver_seeds, graph)
        if seed_clobbered:
            raise ValueError(
                f"host-seeded driver name(s) {seed_clobbered} are also defined by a cell — the cold "
                "run shadows the host-seeded State, so the host's per-bar write would drive an "
                "orphaned State. Read the driver as a free ref (the host owns the driver names)."
            )
    if inject:
        # Fail-closed: an injected name a cell also defines would be silently clobbered when we
        # arm — shadowing the author's value (corrupting downstream cells) or firing an action
        # where a pure helper was meant. Reject instead of clobbering.
        clobbered = _cell_clobbered_names(inject, graph)
        if clobbered:
            raise ValueError(
                f"injected name(s) {clobbered} are also defined by a cell — arming would "
                "silently shadow the cell's definition with the injected callable. Rename the "
                "cell variable or the injected name (injected names are the host action API)."
            )
        # Arm the real callables now that the cold run is done: the hot drain (same glbls)
        # will call them per bar.
        glbls.update(inject)

    # mo.state()'s getter IS the State object (State.__call__ returns the value), so
    # globals[name] is the State and State._set_value is its setter. roots are the cells
    # that read a host-driven state — found by calling marimo's own function, never
    # mirroring it (it folds in stale-ancestor union / import-block relatives / override
    # pruning that an approximation would drop).
    setters: dict[str, Any] = {}
    roots: set = set()
    for name in (*drivers, *(driver_seeds or {})):
        if name not in glbls:
            raise KeyError(f"driver getter {name!r} not found in strategy globals")
        state = glbls[name]
        if not hasattr(state, "_set_value"):
            raise TypeError(
                f"driver getter {name!r} is not an mo.state getter "
                f"(got {type(state).__name__}); declare only host-driven mo.state getters"
            )
        setters[name] = state._set_value
        cells_for_state = kernel._find_cells_for_state(state, _EXTERNAL)
        # Fail-closed: a driver no cell reads makes the hot list silently shrink, so every
        # bar's write to it is a no-op (a dead strategy that looks live). Reject at compile.
        if not cells_for_state:
            raise ValueError(
                f"driver {name!r} has no reader cell — every bar's write to it would be a "
                "silent no-op. A declared driver must be read by at least one cell "
                "(D5: roots = exactly the states the host writes between bars)."
            )
        roots |= cells_for_state

    hot_ids = Runner.compute_cells_to_run(graph, roots, set(), "autorun")
    # Built once so the per-bar drain never re-pays marimo's entry-point scan.
    executor = get_executor(ExecutionConfig())

    return CompiledStrategy(
        executor=executor,
        graph=graph,
        glbls=glbls,
        hot_cells=tuple(graph.cells[cid] for cid in hot_ids),
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
        # Derived from hot_cells (each CellImpl carries its own cell_id) — not stored twice.
        return tuple(str(cell.cell_id) for cell in self._c.hot_cells)

    def step(self) -> None:
        """Execute the precomputed hot cells once, in topological order.

        Each cell runs inside the kernel's execution context (D6) so every cell behaves
        like a normal marimo cell. The context is bound once per step (the install is the
        same context object across cells) — never per cell — keeping the drain native-speed.
        """
        c = self._c
        executor, glbls, graph = c.executor, c.glbls, c.graph
        ctx = get_context()
        for cell in c.hot_cells:
            _execute_hot_cell(ctx, executor, cell, glbls, graph)

    def drain(self, values: dict[str, Any]) -> None:
        """One bar: set every declared driver from ``values``, then step the cell-DAG."""
        setters = self._c.setters
        for name, value in values.items():
            setters[name](value)
        self.step()


@contextlib.contextmanager
def open_runtime(
    app: "App",
    *,
    drivers: Sequence[str] = (),
    inject: "dict[str, Any] | None" = None,
    driver_seeds: "dict[str, Any] | None" = None,
) -> Iterator[StrategyRuntime]:
    """Open a thin-drain runtime for ``app``, owning the headless kernel's lifetime.

    Usage (S6a host-seed style — the host owns the driver name + object)::

        rt_inject = {"submit_market": make_submit_market(ctx, ...)}
        with open_runtime(app, driver_seeds={"get_bar": neutral_bar}, inject=rt_inject) as rt:
            for bar in bars:
                rt.drain({"get_bar": bar})
                signal = rt.globals["signal"]

    ``driver_seeds`` (``{getter_name: initial_value}``) are host-owned driver mo.state the
    runtime builds and seeds — the canonical authoring style, so the cell reads ``get_bar()``
    as a free ref with no ``mo.state`` boilerplate. ``drivers`` is the older author-defined
    style (the cell itself does ``get_bar, set_bar = mo.state(...)``). Either way these are
    exactly the states the host writes between bars (never auto-detect: a cell-written feedback
    state must not become a root — D5). ``inject`` seeds host-provided names (the cell-facing
    ACTION API — ``submit_market`` / ``log``) into the cell globals so per-bar cells can call
    them (S4); host-written VALUES are driver mo.state, not injected. The single try/finally
    guarantees the thread-local marimo context is torn down even when the compile itself raises.
    """
    host = HeadlessKernel()
    try:
        runtime = StrategyRuntime(_compile(app, drivers, inject, driver_seeds))
        yield runtime
    finally:
        host.teardown()
