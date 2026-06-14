"""spike.venue_login_secret.run_secret_smoke — CPython gate for #21 (findings 0012 D3).

Pre-validates, in pure CPython, the seam the C# VenueLoginSecretProbe drives on Mono:

  DataEngine + capture sink → build_secret_mock_server (SecretMockAdapter injected)
    venue_login("MOCK") → set_execution_mode("LiveManual")
    1. SUCCESS: place_order on a WRITE thread blocks → SecretRequired on sink →
       submit_secret from the SECRET thread → place returns success + FILLED OrderEvent.
    2. SECRET_TIMEOUT: place_order, never submit → error_code SECRET_TIMEOUT, order
       NEVER reached the venue (submit_order_call_count unchanged) → orphan-free.
    3. SERIALIZATION: two places on ONE write lane run strictly one-after-another
       (place #1 returns before place #2's SecretRequired is emitted).
    + no plaintext secret in any captured wire event.

PASS = all asserts + "[VENUE LOGIN SECRET CPYTHON PASS]".
"""
from __future__ import annotations

import json
import threading
import time

from engine.core import DataEngine

from spike.venue_login_secret import secret_mock as sm

SECRET_VALUE = "9753-secret"   # known marker — must NOT appear in any wire event


class _CaptureSink:
    def __init__(self):
        self._lock = threading.Lock()
        self.events = []

    def push_json(self, data) -> None:
        s = data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else str(data)
        with self._lock:
            self.events.append(json.loads(s))

    def snapshot(self):
        with self._lock:
            return list(self.events)


def _secret_requests(events):
    return [e["SecretRequired"] for e in events if isinstance(e, dict) and "SecretRequired" in e]


def _filled(events):
    return [
        e["OrderEvent"] for e in events
        if isinstance(e, dict) and "OrderEvent" in e and e["OrderEvent"].get("status") == "FILLED"
    ]


def _place(server, results, key):
    """Order-write lane body: blocks until the order resolves (incl. secret wait)."""
    res = server.place_order("MOCK", sm.IID, "BUY", 100.0, None, "MARKET", "DAY", None)
    results[key] = (res, time.time())


def _wait_secret(sink, baseline, timeout=8.0):
    """Wait for a NEW SecretRequired beyond `baseline` count; return (request_id, ts)."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        reqs = _secret_requests(sink.snapshot())
        if len(reqs) > baseline:
            return reqs[baseline]["request_id"], time.time()
        time.sleep(0.01)
    raise AssertionError(f"timeout waiting SecretRequired > {baseline}")


def run() -> dict:
    sink = _CaptureSink()
    de = DataEngine()
    de.set_rust_event_sink(sink)
    server = sm.build_secret_mock_server(de)

    report = {}
    try:
        assert server.venue_login("MOCK", "env", None)["success"]
        assert server.set_execution_mode("LiveManual")["success"]

        # 1. SUCCESS roundtrip ---------------------------------------------------
        sm.arm_order(server, "FILLED", 100.0, 8.0)
        results = {}
        t = threading.Thread(target=_place, args=(server, results, "ok"), daemon=True)
        t.start()
        req_id, _ = _wait_secret(sink, baseline=0)
        ack = server.submit_secret(req_id, SECRET_VALUE)
        assert ack["success"], ack
        t.join(timeout=10)
        ok_res, _ = results["ok"]
        assert ok_res["success"], f"success leg failed: {ok_res}"
        assert _filled(sink.snapshot()), "no FILLED OrderEvent after secret submit"
        assert sm.submit_order_call_count(server) == 1, "venue not reached once"
        report["success"] = ok_res

        # 2. SECRET_TIMEOUT (never submit) --------------------------------------
        sm.arm_order(server, "FILLED", 100.0, 8.0)
        before_calls = sm.submit_order_call_count(server)
        results = {}
        t0 = time.time()
        t = threading.Thread(target=_place, args=(server, results, "to"), daemon=True)
        t.start()
        _wait_secret(sink, baseline=1)           # prompt appears; we do NOT submit
        t.join(timeout=sm.SECRET_TIMEOUT_S + 6)
        to_res, _ = results["to"]
        elapsed = time.time() - t0
        assert to_res["error_code"] == "SECRET_TIMEOUT", f"want SECRET_TIMEOUT, got {to_res}"
        assert sm.submit_order_call_count(server) == before_calls, "order reached venue on timeout (orphan!)"
        assert elapsed < 40.0, f"timed out via PLACE_TIMEOUT not SECRET_TIMEOUT ({elapsed:.1f}s)"
        report["timeout"] = {"error_code": to_res["error_code"], "elapsed_s": round(elapsed, 2)}

        # 3. SERIALIZATION on one write lane ------------------------------------
        sm.arm_order(server, "FILLED", 100.0, 8.0)
        base = len(_secret_requests(sink.snapshot()))
        results = {}

        def lane():
            _place(server, results, "s1")        # fully resolves before next starts
            sm.arm_order(server, "FILLED", 100.0, 8.0)
            _place(server, results, "s2")

        worker = threading.Thread(target=lane, daemon=True)
        worker.start()
        r1, ts_secret1 = _wait_secret(sink, baseline=base)
        server.submit_secret(r1, SECRET_VALUE)
        # wait for place #1 to RETURN before place #2's prompt is allowed to appear
        while "s1" not in results:
            time.sleep(0.005)
        s1_return_ts = results["s1"][1]
        r2, ts_secret2 = _wait_secret(sink, baseline=base + 1)
        assert ts_secret2 >= s1_return_ts, "place #2 prompted before place #1 returned (not serialized)"
        server.submit_secret(r2, SECRET_VALUE)
        worker.join(timeout=10)
        assert results["s1"][0]["success"] and results["s2"][0]["success"], "serialized places failed"
        report["serialized"] = True

        # 4. no plaintext secret anywhere in the wire ----------------------------
        blob = json.dumps(sink.snapshot(), ensure_ascii=False)
        assert SECRET_VALUE not in blob, "plaintext secret leaked into a wire event!"
        report["no_plaintext"] = True
    finally:
        try:
            server.set_execution_mode("Replay")
            server.venue_logout()
        except Exception:
            pass
        try:
            server.close()
        except Exception:
            pass
    return report


def main() -> int:
    r = run()
    print("[SUCCESS]", json.dumps(r.get("success"), ensure_ascii=False))
    print("[TIMEOUT]", r.get("timeout"))
    print("[SERIALIZED]", r.get("serialized"), "[NO_PLAINTEXT]", r.get("no_plaintext"))
    print("[VENUE LOGIN SECRET CPYTHON PASS] secret roundtrip / SECRET_TIMEOUT / serialization / no-leak")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
