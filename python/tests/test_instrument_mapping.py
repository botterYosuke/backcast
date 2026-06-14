"""Characterization tests for live/instrument_mapping (Issue #36, AFK).

Pins the venue-market → Nautilus InstrumentId suffix mapping tables so that
the supported codes are fixed (regression guard) and unknown codes fail
closed with a single canonical error class. Tachibana market codes are only
documented for ``"00" = 東証`` (manual: CLMIssueSizyouMstKabu.sZyouzyouSizyou
"00：東証", "現状これのみ"); 名証/福証/札証 are intentionally NOT mapped until
their real sSizyouC/sZyouzyouSizyou values are confirmed against live data.
"""
from __future__ import annotations

import pytest

from engine.live.instrument_mapping import (
    kabu_exchange_to_suffix,
    tachibana_market_to_suffix,
)


# --- Tachibana (Issue #36) ---------------------------------------------------


def test_tachibana_market_to_suffix_tse() -> None:
    # "00" = 東証 — the only documented code (manual + order_params.md).
    assert tachibana_market_to_suffix("00") == "TSE"


@pytest.mark.parametrize("unknown", ["01", "02", "03", "99", "", "TSE"])
def test_tachibana_market_to_suffix_unknown_raises(unknown: str) -> None:
    # Fail closed on undocumented markets — never silently coin a suffix.
    # Symmetric with kabu_exchange_to_suffix so callers can catch one class.
    with pytest.raises(ValueError, match="UNKNOWN_VENUE_MARKET"):
        tachibana_market_to_suffix(unknown)


# --- Kabu (regression: same fail-closed contract) ----------------------------


def test_kabu_exchange_to_suffix_known() -> None:
    assert kabu_exchange_to_suffix(1) == "TSE"
    assert kabu_exchange_to_suffix(3) == "NSE"
    assert kabu_exchange_to_suffix(5) == "FSE"
    assert kabu_exchange_to_suffix(6) == "SSE"


def test_kabu_exchange_to_suffix_unknown_raises() -> None:
    with pytest.raises(ValueError, match="UNKNOWN_VENUE_MARKET"):
        kabu_exchange_to_suffix(99)
