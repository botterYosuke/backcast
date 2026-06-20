"""Spike (#95 ④ GIL preflight): does an engine worker thread holding the GIL in a tight
loop (NO per-bar sleep) still let a separate "poll lane" thread acquire the GIL periodically?

Why: #95 D8/D9 - owner's binding priority is "Hakoniwa の更新処理が marimo/engine の実行速度を
拘束してはいけない". The current runner uses an explicit per-bar `time.sleep(_bar_interval)` as
its ONLY GIL-handoff point (runner.py:302). That sleep IS back-pressure (engine spends real
time so the reader can run). We want to DROP it and rely on CPython's automatic GIL switch
(sys.setswitchinterval) so the engine runs flat-out while the poll lane still gets windows
(Hakoniwa may visually lag - owner-accepted).

Faithfulness: the real reader is the C# poll lane doing `Py.GIL()` (== PyGILState_Ensure) on a
foreign thread. CPython's eval-loop `gil_drop_request` hands the GIL to ANY waiting thread
(native threading.Thread OR foreign PyGILState) at switchinterval boundaries - the same drop
path. A pure-Python waiter is therefore a strong proxy. A fully faithful test would be a Unity
AFK pythonnet probe; flagged as residual risk in the finding.

PASS criteria:
  - WITHOUT sleep: engine throughput (bars/s) >> the sleep version, AND the poll thread still
    sees fresh snapshots (poll_updates_seen ~ duration/poll_cadence, last bar ~ engine's last).
  - i.e. dropping the sleep does NOT starve the reader.
"""

import sys
import threading
import time


def trial(*, engine_sleep_s, switch_interval_s=None, duration_s=2.0, poll_cadence_s=0.05):
    if switch_interval_s is not None:
        sys.setswitchinterval(switch_interval_s)

    # The shared running snapshot, rebound atomically (like ReplayKernelObserver ->
    # engine.last_portfolio: build a fresh object, never mutate in place).
    box = {"snap": {"bar": 0}}
    stop = threading.Event()

    poll_gaps_ms = []
    poll_last_bar = [-1]

    def poll():
        last_t = time.perf_counter()
        last_bar = -1
        while not stop.is_set():
            snap = box["snap"]              # read the atomic-swapped snapshot (cheap)
            b = snap["bar"]
            if b != last_bar:
                now = time.perf_counter()
                poll_gaps_ms.append((now - last_t) * 1000.0)
                last_t = now
                last_bar = b
                poll_last_bar[0] = b
            time.sleep(poll_cadence_s)      # 50ms lane cadence; releases GIL while sleeping

    t = threading.Thread(target=poll, name="poll-lane", daemon=True)
    t.start()
    time.sleep(0.02)  # let poll reach its first wait

    # Engine worker: tight per-bar loop holding the GIL. Pure-Python "per-bar work".
    start = time.perf_counter()
    i = 0
    while (time.perf_counter() - start) < duration_s:
        i += 1
        # trivial deterministic per-bar work (stand-in for runner per-bar arithmetic)
        _ = (i * 2654435761) & 0xFFFFFFFF
        box["snap"] = {"bar": i}           # atomic rebind, like last_portfolio swap
        if engine_sleep_s > 0:
            time.sleep(engine_sleep_s)     # the CURRENT design's GIL-handoff-via-sleep
    elapsed = time.perf_counter() - start

    stop.set()
    t.join(timeout=1.0)

    gaps = poll_gaps_ms
    return {
        "switch_interval_s": sys.getswitchinterval(),
        "engine_sleep_s": engine_sleep_s,
        "elapsed_s": round(elapsed, 3),
        "bars_done": i,
        "bars_per_s": round(i / elapsed),
        "poll_updates_seen": len(gaps),
        "poll_last_bar_seen": poll_last_bar[0],
        "engine_last_bar": i,
        "poll_staleness_bars": i - poll_last_bar[0],
        "max_poll_gap_ms": round(max(gaps), 1) if gaps else None,
        "median_poll_gap_ms": round(sorted(gaps)[len(gaps) // 2], 1) if gaps else None,
    }


def main():
    print("Python:", sys.version.split()[0], "default switchinterval:", sys.getswitchinterval())
    print()

    scenarios = [
        ("CURRENT  (sleep 10ms/bar, the existing GIL-handoff design)",
         dict(engine_sleep_s=0.010, switch_interval_s=None)),
        ("PROPOSED (no sleep, default switchinterval ~5ms)",
         dict(engine_sleep_s=0.0, switch_interval_s=0.005)),
        ("PROPOSED (no sleep, switchinterval 1ms - smoother-update knob)",
         dict(engine_sleep_s=0.0, switch_interval_s=0.001)),
    ]
    rows = []
    for name, kw in scenarios:
        r = trial(**kw)
        rows.append((name, r))
        print(f"### {name}")
        for k, v in r.items():
            print(f"    {k:22} {v}")
        print()

    # Verdict. The owner's binding priority: the engine must NOT be throttled by the reader.
    # So PASS = (1) engine runs far faster with no sleep AND (2) the reader is not STARVED
    # (it keeps reading fresh snapshots and tracks the engine within a small TIME lag; a
    # stretched cadence / visual lag is owner-accepted, exact update-count is not the metric).
    current = rows[0][1]
    proposed = rows[1][1]
    engine_freed = proposed["bars_per_s"] > current["bars_per_s"] * 5
    time_staleness_ms = round(proposed["poll_staleness_bars"] / proposed["bars_per_s"] * 1000.0, 1)
    reader_not_starved = proposed["poll_updates_seen"] >= 10 and time_staleness_ms < 150.0
    print("=== VERDICT ===")
    print(f"engine freed (flat-out >> sleep design):  {engine_freed} "
          f"({proposed['bars_per_s']} vs {current['bars_per_s']} bars/s)")
    print(f"reader not starved (tracks engine live):  {reader_not_starved} "
          f"({proposed['poll_updates_seen']} reads, ~{time_staleness_ms}ms behind, "
          f"cadence {proposed['median_poll_gap_ms']}ms vs 50ms ideal)")
    print(f"GIL-HANDOFF SPIKE: {'PASS' if (engine_freed and reader_not_starved) else 'FAIL'}")


if __name__ == "__main__":
    main()
