"""Venue-agnostic InstrumentId normalization for live trading.

Provides bidirectional conversion between kabu's ``Symbol@Exchange`` model and
Nautilus's ``Symbol.SUFFIX`` ``InstrumentId`` form. Tachibana market mapping is
intentionally stubbed pending spec confirmation in a later step.
"""

from __future__ import annotations

_KABU_EXCHANGE_TO_SUFFIX: dict[int, str] = {
    1: "TSE",
    3: "NSE",
    5: "FSE",
    6: "SSE",
}

# Tachibana market code (``CLMIssueSizyouMstKabu.sZyouzyouSizyou``) → Nautilus
# InstrumentId suffix. The manual (``mfds_json_api_ref_text.html``) and
# ``order_params.md`` document only ``"00" = 東証`` ("現状これのみ"). 名証/福証/
# 札証 are intentionally NOT listed until their real codes are confirmed against
# live data — coining a wrong suffix would silently mis-identify instruments,
# which is worse than failing closed (see Issue #36 grill).
_TACHIBANA_MARKET_TO_SUFFIX: dict[str, str] = {
    "00": "TSE",
}

_SUFFIX_TO_KABU_EXCHANGE: dict[str, int] = {
    v: k for k, v in _KABU_EXCHANGE_TO_SUFFIX.items()
}


def kabu_exchange_to_suffix(exchange: int) -> str:
    """Map a kabu Exchange code to a Nautilus InstrumentId suffix."""
    try:
        return _KABU_EXCHANGE_TO_SUFFIX[exchange]
    except KeyError as exc:
        raise ValueError(
            f"UNKNOWN_VENUE_MARKET: kabu exchange={exchange}"
        ) from exc


def suffix_to_kabu_exchange(suffix: str) -> int:
    """Map a Nautilus InstrumentId suffix back to a kabu Exchange code."""
    try:
        return _SUFFIX_TO_KABU_EXCHANGE[suffix]
    except KeyError as exc:
        raise ValueError(f"UNKNOWN_VENUE_MARKET: suffix={suffix}") from exc


def kabu_to_instrument_id(symbol: str, exchange: int) -> str:
    """Build a Nautilus InstrumentId string from a kabu ``(symbol, exchange)``."""
    if not symbol:
        raise ValueError("INVALID_SYMBOL: empty")
    suffix = kabu_exchange_to_suffix(exchange)
    return f"{symbol}.{suffix}"


def instrument_id_to_kabu(instrument_id: str) -> tuple[str, int]:
    """Parse a Nautilus InstrumentId string into a kabu ``(symbol, exchange)``."""
    if "." not in instrument_id:
        raise ValueError(f"INVALID_INSTRUMENT_ID: {instrument_id}")
    symbol, suffix = instrument_id.rsplit(".", 1)
    if not symbol or not suffix:
        raise ValueError(f"INVALID_INSTRUMENT_ID: {instrument_id}")
    exchange = suffix_to_kabu_exchange(suffix)
    return symbol, exchange


def tachibana_market_to_suffix(market: str) -> str:
    """Map a Tachibana market code to a Nautilus InstrumentId suffix.

    ``market`` is ``CLMIssueSizyouMstKabu.sZyouzyouSizyou`` (上場市場). Only
    ``"00" = 東証 → "TSE"`` is supported today. Unknown codes raise
    ``ValueError("UNKNOWN_VENUE_MARKET: ...")`` — symmetric with
    ``kabu_exchange_to_suffix`` so feed/build callers can catch one class and
    skip the row rather than silently coining a suffix.
    """
    try:
        return _TACHIBANA_MARKET_TO_SUFFIX[market]
    except KeyError as exc:
        raise ValueError(
            f"UNKNOWN_VENUE_MARKET: tachibana market={market!r}"
        ) from exc
