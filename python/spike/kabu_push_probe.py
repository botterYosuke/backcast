"""Throwaway real-venue diagnostic (HITL): is the kabu PUSH stream live-ticking?

Distinguishes the two runtime hypotheses for the "depth shows but chart/LAST empty" bug:
  H1: stream flows, board moves, but frames carry no CurrentPrice/TradingVolume -> no trades.
  H2: stream delivered only the initial snapshot (or verify-env synthetic board) -> frozen, no trades.

Measures, over a fixed window, against the REAL kabu body (verify=18081 via DEV_KABU_API_PASSWORD):
  - total raw PUSH frames received
  - per-frame presence of CurrentPrice / TradingVolume / CurrentPriceTime
  - whether CurrentPrice / TradingVolume / best-bid-ask actually CHANGE over the window
  - DepthUpdate vs TradesUpdate fire counts out of adapter.events()

Run: cd python && ./.venv/Scripts/python.exe spike/kabu_push_probe.py [SYMBOL] [SECONDS]
NOT a regression gate — a one-shot measurement. Promote to a proper gate only after the cause is pinned.

WARNING: cleanup calls adapter.logout() -> PUT /unregister/all, which is GLOBAL to the kabuStation
body. Do NOT run this against prod while the live app holds subscriptions — it will wipe the app's
PUSH registrations (its board/chart freezes). Close the app first (these runs assume the app is down).
"""
from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path


def _load_env_password(var: str) -> None:
    """Parse <repo>/.env for `var` into os.environ (password never printed)."""
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
    symbols = (sys.argv[1] if len(sys.argv) > 1 else "7203.TSE").split(",")
    window_s = float(sys.argv[2]) if len(sys.argv) > 2 else 25.0
    env = sys.argv[3] if len(sys.argv) > 3 else "verify"   # "verify" | "prod"
    pw_var = "PROD_KABU_API_PASSWORD" if env == "prod" else "DEV_KABU_API_PASSWORD"
    _load_env_password(pw_var)
    if not os.environ.get(pw_var):
        print(f"FAIL: {pw_var} not found in env or <repo>/.env")
        return 2

    from engine.exchanges.kabusapi import KabuStationAdapter
    from engine.live.adapter import (
        VenueCredentials,
        DepthUpdate,
        TradesUpdate,
    )

    adapter = KabuStationAdapter(environment=env)  # type: ignore[arg-type]

    # ---- raw-frame instrumentation: wrap _on_frame before subscribe ---------
    stats = {
        "frames": 0,
        "cur_price_present": 0,
        "trading_vol_present": 0,
        "cur_price_time_present": 0,
    }
    seen_prices: set = set()
    seen_vols: set = set()
    seen_books: set = set()
    first_frame_keys = None

    orig_on_frame = adapter._on_frame

    async def wrapped(msg: dict):
        nonlocal first_frame_keys
        stats["frames"] += 1
        if first_frame_keys is None and isinstance(msg, dict):
            first_frame_keys = sorted(msg.keys())
        cp = msg.get("CurrentPrice")
        tv = msg.get("TradingVolume")
        cpt = msg.get("CurrentPriceTime")
        if cp is not None:
            stats["cur_price_present"] += 1
            seen_prices.add(cp)
        if tv is not None:
            stats["trading_vol_present"] += 1
            seen_vols.add(tv)
        if cpt is not None:
            stats["cur_price_time_present"] += 1
        b1 = (msg.get("Buy1") or {}).get("Price") if isinstance(msg.get("Buy1"), dict) else None
        a1 = (msg.get("Sell1") or {}).get("Price") if isinstance(msg.get("Sell1"), dict) else None
        # kabu PUSH sends only CHANGED fields, so price-only frames omit the board. Don't record a
        # (None, None) sentinel as a distinct book state (would mask a frozen board in the H2 check).
        if b1 is not None or a1 is not None:
            seen_books.add((b1, a1))
        await orig_on_frame(msg)

    adapter._on_frame = wrapped

    print(f"[probe] env={env} symbols={symbols} window={window_s}s")
    try:
        await adapter.login(VenueCredentials(credentials_source="env"))
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL login: {type(exc).__name__}: {exc}")
        return 1
    print("[probe] login OK")

    for sym in symbols:
        try:
            await adapter.subscribe(sym.strip(), {"trades", "depth"})
        except Exception as exc:  # noqa: BLE001
            print(f"FAIL subscribe {sym}: {type(exc).__name__}: {exc}")
            await _safe_logout(adapter)
            return 1
    print("[probe] subscribed; draining events ...")

    depth_n = 0
    trades_n = 0
    loop = asyncio.get_event_loop()
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
                print("[probe] events() ended (WS task done)")
                break
            if isinstance(evt, DepthUpdate):
                depth_n += 1
            elif isinstance(evt, TradesUpdate):
                trades_n += 1
    finally:
        await agen.aclose()

    print("\n================ RESULT ================")
    print(f"raw PUSH frames received      : {stats['frames']}")
    print(f"  with CurrentPrice           : {stats['cur_price_present']}  distinct={len(seen_prices)}")
    print(f"  with TradingVolume          : {stats['trading_vol_present']}  distinct={len(seen_vols)}")
    print(f"  with CurrentPriceTime       : {stats['cur_price_time_present']}")
    print(f"distinct best (bid1,ask1)     : {len(seen_books)}  e.g. {list(seen_books)[:3]}")
    print(f"DepthUpdate events            : {depth_n}")
    print(f"TradesUpdate events           : {trades_n}")
    print(f"first frame keys              : {first_frame_keys}")
    wt = adapter._ws_task
    print(f"_ws_task                      : done={wt.done() if wt else None} "
          f"exc={wt.exception() if (wt and wt.done() and not wt.cancelled()) else None}")
    print(f"adapter.last_error            : {adapter.last_error!r}")
    if seen_prices:
        sp = sorted(p for p in seen_prices if isinstance(p, (int, float)))
        print(f"CurrentPrice range            : {sp[0]} .. {sp[-1]}")
    print("========================================")

    # Verdict heuristic (advisory; human reads the numbers).
    if stats["frames"] <= 1:
        print("VERDICT -> H2: stream delivered <=1 frame (no ongoing PUSH).")
    elif len(seen_books) <= 1 and stats["frames"] > 1:
        print("VERDICT -> H2: many frames but board NEVER changed (frozen/synthetic snapshot).")
    elif stats["cur_price_present"] == 0:
        print("VERDICT -> H1: frames flow but NO CurrentPrice field present -> trades impossible.")
    elif len(seen_vols) <= 1:
        print("VERDICT -> H1: TradingVolume never increased -> no trade prints (quiet/synthetic).")
    elif trades_n == 0:
        print("VERDICT -> H1-ish: price/volume present & changing but 0 TradesUpdate -> codec gate.")
    else:
        print(f"VERDICT -> TRADES FLOW ({trades_n}): chart SHOULD populate; bug is elsewhere.")

    await _safe_logout(adapter)
    return 0


async def _safe_logout(adapter) -> None:
    try:
        await adapter.logout()
    except Exception as exc:  # noqa: BLE001
        print(f"[probe] logout warning: {type(exc).__name__}: {exc}")


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
