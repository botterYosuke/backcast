"""INV-T3-SECRET — 第二暗証番号の env/log 非保持の契約 (findings/0009)。

skill R10 / handoff 制約: 第二暗証番号 (sSecondPassword) は env にもファイルにも
置かず、発注時に SecretResolver 経由でメモリのみ取得する。ログにも出さない。

固定する契約:
1. env 非保持 — login form は第二暗証番号 env を一切読まない (FormInit に項目が
   無い)。tachibana モジュールに第二暗証番号 env 定数は存在しない。
2. SecretResolver が唯一の供給源 — TachibanaAdapter は secret_resolver 必須
   (None → ValueError)。kabu は第二暗証番号が無いため受理して無視する。
3. log 非保持 — submit_order 経路で第二暗証番号がログに出ない。
"""
from __future__ import annotations

import json
import logging
import pathlib

import pytest

from engine.exchanges.tachibana_login_form_state import FormInit, build_form_init

SENTINEL = "SECOND_PW_DO_NOT_LEAK_4242"


# --- 1. env 非保持 ---------------------------------------------------------


def test_login_form_ignores_second_password_env(monkeypatch) -> None:
    """第二暗証番号 env / DEV_* 資格情報を env に置いても FormInit はそれを surface しない。

    ADR-0027 D3: ログインダイアログは DEV_* を prefill しない (空欄で開く)。よって
    build_form_init は env を一切読まず、FormInit に資格情報フィールドを持たない。
    """
    monkeypatch.setenv("DEV_TACHIBANA_AUTH_ID_DEMO", "authid1")
    monkeypatch.setenv("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO", "/tmp/key.pem")
    monkeypatch.setenv("DEV_TACHIBANA_SECOND_PASSWORD", SENTINEL)
    monkeypatch.setenv("DEV_TACHIBANA_SECRET", SENTINEL)
    init = build_form_init("demo")
    assert isinstance(init, FormInit)
    # FormInit に資格情報フィールドは存在せず、どの値にも漏れていない。
    assert SENTINEL not in repr(init)
    assert "authid1" not in repr(init)  # ADR-0027 D3: prefill しない
    field_names = {f.lower() for f in vars(init)}
    assert not any("second" in n or "secret" in n for n in field_names)
    assert not any("dev_" in n or "auth_id" in n or "private_key" in n for n in field_names)


def test_no_second_password_env_constant_in_tachibana_sources() -> None:
    """tachibana モジュールが第二暗証番号を env から読む箇所が無い (source scan)。"""
    exchanges = pathlib.Path(__file__).resolve().parent.parent / "engine" / "exchanges"
    for src in exchanges.glob("tachibana*.py"):
        text = src.read_text(encoding="utf-8")
        for line in text.splitlines():
            if "environ" in line or "getenv" in line:
                low = line.lower()
                assert "second" not in low and "secondpassword" not in low, (
                    f"{src.name}: 第二暗証番号を env から読んでいる疑い: {line.strip()!r}"
                )


# --- 2. SecretResolver が唯一の供給源 --------------------------------------


class _FakeResolver:
    def __init__(self, secret: str) -> None:
        self.secret = secret
        self.calls: list[tuple[str, str]] = []

    async def resolve(self, venue: str, purpose: str) -> str:
        self.calls.append((venue, purpose))
        return self.secret


def test_tachibana_requires_secret_resolver() -> None:
    from engine.exchanges.tachibana import TachibanaAdapter

    adapter = TachibanaAdapter(environment="demo")
    with pytest.raises(ValueError):
        adapter.set_execution_hooks(
            secret_resolver=None, on_order_event=lambda _e: None
        )


def test_kabu_accepts_and_ignores_secret_resolver() -> None:
    """kabu は第二暗証番号が無いので secret_resolver を受理して無視する。"""
    from engine.exchanges.kabusapi_execution import KabuExecutionEngine

    eng = KabuExecutionEngine(
        client=object(), rl=object(), env="verify", time_source=lambda: 0.0
    )
    cb = lambda _e: None  # noqa: E731
    eng.set_execution_hooks(secret_resolver=_FakeResolver("ignored"), on_order_event=cb)
    assert eng._on_order_event is cb


# --- 3. log 非保持 ----------------------------------------------------------


