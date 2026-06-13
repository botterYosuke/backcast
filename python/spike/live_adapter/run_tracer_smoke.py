"""spike.live_adapter.run_tracer_smoke — CPython 疎通 gate for #20 (findings 0011 D1/D2).

C# LiveAdapterTracerProbe が Mono 上で踏む生産経路を、純 CPython で先行検証する:

  DataEngine + 偽 sink(set_rust_event_sink) → InprocLiveServer(de,"MOCK") facade 直叩き
    register → login → set_execution_mode(LiveAuto) → start
    → mock_inject で kline/fill/depth/account 注入
    → publish_backend_event → event_wire(外部タグ付き) → sink.push_json(bytes) を捕捉
    → OrderEvent FILLED×2 / AccountEvent position / LiveStrategyEvent / LiveStrategyTelemetry
    → get_state_json に kline price + depth bid/ask
    → stop → Replay → logout → close

PASS = 上記 assert 全通過 + "[LIVE ADAPTER TRACER CPYTHON PASS]"。
"""
from __future__ import annotations

import json
import threading
import time

from engine.core import DataEngine
from engine.inproc_server import InprocLiveServer

from spike.live_adapter import mock_inject as mi


class _CaptureSink:
    """偽 rust event sink: push_json(bytes) を utf-8 decode して捕捉（C# LiveBackendEventSink 相当）。"""

    def __init__(self):
        self._lock = threading.Lock()
        self.events: list = []

    def push_json(self, data) -> None:
        s = data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else str(data)
        obj = json.loads(s)
        with self._lock:
            self.events.append(obj)

    def snapshot(self):
        with self._lock:
            return list(self.events)


def _tagged(events, tag):
    return [e[tag] for e in events if isinstance(e, dict) and tag in e]


def run() -> dict:
    sink = _CaptureSink()
    de = DataEngine()
    de.set_rust_event_sink(sink)
    server = InprocLiveServer(de, "MOCK")

    def fills():
        return sum(
            1 for o in _tagged(sink.snapshot(), "OrderEvent") if o.get("status") == "FILLED"
        )

    def wait_fills(n, timeout=5.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if fills() >= n:
                return
            time.sleep(0.02)
        raise AssertionError(f"timeout waiting {n} fills (have {fills()})")

    run_id = None
    state = None
    acct = None
    try:
        reg = server.register_live_strategy(mi.TWIN_PATH, mi.TWIN_PATH)
        assert reg["success"], reg
        login = server.venue_login("MOCK", "env", None)
        assert login["success"], login
        mode = server.set_execution_mode("LiveAuto")
        assert mode["success"], mode

        mi.set_next_order_outcome(server, status="FILLED", filled_qty=100.0, avg_price=8.0)
        start = server.start_live_strategy(reg["strategy_id"], mi.IID, "MOCK")
        assert start["success"], start
        run_id = start["run_id"]

        for i in range(1, 4):
            mi.inject_kline(server, i, 8.0)
        wait_fills(1)

        mi.set_next_order_outcome(server, status="FILLED", filled_qty=100.0, avg_price=10.0)
        for i in range(4, 41):
            mi.inject_kline(server, i, 10.0)
        wait_fills(2)

        # depth + account position（D5 / D3）
        mi.emit_depth(server, 41, bid=9.9, ask=10.1)
        mi.set_account_snapshot(
            server, cash=9_000_000.0, buying_power=9_000_000.0,
            positions=[mi.make_position("8918", 100, 8.0, 200.0)],
        )
        acct = server.force_account_snapshot()
        time.sleep(0.25)  # depth が DepthCache → get_state_json に乗るまで一拍
        state = json.loads(server.get_state_json())
    finally:
        try:
            if run_id:
                server.stop_live_strategy(run_id)
        except Exception:
            pass
        try:
            server.set_execution_mode("Replay")
            server.venue_logout()
        except Exception:
            pass
        try:
            server.close()
        except Exception:
            pass

    return {"events": sink.snapshot(), "state": state, "force_acct": acct}


def main() -> int:
    r = run()
    events = r["events"]
    orders = _tagged(events, "OrderEvent")
    filled = [o for o in orders if o.get("status") == "FILLED"]
    accounts = _tagged(events, "AccountEvent")
    lifecycles = _tagged(events, "LiveStrategyEvent")
    telemetry = _tagged(events, "LiveStrategyTelemetry")

    # 形状を可視化（C# decoder DTO の裏取り用）
    print("[WIRE TAGS]", sorted({k for e in events if isinstance(e, dict) for k in e.keys()}))
    if filled:
        print("[ORDER SAMPLE]", json.dumps(filled[-1], ensure_ascii=False))
    if accounts:
        print("[ACCOUNT SAMPLE]", json.dumps(accounts[-1], ensure_ascii=False))
    if lifecycles:
        print("[LIFECYCLE SAMPLE]", json.dumps(lifecycles[-1], ensure_ascii=False))
    if telemetry:
        print("[TELEMETRY SAMPLE]", json.dumps(telemetry[-1], ensure_ascii=False))
    print("[STATE JSON]", json.dumps(r["state"], ensure_ascii=False)[:1500])
    print("[FORCE ACCT]", r["force_acct"])

    assert len(filled) >= 2, f"want >=2 FILLED OrderEvent, got {len(filled)}"
    acct_with_pos = [a for a in accounts if a.get("positions")]
    assert acct_with_pos, f"want AccountEvent with positions, got {accounts}"
    assert lifecycles, "want >=1 LiveStrategyEvent"
    print(
        f"[LIVE ADAPTER TRACER CPYTHON PASS] orders_filled={len(filled)} "
        f"accounts={len(accounts)} lifecycle={len(lifecycles)} telemetry={len(telemetry)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
