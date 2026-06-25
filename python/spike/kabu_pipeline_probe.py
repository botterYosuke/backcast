"""Throwaway full-pipeline diagnostic (HITL, prod): does the LIVE chart-bar chain populate?

The adapter probe proved kabu PROD yields TradesUpdate during za-raba. This drives the EXACT
production live wiring downstream of the adapter (mirrors live_orchestrator._start_live_components_async):

    KabuStationAdapter -> LiveRunner(+TickBarAggregator, partial_push 1s) -> LiveReducerBridge
        -> DataEngine.apply_replay_event -> reducer per_id_ohlc_points / per_id_close

and checks whether per_id_ohlc_points[<iid>] (what ChartView renders) actually fills.

Run: cd python && ./.venv/Scripts/python.exe spike/kabu_pipeline_probe.py [SYMBOL] [SECONDS] [env]
NOT a regression gate. Promote to a pytest/AFK gate once the cause is pinned.

WARNING: cleanup calls adapter.logout() -> PUT /unregister/all, GLOBAL to the kabuStation body.
Do NOT run against prod while the live app holds subscriptions — it wipes the app's PUSH registrations
(board/chart freeze). Close the app first (these runs assume the app is down).
"""
from __future__ import annotations

import asyncio
import logging
import os
import sys
from pathlib import Path