async def test_submit_order_does_not_log_second_password(
    httpx_mock, caplog: pytest.LogCaptureFixture
) -> None:
    """submit_order が第二暗証番号をログに出さない (発注経路の behavioral)。"""
    from engine.exchanges.tachibana import TachibanaAdapter
    from engine.exchanges.tachibana_auth import TachibanaSession
    from engine.exchanges.tachibana_url import (
        EventUrl,
        MasterUrl,
        PriceUrl,
        RequestUrl,
    )

    # 仮想 URL は session-secret (ND= token を埋め込む)。漏洩したら検知できるよう
    # 識別可能な marker を path に入れておく。
    url_secret = "ND_SESSION_SECRET_9f3a"
    base = f"https://demo-kabuka.e-shiten.jp/e_api_v4r9/{url_secret}"
    session = TachibanaSession(
        url_request=RequestUrl(f"{base}/request/"),
        url_master=MasterUrl(f"{base}/master/"),
        url_price=PriceUrl(f"{base}/price/"),
        url_event=EventUrl(f"{base}/event/"),
        url_event_ws=f"wss://demo-kabuka.e-shiten.jp/e_api_v4r9/event/ws",
        zyoutoeki_kazei_c="1",
    )

    adapter = TachibanaAdapter(environment="demo")
    resolver = _FakeResolver(SENTINEL)
    # session=None のうちに hooks を入れて EC ストリーム起動を回避し、その後 session を注入。
    adapter.set_execution_hooks(secret_resolver=resolver, on_order_event=lambda _e: None)
    adapter._session = session

    # 業務リジェクト (p_errno=0, sResultCode!=0) を SJIS で返す。
    reject = {"p_errno": "0", "sResultCode": "1", "sResultText": "残高不足"}
    httpx_mock.add_response(content=json.dumps(reject).encode("shift_jis"))

    with caplog.at_level(logging.DEBUG):
        result = await adapter.submit_order(
            venue="TACHIBANA",
            instrument_id="7203.TSE",
            side="buy",
            qty=100,
            price=None,
            order_type="MARKET",
            time_in_force="DAY",
        )

    assert result.status == "REJECTED"
    assert resolver.calls == [("TACHIBANA", "new_order")]  # resolver が唯一の供給源
    # 第二暗証番号も session-secret な仮想 URL も httpx request ログも出ていない (R10)。
    assert SENTINEL not in caplog.text
    assert url_secret not in caplog.text
    assert "HTTP Request" not in caplog.text  # httpx INFO request ログが沈黙している
    # ただしリクエストは正常に送信され、秘密は wire 上には乗っている (ログ非保持だけ)。
    sent = httpx_mock.get_requests()
    assert len(sent) == 1
    assert SENTINEL in str(sent[0].url)  # venue へは確かに送っている
    await adapter._client.aclose()


async def test_auth_login_does_not_log_credentials_without_adapter(
    httpx_mock, caplog: pytest.LogCaptureFixture
) -> None:
    """adapter を経由しない直接 login (login dialog 経路・#122 で in-process tkinter) でも creds を
    ログに出さない。v4r9: R2 により sAuthId は URL に乗るため、httpx INFO の request
    ログ抑制が login() 自身でも効いている必要がある (#19 Finding 1 / ADR-0023)。"""
    from Cryptodome.PublicKey import RSA

    from engine.exchanges.tachibana_auth import ApiError, PNoCounter, login

    leak_auth_id = "LEAK_AUTHID_7e1c"
    test_key = RSA.generate(2048)  # 復号前に check_response で弾かれるので未使用だが型は要る
    # ログイン失敗 (p_errno != "0") を SJIS で返す → ApiError。POST は実行され URL に
    # sAuthId が乗るので、抑制が無ければここで漏洩する。
    httpx_mock.add_response(
        content=json.dumps({"p_errno": "1", "sResultCode": "1"}).encode("shift_jis")
    )

    with caplog.at_level(logging.DEBUG):
        with pytest.raises(ApiError):  # p_errno != "0" → 業務エラー (型は本題でない)
            await login(
                leak_auth_id, test_key, is_demo=True, p_no_counter=PNoCounter()
            )

    assert leak_auth_id not in caplog.text
    assert "HTTP Request" not in caplog.text  # httpx INFO request ログが沈黙
    # sAuthId は wire 上には乗っている (ログ非保持だけ)。
    sent = httpx_mock.get_requests()
    assert sent and leak_auth_id in str(sent[0].url)
