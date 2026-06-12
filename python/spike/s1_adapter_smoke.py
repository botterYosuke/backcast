"""S1 spike — pure-CPython adapter smoke gate (issue #9, M2 move 2).

Proves the production replay seam drives the C# RustBacktestSink 5-method
contract under plain CPython, BEFORE introducing Mono. It calls the SAME
production entry the headless C# probe (M2 move 4) will call —
InprocLiveServer.start_nautilus_replay(cfg) — so that a later Mono failure
with this smoke passing cleanly isolates the fault to the Mono callback layer
("engine replay works" vs "Mono callback works").

start_nautilus_replay runs the backtest on a daemon thread and returns a dict
immediately; completion is signalled out-of-band via the sink's
push_run_complete()/push_run_failed(). _SmokeSink models the C# side: push_bar
enqueues a JSON string onto a thread-safe deque and returns at once (mimicking
a C# ConcurrentQueue enqueue under the GIL); a single drain loop on the main
thread dequeues + json.loads each after the run completes.

Self-failing gate (mirrors python/spike/s0_backtest.py style):
  [ADAPTER SMOKE PASS] pushed=N drained=N parsed_ok   -> exit 0
  any mismatch / parse failure / timeout / exception   -> exit 1
"""

from __future__ import annotations

import collections
import json
import sys
import threading
from pathlib import Path

# ---------------------------------------------------------------------------
# Fixture wiring (paths via __file__; engine is editable-installed so `import
# engine` works regardless of cwd — mirrors s0_backtest.py).
# ---------------------------------------------------------------------------

SPIKE_ROOT = Path(__file__).resolve().parent
FIXTURE_ROOT = SPIKE_ROOT / "fixtures"
CATALOG_PATH = FIXTURE_ROOT / "jquants-catalog"
STRATEGY_FILE = FIXTURE_ROOT / "strategies" / "spike_bar_consumer.py"

INSTRUMENTS = ["8918.TSE"]
START_DATE = "2024-10-01"
END_DATE = "2025-01-10"
GRANULARITY = "Daily"
INITIAL_CASH = 10_000_000

# 8918 over this window streams 68 daily bars at 0.1s/bar (speed_ref defaults
# to 1.0 inside start_nautilus_replay) -> ~7s run; wait generously.
WAIT_TIMEOUT_S = 60.0


# ---------------------------------------------------------------------------
# CPython model of the C# RustBacktestSink (5-method push contract)
# ---------------------------------------------------------------------------


class _SmokeSink:
    """In-proc stand-in for the C# RustBacktestSink.

    push_bar enqueues the JSON string and returns immediately (models a C#
    ConcurrentQueue enqueue under the GIL). It must never raise: the engine's
    GuiBridgeActor._on_bar swallows sink exceptions as warnings, which would
    silently drop bars and corrupt the pushed/drained accounting.

    push_run_complete / push_run_failed set a done Event so the main thread
    knows when the daemon backtest thread has finished.
    """

    def __init__(self) -> None:
        # deque.append / popleft are atomic under the GIL; a single producer
        # (the backtest daemon thread) calls push_bar, a single consumer (main)
        # drains after _done is set, so no lock is needed.
        self._bars: collections.deque[str] = collections.deque()
        self.pushed = 0
        self.orders = 0
        self.portfolios = 0
        self.complete_summary: str | None = None
        self.failed_error: str | None = None
        self._done = threading.Event()

    # --- 5-method sink contract (all payloads are JSON strings) ---

    def push_bar(self, json_str: str) -> None:
        self._bars.append(json_str)
        self.pushed += 1  # single producer thread -> safe under GIL

    def push_order(self, json_str: str) -> None:
        self.orders += 1

    def push_portfolio(self, json_str: str) -> None:
        self.portfolios += 1

    def push_run_complete(self, run_id: str, summary: str) -> None:
        self.complete_summary = summary
        self._done.set()

    def push_run_failed(self, err: str) -> None:
        self.failed_error = err
        self._done.set()


# ---------------------------------------------------------------------------
# Gate body
# ---------------------------------------------------------------------------


class S1SmokeError(RuntimeError):
    """Raised by the adapter smoke gate on any mismatch (exit 1)."""


def run_smoke() -> str:
    """Drive start_nautilus_replay through a CPython sink and verify the seam.

    Returns the PASS gate line on success; raises S1SmokeError on any failure.
    """
    from engine.core import DataEngine
    from engine.inproc_server import InprocLiveServer

    sink = _SmokeSink()

    # Production façade — same entry the C# headless probe (move 4) calls.
    data_engine = DataEngine()
    server = InprocLiveServer(data_engine)

    cfg = {
        "strategy_file": str(STRATEGY_FILE),
        "instruments": list(INSTRUMENTS),
        "start_date": START_DATE,
        "end_date": END_DATE,
        "granularity": GRANULARITY,
        "initial_cash": INITIAL_CASH,
        "catalog_path": str(CATALOG_PATH),
        "rust_sink": sink,
    }

    # Returns immediately; backtest runs on a daemon thread.
    result = server.start_nautilus_replay(cfg)
    if not result.get("success"):
        raise S1SmokeError(
            "[ADAPTER SMOKE FAIL] start_nautilus_replay rejected: "
            f"error_code={result.get('error_code')!r} "
            f"error_message={result.get('error_message')!r}"
        )

    # Wait for the daemon thread to signal completion (or failure).
    if not sink._done.wait(timeout=WAIT_TIMEOUT_S):
        raise S1SmokeError(
            f"[ADAPTER SMOKE FAIL] timed out after {WAIT_TIMEOUT_S:.0f}s "
            f"waiting for run completion (pushed so far={sink.pushed})"
        )

    if sink.failed_error is not None:
        raise S1SmokeError(
            f"[ADAPTER SMOKE FAIL] push_run_failed: {sink.failed_error!r}"
        )

    if sink.complete_summary is None:
        raise S1SmokeError(
            "[ADAPTER SMOKE FAIL] done set without push_run_complete summary"
        )

    # Drain on the main thread: every enqueued bar must json.loads cleanly.
    drained = 0
    parse_failures: list[str] = []
    while sink._bars:
        payload = sink._bars.popleft()
        try:
            obj = json.loads(payload)
        except Exception as exc:  # noqa: BLE001 — report any malformed payload
            parse_failures.append(str(exc))
            continue
        if not isinstance(obj, dict):
            parse_failures.append(f"payload is {type(obj).__name__}, not dict")
            continue
        drained += 1

    if parse_failures:
        raise S1SmokeError(
            "[ADAPTER SMOKE FAIL] "
            f"{len(parse_failures)} payload(s) failed to parse: "
            + "; ".join(parse_failures[:3])
        )

    if sink.pushed <= 0:
        raise S1SmokeError(
            "[ADAPTER SMOKE FAIL] no bars pushed (pushed=0) — "
            "replay produced no push_bar callbacks"
        )

    if sink.pushed != drained:
        raise S1SmokeError(
            f"[ADAPTER SMOKE FAIL] pushed={sink.pushed} != drained={drained}"
        )

    return f"[ADAPTER SMOKE PASS] pushed={sink.pushed} drained={drained} parsed_ok"


def main() -> None:
    try:
        line = run_smoke()
    except S1SmokeError as exc:
        print(exc)
        sys.exit(1)
    except Exception as exc:  # noqa: BLE001 — any unexpected error fails the gate
        print(f"[ADAPTER SMOKE FAIL] unexpected error: {exc!r}")
        sys.exit(1)
    print(line)
    sys.exit(0)


if __name__ == "__main__":
    main()