def _load_env_password(var: str) -> None:
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
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(name)s: %(message)s")

    symbol = sys.argv[1] if len(sys.argv) > 1 else "7203.TSE"
    window_s = float(sys.argv[2]) if len(sys.argv) > 2 else 25.0
    env = sys.argv[3] if len(sys.argv) > 3 else "prod"
    pw_var = "PROD_KABU_API_PASSWORD" if env == "prod" else "DEV_KABU_API_PASSWORD"
    _load_env_password(pw_var)
    if not os.environ.get(pw_var):
        print(f"FAIL: {pw_var} not found")
        return 2

    from engine.core import DataEngine
    from engine.exchanges.kabusapi import KabuStationAdapter
    from engine.live.adapter import VenueCredentials, TradesUpdate
    from engine.live.live_runner import LiveRunner
    from engine.live.reducer_bridge import LiveReducerBridge
    from engine.live.last_price_cache import LastPriceCache
    from engine.live.depth_cache import DepthCache
    from engine.models import PerInstrumentState
    from engine.kernel.duckdb_bars import granularity_to_interval_ns

    engine = DataEngine()
    adapter = KabuStationAdapter(environment=env)  # type: ignore[arg-type]
    runner = LiveRunner(
        adapter=adapter,
        interval_ns=granularity_to_interval_ns("Minute"),
        partial_push_interval_s=1.0,
    )
    runner._loop = asyncio.get_event_loop()
    bridge = LiveReducerBridge(
        bus=runner.bus,
        data_engine=engine,
        mode_provider=lambda: "LiveManual",  # NOT "Replay" -> bridge forwards live klines
    )
    cache = LastPriceCache(bus=runner.bus)
    depth_cache = DepthCache(bus=runner.bus)

    raw_ticks = {"n": 0}
    runner.add_tick_listener(lambda t: raw_ticks.__setitem__("n", raw_ticks["n"] + 1))

    await bridge.start()
    await cache.start()
    await depth_cache.start()
    await runner.start()

    print(f"[pipeline] env={env} symbol={symbol} window={window_s}s")
    try:
        await adapter.login(VenueCredentials(credentials_source="env"))
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL login: {type(exc).__name__}: {exc}")
        return 1
    print("[pipeline] login OK; subscribing via runner.subscribe (creates aggregators) ...")
    try:
        await runner.subscribe(symbol)
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL subscribe: {type(exc).__name__}: {exc}")
        await _safe_logout(adapter)
        return 1

    await asyncio.sleep(window_s)

    # ---- inspect the reducer state the chart reads ----
    rs = engine._rs
    pts = list(rs.per_id_ohlc_points.get(symbol, []))
    close = rs.per_id_close.get(symbol)
    st = engine.get_current_state()
    pi = st.per_instrument.get(symbol)
    pi_pts = list(pi.ohlc_points) if pi is not None else None

    print("\n================ PIPELINE RESULT ================")
    print(f"raw TradesUpdate reaching runner   : {raw_ticks['n']}")
    print(f"per_id_close[{symbol}]             : {close}")
    print(f"per_id_ohlc_points[{symbol}] count : {len(pts)}")
    distinct_t = sorted({p.open_time_ms for p in pts})
    print(f"  distinct open_time_ms (buckets)  : {len(distinct_t)} -> {distinct_t}")
    if pts:
        f, l = pts[0], pts[-1]
        print(f"  first pt: o={f.open} h={f.high} l={f.low} c={f.close} t={f.open_time_ms}")
        print(f"  last  pt: o={l.open} h={l.high} l={l.low} c={l.close} t={l.open_time_ms}")
    print(f"per_instrument[{symbol}] present   : {pi is not None}")
    print(f"per_instrument[{symbol}].ohlc count: {None if pi_pts is None else len(pi_pts)}")
    print(f"runner.last_error                  : {runner.last_error!r}")

    # ---- FAITHFUL get_state_json live-branch replication (the C#-facing JSON) ----
    import json as _json
    base_pi = st.per_instrument
    depth_by_id = depth_cache.snapshot()
    merged_pi = {
        k: (v.model_copy(update={"depth": d}) if (d := depth_by_id.get(k)) else v)
        for k, v in base_pi.items()
    }
    for k, d in depth_by_id.items():
        if k not in merged_pi:
            merged_pi[k] = PerInstrumentState(depth=d)
    serial_state = st.model_copy(update={"per_instrument": merged_pi})
    js = serial_state.model_dump_json()
    parsed = _json.loads(js)
    node = (parsed.get("per_instrument") or {}).get(symbol)
    j_ohlc = (node or {}).get("ohlc_points")
    j_depth = (node or {}).get("depth")
    print(f"--- serialized state (what C# decodes) ---")
    print(f"  depth_cache has {symbol}            : {symbol in depth_by_id}")
    print(f"  JSON per_instrument[{symbol}] keys  : {sorted(node.keys()) if node else None}")
    print(f"  JSON ohlc_points count             : {None if j_ohlc is None else len(j_ohlc)}")
    print(f"  JSON depth present                 : {j_depth is not None}")
    # Persist the real state JSON as a C#-decoder fixture (full state — decoder locates per_instrument[id]).
    # tempdir (not a hardcoded session scratchpad) so the spike works on any machine.
    import tempfile
    fixture = Path(tempfile.gettempdir()) / "kabu_prod_state.json"
    try:
        fixture.parent.mkdir(parents=True, exist_ok=True)
        fixture.write_text(js, encoding="utf-8")
        print(f"  fixture written                    : {fixture} ({len(js)} bytes)")
    except Exception as exc:  # noqa: BLE001
        print(f"  fixture write failed               : {exc}")
    print(f"  raw node JSON                      : {_json.dumps(node)[:400]}")
    print("=================================================")
    if raw_ticks["n"] == 0:
        print("VERDICT -> no ticks reached runner (subscribe/_is_subscribed/adapter gap).")
    elif len(pts) == 0:
        print("VERDICT -> ticks arrived but per_id_ohlc_points EMPTY: aggregator/bridge/reducer break -> THE BUG.")
    elif pi_pts is None or len(pi_pts) == 0:
        print("VERDICT -> per_id_ohlc_points filled but per_instrument projection empty: get_current_state break.")
    else:
        print("VERDICT -> chart series IS populated end-to-end; bug must be in get_state_json depth-branch or C#.")

    await _safe_logout(adapter)
    return 0


async def _safe_logout(adapter) -> None:
    try:
        await adapter.logout()
    except Exception as exc:  # noqa: BLE001
        print(f"[pipeline] logout warning: {type(exc).__name__}: {exc}")


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
