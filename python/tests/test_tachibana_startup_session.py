"""#35 — Tachibana 起動時セッション生存検証 (findings 0015)。

ギャップ: login() の session_cache 分岐は is_session_valid_for_today (JST 日付
チェックのみ) で session を適用していた。同一 JST 日でも死んだ session
(p_errno="2" / 夜間閉局越え / サーバ無効化) を is_logged_in=True のまま掴み発注
経路に入りうる。validate_session_on_startup が純読取り REQUEST 1 発で liveness を
確認し、失効を SESSION_CACHE_EXPIRED に翻訳して再ログイン誘導へ落とす。

固定する契約:
1. validate_session_on_startup は生存 session (p_errno=0) でそのまま継続。
2. validate_session_on_startup は失効 session (p_errno=2) で SessionExpiredError。
3. login(session_cache) は失効 probe で SESSION_CACHE_EXPIRED を raise し、corpse を
   握らない (is_logged_in=False) ・EC ストリームを起動しない (発注経路を armしない)。
4. login(session_cache) は生存 probe で接続を継続し EC ストリームを起動する。
"""
from __future__ import annotations

import json
from datetime import datetime
from unittest.mock import Mock
from zoneinfo import ZoneInfo

import pytest

from engine.exchanges import tachibana_file_store
from engine.exchanges.tachibana import TachibanaAdapter
from engine.exchanges.tachibana_auth import (
    SessionExpiredError,
    validate_session_on_startup,
)
from engine.live.adapter import VenueCredentials


def _today_jst_cache() -> dict:
    base = "https://demo-kabuka.e-shiten.jp/e_api_v4r9/ND_TESTSESSION"
    return {
        "url_request": f"{base}/request/",
        "url_master": f"{base}/master/",
        "url_price": f"{base}/price/",
        "url_event": f"{base}/event/",
        "url_event_ws": "wss://demo-kabuka.e-shiten.jp/e_api_v4r9/event/ws",
        "zyoutoeki_kazei_c": "1",
        "last_p_no": 100,
        "issued_jst_date": datetime.now(ZoneInfo("Asia/Tokyo")).date().isoformat(),
    }


def _session_cache_creds() -> VenueCredentials:
    return VenueCredentials(
        credentials_source="session_cache", environment_hint="demo"
    )


# --- 1/2. validate_session_on_startup unit ---------------------------------


async def test_validate_session_on_startup_passes_on_live_session() -> None:
    calls: list[dict] = []

    async def fake_request(payload: dict) -> dict:
        calls.append(payload)
        return {"p_errno": "0", "sResultCode": "0"}

    await validate_session_on_startup(fake_request)

    assert len(calls) == 1
    assert calls[0]["sCLMID"] == "CLMZanKaiKanougaku"  # 純読取り probe


async def test_validate_session_on_startup_raises_on_expired() -> None:
    async def fake_request(payload: dict) -> dict:
        return {"p_errno": "2"}  # 仮想 URL 無効 = 失効

    with pytest.raises(SessionExpiredError):
        await validate_session_on_startup(fake_request)


# --- 3. login(session_cache) rejects an expired-but-same-day session -------


async def test_login_session_cache_rejects_expired_session(
    httpx_mock, monkeypatch: pytest.MonkeyPatch
) -> None:
    cache = _today_jst_cache()
    monkeypatch.setattr(tachibana_file_store, "load_session", lambda: cache)
    monkeypatch.setattr(
        tachibana_file_store, "is_session_valid_for_today", lambda d, **k: True
    )

    adapter = TachibanaAdapter(environment="demo")
    ec = Mock()
    monkeypatch.setattr(adapter, "_ensure_ec_stream", ec)

    # probe → dead virtual URL replies p_errno="2"
    httpx_mock.add_response(content=json.dumps({"p_errno": "2"}).encode("shift_jis"))

    with pytest.raises(ValueError, match="SESSION_CACHE_EXPIRED"):
        await adapter.login(_session_cache_creds())

    assert adapter.is_logged_in is False  # corpse を握らない
    ec.assert_not_called()  # 発注経路 (EC stream) を arm しない
    await adapter._client.aclose()


# --- 4. login(session_cache) accepts a live session ------------------------


async def test_login_session_cache_clears_corpse_on_probe_transport_error(
    httpx_mock, monkeypatch: pytest.MonkeyPatch
) -> None:
    """probe が失効ではなく transport error で落ちても corpse を握らない (fail closed)。

    #35 が新たに足す probe round-trip は失敗し得る。expired (ApiError) でない例外
    でも login() は session を残さず (is_logged_in=False)・EC stream を起動しない。
    """
    import httpx

    cache = _today_jst_cache()
    monkeypatch.setattr(tachibana_file_store, "load_session", lambda: cache)
    monkeypatch.setattr(
        tachibana_file_store, "is_session_valid_for_today", lambda d, **k: True
    )

    adapter = TachibanaAdapter(environment="demo")
    ec = Mock()
    monkeypatch.setattr(adapter, "_ensure_ec_stream", ec)

    httpx_mock.add_exception(httpx.ConnectError("boom"))

    with pytest.raises(httpx.ConnectError):  # 元の semantics のまま伝播
        await adapter.login(_session_cache_creds())

    assert adapter.is_logged_in is False  # corpse を握らない
    ec.assert_not_called()
    await adapter._client.aclose()


async def test_login_session_cache_accepts_live_session(
    httpx_mock, monkeypatch: pytest.MonkeyPatch
) -> None:
    cache = _today_jst_cache()
    monkeypatch.setattr(tachibana_file_store, "load_session", lambda: cache)
    monkeypatch.setattr(
        tachibana_file_store, "is_session_valid_for_today", lambda d, **k: True
    )

    adapter = TachibanaAdapter(environment="demo")
    ec = Mock()
    monkeypatch.setattr(adapter, "_ensure_ec_stream", ec)

    httpx_mock.add_response(
        content=json.dumps({"p_errno": "0", "sResultCode": "0"}).encode("shift_jis")
    )

    await adapter.login(_session_cache_creds())

    assert adapter.is_logged_in is True
    ec.assert_called_once()
    await adapter._client.aclose()
