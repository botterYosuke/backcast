"""v19 Replay core characterization (#72).

Two layers:

1. `test_v19_timing_logic_*` — fast, mount-free, deterministic. Synthetic minute bars
   over two business days drive the kernel-native v19 strategy with a stub model, locking
   the timing reconstruction agreed in findings 0029 decision 7:
     - daily reset → every business day trades (multiple round-trips, not one),
     - entry fires ONCE per day on the first bar at/after 10:00 JST,
     - exit flattens all positions on the first bar at/after 14:55 JST,
     - the buying-power gate trims picks to fit cash.

2. `test_v19_replay_real_data_roundtrips` — the honest AC: a headless AFK Replay over the
   real 2025-01-06..10 J-Quants DuckDB minute window with the real 1.8.0 model. Skipped
   when the owner's DuckDB mount is absent (repo convention).
"""
from __future__ import annotations

import os
import sys
from collections import defaultdict
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.duckdb_bars import Bar  # noqa: E402
from engine.kernel.orders import OrderFilled, OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy as KernelStrategy  # noqa: E402
from engine.strategy_runtime.strategy_loader import load as load_strategy  # noqa: E402
from engine.synth import BarPoint, from_fn, synth_bars, ts_to_jst_minute, universe_bars  # noqa: E402  #153

_JST = ZoneInfo("Asia/Tokyo")
_HERE = os.path.dirname(os.path.abspath(__file__))
_V19 = os.path.join(_HERE, "..", "strategies", "v19", "v19_morning.py")


class _CapSink:
    def __init__(self) -> None:
        self.fills: list[OrderFilled] = []

    def push_bar(self, bar) -> None: ...
    def push_order(self, fill) -> None: self.fills.append(fill)
    def push_portfolio(self, pf) -> None: ...
    def on_equity(self, ts_ms, equity, cash) -> None: ...
    def push_run_complete(self, run_id, summary) -> None: ...


class _StubModel:
    """Deterministic scorer: rank by the row's price_at_decision (higher → better)."""

    def predict(self, X):
        # X is z-scored; the last feature column is price_at_decision. Rank by it so the
        # top picks are stable regardless of model internals.
        return [float(row[-1]) for row in X]


# ---------------------------------------------------------------------------------------
# Layer 1 — fast deterministic timing logic
# ---------------------------------------------------------------------------------------

_RC_UNIVERSE = ["7203.TSE", "6758.TSE", "1306.TSE"]
_RC_PRICES = {"7203.TSE": 100.0, "6758.TSE": 120.0, "1306.TSE": 200.0}
_RC_SESSION = [(9, 55), (9, 56), (9, 57), (9, 58), (9, 59), (10, 0), (10, 30), (14, 55)]


def _synthetic_bars() -> list[Bar]:
    """Two business days, 3 instruments (A, B, rs-ref). Per day: five 09:5x snapshots, a
    10:00 entry bar, a 10:30 bar (must NOT re-enter), a 14:55 exit bar. Time-merged.

    #153: 旧アドホック生成（flat 足・vol=1000・px=prices[iid]+(分)*0.01+idx）を synth_bars + from_fn で
    再現（byte-identical）。ts 規約・スロット・価格式は据え置き。"""
    idx = {iid: i for i, iid in enumerate(_RC_UNIVERSE)}

    def _path(iid: str):
        def _f(i: int, ts: int, prev_close):
            px = _RC_PRICES[iid] + ts_to_jst_minute(ts) * 0.01 + idx[iid]
            return BarPoint(close=px, open=px, volume=1000.0)  # flat 足
        return from_fn(_f)

    by_iid = synth_bars(
        _RC_UNIVERSE, "2025-01-06", "2025-01-07", "Minute",
        session=_RC_SESSION, path={iid: _path(iid) for iid in _RC_UNIVERSE},
    )
    return universe_bars(by_iid)


def _make_v19(stub_model: bool = True):
    _module, _scn, cls = load_strategy(_V19, base_cls=KernelStrategy)
    strat = cls(instrument_id="7203.TSE")

    def fake_on_start() -> None:
        strat._instruments = ["7203.TSE", "6758.TSE", "1306.TSE"]
        strat._rs_ref = "1306.TSE"
        strat._adv_baseline = {}
        strat._prev_close = {}
        if stub_model:
            strat._model = _StubModel()

    strat.on_start = fake_on_start  # type: ignore[method-assign]
    return strat


