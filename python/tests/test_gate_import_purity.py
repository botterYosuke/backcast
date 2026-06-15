"""Import-purity gate (#24, broadened in #50 AC④): the runtime must be Rust-core-free.

#50 / ADR-0006 retired nautilus entirely. This gate now imports the WHOLE production runtime
seam — `engine._backend_impl` (InProc entry → DataEngine + the DuckDB Replay chain + the Live
orchestrator/kernel-live controller), plus `engine.kernel.duckdb_bars` and
`engine.strategy_runtime.replay_kernel_observer` — in a clean interpreter and asserts no
`nautilus_trader*`/`nautilus_pyo3` module leaks into `sys.modules`. So Replay AND Live module
import is pinned nautilus-free here; the full lifecycle is covered by `test_kernel_live_purity`
(LiveAuto roundtrip) and `test_replay_duckdb_kernel_afk` (DuckDB→kernel Replay roundtrip).


ADR-0004 案 C / findings 0008 §1.1 — the Backcast Execution Kernel runs in the
Unity-Mono process WITHOUT the Nautilus Rust core (`nautilus_trader.core.nautilus_pyo3`),
because loading that .pyd re-introduces the multi-CRT/FLS teardown crash this whole
approach exists to remove (s0-result §1.1–§1.4 / ADR-0004 native dump analysis).

The kernel reuses the existing pre-trade / post-trade rail *logic* (`evaluate_pre_trade`
/ `evaluate_post_trade` / `SafetyRails.check_*`). So importing those modules must NOT
pull in `nautilus_trader` at all. The one Nautilus-coupled helper
(`build_live_risk_engine_config`) lives in a separate module imported only on the
Nautilus live path.

This is verified in a fresh subprocess so an unrelated test that already imported
Nautilus can't mask a regression: a clean interpreter imports only the gates and
asserts no `nautilus_trader*` module is present in `sys.modules`.

Runnable directly (`python tests/test_gate_import_purity.py`) or via pytest.
"""
from __future__ import annotations

import os
import subprocess
import sys

_PYTHON_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Child program: import the gates AND the whole kernel package (the code that runs in
# the Mono process) in a clean interpreter, then report any Nautilus module that leaked
# into sys.modules. Exit 0 = pure, exit 1 = Rust core / nautilus loaded.
_CHILD = r"""
import sys

# Safety Rails gates (#24).
import engine.live.safety_rails       # noqa: F401
import engine.live.pre_trade_gate     # noqa: F401
import engine.live.post_trade_gate    # noqa: F401
# Kernel execution core (pulls bars/orders/portfolio/broker/strategy/sink/risk).
import engine.kernel.runner           # noqa: F401
# #50 (AC④): the WHOLE production runtime seam must import nautilus-free. `engine._backend_impl`
# is the InProc entry point — importing it pulls core (DataEngine), the DuckDB Replay chain
# (_start_engine_duckdb → kernel.runner + duckdb_bars + replay_kernel_observer) AND the Live
# orchestrator (live_orchestrator → engine.kernel.live controller/driver). If any module on
# Replay or Live loads nautilus_trader/nautilus_pyo3, it surfaces here in a clean interpreter.
import engine._backend_impl                            # noqa: F401
import engine.kernel.duckdb_bars                       # noqa: F401
import engine.strategy_runtime.replay_kernel_observer  # noqa: F401

from spike.kernel_golden.purity import leaked_nautilus_modules

leaked = leaked_nautilus_modules(sys.modules)
if leaked:
    print("LEAKED:" + ",".join(leaked))
    sys.exit(1)
print("PURE")
sys.exit(0)
"""


def _run_purity_child() -> subprocess.CompletedProcess:
    from spike.kernel_golden.subprocess_util import run_python

    # 120s: imports only; a longer wait would mean a hang.
    return run_python(["-c", _CHILD], timeout=120)


def test_gates_import_without_nautilus_rust_core() -> None:
    result = _run_purity_child()
    assert result.returncode == 0, (
        "importing the production runtime seam (Safety Rails gates + kernel + _backend_impl "
        "Replay/Live chain) loaded Nautilus into a clean interpreter — #50 AC④ requires every "
        "Replay/Live runtime path to stay Rust-core-free.\n"
        f"stdout={result.stdout!r}\nstderr={result.stderr!r}"
    )
    assert "PURE" in result.stdout


if __name__ == "__main__":
    res = _run_purity_child()
    if res.returncode != 0:
        print(f"[GATE IMPORT PURITY FAIL] {res.stdout.strip()} {res.stderr.strip()}")
        sys.exit(1)
    print("[GATE IMPORT PURITY PASS] gates import with no nautilus_trader/nautilus_pyo3")
