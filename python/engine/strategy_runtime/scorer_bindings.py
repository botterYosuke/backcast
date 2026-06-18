"""Generic scorer-binding resolver for marimo strategies (#76 follow-up / findings 0046 R4).

A marimo cell strategy can declare a host-resolved scorer in its sidecar's ``scorer`` key
(alongside ``scenario`` / ``layout``). ``load_scorer_bindings`` reads that spec, dispatches on
``kind`` to a per-kind factory, and returns the ``(services, constants)`` the dispatch site
hands to ``MarimoStrategy`` — so ``_select_replay_strategy`` stays strategy-agnostic (S6a
detect-first). A file with no ``scorer`` key resolves to ``({}, {})`` (toy / parity-fixture
marimo strategies run with no injection — R5).

This module stays marimo-free AND joblib-free at module load (AC3): the per-kind factory is
imported lazily (only when its ``kind`` is requested), and the heavy model load lives behind
the factory's scorer closure (first score call). The runtime seam imports this module lazily
too (only on the marimo branch), so module-load purity is preserved.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable, Tuple


def _v19_factory() -> Callable:
    from strategies.v19.v19_scorer import make_v19_scorer_bindings

    return make_v19_scorer_bindings


# kind -> a thunk that lazily imports and returns the factory (keeps this module import-light).
_REGISTRY: dict[str, Callable[[], Callable]] = {
    "v19": _v19_factory,
}


def _read_scorer_spec(strategy_path: Path) -> "dict | None":
    """The ``scorer`` key from the co-located ``<stem>.json`` sidecar, or None when absent."""
    sidecar = strategy_path.with_suffix(".json")
    if not sidecar.exists():
        return None
    doc = json.loads(sidecar.read_text(encoding="utf-8"))
    return doc.get("scorer") if isinstance(doc, dict) else None


def load_scorer_bindings(strategy_path, scenario: dict) -> Tuple[dict, dict]:
    """Resolve ``(services, constants)`` for ``strategy_path``'s scorer spec.

    Returns ``({}, {})`` when the sidecar has no ``scorer`` key. ``scenario`` is passed to the
    factory so it can fail-loud on a scoring-universe / bar-universe mismatch (R3). Paths inside
    the spec are resolved relative to the sidecar directory.
    """
    path = Path(strategy_path)
    spec = _read_scorer_spec(path)
    if not spec:
        return {}, {}
    kind = spec.get("kind")
    if kind not in _REGISTRY:
        raise ValueError(f"unknown scorer kind {kind!r} (known: {sorted(_REGISTRY)})")
    factory = _REGISTRY[kind]()
    services, constants = factory(spec, path.parent, scenario)
    return services, constants