def _fills_by_day(fills):
    out = defaultdict(lambda: {"BUY": 0, "SELL": 0})
    for f in fills:
        dt = datetime.fromtimestamp(f.ts_event_ns // 1_000_000_000, tz=timezone.utc).astimezone(_JST)
        out[dt.date()]["BUY" if f.side is OrderSide.BUY else "SELL"] += 1
    return out


def test_v19_timing_logic_daily_roundtrips(monkeypatch) -> None:
    bars = _synthetic_bars()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    strat = _make_v19()
    sink = _CapSink()
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=["7203.TSE", "6758.TSE", "1306.TSE"],
        granularity="Minute",
        start="2025-01-06",
        end="2025-01-07",
        initial_cash=1_000_000.0,
        strategy=strat,
        sink=sink,
    ).run()

    byday = _fills_by_day(sink.fills)
    # AC#3: each business day is an independent round-trip (daily reset re-enters).
    assert sorted(byday.keys()) == [
        datetime(2025, 1, 6).date(), datetime(2025, 1, 7).date()
    ]
    for day, counts in byday.items():
        # 2 non-rs-ref instruments, both affordable → entry buys both; rs-ref is never traded.
        assert counts["BUY"] == 2, (day, counts)
        # Exit flattens exactly what was opened (round-trip each day).
        assert counts["SELL"] == counts["BUY"], (day, counts)
    # Entry fires once per day (10:00), not again at 10:30: 2 buys/day, not 4.
    assert result.fills == 8
    # Book is flat at the end (every position closed at 14:55): realized P&L equals the
    # full cash delta only when nothing is left open.
    assert result.realized_pnl == pytest.approx(result.final_cash - 1_000_000.0)


def test_v19_entry_skipped_when_model_unavailable(monkeypatch) -> None:
    bars = _synthetic_bars()
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: list(bars))
    strat = _make_v19(stub_model=False)
    # Force the deferred load to fail → strategy survives, places no orders.
    monkeypatch.setattr(strat, "_ensure_model", lambda: False)
    sink = _CapSink()
    result = KernelRunner(
        data_root="/unused",
        instrument_ids=["7203.TSE", "6758.TSE", "1306.TSE"],
        granularity="Minute",
        start="2025-01-06",
        end="2025-01-07",
        initial_cash=1_000_000.0,
        strategy=strat,
        sink=sink,
    ).run()
    assert result.fills == 0
    assert result.final_cash == 1_000_000.0


# ---------------------------------------------------------------------------------------
# Layer 2 — real-data AFK Replay (skip-if-mount-absent)
# ---------------------------------------------------------------------------------------

def _minute_root() -> str | None:
    """Owner's DuckDB root via the same .env resolver the engine uses, if the minute mount
    is physically present."""
    from engine.paths import jquants_duckdb_root

    root = jquants_duckdb_root()
    if root is None:
        return None
    if not os.path.exists(os.path.join(str(root), "stocks_minute", "6920.duckdb")):
        return None
    return str(root)


@pytest.mark.skipif(
    _minute_root() is None,
    reason="J-Quants DuckDB minute mount absent (BACKCAST_JQUANTS_DUCKDB_ROOT)",
)
def test_v19_replay_real_data_roundtrips() -> None:
    root = _minute_root()
    _module, scn, cls = load_strategy(_V19, base_cls=KernelStrategy)
    strat = cls(instrument_id=scn["instruments"][0])
    sink = _CapSink()
    result = KernelRunner(
        data_root=root,
        instrument_ids=list(scn["instruments"]),
        granularity="Minute",
        start=scn["start"],
        end=scn["end"],
        initial_cash=scn["initial_cash"],
        strategy=strat,
        sink=sink,
    ).run()

    byday = _fills_by_day(sink.fills)
    # AC#2/#3: each of the 5 business days has an entry + a matching exit (round-trip),
    # and there is more than one round-trip (not a single-day artifact).
    assert len(byday) == 5, byday
    for day, counts in byday.items():
        assert counts["BUY"] >= 1, (day, counts)
        assert counts["SELL"] == counts["BUY"], (day, counts)
        # AC#4: the buying-power gate keeps picks within cash (top_k=5 but ¥1M / 100-share
        # lots affords fewer), so a day never submits more than top_k buys.
        assert counts["BUY"] <= 5, (day, counts)
    assert result.fills == sum(c["BUY"] + c["SELL"] for c in byday.values())
    assert result.fills > 0
