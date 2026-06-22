"""Standalone Tachibana EVENT WebSocket probe — issue #92 reproduction harness.

Issue #92: Tachibana demo の EVENT WebSocket が `ST p_errno=2` (仮想URL無効) で
接続を 100% 拒否される。issue #85 / #92 では Unity batchmode を hypothesise →
fix ループの単位にしたため 1 試行 75s + rate-limit `p_errno=-3` に詰まった。

この probe は `diagnose` skill の **minimise** step:
  fresh login → build_event_url で subscribe URL を組む（production と同一経路）→
  WS connect → 最初の ST frame の p_errno を観測 → PASS/FAIL を 1 行で判定。

demo session 1 回 + WS connect 1 本 = 数秒で #92 の現状を判定できる。

Usage (from python/ dir):
    uv run python scripts/tachibana_ws_probe.py [TICKER]

Defaults: TICKER=7203, demo env. v4r9 公開鍵認証: credentials are resolved via
`tachibana_credentials.resolve_credentials(is_demo=True)` — demo は `_DEMO` サフィックス
(`DEV_TACHIBANA_AUTH_ID_DEMO` + `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`、または Fernet
`secure_config_demo.enc` + `API_DECRYPT_KEY_DEMO`) を project-root `.env` から読む。
場中 (market open) required for ST subscription to be accepted — run during TSE hours.
"""

from __future__ import annotations

import asyncio
import logging
import os
import re
import ssl
import sys
from pathlib import Path

# --- repo paths -------------------------------------------------------------
_HERE = Path(__file__).resolve()
_PY_ROOT = _HERE.parent.parent           # python/
_REPO_ROOT = _PY_ROOT.parent             # backcast/
sys.path.insert(0, str(_PY_ROOT))

import certifi  # noqa: E402
import websockets  # noqa: E402

from engine.exchanges.tachibana_auth import PNoCounter, login  # noqa: E402
from engine.exchanges.tachibana_codec import (  # noqa: E402
    decode_response_body,
    parse_event_frame,
)
from engine.exchanges.tachibana_file_store import session_file_path  # noqa: E402
from engine.exchanges.tachibana_url import EventUrl, build_event_url  # noqa: E402

log = logging.getLogger("ws_probe")

# Windows console は cp932。server 由来の rejection 理由 (em-dash 等) を print する
# 際に UnicodeEncodeError で本来の例外を握り潰さないよう、stdout/stderr を UTF-8 化。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
    except Exception:  # noqa: BLE001
        pass


def _load_dotenv() -> dict[str, str]:
    """Minimal .env parser (no python-dotenv dependency)."""
    env: dict[str, str] = {}
    path = _REPO_ROOT / ".env"
    if not path.exists():
        return env
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, val = line.partition("=")
        env[key.strip()] = val.strip().strip('"').strip("'")
    return env


def _mask(url: str) -> str:
    return re.sub(r"ND=[^/?&]+", "ND=<token>", url)


