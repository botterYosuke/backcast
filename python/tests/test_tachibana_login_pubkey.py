"""#92 / ADR-0023 — 立花 v4r9 公開鍵認証ログインの回帰ゲート。

固定する契約（RED→GREEN litmus は production の復号ロジックを消すと必ず落ちる）:
1. login() は `sAuthId` を送り、`sUserId`/`sPassword` を送らない。
2. 応答の暗号化仮想 URL 5 本を秘密鍵で復号し、TachibanaSession に復号済み URL を載せる。
3. `sUrlRequest` 空（暗号化文字列が空）＝契約締結前書面 未読 → UnreadNoticesError。
4. `p_errno="9"` → ServiceOutOfHoursError（利用時間外）。
5. 間違った秘密鍵では復号に失敗する（PubkeyCryptoError）＝復号が実際に効いている証明。

復号は OAEP/SHA256。テストは pycryptodome でテスト用 RSA 鍵を生成し、公開鍵で URL を暗号化
→ base64 → shift_jis JSON 応答を mock し、login() が秘密鍵で復号して https/wss を取り戻すことを assert。
"""
from __future__ import annotations

import base64
import json

import pytest
from Cryptodome.Cipher import PKCS1_OAEP
from Cryptodome.Hash import SHA256
from Cryptodome.PublicKey import RSA

from engine.exchanges.tachibana_auth import (
    PNoCounter,
    ServiceOutOfHoursError,
    UnreadNoticesError,
    login,
)
from engine.exchanges.tachibana_pubkey import PubkeyCryptoError

# 復号後の平文（_validate_virtual_urls が https:// / wss:// を要求する）。
_PLAIN = {
    "sUrlRequest": "https://demo-kabuka.e-shiten.jp/e_api_v4r9/ND_REQ_9f3a/request/",
    "sUrlMaster": "https://demo-kabuka.e-shiten.jp/e_api_v4r9/ND_MST_9f3a/master/",
    "sUrlPrice": "https://demo-kabuka.e-shiten.jp/e_api_v4r9/ND_PRC_9f3a/price/",
    "sUrlEvent": "https://demo-kabuka.e-shiten.jp/e_api_v4r9/ND_EVT_9f3a/event/",
    "sUrlEventWebSocket": "wss://demo-kabuka.e-shiten.jp/e_api_v4r9/event_ws/ND_WS_9f3a/",
}


def _keypair():
    key = RSA.generate(2048)
    return key, key.publickey()


def _encrypt(plaintext: str, pub_key) -> str:
    cipher = PKCS1_OAEP.new(pub_key, hashAlgo=SHA256)
    return base64.b64encode(cipher.encrypt(plaintext.encode("utf-8"))).decode("ascii")


def _encrypted_login_response(pub_key, *, extra: dict | None = None) -> bytes:
    payload = {
        "p_no": "1",
        "p_errno": "0",
        "sResultCode": "0",
        "sZyoutoekiKazeiC": "1",
        **{k: _encrypt(v, pub_key) for k, v in _PLAIN.items()},
    }
    if extra:
        payload.update(extra)
    return json.dumps(payload).encode("shift_jis")


# --- 1/2. happy path: sAuthId 送信 + 仮想 URL 復号 --------------------------------


async def test_login_sends_auth_id_and_decrypts_virtual_urls(httpx_mock) -> None:
    priv, pub = _keypair()
    httpx_mock.add_response(content=_encrypted_login_response(pub))

    session = await login(
        "AUTHID_7e1c", priv, is_demo=True, p_no_counter=PNoCounter()
    )

    # 2: 復号済み URL が TachibanaSession に載る。
    assert str(session.url_request) == _PLAIN["sUrlRequest"]
    assert str(session.url_master) == _PLAIN["sUrlMaster"]
    assert str(session.url_price) == _PLAIN["sUrlPrice"]
    assert str(session.url_event) == _PLAIN["sUrlEvent"]
    assert session.url_event_ws == _PLAIN["sUrlEventWebSocket"]
    assert session.zyoutoeki_kazei_c == "1"

    # 1: リクエスト URL に sAuthId が乗り、sUserId/sPassword は乗らない。
    sent = httpx_mock.get_requests()
    assert len(sent) == 1
    url = str(sent[0].url)
    assert "sAuthId" in url
    assert "AUTHID_7e1c" in url
    assert "sUserId" not in url
    assert "sPassword" not in url


