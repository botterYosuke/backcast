"""KABU-LIVE-PROD-01 — owner HITL: 本番 kabuステーション (18080) への実ログイン (findings 0109 / D3)。

ADR-0027 D4 の「自動 runner は verify/demo 固定で本番非接触」に対する**唯一の意図的例外**。
owner HITL 専用で、二重ガードにより通常 CI では走らない:
  1. `PROD_KABU_API_PASSWORD` が env / .env に無ければ skip。
  2. prod 本体ポート 18080 が listen していなければ skip (本体未起動 / verify モード起動)。

前提 (揃えてから実行): kabuステーション本体を**本番モード**で起動・ログイン済み・API 有効、
`.env` に `PROD_KABU_API_PASSWORD`= 本体の API パスワード。

  cd python && uv run pytest tests/test_kabu_prod_login_live.py -v

credentials_source="env" + environment="prod" 経路で `PROD_KABU_API_PASSWORD` を読む (D2)。
"""
from __future__ import annotations

import asyncio
import os

import pytest

# import で repo-root .env を os.environ に流し込む (engine.paths._load_dotenv_once / setdefault)。
import engine.paths  # noqa: F401
from engine.exchanges.kabusapi import KabuStationAdapter, _ENV_API_PASSWORD_PROD
from engine.exchanges.kabusapi_auth import STATION_LOGGED_OUT_CODES, KabuTokenExpiredError
from engine.exchanges.kabusapi_login_form_state import probe_station
from engine.live.adapter import VenueCredentials

_PROD_PORT = 18080


@pytest.mark.scenario("KABU-LIVE-PROD-01")
def test_kabu_prod_login_via_env_credentials() -> None:
    api_pw = os.environ.get(_ENV_API_PASSWORD_PROD)
    if not api_pw:
        pytest.skip(
            f"{_ENV_API_PASSWORD_PROD} 未設定 — owner HITL 専用 (prod login をスキップ)"
        )
    if not probe_station(port=_PROD_PORT):
        pytest.skip(
            f"prod kabuステーション本体が 18080 で listen していない "
            f"(本番モードで起動・ログイン済みか確認)"
        )

    async def _run() -> None:
        adapter = KabuStationAdapter(environment="prod")
        try:
            try:
                await adapter.login(VenueCredentials(credentials_source="env"))
            except KabuTokenExpiredError as exc:
                # 本体が口座へ未ログイン (STATION_LOGGED_OUT_CODES) は前提未充足 → skip。
                # それ以外 (例: 4001013 = API パスワード不正) は本物の FAIL。
                if exc.body_code in STATION_LOGGED_OUT_CODES:
                    pytest.skip(
                        "prod kabuステーション本体が口座へ未ログイン "
                        f"(kabu code {exc.body_code}) — 本体でログインしてから再実行"
                    )
                raise
            assert adapter.is_logged_in(), (
                "login は返ったが token を保持していない "
                f"(last_error={adapter.last_error!r})"
            )
        finally:
            await adapter.logout()

    asyncio.run(_run())
