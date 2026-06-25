"""kabu STATION API auth & error helpers.

Implements:
- exception hierarchy (KabuError / KabuApiError / KabuTokenExpiredError /
  KabuRateLimitError / KabuConnectionError)
- check_response(payload, http_status): two-stage HTTP + Code judgement (R7)
- auth_headers(token): build X-API-KEY header (R3)
- fetch_token(api_password, env): POST /token, returns Token (R1+R3+R7+R10)
"""

from __future__ import annotations

import logging

import httpx

from engine.exchanges.kabusapi_url import Env, endpoint

logger = logging.getLogger(__name__)

# kabu code: 本体がブローカー口座へ未ログイン (R7, ptal/error.html)。4001007=ログイン認証エラー /
# 4001017=未ログイン状態。「アプリ起動済み」≠「口座ログイン済み」で、kabu は早朝に本体を強制ログアウト
# する。/token の HTTP 401 body code 判別 (findings 0109) と watchdog の logout 検知 (kabusapi.py)
# の単一正本。
STATION_LOGGED_OUT_CODES = frozenset({4001007, 4001017})


class KabuError(Exception):
    """Base class for all kabu STATION API failures."""


class KabuApiError(KabuError):
    def __init__(self, code: int | str, message: str) -> None:
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message


class KabuTokenExpiredError(KabuApiError):
    """HTTP 401 — token 失効 / 未認証 (R7:230)、caller must re-auth。

    token 期限切れ・未認証は body Code ではなく **HTTP 401** で返る (kabusapi skill
    R7, ptal/error.html 2026-05-20)。INV-K5-ERRCODE / findings/0009。
    (移植元は body 4001005 を token-expired と誤分類していたが、4001005 は
    「パラメータ変換エラー」であり再認証対象ではない。一次資料に基づき 401 へ訂正。)

    ``.code`` は契約どおり決定的に 401 だが、401 応答の body に載る kabu code は
    ``.body_code`` に温存する。/token の 401 は body code で意味が分かれる (findings 0109):
    4001007/4001017=本体未ログイン、4001013=ログイン済みだが API パスワード不正。
    """

    def __init__(self, code: int | str, message: str, *, body_code: int | None = None) -> None:
        super().__init__(code, message)
        self.body_code = body_code


class KabuRateLimitError(KabuApiError):
    """HTTP 429 — スロットリング (流量超過、R5/R7)。

    流量超過は body Code ではなく **HTTP 429** で返る (kabusapi skill R7,
    ptal/error.html 2026-05-20 検証)。INV-K5-ERRCODE / findings/0009。
    """


class KabuConnectionError(KabuError):
    """kabu STATION body process not reachable (connection refused etc.)."""


class KabuRegisterFullError(KabuApiError):
    """Code 4002006 — レジスト数エラー (登録銘柄 50 上限超過、R6/R7)。

    Q-K5: 暗黙 evict は行わない。INV-K1-CAP / INV-K5-ERRCODE / findings/0009。
    (移植元は 4002001 を誤用していた。一次資料に基づき 4002006 へ訂正。)
    """


def check_response(payload: dict, http_status: int) -> None:
    """Two-stage response validation per kabu skill R7 (INV-K5-ERRCODE).

    1. HTTP 401 → KabuTokenExpiredError (token 失効/未認証は HTTP status で来る)。
    2. HTTP 429 → KabuRateLimitError (流量超過も body Code ではなく HTTP status)。
    3. HTTP >= 400 (401/429 以外) → generic KabuApiError (attach Code/Message if present)。
    4. HTTP 2xx but payload Code != 0 → specialized subclass for known codes,
       otherwise generic KabuApiError.

    一次資料 (ptal/error.html 2026-05-20): 4002006=レジスト数エラー(50上限),
    4002001=銘柄が見つからない, 4001005=パラメータ変換エラー, 流量超過=HTTP 429,
    token 失効/未認証=HTTP 401。移植元の取り違え (4002006↔4002001 / 流量を body
    Code 扱い / 4001005 を token-expired 扱い) を訂正済み (findings/0009)。
    """
    if http_status >= 400:
        code = payload.get("Code", http_status) if isinstance(payload, dict) else http_status
        message = payload.get("Message", f"HTTP {http_status}") if isinstance(payload, dict) else f"HTTP {http_status}"
        # token 失効/未認証 (401)・流量超過 (429) の分類子は HTTP status。body に Code が
        # 混じっても .code は決定的に status とし（docstring 契約）、詳細は message に残す。
        if http_status == 401:
            raw_code = payload.get("Code") if isinstance(payload, dict) else None
            try:
                body_code = int(raw_code) if raw_code is not None else None
            except (TypeError, ValueError):
                body_code = None
            raise KabuTokenExpiredError(401, message, body_code=body_code)
        if http_status == 429:
            raise KabuRateLimitError(429, message)
        raise KabuApiError(code, message)

    if not isinstance(payload, dict):
        return

    code = payload.get("Code", 0)
    if code == 0:
        return

    message = payload.get("Message", "")
    if code == 4002006:
        raise KabuRegisterFullError(code, message)
    # 4001005 (パラメータ変換エラー) / 4002001 (銘柄が見つからない) ほかは generic に落とす。
    # token 失効は body Code ではなく HTTP 401 (上の分岐) で来る。
    raise KabuApiError(code, message)


def auth_headers(token: str) -> dict[str, str]:
    """Build kabu STATION auth header per R3.

    Raises ValueError if token is empty.
    """
    if not token:
        raise ValueError("token must be non-empty")
    return {"X-API-KEY": token}


async def fetch_token(api_password: str, *, env: Env) -> str:
    """POST /token. Returns the Token string on success (kabu skill R1+R3+R7+R10).

    Raises:
        KabuApiError: HTTP 4xx/5xx or non-zero Code in payload.
        KabuTokenExpiredError / KabuRateLimitError: specialized Code subclasses.
        KabuConnectionError: kabu body process unreachable, non-JSON body,
            or missing Token field on otherwise-OK response.
    """
    url = endpoint("token", env=env)
    try:
        async with httpx.AsyncClient(timeout=30.0) as client:
            resp = await client.post(url, json={"APIPassword": api_password})
    except httpx.ConnectError as exc:
        raise KabuConnectionError(
            f"kabu body process unreachable at {url}: {exc}"
        ) from exc

    try:
        body = resp.json()
    except Exception as exc:
        raise KabuConnectionError(
            f"kabu /token returned non-JSON body (HTTP {resp.status_code})"
        ) from exc

    # /token uses ResultCode (OpenAPI TokenSuccess) where other endpoints use Code.
    # Normalize so check_response's error mapping (token-expired / generic + HTTP 401/429)
    # applies uniformly and auth failures are not misclassified as KabuConnectionError.
    if isinstance(body, dict) and "Code" not in body and "ResultCode" in body:
        body = {**body, "Code": body["ResultCode"]}

    check_response(body, resp.status_code)

    token = body.get("Token") or ""
    if not token:
        raise KabuConnectionError("kabu /token response missing 'Token' field")

    masked = f"***{token[-4:]}" if len(token) >= 4 else "***"
    logger.info("kabu /token: 200 OK, token=%s", masked)
    return token
