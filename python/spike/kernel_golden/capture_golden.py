"""spike.kernel_golden.capture_golden — (re)generate the committed golden from the oracle (#24).

EXPLICIT-RUN ONLY. Spawns the Nautilus oracle in a SEPARATE process (findings 0008 §4 —
the golden is recorded from the real oracle path, never computed from the kernel's own
assumptions) and writes the normalized contract + build provenance to golden.json.

`verify_golden.py` never writes; only this script does. Updating the golden is a reviewed
event: the contract diff + provenance (nautilus version / PRECISION_BYTES / strategy /
catalog / scenario hashes) must be inspected before committing.

    python -m spike.kernel_golden.capture_golden
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from spike.kernel_golden import scenario
from spike.kernel_golden.normalize import SCHEMA_VERSION

GOLDEN_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "golden.json")


def capture() -> dict:
    """Run the oracle in a fresh subprocess and return {contract, provenance}."""
    from spike.kernel_golden.subprocess_util import run_python

    # 300s: oracle leg imports Nautilus + runs the backtest.
    proc = run_python(["-m", "spike.kernel_golden.run_oracle"], timeout=300)
    if proc.returncode != 0:
        raise RuntimeError(f"oracle subprocess failed:\n{proc.stderr}")
    return json.loads(proc.stdout)


def main() -> int:
    captured = capture()
    golden = {
        "schema_version": SCHEMA_VERSION,
        "provenance": captured["provenance"],
        "contract": captured["contract"],
    }
    with open(GOLDEN_PATH, "w", encoding="utf-8") as fh:
        json.dump(golden, fh, ensure_ascii=False, indent=2, sort_keys=True)
        fh.write("\n")
    print(f"[CAPTURE GOLDEN] wrote {GOLDEN_PATH}")
    print(f"  provenance: {json.dumps(golden['provenance'])}")
    print(f"  bar_count={golden['contract']['bar_count']} fills={golden['contract']['order_states']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
