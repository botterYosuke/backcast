"""INV-K1-CAP — kabu PUSH 銘柄登録 50 上限と LRU の契約 (findings/0009)。

skill R6: REST/PUSH 合算で 50 銘柄まで。Q-K5: 満杯時の暗黙 evict は行わず
KabuRegisterFullError(code=4002006) を投げる。エラーコードは INV-K5-ERRCODE
で 4002006 (レジスト数エラー) に訂正済み。
"""
from __future__ import annotations

import pytest

from engine.exchanges.kabusapi_auth import KabuRegisterFullError
from engine.exchanges.kabusapi_register import MAX_SYMBOLS, RegisterSet


def test_cap_is_50() -> None:
    assert MAX_SYMBOLS == 50
    assert RegisterSet.MAX == 50


def test_register_up_to_cap_ok() -> None:
    rs = RegisterSet()
    for i in range(MAX_SYMBOLS):
        rs.register(str(1000 + i), 1)
    assert len(rs) == MAX_SYMBOLS


def test_register_over_cap_raises_register_full_4002006() -> None:
    """51 銘柄目は KabuRegisterFullError(code=4002006)。暗黙 evict しない (Q-K5)。"""
    rs = RegisterSet()
    for i in range(MAX_SYMBOLS):
        rs.register(str(1000 + i), 1)
    with pytest.raises(KabuRegisterFullError) as exc_info:
        rs.register("9999", 1)
    assert exc_info.value.code == 4002006
    # 暗黙 evict していない: 上限のまま、新銘柄は入っていない。
    assert len(rs) == MAX_SYMBOLS
    assert ("9999", 1) not in rs


def test_duplicate_register_is_touch_not_count() -> None:
    """同一 (symbol, exchange) の再登録は touch 扱いで件数を増やさない。"""
    rs = RegisterSet()
    rs.register("7203", 1)
    rs.register("7203", 1)
    assert len(rs) == 1


def test_symbol_exchange_compound_key() -> None:
    """(symbol, exchange) 複合キー: 同一 symbol でも exchange 違いは別銘柄 (R4)。"""
    rs = RegisterSet()
    rs.register("7203", 1)
    rs.register("7203", 3)
    assert len(rs) == 2


def test_evict_lru_returns_oldest_and_fires_callback() -> None:
    evicted: list[tuple[str, int]] = []
    rs = RegisterSet(on_evict=lambda s, e: evicted.append((s, e)))
    rs.register("1111", 1)
    rs.register("2222", 1)
    rs.touch("1111", 1)  # 1111 を最新へ → 2222 が最古
    assert rs.evict_lru() == ("2222", 1)
    assert evicted == [("2222", 1)]


def test_unregister_and_unregister_all() -> None:
    rs = RegisterSet()
    rs.register("1111", 1)
    rs.register("2222", 1)
    assert rs.unregister("1111", 1) is True
    assert rs.unregister("1111", 1) is False  # 既に不在
    rs.unregister_all()
    assert len(rs) == 0
