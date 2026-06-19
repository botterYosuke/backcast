"""spike.venue_login_secret.secret_mock — throwaway Tachibana-like adapter for #21.

findings 0012 D3: production ``MockVenueAdapter`` accepts ``secret_resolver`` as a
no-op and never emits ``SecretRequired``. To exercise the real second-password seam
(SecretRequired → submit_secret → SecondSecretResolver) under the production order
RPC, this throwaway subclass STORES the resolver (like ``TachibanaAdapter``) and
``await``s it inside ``submit_order`` — so the place blocks on the urgent-secret lane
exactly as a real tachibana order would.

Differences from a real venue (all test-only, throwaway):
- The resolver's wait is shortened to ``SECRET_TIMEOUT_S`` so the SECRET_TIMEOUT leg
  fires in seconds instead of the production 30s (the production 30s path is unit
  tested in ``secret_provider`` tests; here we only need the order-RPC mapping).
- Each ``submit_order`` uses a UNIQUE purpose (``PLACE-<n>``) so every order forces a
  fresh ``SecretRequired`` (the production "reuse within TTL" behavior would otherwise
  suppress the prompt on consecutive orders and is out of scope for this seam gate).
- The fill outcome is whatever ``arm_order`` armed (default FILLED), via the base
  ``MockVenueAdapter`` machinery.

The C# VenueLoginSecretProbe drives the production InprocLiveServer façade and only
injects this adapter through ``build_secret_mock_server`` — no production API is
extended (findings 0011 D2 discipline, inherited).
"""
from __future__ import annotations

import threading

from engine.live.mock_adapter import MockVenueAdapter

# Shortened resolver wait for the SECRET_TIMEOUT leg. Must satisfy the containment
# 25s(C# modal) < 30s(prod secret wait) — here we only shorten the *backend* wait so
# the timeout leg is fast; keep it comfortably below the 40s order-write timeout so
# the RPC maps to SECRET_TIMEOUT (not PLACE_TIMEOUT).
SECRET_TIMEOUT_S = 6.0
IID = "8918.TSE"


class SecretMockAdapter(MockVenueAdapter):
    """MockVenueAdapter that actually requires a second password (like tachibana)."""

    def __init__(self) -> None:
        super().__init__()
        self._secret_resolver = None
        self._purpose_lock = threading.Lock()
        self._purpose_seq = 0

    def set_execution_hooks(
        self,
        *,
        secret_resolver=None,
        on_order_event,
        on_venue_logout=None,
    ) -> None:
        # Unlike the no-op base: STORE the resolver and shorten its wait so the
        # SECRET_TIMEOUT leg fires fast. Faithful path otherwise (resolve → vault).
        self._secret_resolver = secret_resolver
        if secret_resolver is not None:
            try:
                secret_resolver._timeout = SECRET_TIMEOUT_S
            except Exception:
                pass

    def _next_purpose(self) -> str:
        with self._purpose_lock:
            self._purpose_seq += 1
            return f"PLACE-{self._purpose_seq}"

    async def submit_order(self, **kwargs):
        # Require the second password BEFORE the order touches the venue, so a
        # SECRET_TIMEOUT leaves no order (orphan-free, like tachibana).
        if self._secret_resolver is not None:
            await self._secret_resolver.resolve(self.venue_id, self._next_purpose())
        return await super().submit_order(**kwargs)


# ----------------------------------------------------------------------------------
# Throwaway drive helpers (white-box, positional sigs for pythonnet from C#).
# ----------------------------------------------------------------------------------

def build_secret_mock_server(data_engine):
    """Construct InprocLiveServer(data_engine, "MOCK") but with the SecretMockAdapter.

    Monkeypatches the venue factory module attribute before constructing the server
    (InprocLiveServer imports the symbol inside __init__, so patching the module
    attribute first is enough). Production files are untouched.
    """
    import engine.live.live_adapter_factory as laf
    from engine.inproc_server import InprocLiveServer

    orig = laf.build_live_adapter_factory

    def patched(venue):
        if venue == "MOCK":
            return lambda env_hint=None: SecretMockAdapter()
        return orig(venue)

    laf.build_live_adapter_factory = patched
    try:
        return InprocLiveServer(data_engine, "MOCK")
    finally:
        laf.build_live_adapter_factory = orig


def _mgr(server):
    return server._svc._srv._live_mgr


def _adapter(server):
    sess = _mgr(server)._session
    return sess.runner.adapter if sess is not None else None


def arm_order(server, status: str, filled_qty: float, avg_price: float) -> None:
    """Arm the next submit_order outcome (positional wrapper for C#; throwaway)."""
    _adapter(server).set_next_order_outcome(
        status=status, filled_qty=filled_qty, avg_price=avg_price
    )


def submit_order_call_count(server) -> int:
    """How many times the order actually reached the venue (orphan-free assertion)."""
    a = _adapter(server)
    return a.submit_order_call_count if a is not None else -1


def arm_account_position(
    server,
    symbol: str,
    qty: float,
    avg_price: float,
    unrealized_pnl: float,
    cash: float,
    buying_power: float,
) -> None:
    """Arm fetch_account's snapshot with ONE position (positional wrapper for C#; throwaway).

    The MockVenueAdapter does NOT derive positions from fills (submit_order only returns an
    OrderResult); fetch_account returns whatever set_account_snapshot armed. The Journey E2E
    arms a position here, then force_account_snapshot() pushes it as an AccountEvent so the
    Positions-tile decode seam (AccountEvent -> LivePanelViewModel -> FormatPositions) is
    exercised non-vacuously. The causal fill->position bookkeeping is the venue's job (HITL).
    """
    from engine.live.order_types import AccountPositionData

    _adapter(server).set_account_snapshot(
        cash=cash,
        buying_power=buying_power,
        positions=[
            AccountPositionData(
                symbol=symbol,
                qty=int(qty),
                avg_price=avg_price,
                unrealized_pnl=unrealized_pnl,
            )
        ],
    )
