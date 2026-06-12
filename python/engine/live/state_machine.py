"""Venue connection state machine for Phase 8 live trading."""

from __future__ import annotations

from engine.live.transition_table import InvalidTransition, StateMachine


class InvalidVenueTransition(InvalidTransition):
    """Raised when an illegal venue state transition is requested."""


_STATES: frozenset[str] = frozenset(
    {
        "DISCONNECTED",
        "AUTHENTICATING",
        "CONNECTED",
        "SUBSCRIBED",
        "RECONNECTING",
        "ERROR",
    }
)

_ALLOWED: dict[str, set[str]] = {
    "DISCONNECTED": {"AUTHENTICATING"},
    "AUTHENTICATING": {"CONNECTED", "ERROR"},
    "CONNECTED": {"SUBSCRIBED", "ERROR"},
    "SUBSCRIBED": {"RECONNECTING", "ERROR"},
    "RECONNECTING": {"SUBSCRIBED", "ERROR"},
    # ERROR は外部 transition_to("DISCONNECTED") も許容する (Phase 8 post-merge fix):
    # _fail() などのリカバリ経路で reset() を経由せず明示的に DISCONNECTED へ
    # 戻したいケースを許可する。reset() による DISCONNECTED 復帰は引き続き有効。
    "ERROR": {"DISCONNECTED"},
}


class VenueStateMachine(StateMachine):
    def __init__(self) -> None:
        super().__init__(
            states=_STATES,
            allowed=_ALLOWED,
            initial="DISCONNECTED",
            exception_cls=InvalidVenueTransition,
        )
