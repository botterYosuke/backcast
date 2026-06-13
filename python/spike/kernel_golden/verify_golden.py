"""spike.kernel_golden.verify_golden — read-only golden gate for the kernel (#24).

Runs the kernel tracer (Nautilus-free, in-process — works both in standalone CPython and
in-proc under Unity-Mono) and asserts its normalized contract equals the committed golden.
NEVER writes the golden (only capture_golden.py does). Exit 0 = match, 1 = drift, 2 = error.

    python -m spike.kernel_golden.verify_golden
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from spike.kernel_golden.capture_golden import GOLDEN_PATH
from spike.kernel_golden.normalize import canonical_json


def load_golden() -> dict:
    with open(GOLDEN_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def first_difference(expected, actual, path: str = "contract") -> str | None:
    """Return a human-readable path to the first differing value, or None if equal."""
    if isinstance(expected, dict) and isinstance(actual, dict):
        for key in expected:
            if key not in actual:
                return f"{path}.{key}: missing in actual"
            diff = first_difference(expected[key], actual[key], f"{path}.{key}")
            if diff:
                return diff
        for key in actual:
            if key not in expected:
                return f"{path}.{key}: unexpected in actual"
        return None
    if isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            return f"{path}: length {len(expected)} != {len(actual)}"
        for i, (e, a) in enumerate(zip(expected, actual)):
            diff = first_difference(e, a, f"{path}[{i}]")
            if diff:
                return diff
        return None
    # Type-strict scalar compare: 1 == 1.0 and True == 1 are False here, so a type
    # regression (count as float, status as bool) is caught rather than silently equal.
    if type(expected) is not type(actual):
        return f"{path}: type {type(expected).__name__} != {type(actual).__name__} ({expected!r} vs {actual!r})"
    if expected != actual:
        return f"{path}: {expected!r} != {actual!r}"
    return None


def verify() -> tuple[bool, str]:
    """Run the kernel and compare to the committed golden contract."""
    from spike.kernel_golden.run_kernel import run as run_kernel

    golden = load_golden()
    kernel = run_kernel()
    diff = first_difference(golden["contract"], kernel["contract"])
    if diff is None:
        return True, "kernel contract matches committed golden"
    return False, f"kernel contract drifted from golden: {diff}"


def main() -> int:
    try:
        ok, message = verify()
    except Exception as exc:  # noqa: BLE001
        print(f"[VERIFY GOLDEN ERROR] {exc}")
        return 2
    if ok:
        print(f"[VERIFY GOLDEN PASS] {message}")
        return 0
    print(f"[VERIFY GOLDEN FAIL] {message}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
