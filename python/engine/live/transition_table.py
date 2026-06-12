"""Shared transition-table state machine engine.

Both VenueStateMachine and LiveStrategyStateMachine delegate their
validation logic here.  Rust RunState (ADR-0017 single writer) is NOT
integrated across the PyO3 boundary — see ADR-0017 for rationale.
"""

from __future__ import annotations


class InvalidTransition(Exception):
    """Raised when an illegal state transition is requested."""


class StateMachine:
    """Generic transition-table state machine.

    Args:
        states: Complete set of valid state names.
        allowed: Adjacency dict mapping each state to its reachable states.
        initial: The starting (and reset) state.
        exception_cls: Exception subclass to raise on invalid transitions.
            Defaults to InvalidTransition; pass a domain-specific subclass
            to keep existing callers' except-clauses working.
    """

    def __init__(
        self,
        *,
        states: frozenset[str],
        allowed: dict[str, set[str]],
        initial: str,
        exception_cls: type[InvalidTransition] = InvalidTransition,
    ) -> None:
        assert initial in states, f"initial {initial!r} not in states"
        assert states == frozenset(allowed), (
            f"allowed keys {set(allowed)} do not match states {states}"
        )
        self._states = states
        self._allowed = allowed
        self._initial = initial
        self._exception_cls = exception_cls
        self.current: str = initial

    def transition_to(self, target: str) -> None:
        if target not in self._states:
            raise self._exception_cls(f"unknown target state: {target!r}")
        if target not in self._allowed[self.current]:
            raise self._exception_cls(
                f"illegal transition: {self.current} -> {target}"
            )
        self.current = target

    def reset(self) -> None:
        self.current = self._initial
