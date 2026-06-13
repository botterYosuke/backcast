"""spike.kernel_golden.purity — the single definition of "Rust core loaded" (#24).

AC#2's structural guarantee is that the kernel process loads NO Nautilus. That predicate
is the spec, so it lives in one place: `run_kernel --assert-pure` and the import-purity
test both use it (rather than re-encoding the rule, once inside a string literal).
Nautilus-free.
"""
from __future__ import annotations

from typing import Iterable


def is_nautilus_module(name: str) -> bool:
    return name == "nautilus_trader" or name.startswith("nautilus_trader.") or "nautilus_pyo3" in name


def leaked_nautilus_modules(module_names: Iterable[str]) -> list[str]:
    return sorted(n for n in module_names if is_nautilus_module(n))
