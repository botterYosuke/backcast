"""tachibana EVENT WS frame を demo / 本番から採取し、"本物そっくり" mock データを書き出す。

目的: live チャート/集約パイプラインの回帰を **実 venue に繋がず決定的に**回せる素材を作る
(findings 0117 の kabu mock と対称な codec-replayable fixture を tachibana 用に整備)。

採取は read-only (FD/KP/ST/EC/SS/US を subscribe しているだけで発注しない)。kabu の
`PUT /unregister/all` のような body グローバル副作用は無く、ticker hub の aclose は
ticker-local。ただし本番 (`prod`) 採取中は本番セッションを 1 本占有するので、別 GUI を
立ち上げている場合は競合を避けるため一旦閉じることを推奨する。

Run:
  cd python
  ./.venv/Scripts/python.exe spike/tachibana_capture_mock.py \
      "7203.TSE,8306.TSE,9984.TSE,285A.TSE" 150 demo

引数: [SYMBOLS(comma)] [WINDOW_SECONDS] [ENV(demo|prod)]
  - 既定 SYMBOLS = 主力高流動 4 銘柄 (kabu fixture と対称) / WINDOW = 150s / ENV = demo
  - demo は `demo-kabuka.e-shiten.jp` (`DEV_TACHIBANA_AUTH_ID_DEMO` +
    `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`)
  - prod は `kabuka.e-shiten.jp` (`DEV_TACHIBANA_AUTH_ID` +
    `DEV_TACHIBANA_PRIVATE_KEY_PATH`)。ADR-0027 で `TACHIBANA_ALLOW_PROD` は廃止——
    prod creds が解決できるだけで本番接続される

出力: python/spike/captures/tachibana_mock_<UTCstamp>.json
  {
    "meta": {...},
    "frames": [
      { "t_ms": <到着相対ms>, "recv": "<ISO>",
        "instrument_id": "7203.TSE", "frame_type": "FD"|"KP"|"ST"|"EC"|"SS"|"US",
        "fields": { "p_1_DPP": "...", "p_1_DV": "...", ... } },
      ...
    ]
  }

NOT a regression gate — 採取ツール。回帰の正本は採取後に作る Python 再生テスト
(`tachibana_replay_multi.py` / `tests/fixtures/tachibana_live_mock_4sym.json`)。

設計詳細: docs/findings/0118-tachibana-live-mock-fixture.md
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


def _load_env_var(var: str) -> None:
    """<repo>/.env から `var` を os.environ へ (値は表示しない)。

    kabu_capture_mock._load_env_password と同形。tachibana の env 解決は
    `tachibana_credentials.resolve_credentials` が os.environ を直接見るため、
    spike 実行前に `.env` から os.environ へ昇格させておく必要がある。
    """
    if os.environ.get(var):
        return
    repo_env = Path(__file__).resolve().parents[2] / ".env"
    if not repo_env.exists():
        return
    for line in repo_env.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if line.startswith(var + "="):
            _, _, val = line.partition("=")
            val = val.strip().strip('"').strip("'")
            if val:
                os.environ[var] = val
            return


async def main() -> int:
    import logging
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    logging.getLogger("engine.exchanges.tachibana").setLevel(logging.INFO)
    logging.getLogger("engine.exchanges.tachibana_ws").setLevel(logging.INFO)

    default_symbols = "7203.TSE,8306.TSE,9984.TSE,285A.TSE"
    symbols = [s.strip() for s in (sys.argv[1] if len(sys.argv) > 1 else default_symbols).split(",") if s.strip()]
    window_s = float(sys.argv[2]) if len(sys.argv) > 2 else 150.0
    env = sys.argv[3] if len(sys.argv) > 3 else "demo"
    if env not in ("demo", "prod"):
        print(f"FAIL: env must be demo|prod, got {env!r}")
        return 2

    # demo / prod で credential env が別セット (tachibana_credentials._DEMO_KEYS / _PROD_KEYS)。
    # .env から os.environ へ昇格させてから adapter に渡す。
    if env == "demo":
        _load_env_var("DEV_TACHIBANA_AUTH_ID_DEMO")
        _load_env_var("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO")
        if not (os.environ.get("DEV_TACHIBANA_AUTH_ID_DEMO")
                and os.environ.get("DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO")):
            print("FAIL: DEV_TACHIBANA_AUTH_ID_DEMO / DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO not in env or <repo>/.env")
            return 2
    else:
        _load_env_var("DEV_TACHIBANA_AUTH_ID")
        _load_env_var("DEV_TACHIBANA_PRIVATE_KEY_PATH")
        if not (os.environ.get("DEV_TACHIBANA_AUTH_ID")
                and os.environ.get("DEV_TACHIBANA_PRIVATE_KEY_PATH")):
            print("FAIL: DEV_TACHIBANA_AUTH_ID / DEV_TACHIBANA_PRIVATE_KEY_PATH not in env or <repo>/.env")
            return 2

    from engine.exchanges.tachibana import TachibanaAdapter
    from engine.live.adapter import DepthUpdate, TradesUpdate, VenueCredentials

    adapter = TachibanaAdapter(environment=env)  # type: ignore[arg-type]

    # ---- frame capture: wrap adapter._make_callback ---------------------------
    # tachibana は per-ticker WS hub × N で `(frame_type, fields, recv_ts_ms)` を
    # fanout する構造 (kabu の single `_on_frame(dict)` とは非対称)。最も忠実な
    # 録音点は adapter._make_callback が返す `_cb` で、ここを wrap すると:
    #   - 全 ticker・全 frame_type が 1 ストリームに集約 (loop 単一スレッドなので
    #     append 順 = 到着順)
    #   - adapter の queue → events() 経路は wrap 後も実走 (kabu と同様)
    #   - record の (instrument_id, frame_type, fields) は FdFrameProcessor の
    #     入力点とそのまま等価 = codec-replayable
    records: list[dict] = []
    t0: dict = {"mono": None}
    loop = asyncio.get_event_loop()
    orig_make = adapter._make_callback

    def make_recording(instrument_id, processor):
        inner = orig_make(instrument_id, processor)

        async def cb(frame_type: str, fields: dict, recv_ts_ms: int) -> None:
            now = loop.time()
            if t0["mono"] is None:
                t0["mono"] = now
            records.append({
                "t_ms": round((now - t0["mono"]) * 1000.0, 1),
                "recv": datetime.now(timezone.utc).isoformat(),
                "instrument_id": instrument_id,
                "frame_type": frame_type,
                "fields": dict(fields),  # defensive copy (inner may mutate)
            })
            await inner(frame_type, fields, recv_ts_ms)

        return cb

    adapter._make_callback = make_recording  # type: ignore[assignment]

    print(f"[capture] env={env} symbols={symbols} window={window_s}s")
    if env == "prod":
        print("[capture] WARNING: prod 接続中は本番セッションを 1 本占有する (別 GUI を閉じることを推奨)")
    try:
        await adapter.login(VenueCredentials(credentials_source="env"))
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL login: {type(exc).__name__}: {exc}")
        return 1
    print("[capture] login OK")

    for sym in symbols:
        try:
            await adapter.subscribe(sym, {"trades", "depth"})
        except Exception as exc:  # noqa: BLE001
            print(f"FAIL subscribe {sym}: {type(exc).__name__}: {exc}")
            await _safe_logout(adapter)
            return 1
    print(f"[capture] subscribed {len(symbols)} symbols; draining {window_s}s ...")

    depth_n = 0
    trades_n = 0
    deadline = loop.time() + window_s
    agen = adapter.events()
    try:
        while True:
            remaining = deadline - loop.time()
            if remaining <= 0:
                break
            try:
                evt = await asyncio.wait_for(agen.__anext__(), timeout=remaining)
            except asyncio.TimeoutError:
                break
            except StopAsyncIteration:
                print("[capture] events() ended (queue closed)")
                break
            if isinstance(evt, DepthUpdate):
                depth_n += 1
            elif isinstance(evt, TradesUpdate):
                trades_n += 1
    finally:
        await agen.aclose()

    # ---- frame_type 集計 ------------------------------------------------------
    type_counts: dict[str, int] = {}
    for r in records:
        type_counts[r["frame_type"]] = type_counts.get(r["frame_type"], 0) + 1

    # ---- write artifact -------------------------------------------------------
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out_dir = Path(__file__).resolve().parent / "captures"
    out_dir.mkdir(exist_ok=True)
    out_path = out_dir / f"tachibana_mock_{stamp}.json"
    span_ms = records[-1]["t_ms"] if records else 0.0
    artifact = {
        "meta": {
            "venue": "TACHIBANA",
            "env": env,
            "symbols": symbols,
            "window_s": window_s,
            "captured_utc": stamp,
            "frame_count": len(records),
            "frame_type_counts": type_counts,
            "span_ms": span_ms,
            "decoded_depth_events": depth_n,
            "decoded_trades_events": trades_n,
            "note": (
                "raw tachibana EVENT WS frames (parse_event_frame後の fields dict) + "
                "ticker + frame_type + 相対 t_ms。replay は FdFrameProcessor.process(fields, recv_ts_ms) "
                "を ticker 別に駆動する (kabu の KabuPushFrameProcessor と対称)。"
            ),
        },
        "frames": records,
    }
    out_path.write_text(json.dumps(artifact, ensure_ascii=False, indent=2), encoding="utf-8")

    print("\n================ CAPTURE RESULT ================")
    print(f"raw EVENT frames captured: {len(records)}")
    print(f"  by frame_type          : {type_counts}")
    print(f"span                     : {span_ms/1000.0:.1f}s")
    print(f"decoded DepthUpdate      : {depth_n}")
    print(f"decoded TradesUpdate     : {trades_n}")
    print(f"written                  : {out_path}")
    print("================================================")
    fd_count = type_counts.get("FD", 0)
    if not records:
        print("WARN: 0 frames — 場が動いていない / セッション無効 / WS 接続失敗の疑い")
        if env == "demo":
            print("HINT: demo は無配信の可能性 — `env=prod` で再採取を検討 (kabu の verify→prod fallback と同型)")
    elif fd_count == 0:
        print("WARN: 0 FD frames — KP/ST のみ。市場閉局帯か WS 認証問題の疑い")
        if env == "demo":
            print("HINT: demo の FD push が空なら `env=prod` で再採取を検討")
    elif trades_n == 0:
        print("WARN: 0 trades — 約定が疎ら。チャート用 bar 素材としては弱い (窓を延ばすか主力銘柄に)")

    await _safe_logout(adapter)
    return 0


async def _safe_logout(adapter) -> None:
    try:
        await adapter.logout()
    except Exception as exc:  # noqa: BLE001
        print(f"[capture] logout warning: {type(exc).__name__}: {exc}")


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
