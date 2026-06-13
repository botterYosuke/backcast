"""spike.kernel_golden.run_kernel — run the kernel tracer, print normalized contract (#24).

Subprocess entry point for the golden gates. MUST stay Nautilus-free: importing this
and everything it pulls in may NOT load `nautilus_trader` (asserted by --assert-pure and
by the Mono teardown gate). Prints the normalized contract as canonical JSON to stdout.

Usage:
    python -m spike.kernel_golden.run_kernel              # print normalized contract
    python -m spike.kernel_golden.run_kernel --assert-pure  # also fail if nautilus loaded
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from spike.kernel_golden import scenario
from spike.kernel_golden.normalize import CaptureSink, canonical_json, normalize


def run() -> dict:
    from engine.kernel.runner import KernelRunner
    from spike.fixtures.strategies.kernel_spike_buy_sell import KernelSpikeBuySell

    sink = CaptureSink()
    KernelRunner(
        catalog_path=scenario.CATALOG,
        instrument_id=scenario.INSTRUMENT,
        start=scenario.START,
        end=scenario.END,
        initial_cash=scenario.INITIAL_CASH,
        strategy=KernelSpikeBuySell(),
        push_target=sink,
    ).run()
    return normalize(sink.events, initial_cash=scenario.INITIAL_CASH)


def main() -> int:
    contract = run()
    if "--assert-pure" in sys.argv:
        from spike.kernel_golden.purity import leaked_nautilus_modules

        leaked = leaked_nautilus_modules(sys.modules)
        if leaked:
            print("IMPURE:" + ",".join(leaked[:5]), file=sys.stderr)
            return 2
    print(canonical_json(contract))
    return 0


if __name__ == "__main__":
    sys.exit(main())
