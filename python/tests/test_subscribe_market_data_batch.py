"""#107 — 一括購読(subscribe_many) と人工 cap 撤去・venue 実上限の typed surface。

方針: ADR-0022 / findings 0086。

ここで gate するのは engine seam の正しさ:
- 人工的な件数 cap を撤去した（50 超でも全部購読できる）。
- venue 実上限は `SubscriptionLimitExceeded` → "SUBSCRIPTION_LIMIT_EXCEEDED" で per-id surface。
- それ以外の失敗は "SUBSCRIBE_FAILED"。1 銘柄の失敗が他を止めない。
- membership（購読成功集合）は失敗銘柄を含まない（runner.subscribe のロールバック）。
- kabu adapter が `KabuRegisterFullError(4002006)` を venue 非依存例外へ翻訳する。

C# 配線 + depth 描画までの end-to-end は full-stack AFK gate（LiveSubscribeWiringE2ERunner）が担う。
"""
from __future__ import annotations

import asyncio

import pytest

from engine.live.adapter import SubscriptionLimitExceeded
from engine.live.live_runner import LiveRunner

_IV = 60_000_000_000  # 60s in ns


class _FakeAdapter:
    """最小 LiveVenueAdapter: subscribe を記録し、指定 id で例外を投げる。"""

    def __init__(self, *, limit_ids=(), fail_ids=()) -> None:
        self.subscribed: list[str] = []
        self._limit_ids = set(limit_ids)
        self._fail_ids = set(fail_ids)

    async def subscribe(self, instrument_id, channels) -> None:
        if instrument_id in self._limit_ids:
            raise SubscriptionLimitExceeded("venue full", venue_code=4002006)
        if instrument_id in self._fail_ids:
            raise ValueError(f"boom {instrument_id}")
        self.subscribed.append(instrument_id)

    async def unsubscribe(self, instrument_id) -> None:
        if instrument_id in self.subscribed:
            self.subscribed.remove(instrument_id)


def _runner(adapter: _FakeAdapter) -> LiveRunner:
    return LiveRunner(adapter, intervals_ns=[_IV])


def test_subscribe_many_all_succeed() -> None:
    adapter = _FakeAdapter()
    runner = _runner(adapter)
    ids = [f"{1000 + i}.TSE" for i in range(3)]
    results = asyncio.run(runner.subscribe_many(ids))
    assert [(i, ok, ec) for (i, ok, ec) in results] == [(i, True, "") for i in ids]
    assert runner.subscribed_ids() == set(ids)


def test_no_artificial_cap_beyond_50() -> None:
    """litmus: 人工 50 cap を撤去したので 60 銘柄でも全部購読できる。

    旧 cap が復活すると 51 件目以降が SUBSCRIPTION_LIMIT_EXCEEDED になり RED。
    """
    adapter = _FakeAdapter()  # 実上限なし（tachibana 相当）
    runner = _runner(adapter)
    ids = [f"{1000 + i}.TSE" for i in range(60)]
    results = asyncio.run(runner.subscribe_many(ids))
    assert all(ok for (_i, ok, _ec) in results)
    assert len(runner.subscribed_ids()) == 60


@pytest.mark.scenario("SUBWIRE-05")
def test_venue_limit_surfaces_typed_and_does_not_stop_others() -> None:
    over = "9999.TSE"
    ids = ["1000.TSE", over, "1001.TSE"]
    adapter = _FakeAdapter(limit_ids={over})
    runner = _runner(adapter)
    results = asyncio.run(runner.subscribe_many(ids))
    by_id = {i: (ok, ec) for (i, ok, ec) in results}
    assert by_id["1000.TSE"] == (True, "")
    assert by_id["1001.TSE"] == (True, "")
    assert by_id[over] == (False, "SUBSCRIPTION_LIMIT_EXCEEDED")
    # membership(購読成功集合)は失敗銘柄を含まない（runner.subscribe のロールバック）。
    assert runner.subscribed_ids() == {"1000.TSE", "1001.TSE"}