async def _probe(ticker: str, is_demo: bool = True) -> int:
    env = {**_load_dotenv(), **os.environ}
    env_label = "demo" if is_demo else "PROD"
    # v4r9 公開鍵認証 (ADR-0023): 認証ID + RSA 秘密鍵を Fernet/dev-env から解決する。
    # demo/prod は別セット (is_demo で読む env を切替: demo=_DEMO / prod=無印)。
    from engine.exchanges.tachibana_credentials import (  # noqa: E402
        CredentialsError,
        resolve_credentials,
    )
    from engine.exchanges.tachibana_pubkey import PubkeyCryptoError  # noqa: E402

    if not is_demo:
        # READ-ONLY guard: この probe は発注しない (login + WS 購読のみ)。本番接続だが
        # 実弾リスクは無い。明示意思 (prod 引数) でのみ到達する。
        print("[WS-PROBE] ⚠ PROD (本番) — READ-ONLY: login + EVENT WS subscribe only, NO orders.")

    try:
        creds = resolve_credentials(is_demo=is_demo, is_debug_build=True, env=env)
    except (CredentialsError, PubkeyCryptoError) as exc:
        print(f"[WS-PROBE FAIL] credentials not configured: {exc}")
        return 2

    # --- candidate #1: stale server-side sessions — drop the local cache so we
    #     always force a fresh login (no resume of a possibly-orphaned session).
    cache = session_file_path()
    if cache.exists():
        cache.unlink()
        print(f"[WS-PROBE] removed stale session cache: {cache}")

    # --- fresh login (v4r9 sAuthId + private-key decrypt) --------------------
    print(f"[WS-PROBE] login ({env_label}) auth_id={creds.auth_id[:3]}*** ...")
    session = await login(
        creds.auth_id, creds.private_key, is_demo=is_demo, p_no_counter=PNoCounter()
    )
    ws_url = session.url_event_ws
    print(f"[WS-PROBE] login OK. url_event_ws = {_mask(ws_url)!r}")
    print(f"[WS-PROBE]   starts_with_wss={ws_url.startswith('wss://')} len={len(ws_url)}")

    # --- build subscribe URL via the EXACT production path (tachibana.py:417) -
    sub_url = build_event_url(
        EventUrl(ws_url),
        {
            "p_rid": "22",
            "p_board_no": "1000",
            "p_gyou_no": "1",
            "p_issue_code": ticker,
            "p_mkt_code": "00",
            "p_eno": "0",
            "p_evt_cmd": "ST,KP,EC,SS,US,FD",
        },
    )
    print(f"[WS-PROBE] subscribe url = {_mask(sub_url)}")

    tls_ctx = ssl.create_default_context(cafile=certifi.where())

    # --- connect & watch for the first ST frame ----------------------------
    first_st_errno: str | None = None
    frame_counts: dict[str, int] = {}
    deadline = 20.0
    print(f"[WS-PROBE] connecting (timeout {deadline:.0f}s, ticker={ticker}) ...")
    try:
        async with websockets.connect(
            sub_url, ping_interval=None, ssl=tls_ctx
        ) as ws:
            print("[WS-PROBE] TCP/TLS/WS handshake OK — connection established.")

            async def _recv() -> None:
                nonlocal first_st_errno
                async for raw in ws:
                    text = decode_response_body(raw) if isinstance(raw, bytes) else raw
                    fields = dict(parse_event_frame(text))
                    cmd = fields.get("p_cmd", "?")
                    frame_counts[cmd] = frame_counts.get(cmd, 0) + 1
                    if cmd == "ST":
                        errno = fields.get("p_errno", "?")
                        print(f"[WS-PROBE]   <ST> p_errno={errno} fields={fields}")
                        if first_st_errno is None:
                            first_st_errno = errno
                            return
                    else:
                        print(f"[WS-PROBE]   <{cmd}> ({len(fields)} fields)")

            try:
                await asyncio.wait_for(_recv(), timeout=deadline)
            except asyncio.TimeoutError:
                print(f"[WS-PROBE] no terminal ST frame within {deadline:.0f}s")
    except Exception as exc:  # noqa: BLE001
        print(f"[WS-PROBE FAIL] WS connect/handshake raised: {type(exc).__name__}: {exc}")
        return 1

    print(f"[WS-PROBE] frame counts: {frame_counts}")

    # --- verdict ------------------------------------------------------------
    # #92 の旧症状は「ST p_errno=2 'session inactive.' が即届いて 100% 拒否」。修正後の
    # サーバは ST エラーを送らず、購読が受理されると FD/KP/SS/US/EC の実フレームを stream する。
    # よって判定軸は「ST 拒否フレームの有無」＋「実データフレームが流れたか」。
    data_frames = {k: v for k, v in frame_counts.items() if k != "ST"}

    if first_st_errno not in (None, "", "0"):
        print(f"[WS-PROBE FAIL] ST p_errno={first_st_errno} — subscription REJECTED. "
              "#92 STILL REPRODUCES.")
        return 1
    if first_st_errno in ("", "0"):
        print(f"[WS-PROBE PASS] ST p_errno={first_st_errno} — subscription ACCEPTED. "
              "#92 is RESOLVED on this run.")
        return 0
    # No ST frame at all. If real EVENT frames streamed, the v4r9 session was
    # accepted (no 'session inactive.' rejection) — that IS the #92-resolved signal.
    if data_frames:
        print(f"[WS-PROBE PASS] no ST rejection and EVENT frames streamed "
              f"({data_frames}) — v4r9 session ACCEPTED by EVENT WS. #92 is RESOLVED.")
        return 0
    print("[WS-PROBE INCONCLUSIVE] connection succeeded but no frames arrived "
          "(market fully closed + no snapshot? wrong ticker?). #92 not reproducible.")
    return 3


def main() -> int:
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(name)s: %(message)s")
    ticker = sys.argv[1] if len(sys.argv) > 1 else "7203"
    # env arg: "prod" → 本番 (READ-ONLY: login + WS subscribe のみ・発注しない)。既定 demo。
    env_arg = sys.argv[2].lower() if len(sys.argv) > 2 else "demo"
    is_demo = env_arg != "prod"
    return asyncio.run(_probe(ticker, is_demo=is_demo))


if __name__ == "__main__":
    raise SystemExit(main())
