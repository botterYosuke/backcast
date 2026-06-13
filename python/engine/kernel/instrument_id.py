"""engine.kernel.instrument_id — nautilus-free instrument-id validation (#25 D8).

`live_orchestrator.start_live_strategy` validated the instrument id with
`nautilus_trader.model.identifiers.InstrumentId.from_str` (loads the Rust core), which breaks
the LiveAuto import-purity gate. This stdlib check mirrors the well-formedness Nautilus
enforces — `SYMBOL.VENUE`: exactly one dot, two non-empty whitespace-free segments — without
importing nautilus. Nautilus-free.
"""
from __future__ import annotations

import re

# SYMBOL.VENUE: 一つのドット・前後とも非空・空白なし（Nautilus InstrumentId.from_str の最低条件）。
_INSTRUMENT_ID_RE = re.compile(r"^[^.\s]+\.[^.\s]+$")


def is_valid_instrument_id(value: str) -> bool:
    """`SYMBOL.VENUE` 形式なら True。空・venue サフィックス欠落・空白混入は False。"""
    return isinstance(value, str) and bool(_INSTRUMENT_ID_RE.match(value))
