"""ADR-0033 — ログインダイアログの資格情報 prefill (demo/verify・debug ビルド限定) 回帰ゲート。

方針: ADR-0033（ADR-0027 D3 を supersede）。固定する不変条件（delete-the-production-logic
litmus で RED→GREEN が立つ）:

- PREFILL-01: tachibana build_form_init("demo") は debug ビルドで DEV_TACHIBANA_AUTH_ID_DEMO /
  DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO を FormInit.auth_id_prefill / key_path_prefill に載せる。
- PREFILL-02: tachibana build_form_init("prod") は prod 資格情報を一切 prefill しない（demo/prod
  両方の env が set されていても prefill は空）。run_dialog はラジオ切替時に build_form_init(mode)
  を呼び直すので、これが「Prod 切替で欄をクリア」の SoT。
- PREFILL-03: kabu build_form_init("verify") は debug ビルドで DEV_KABU_API_PASSWORD を
  FormInit.api_password_prefill に載せる。
- PREFILL-04: kabu build_form_init("prod") は PROD_KABU_API_PASSWORD を prefill しない（実弾は手入力）。
- PREFILL-05: release ビルド (IS_DEBUG_BUILD False) では demo/verify でも一切 prefill しない。

env 読みは presenter (build_form_init) に閉じる契約は PRODGATE-08（test_prod_gate_abolished.py）が
別途固定する（run_dialog は os.environ を直接読まない）。
"""
from __future__ import annotations

import pytest

from engine.exchanges import kabusapi_login_form_state as kabu_form
from engine.exchanges import tachibana_login_form_state as tachi_form

_AUTH = "AUTHID_DEMO_SENTINEL_48"
_KEY = "/path/to/demo_key.pem"
_KABU_PW = "KABU_VERIFY_PW_SENTINEL"
_BUILD_MODE = "engine.live._build_mode.IS_DEBUG_BUILD"


# --- PREFILL-01: tachibana demo は prefill する -----------------------------------


@pytest.mark.scenario("PREFILL-01")
def test_tachibana_demo_prefills_from_env(monkeypatch):
    monkeypatch.setattr(_BUILD_MODE, True)
    monkeypatch.setenv("DEV_TACHIBANA_AUTH_ID_DEMO", _AUTH)
    monkeypatch.setenv("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO", _KEY)
    init = tachi_form.build_form_init("demo")
    assert init.initial_mode == "demo"
    assert init.auth_id_prefill == _AUTH
    assert init.key_path_prefill == _KEY


# --- PREFILL-02: tachibana prod は prefill しない（prod キーも読まない） ------------


@pytest.mark.scenario("PREFILL-02")
def test_tachibana_prod_never_prefills(monkeypatch):
    monkeypatch.setattr(_BUILD_MODE, True)
    # demo / prod 両方の env を置いても prod モードは空で開く。
    monkeypatch.setenv("DEV_TACHIBANA_AUTH_ID_DEMO", _AUTH)
    monkeypatch.setenv("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO", _KEY)
    monkeypatch.setenv("DEV_TACHIBANA_AUTH_ID", "PROD_AUTHID_DO_NOT_PREFILL")
    monkeypatch.setenv("DEV_TACHIBANA_PRIVATE_KEY_PATH", "/prod/key.pem")
    init = tachi_form.build_form_init("prod")
    assert init.initial_mode == "prod"
    assert init.auth_id_prefill == ""
    assert init.key_path_prefill == ""


# --- PREFILL-03: kabu verify は prefill する --------------------------------------


@pytest.mark.scenario("PREFILL-03")
def test_kabu_verify_prefills_from_env(monkeypatch):
    monkeypatch.setattr(_BUILD_MODE, True)
    monkeypatch.setenv("DEV_KABU_API_PASSWORD", _KABU_PW)
    init = kabu_form.build_form_init("verify")
    assert init.station_port == 18081
    assert init.api_password_prefill == _KABU_PW


# --- PREFILL-04: kabu prod は prefill しない（実弾は手入力） -----------------------


@pytest.mark.scenario("PREFILL-04")
def test_kabu_prod_never_prefills(monkeypatch):
    monkeypatch.setattr(_BUILD_MODE, True)
    monkeypatch.setenv("DEV_KABU_API_PASSWORD", _KABU_PW)
    monkeypatch.setenv("PROD_KABU_API_PASSWORD", "PROD_PW_DO_NOT_PREFILL")
    init = kabu_form.build_form_init("prod")
    assert init.station_port == 18080
    assert init.api_password_prefill == ""


# --- PREFILL-05: release ビルドは prefill しない ----------------------------------


@pytest.mark.scenario("PREFILL-05")
def test_release_build_does_not_prefill(monkeypatch):
    monkeypatch.setattr(_BUILD_MODE, False)
    monkeypatch.setenv("DEV_TACHIBANA_AUTH_ID_DEMO", _AUTH)
    monkeypatch.setenv("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO", _KEY)
    monkeypatch.setenv("DEV_KABU_API_PASSWORD", _KABU_PW)
    tachi = tachi_form.build_form_init("demo")
    kabu = kabu_form.build_form_init("verify")
    assert tachi.auth_id_prefill == ""
    assert tachi.key_path_prefill == ""
    assert kabu.api_password_prefill == ""
