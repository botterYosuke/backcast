"""INV-K5-ERRCODE — kabu STATION エラーコード分類の契約 (findings/0009)。

移植元 (TTWR) から継承した誤った定数対応を、一次資料 (kabusapi skill R7 /
ptal/error.html, 2026-05-20 検証) に基づき訂正したことを固定する RED→fix テスト。

正しい契約 (一次資料):
- HTTP 429            → スロットリング      → KabuRateLimitError
- body Code 4002006   → レジスト数エラー(50上限) → KabuRegisterFullError
- body Code 4002001   → 銘柄が見つからない   → generic KabuApiError (上記 subclass ではない)
- body Code 4001005   → パラメータ変換エラー   → KabuTokenExpiredError (現状維持・回帰ガード)

移植時の現挙動は 4002006↔4002001 が register-full/rate-limit で入れ替わり、
流量超過を body Code 扱いにしていた (kabusapi_auth.py)。これは characterization
で温存せず訂正する (owner 決定 2026-06-13, findings/0009 §错误码)。
"""
from __future__ import annotations

import pytest

from engine.exchanges.kabusapi_auth import (
    KabuApiError,
    KabuRateLimitError,
    KabuRegisterFullError,
    KabuTokenExpiredError,
    check_response,
)


def test_http_429_is_rate_limit() -> None:
    """流量超過は body Code ではなく HTTP 429 で来る → KabuRateLimitError。"""
    with pytest.raises(KabuRateLimitError):
        check_response({"Message": "スロットリング制限エラー"}, 429)


def test_body_4002006_is_register_full() -> None:
    """4002006 = レジスト数エラー (登録銘柄 50 上限) → KabuRegisterFullError。"""
    with pytest.raises(KabuRegisterFullError):
        check_response({"Code": 4002006, "Message": "レジスト数エラー"}, 200)


def test_body_4002001_is_generic_not_register_full() -> None:
    """4002001 = 銘柄が見つからない。register-full でも rate-limit でもない generic。"""
    with pytest.raises(KabuApiError) as exc_info:
        check_response({"Code": 4002001, "Message": "銘柄が見つかりません"}, 200)
    assert not isinstance(exc_info.value, KabuRegisterFullError)
    assert not isinstance(exc_info.value, KabuRateLimitError)
    assert exc_info.value.code == 4002001


def test_body_4001005_is_generic_not_token_expired() -> None:
    """4001005 = パラメータ変換エラー (R7:231)。token-expired ではない → generic。

    移植元は 4001005→KabuTokenExpiredError と誤分類し、パラメータ不正で
    不要な再認証を誘発していた。一次資料に基づき generic KabuApiError へ訂正。
    """
    with pytest.raises(KabuApiError) as exc_info:
        check_response({"Code": 4001005, "Message": "パラメータ変換エラー"}, 200)
    assert not isinstance(exc_info.value, KabuTokenExpiredError)
    assert exc_info.value.code == 4001005


def test_http_401_is_token_expired() -> None:
    """token 失効・未認証は HTTP 401 で来る (R7:230) → KabuTokenExpiredError。"""
    with pytest.raises(KabuTokenExpiredError):
        check_response({"Message": "認証エラー（トークン不正など）"}, 401)


def test_code_zero_is_ok() -> None:
    """Code==0 / Code 非存在は正常 (例外なし)。"""
    check_response({"Code": 0}, 200)
    check_response({}, 200)


def test_rate_limit_error_carries_429_status_code() -> None:
    """KabuRateLimitError は HTTP 429 を code として運ぶ (body Code が無くても)。"""
    with pytest.raises(KabuRateLimitError) as exc_info:
        check_response({}, 429)
    assert exc_info.value.code == 429


def test_rate_limit_code_is_429_even_with_body_code() -> None:
    """429 応答に body Code が混じっても .code は決定的に 429（分類子は HTTP status）。"""
    with pytest.raises(KabuRateLimitError) as exc_info:
        check_response({"Code": 4001006, "Message": "API実行回数エラー"}, 429)
    assert exc_info.value.code == 429