def test_generic_failure_is_subscribe_failed() -> None:
    bad = "5555.TSE"
    adapter = _FakeAdapter(fail_ids={bad})
    runner = _runner(adapter)
    results = asyncio.run(runner.subscribe_many(["1000.TSE", bad]))
    by_id = {i: (ok, ec) for (i, ok, ec) in results}
    assert by_id["1000.TSE"] == (True, "")
    assert by_id[bad] == (False, "SUBSCRIBE_FAILED")


@pytest.mark.scenario("KABU-LIVE-03")
def test_kabu_subscribe_translates_register_full_to_typed() -> None:
    """kabu adapter は満杯時 KabuRegisterFullError を venue 非依存 typed 例外へ翻訳する。"""
    from engine.exchanges.kabusapi import KabuStationAdapter

    adapter = KabuStationAdapter(environment="verify")
    adapter._token = "tok"  # login 済み相当（network は register が先に raise するので叩かない）
    for i in range(50):
        adapter._register_set.register(str(1000 + i), 1)

    async def _go():
        await adapter.subscribe("9999.TSE", {"trades", "depth"})

    with pytest.raises(SubscriptionLimitExceeded) as exc_info:
        asyncio.run(_go())
    assert exc_info.value.venue_code == 4002006


# ── orchestrator-level subscribe_market_data_batch dict assembly (the layer the C# RPC reads) ──
# Drives the SAME InprocLiveServer surface WorkspaceEngineHost calls, with a MockVenueAdapter so no
# real venue is needed (mirrors test_live_auto_lifecycle_inproc_server.py).

def _mock_server(tmp_path, monkeypatch):
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer
    from engine.live.mock_adapter import MockVenueAdapter
    import engine.live.live_adapter_factory as laf

    mock = MockVenueAdapter()
    mock.set_account_snapshot(cash=1_000_000.0, buying_power=1_000_000.0, positions=())
    monkeypatch.setattr(laf, "build_live_adapter_factory", lambda venue: (lambda env_hint=None: mock))
    eng = DataEngine(duckdb_root=str(tmp_path))
    eng.set_rust_event_sink(lambda *a, **k: None)
    return InprocLiveServer(eng, "MOCK")


def test_batch_precondition_when_no_session(tmp_path, monkeypatch) -> None:
    server = _mock_server(tmp_path, monkeypatch)
    try:
        res = server.subscribe_market_data_batch(["7203.TSE"])  # no venue_login yet → _session None
        assert res["success"] is False
        assert res["error_code"] == "EXECUTION_MODE_PRECONDITION"
        assert res["results"] == []
    finally:
        server.close()


def test_batch_empty_ids_is_success(tmp_path, monkeypatch) -> None:
    server = _mock_server(tmp_path, monkeypatch)
    try:
        assert server.venue_login("MOCK", "env", "")["success"]
        assert server.set_execution_mode("LiveManual")["success"]
        res = server.subscribe_market_data_batch([])
        assert res["success"] is True
        assert res["error_code"] == ""
        assert res["results"] == []
    finally:
        server.close()


def test_batch_all_succeed_dict_shape(tmp_path, monkeypatch) -> None:
    import json
    server = _mock_server(tmp_path, monkeypatch)
    try:
        assert server.venue_login("MOCK", "env", "")["success"]
        assert server.set_execution_mode("LiveManual")["success"]
        ids = ["7203.TSE", "9984.TSE", "6758.TSE"]
        res = server.subscribe_market_data_batch(ids)
        assert res["success"] is True, res
        assert res["error_code"] == ""
        got = {(r["instrument_id"], r["success"], r["error_code"]) for r in res["results"]}
        assert got == {(i, True, "") for i in ids}
        # market-data 購読が成立したら badge は SUBSCRIBED へ（CONNECTED→SUBSCRIBED の遷移）。
        assert json.loads(server.get_state_json())["venue_state"] == "SUBSCRIBED"
    finally:
        server.close()
