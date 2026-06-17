"""Marimo-vs-imperative strategy detection (#76 S6a / findings 0046 / ADR-0012).

The dispatch seam (``_backend_impl``) must decide whether a strategy ``.py`` is a marimo
reactive notebook (``app = marimo.App()`` + ``@app.cell``) or an imperative
``engine.kernel.strategy.Strategy`` subclass — WITHOUT importing marimo and WITHOUT
executing the user module. A marimo file's own ``import marimo`` would otherwise leak onto
the runtime seam and break the lazy-import discipline (``tests/test_strategy_runtime_offline``):
the seam may import marimo only when a marimo strategy actually runs (ADR-0012 Decision 4).

Detection is therefore a pure stdlib-``ast`` scan — no import, no exec — mirroring how
marimo itself recognizes its files structurally (a textual ``import marimo`` guard) rather
than by importing them. A file is a marimo strategy iff it BOTH:

  - imports marimo at module level (``import marimo`` / ``import marimo as mo`` /
    ``from marimo import App``), AND
  - constructs an ``App`` at MODULE level (``marimo.App(...)`` / ``mo.App(...)`` / a bare
    ``App(...)`` when ``App`` was imported from marimo).

The ``App()`` construction is authoritative: ``import marimo`` alone is too weak (an
imperative strategy may import marimo for a helper). When a file has both an ``App()`` and
a ``Strategy`` subclass, the ``App`` wins (marimo priority — ``App`` is the decisive
notebook signal). A ``SyntaxError`` propagates so a broken file surfaces as a load error
rather than being silently routed to either kind.

This module is deliberately marimo-free and is part of the offline gate's import set.
"""
from __future__ import annotations

import ast
from pathlib import Path


def _is_app_call(value: ast.expr, marimo_aliases: set[str], app_names: set[str]) -> bool:
    """True if ``value`` is a call to ``<marimo>.App(...)`` or a bare ``App(...)``."""
    if not isinstance(value, ast.Call):
        return False
    func = value.func
    if (
        isinstance(func, ast.Attribute)
        and func.attr == "App"
        and isinstance(func.value, ast.Name)
        and func.value.id in marimo_aliases
    ):
        return True
    return isinstance(func, ast.Name) and func.id in app_names


def is_marimo_app_source(src: str) -> bool:
    """True iff ``src`` imports marimo and constructs an ``App`` at module level.

    Pure ``ast`` — never imports marimo nor executes the module. ``SyntaxError`` propagates.
    """
    # Cheap textual pre-filter: no mention of marimo at all → cannot be a marimo notebook,
    # so skip the parse (and a non-marimo file's own syntax error surfaces later on the
    # imperative load path, not here). "marimo" also matches `from marimo import App`, which
    # `"import marimo"` would miss.
    if "marimo" not in src:
        return False

    tree = ast.parse(src)

    marimo_aliases: set[str] = set()  # names bound to the marimo module object
    app_names: set[str] = set()  # names bound to marimo.App via `from marimo import App`
    # Module-level imports only (matches the canonical marimo file): a function-local
    # `import marimo` in an otherwise-imperative strategy must not flip detection.
    for node in tree.body:
        if isinstance(node, ast.Import):
            for alias in node.names:
                if alias.name == "marimo":
                    marimo_aliases.add(alias.asname or alias.name)
        elif isinstance(node, ast.ImportFrom) and node.module == "marimo":
            for alias in node.names:
                if alias.name == "App":
                    app_names.add(alias.asname or "App")

    if not marimo_aliases and not app_names:
        return False

    # The App() construction must be at MODULE level (canonical marimo: `app = marimo.App()`).
    # Restricting to module level avoids a false positive where an imperative strategy calls
    # marimo.App() inside a helper it never wires into a notebook.
    for node in tree.body:
        if isinstance(node, ast.Assign) and _is_app_call(node.value, marimo_aliases, app_names):
            return True
        if (
            isinstance(node, ast.AnnAssign)
            and node.value is not None
            and _is_app_call(node.value, marimo_aliases, app_names)
        ):
            return True
        if isinstance(node, ast.Expr) and _is_app_call(node.value, marimo_aliases, app_names):
            return True
    return False


def is_marimo_app_file(path: str | Path) -> bool:
    """Read ``path`` (the canonical strategy .py, ADR-0011) and detect a marimo app."""
    return is_marimo_app_source(Path(path).read_text(encoding="utf-8"))
