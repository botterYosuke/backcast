"""Headless in-proc marimo KernelRuntimeContext stand-up (#76 spike, shared).

This is the load-bearing recipe the whole #76 spike rests on: bringing a marimo
``KernelRuntimeContext`` up *in-process, headless* (no ASGI server, no websocket,
no browser) so that ``running_in_notebook()`` returns True and therefore
``App.embed()`` takes its **AppKernelRunner reactive branch** (app.py:875) rather
than the run-once ``else`` (app.py:921). That reactive branch is the per-bar
stale-drain machinery AC2/AC3 depend on.

The recipe is marimo's own test fixture ``MockedKernel`` (tests/conftest.py),
reproduced here MINUS the one ``from marimo._server.utils import
initialize_mimetypes`` import the fixture does at module top. We drop it on
purpose: the spike must not *add* a ``marimo._server`` import of its own (see the
F1 adjudication in docs/spike/marimo-embed-result.md). Note that several
``marimo._server.*`` modules are import-resident ANYWAY — pulled in transitively
by ``marimo._ast.app`` via ``marimo._ai._pydantic_ai_utils`` — which is exactly
the finding spike-0 records (and why strict module-purity was replaced by the two
real invariants: ADR-0001 orphan-free + Mono native-teardown-absence).

Everything here runs under plain CPython. The Mono verdict is a separate, minimal
smoke (AC3), per the S0/S2 CPython-smoke + Mono-probe split.
"""

from __future__ import annotations

import dataclasses
from typing import TYPE_CHECKING

from marimo._ast.app_config import _AppConfig
from marimo._config.config import DEFAULT_CONFIG
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
from marimo._runtime.input_override import input_override
from marimo._runtime.marimo_pdb import MarimoPdb
from marimo._messaging.print_override import print_override
from marimo._runtime.runner.hooks import create_default_hooks
from marimo._runtime.runtime import Kernel
from marimo._session.model import SessionMode

if TYPE_CHECKING:
    from marimo._messaging.types import KernelMessage


@dataclasses.dataclass
class _SilentStream(ThreadSafeStream):
    """Captures kernel ops without a real pipe/queue.

    Mirrors conftest._MockStream: a dataclass subclass overrides ThreadSafeStream's
    __init__, so the base's queue/pipe machinery is never constructed and ``write``
    only touches ``self.messages``.
    """

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

    Construct on the SAME thread that will later call ``await app.embed()`` (the
    context is thread-local). Call ``teardown()`` when done.
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
# Real-invariant assertions (replacing the strict module-purity proxy).
# ADR-0001 orphan-free is the contract: same-process / same-lifetime, no spawned
# child process, no IPC server. Mono crash-safety is the OTHER real invariant, but
# it is owned by the AC3 Mono smoke (native-teardown surface), not asserted here.
# ---------------------------------------------------------------------------


def assert_orphan_free() -> dict:
    """Assert ADR-0001 orphan-free holds RIGHT NOW. Returns evidence dict.

    Raises AssertionError on violation (no spawned children, no LISTEN socket
    owned by this process).
    """
    import multiprocessing

    children = multiprocessing.active_children()
    assert children == [], f"orphan-free violated: spawned children {children!r}"

    listen_addrs: list[str] = []
    try:
        import psutil

        proc = psutil.Process()
        for c in proc.net_connections(kind="inet"):
            if c.status == psutil.CONN_LISTEN:
                listen_addrs.append(f"{c.laddr.ip}:{c.laddr.port}")
    except Exception as exc:  # psutil optional / platform quirks
        listen_addrs = [f"<psutil unavailable: {exc}>"]

    real_listen = [a for a in listen_addrs if not a.startswith("<")]
    assert not real_listen, f"orphan-free violated: listening sockets {real_listen!r}"

    return {
        "active_children": len(children),
        "listening_sockets": listen_addrs,
    }


async def init_strategy_runner(app):  # noqa: ANN001, ANN201
    """Bring an embed app up and return (runner, strategy_cell_id).

    Shared by every AC leg: grabs the AppKernelRunner, runs ALL cells once (so the
    state setters land in ``runner.globals``), and locates the cell that defines
    ``result`` — scanning ``valid_cells()`` exactly once. Callers then read whatever
    setters they need from ``runner.globals`` and drain ``{strat_cid}`` per bar.
    """
    runner = app._get_kernel_runner()
    cells = list(app._cell_manager.valid_cells())
    await runner.run({cid for cid, _ in cells})
    strat_cid = next(cid for cid, cell in cells if "result" in cell._cell.defs)
    return runner, strat_cid


def resident_server_modules() -> list[str]:
    """The ``marimo._server.*`` modules currently import-resident in sys.modules.

    Recorded (not failed on): they are pulled in transitively by importing
    ``marimo._ast.app`` and are pure-Python / never-run / zero-teardown — so they
    do NOT carry the nautilus_pyo3 native-teardown crash risk the proxy guarded.
    """
    import sys

    return sorted(m for m in sys.modules if m.startswith("marimo._server"))