# --- 3. 未読書面 = sUrlRequest 空 ------------------------------------------------


async def test_login_empty_url_request_raises_unread_notices(httpx_mock) -> None:
    priv, pub = _keypair()
    # p_errno=0/sResultCode=0 でも sUrlRequest が空 → 契約締結前書面 未読。
    resp = _encrypted_login_response(pub, extra={"sUrlRequest": ""})
    httpx_mock.add_response(content=resp)

    with pytest.raises(UnreadNoticesError):
        await login("AUTHID_7e1c", priv, is_demo=True, p_no_counter=PNoCounter())


# --- 4. p_errno=9 = 利用時間外 ---------------------------------------------------


async def test_login_p_errno_9_raises_service_out_of_hours(httpx_mock) -> None:
    priv, _ = _keypair()
    httpx_mock.add_response(
        content=json.dumps({"p_errno": "9", "p_err": "service stopped"}).encode(
            "shift_jis"
        )
    )

    with pytest.raises(ServiceOutOfHoursError):
        await login("AUTHID_7e1c", priv, is_demo=True, p_no_counter=PNoCounter())


# --- 5. 間違った鍵では復号できない（復号が実際に効いている証明）-------------------


async def test_login_wrong_private_key_fails_to_decrypt(httpx_mock) -> None:
    _, pub = _keypair()  # 暗号化に使う公開鍵
    wrong_priv, _ = _keypair()  # 対応しない別の秘密鍵
    httpx_mock.add_response(content=_encrypted_login_response(pub))

    with pytest.raises(PubkeyCryptoError):
        await login(
            "AUTHID_7e1c", wrong_priv, is_demo=True, p_no_counter=PNoCounter()
        )


# --- 6. demo/prod は別セット — env の取り違えを構造的に防ぐ -----------------------


def _write_pem(tmp_path, name: str) -> str:
    key = RSA.generate(2048)
    p = tmp_path / name
    p.write_bytes(key.export_key())
    return str(p)


def test_resolve_credentials_demo_and_prod_use_separate_env(tmp_path) -> None:
    from engine.exchanges.tachibana_credentials import (
        CredentialsError,
        resolve_credentials,
    )

    demo_pem = _write_pem(tmp_path, "demo.pem")
    prod_pem = _write_pem(tmp_path, "prod.pem")
    # owner 規約: 本番=無印、デモ=_DEMO サフィックス。
    env = {
        "DEV_TACHIBANA_AUTH_ID_DEMO": "DEMO_AUTHID",
        "DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO": demo_pem,
        "DEV_TACHIBANA_AUTH_ID": "PROD_AUTHID",
        "DEV_TACHIBANA_PRIVATE_KEY_PATH": prod_pem,
    }

    demo = resolve_credentials(is_demo=True, is_debug_build=True, env=env)
    prod = resolve_credentials(is_demo=False, is_debug_build=True, env=env)
    assert demo.auth_id == "DEMO_AUTHID"  # demo は _DEMO を読む
    assert prod.auth_id == "PROD_AUTHID"  # prod は無印を読む。互いを拾わない

    # demo 側 env が無いと、prod(無印) creds があっても demo は解決しない（取り違え防止）。
    prod_only = {
        "DEV_TACHIBANA_AUTH_ID": "PROD_AUTHID",
        "DEV_TACHIBANA_PRIVATE_KEY_PATH": prod_pem,
    }
    with pytest.raises(CredentialsError):
        resolve_credentials(is_demo=True, is_debug_build=True, env=prod_only)


def test_resolve_credentials_dev_env_gated_by_debug_build(tmp_path) -> None:
    from engine.exchanges.tachibana_credentials import (
        CredentialsError,
        resolve_credentials,
    )

    env = {
        "DEV_TACHIBANA_AUTH_ID_DEMO": "DEMO_AUTHID",
        "DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO": _write_pem(tmp_path, "k.pem"),
    }
    # release ビルドでは dev env を読まない（R10 / S1）。
    with pytest.raises(CredentialsError):
        resolve_credentials(is_demo=True, is_debug_build=False, env=env)
