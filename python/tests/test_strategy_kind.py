"""Marimo-vs-imperative strategy detection (#76 S6a / findings 0046 / ADR-0012).

The dispatch seam must decide whether a strategy ``.py`` is a marimo reactive notebook
or an imperative ``Strategy`` subclass WITHOUT importing marimo and WITHOUT executing the
user module (a marimo file's ``import marimo`` would otherwise leak onto the runtime seam
and break the lazy-import discipline). These gates pin the AST scan: marimo iff it imports
marimo AND constructs an ``App`` at module level; ``import marimo`` alone is too weak.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

from engine.strategy_runtime.strategy_kind import is_marimo_app_source  # noqa: E402

_MARIMO_ATTR = """
import marimo as mo

app = mo.App()

@app.cell
def _c():
    x = 1
    return (x,)
"""

_MARIMO_PLAIN = """
import marimo

app = marimo.App()
"""

_MARIMO_FROM_IMPORT = """
from marimo import App

app = App()
"""

_IMPERATIVE = """
from engine.kernel.strategy import Strategy
from engine.kernel.orders import OrderSide

class MyStrat(Strategy):
    def on_bar(self, bar):
        self.submit_market(self.instrument_id, OrderSide.BUY, 1)
"""

_IMPERATIVE_IMPORTS_MARIMO_HELPER = """
import marimo  # imported for a helper, but no App() — still imperative
from engine.kernel.strategy import Strategy

class MyStrat(Strategy):
    def on_bar(self, bar):
        pass
"""

_HYBRID = """
import marimo as mo
from engine.kernel.strategy import Strategy

app = mo.App()  # App present → marimo (authoritative)

class MyStrat(Strategy):
    pass
"""


@pytest.mark.parametrize("src", [_MARIMO_ATTR, _MARIMO_PLAIN, _MARIMO_FROM_IMPORT])
def test_detects_marimo_app(src):
    assert is_marimo_app_source(src) is True


@pytest.mark.parametrize("src", [_IMPERATIVE, _IMPERATIVE_IMPORTS_MARIMO_HELPER])
def test_imperative_is_not_marimo(src):
    assert is_marimo_app_source(src) is False


def test_hybrid_app_present_is_marimo():
    # Both an App() and a Strategy subclass: App is the authoritative notebook signal.
    assert is_marimo_app_source(_HYBRID) is True


def test_syntax_error_propagates():
    # A marimo-ish file that won't parse must surface as a load error, not be guessed as
    # either kind. (A non-marimo broken file short-circuits the pre-filter and its real error
    # surfaces later on the imperative load path.)
    with pytest.raises(SyntaxError):
        is_marimo_app_source("import marimo\ndef broken(:\n")
