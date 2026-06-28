"""KABU-AUTH-REJECT-01 — login ダイアログの HTTP 401 は「トークン期限切れ」ではなく
「API パスワード不正」として提示する (findings 0109 / D1)。

機序: login flow の唯一の auth 呼び出しは /token (fetch_token)。その HTTP 401 実体は
kabu code 4001013「kabuステーションはログイン済みだが API パスワードが不正」(ptal/error.html)
であり、トークン失効ではない (発行時点で失効すべき既存トークンは無い)。

回帰の RED: KabuTokenExpiredError(401) を KABU_TOKEN_EXPIRED にマップし「トークン期限切れ」と
表示する旧挙動に戻すと、本テストが FAIL する。
"""
from __future__ import annotations

import pytest

from engine.exchanges.kabusapi_auth import (
    KabuApiError,
    KabuConnectionError,
    KabuTokenExpiredError,
)
# #181/ADR-0040: the kabu exception→code mapping moved from the retired
# kabusapi_login_flow (tkinter dialog) to the headless auth module.
from engine.exchanges.venue_login_headless import _map_kabu_exception as _map_exception
from engine.exchanges.kabusapi_login_form_state import (
    AUTH_FAILED,
    KABU_API_DISABLED,
    KABU_AUTH_REJECTED,
    KABU_STATION_NOT_LOGGED_IN,
    KABU_STATION_NOT_RUNNING,
    auth_failure_view,
)


@pytest.mark.scenario("KABU-AUTH-REJECT-01")
def test_http_401_with_wrong_password_code_maps_to_auth_rejected() -> None:
    """4001013 (ログイン済みだが API パスワード不正) → KABU_AUTH_REJECTED。"""
    exc = KabuTokenExpiredError(401, "トークン取得失敗", body_code=4001013)
    assert _map_exception(exc) == KABU_AUTH_REJECTED


@pytest.mark.scenario("KABU-AUTH-REJECT-01")
def test_http_401_with_logged_out_code_maps_to_not_logged_in() -> None:
    """4001007 / 4001017 (本体が口座へ未ログイン) → KABU_STATION_NOT_LOGGED_IN。

    owner 環境の実 prod 応答が 4001007 を返したことの empirical 回帰 (findings 0109)。
    旧実装はこれを「トークン期限切れ」と誤表示し「API パスワードを確認」と誤誘導していた。
    """
    assert _map_exception(KabuTokenExpiredError(401, "ログイン認証エラー", body_code=4001007)) \
        == KABU_STATION_NOT_LOGGED_IN
    assert _map_exception(KabuTokenExpiredError(401, "ログイン認証エラー", body_code=4001017)) \
        == KABU_STATION_NOT_LOGGED_IN


@pytest.mark.scenario("KABU-AUTH-REJECT-01")
def test_http_401_unknown_body_code_falls_back_to_auth_rejected() -> None:
    """body code 不明 (None 等) の 401 は安全側の KABU_AUTH_REJECTED に落とす。"""
    assert _map_exception(KabuTokenExpiredError(401, "認証エラー")) == KABU_AUTH_REJECTED


@pytest.mark.scenario("KABU-AUTH-REJECT-01")
def test_views_are_accurate_and_not_token_expired() -> None:
    """どの 401 文言も「トークン期限切れ」を出さない。語彙が原因に一致する。"""
    rejected = auth_failure_view(KABU_AUTH_REJECTED)
    assert "API パスワード" in rejected.status_text and "正しく" in rejected.status_text
    assert "期限切れ" not in rejected.status_text
    assert rejected.allow_retry is True

    logged_out = auth_failure_view(KABU_STATION_NOT_LOGGED_IN)
    assert "本体" in logged_out.status_text and "ログイン" in logged_out.status_text
    assert "期限切れ" not in logged_out.status_text
    assert logged_out.allow_retry is True


@pytest.mark.scenario("KABU-AUTH-REJECT-01")
def test_other_failure_mappings_unchanged() -> None:
    """誤って 401 以外まで auth-rejected に巻き込んでいないことの境界ガード。"""
    assert _map_exception(KabuConnectionError("body down")) == KABU_STATION_NOT_RUNNING
    assert _map_exception(KabuApiError(4001003, "API 利用不可")) == KABU_API_DISABLED
    # 4001005 = パラメータ変換エラー (token-expired ではない) → 汎用 AUTH_FAILED。
    assert _map_exception(KabuApiError(4001005, "パラメータ変換エラー")) == AUTH_FAILED
