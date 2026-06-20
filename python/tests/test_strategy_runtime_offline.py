"""Lazy-import discipline guard for the runtime seam (#76 / findings 0046 / ADR-0012).

Since S3 (ADR-0012) marimo is a PROD dependency — it is the reactive strategy execution
model. But the runtime seam must still NOT pull marimo at module-load time: a top-level
``import marimo`` costs ~500ms and drags a heavy chain a headless strategy runtime never
needs (``_ai.llm`` / ``_plugins.ui.chat`` / altair / the ``_server`` submodules). marimo
is loaded LAZILY, only when a marimo strategy is actually run (ADR-0012 Decision 4). The
non-marimo paths (imperative strategies, marimo-free Replay) keep their orphan-free,
light-startup property.

This gate imports the whole production runtime seam in a clean interpreter and asserts no
``marimo`` module leaked into ``sys.modules`` — even though marimo IS now installed. The
mechanism is unchanged from the old dormancy guard; the INTENT was promoted from "marimo
is spike-only, the seam is dormant" to "marimo is a prod dep, the seam imports it only
lazily". Because the gate imports ``engine.kernel.runner`` (the S6 wiring target), placing
``import marimo`` / ``import …thin_drain`` at the seam's module top would turn this RED —
so leaving the gate in place STRUCTURALLY FORCES S6 to lazy-import (in the same narrow-
submodule style thin_drain uses, never a bare top-level ``import marimo``).

Runnable directly (``python tests/test_strategy_runtime_offline.py``) or via pytest.
"""

from __future__ import annotations

import sys

# Clean-interpreter child: import the production runtime seam (Replay + Live entry points)
# and report any marimo module that leaked in. marimo is now installed (prod dep, ADR-0012),
# so a leak means the seam imported it at MODULE LOAD instead of lazily — if anything on this
# seam top-imported marimo (or the thin-drain module, which top-imports marimo), it surfaces.
_CHILD = r"""
import sys

import engine.kernel.runner                            # noqa: F401  per-bar Replay loop (S6 target)
import engine.kernel.stepper                            # noqa: F401  #95 Phase 3 per-bar state machine
import engine.strategy_runtime.backtester               # noqa: F401  #95 Phase 3 bt handle (must be marimo-free)
import engine._backend_impl                            # noqa: F401  InProc entry (Replay + Live)
import engine.strategy_runtime.replay_kernel_observer  # noqa: F401  strategy_runtime package sibling
import engine.strategy_runtime.cell_api                # noqa: F401  S4 cell-facing adapter (S6 imports it; must be marimo-free)
import engine.strategy_runtime.strategy_kind          # noqa: F401  S6a AST detector (dispatch reads it; must be marimo-free)

# marimo (ADR-0012 lazy) AND the v19 ML deps (joblib + sklearn load only on the first score call
# inside the cell's score_v19_rows closure, never at module import) must all be absent after
# importing the seam.
_HEAVY = ("marimo", "joblib", "sklearn")
leaked = sorted(m for m in sys.modules if any(m == h or m.startswith(h + ".") for h in _HEAVY))
if leaked:
    print("LEAKED:" + ",".join(leaked))
    sys.exit(1)
print("MARIMO-FREE")
sys.exit(0)
"""


def _run_child():
    from spike.kernel_golden.subprocess_util import run_python

    return run_python(["-c", _CHILD], timeout=120)


def test_runtime_seam_does_not_import_marimo() -> None:
    result = _run_child()
    assert result.returncode == 0, (
        "the production runtime seam imported marimo at MODULE LOAD — marimo is a prod dep "
        "since ADR-0012, but the seam must lazy-import it (only when a marimo strategy runs); "
        "S6 must lazy-import in thin_drain's narrow-submodule style, never a top-level "
        "`import marimo` / `import …thin_drain` on the seam.\n"
        f"stdout={result.stdout!r}\nstderr={result.stderr!r}"
    )
    assert "MARIMO-FREE" in result.stdout


if __name__ == "__main__":
    res = _run_child()
    if res.returncode != 0:
        print(f"[STRATEGY-RUNTIME OFFLINE FAIL] {res.stdout.strip()} {res.stderr.strip()}")
        sys.exit(1)
    print("[STRATEGY-RUNTIME OFFLINE PASS] runtime seam imports with no marimo dependency")
