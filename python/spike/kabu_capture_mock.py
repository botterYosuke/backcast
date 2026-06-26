"""kabu PUSH フレームを本番(18080)から採取し、"本物そっくり" mock データを書き出す。

目的: live チャートの partial-bar 重複追記ワート（finding 0111 §結論-2）を改善するための
回帰素材を作る。生 PUSH フレーム（kabuStation が WebSocket で送ってくる JSON dict）を到着
相対時刻つきで丸ごと記録するので、後で

    adapter._on_frame(frame) を相対時刻どおりに再生
        → KabuPushFrameProcessor（codec）→ TradesUpdate/DepthUpdate
        → TickBarAggregator → reducer.per_id_ohlc_points

を Python-only / venue-FREE で決定的に再現できる（実 venue 無しでバグを再現＆fix を gate）。

採取は read-only（PUSH 受信のみ・発注しない）。ただし cleanup の logout() は
PUT /unregister/all を撃つ＝**body グローバル**。実行前に live アプリを必ず閉じること
（register 枠の競合と購読巻き戻しを避ける）。

Run:
  cd python
  ./.venv/Scripts/python.exe spike/kabu_capture_mock.py "7203.TSE,8306.TSE,9984.TSE,285A.TSE" 150 prod

引数: [SYMBOLS(comma)] [WINDOW_SECONDS] [ENV(prod|verify)]
  - 既定 SYMBOLS = 主力高流動 4 銘柄 / WINDOW = 150s（>2 分足バケットを跨ぐ）/ ENV = prod
  - verify(18081) は PUSH 無配信なので空キャプチャになる（mock 素材にならない・memory 参照）

出力: python/spike/captures/kabu_mock_<UTCstamp>.json
  { "meta": {...}, "frames": [ {"t_ms": <到着相対ms>, "recv": "<ISO>", "frame": {...生JSON...}}, ... ] }

NOT a regression gate — 採取ツール。回帰の正本は採取後に作る Python 再生テスト。
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


def _load_env_password(var: str) -> None:
    """<repo>/.env から `var` を os.environ へ（値は表示しない）。"""
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
    logging.getLogger("engine.exchanges.kabusapi_ws").setLevel(logging.INFO)
    logging.getLogger("engine.exchanges.kabusapi").setLevel(logging.INFO)

    default_symbols = "7203.TSE,8306.TSE,9984.TSE,285A.TSE"
    symbols = [s.strip() for s in (sys.argv[1] if len(sys.argv) > 1 else default_symbols).split(",") if s.strip()]
    window_s = float(sys.argv[2]) if len(sys.argv) > 2 else 150.0
    env = sys.argv[3] if len(sys.argv) > 3 else "prod"   # "prod" | "verify"
    if env not in ("prod", "verify"):
        print(f"FAIL: env must be prod|verify, got {env!r}")
        return 2
    pw_var = "PROD_KABU_API_PASSWORD" if env == "prod" else "DEV_KABU_API_PASSWORD"
    _load_env_password(pw_var)
    if not os.environ.get(pw_var):
        print(f"FAIL: {pw_var} not found in env or <repo>/.env")
        return 2

    from engine.exchanges.kabusapi import KabuStationAdapter
    from engine.live.adapter import VenueCredentials, DepthUpdate, TradesUpdate

    adapter = KabuStationAdapter(environment=env)  # type: ignore[arg-type]

    # ---- raw-frame capture: wrap _on_frame before subscribe --------------------
    records: list[dict] = []
    t0: dict = {"mono": None}
    loop = asyncio.get_event_loop()
    orig_on_frame = adapter._on_frame

    async def wrapped(msg: dict):
        now = loop.time()
        if t0["mono"] is None:
            t0["mono"] = now
        if isinstance(msg, dict):
            records.append({
                "t_ms": round((now - t0["mono"]) * 1000.0, 1),
                "recv": datetime.now(timezone.utc).isoformat(),
                "frame": msg,
            })
        await orig_on_frame(msg)

    adapter._on_frame = wrapped

    print(f"[capture] env={env} symbols={symbols} window={window_s}s")
    print("[capture] WARNING: live アプリを閉じてから実行すること（logout が body グローバルに unregister/all）")
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
                print("[capture] events() ended (WS task done)")
                break
            if isinstance(evt, DepthUpdate):
                depth_n += 1
            elif isinstance(evt, TradesUpdate):
                trades_n += 1
    finally:
        await agen.aclose()

    # ---- write artifact --------------------------------------------------------
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out_dir = Path(__file__).resolve().parent / "captures"
    out_dir.mkdir(exist_ok=True)
    out_path = out_dir / f"kabu_mock_{stamp}.json"
    span_ms = records[-1]["t_ms"] if records else 0.0
    artifact = {
        "meta": {
            "venue": "KABU",
            "env": env,
            "symbols": symbols,
            "window_s": window_s,
            "captured_utc": stamp,
            "frame_count": len(records),
            "span_ms": span_ms,
            "decoded_depth_events": depth_n,
            "decoded_trades_events": trades_n,
            "note": "raw kabuStation PUSH frames + relative arrival t_ms; replay via adapter._on_frame",
        },
        "frames": records,
    }
    out_path.write_text(json.dumps(artifact, ensure_ascii=False, indent=2), encoding="utf-8")

    print("\n================ CAPTURE RESULT ================")
    print(f"raw PUSH frames captured : {len(records)}")
    print(f"span                     : {span_ms/1000.0:.1f}s")
    print(f"decoded DepthUpdate       : {depth_n}")
    print(f"decoded TradesUpdate      : {trades_n}")
    print(f"written                  : {out_path}")
    print("================================================")
    if not records:
        print("WARN: 0 frames — verify(18081) は無配信 / 場が動いていない / 本体未ログインの疑い")
    elif trades_n == 0:
        print("WARN: 0 trades — 約定が疎ら。チャート用 bar 素材としては弱い（窓を延ばすか主力銘柄に）")

    await _safe_logout(adapter)
    return 0


async def _safe_logout(adapter) -> None:
    try:
        await adapter.logout()
    except Exception as exc:  # noqa: BLE001
        print(f"[capture] logout warning: {type(exc).__name__}: {exc}")


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
