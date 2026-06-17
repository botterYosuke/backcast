"""S1 "not wired" guard (#76 / findings 0046).

S1 lands the host-owned thin-drain runtime (``engine.strategy_runtime.thin_drain``) as a
standalone, dormant module. marimo is a SPIKE-ONLY dependency until the S3 ADR promotes
it, so the production runtime import path must NOT pull marimo (nor the thin-drain module)
into the interpreter — wiring the per-bar loop onto this runtime is S6, after promotion.

This gate imports the whole production runtime seam (the same set the nautilus import-purity
gate uses) in a clean interpreter and asserts no ``marimo`` module leaked — even though
marimo IS importable in the spike venv. It therefore runs in BOTH the default and the spike
test runs (no marimo import-skip): the invariant is "the runtime does not depend on marimo",
which holds regardless of whether marimo happens to be installed.

Runnable directly (``python tests/test_strategy_runtime_offline.py``) or via pytest.
"""

from __future__ import annotations

import sys

# Clean-interpreter child: import the production runtime seam (Replay + Live entry points)
# and report any marimo module that leaked in. The thin-drain module is NOT imported here —
# if anything on this seam imported it, marimo would surface, failing the gate.
_CHILD = r"""
import sys

import engine.kernel.runner                            # noqa: F401  per-bar Replay loop (S6 target)
import engine._backend_impl                            # noqa: F401  InProc entry (Replay + Live)
import engine.strategy_runtime.replay_kernel_observer  # noqa: F401  strategy_runtime package sibling

leaked = sorted(m for m in sys.modules if m == "marimo" or m.startswith("marimo."))
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
        "the production runtime seam imported marimo into a clean interpreter — #76 S1 lands "
        "the thin-drain runtime as a dormant, standalone module; marimo stays spike-only until "
        "the S3 ADR promotes it, and wiring the per-bar loop onto it is S6.\n"
        f"stdout={result.stdout!r}\nstderr={result.stderr!r}"
    )
    assert "MARIMO-FREE" in result.stdout


if __name__ == "__main__":
    res = _run_child()
    if res.returncode != 0:
        print(f"[STRATEGY-RUNTIME OFFLINE FAIL] {res.stdout.strip()} {res.stderr.strip()}")
        sys.exit(1)
    print("[STRATEGY-RUNTIME OFFLINE PASS] runtime seam imports with no marimo dependency")
