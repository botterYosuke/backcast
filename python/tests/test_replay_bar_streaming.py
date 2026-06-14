"""Regression gate (#29): the production Replay path must stream bars ONE-AT-A-TIME
during the run, not bulk-inject them after engine_run completes.

THE GAP (#29 AC③ "チャートが更新される" = bar-by-bar ライブ追従): start_engine used to
loop over bars_by_instrument and call apply_replay_event AFTER engine_run finished
(_backend_impl ~L850), so GetState polling saw the whole series appear at once at the
end. For live following, each bar must reach GetState as the streaming loop processes it.

THE SEAM: engine_runner.run() now takes an optional on_bar(bar, instrument_id) callback.
When provided, the RunBuffer adapter exposes get_extra_subscriptions() so replay_runner
subscribes a per-instrument bar handler that forwards each bar to on_bar — firing inside
the 1-bar-at-a-time streaming loop. This test pins that wiring (no full Nautilus backtest:
the adapter's subscription factory is deterministic and pure).

Pure-Python + fakes — runnable directly (`python tests/test_replay_bar_streaming.py`) or
via pytest.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.strategy_runtime.engine_runner import _RunBufferAdapter  # noqa: E402


class _FakeBuf:
    def write_fill(self, event: dict) -> None: ...
    def write_equity(self, event: dict) -> None: ...


def _subs(adapter, instruments):
    return adapter.get_extra_subscriptions(
        engine=None,
        instruments=instruments,
        granularity="Daily",
        strategy_id_str="s",
        cache=None,
        venue_str="TSE",
    ) or {}


def test_no_on_bar_means_no_extra_subscriptions() -> None:
    """Backward compatible: without on_bar the adapter subscribes nothing extra."""
    adapter = _RunBufferAdapter(_FakeBuf())
    assert _subs(adapter, ["1301.TSE"]) == {}


def test_on_bar_streams_per_instrument() -> None:
    got: list[tuple[object, str]] = []
    adapter = _RunBufferAdapter(_FakeBuf(), on_bar=lambda bar, iid: got.append((bar, iid)))

    subs = _subs(adapter, ["1301.TSE", "7203.TSE"])
    assert len(subs) == 2, f"expected one bar subscription per instrument, got {len(subs)}"

    sentinel = object()
    for handler in subs.values():
        handler(sentinel)

    assert len(got) == 2, "each per-instrument handler must forward exactly once"
    assert {iid for _, iid in got} == {"1301.TSE", "7203.TSE"}, "instrument ids not forwarded"
    assert all(bar is sentinel for bar, _ in got), "bar object not forwarded verbatim"


if __name__ == "__main__":
    try:
        test_no_on_bar_means_no_extra_subscriptions()
        test_on_bar_streams_per_instrument()
    except AssertionError as exc:
        print(f"[REPLAY BAR STREAMING FAIL] {exc}")
        sys.exit(1)
    except Exception as exc:  # AttributeError before the seam exists = RED
        print(f"[REPLAY BAR STREAMING FAIL] {type(exc).__name__}: {exc}")
        sys.exit(1)
    print("[REPLAY BAR STREAMING PASS] engine_run streams bars per-instrument via on_bar")
