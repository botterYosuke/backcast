"""SYNTH-01..06 — 合成マーケットデータ生成器コア + Replay feed（#151 · findings 0120）.

engine.synth の生成器コア（synth_bars / BarPoint / PricePath / 4 ビルダー）と Replay feed を gate する。
RED→GREEN litmus は各 test の docstring に記す（production logic を壊すと必ず落ちる）。
"""
from __future__ import annotations

import os
import sys
from datetime import datetime
from zoneinfo import ZoneInfo

_PY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _PY)

import pytest  # noqa: E402

import engine.kernel.runner as runner_mod  # noqa: E402
from engine.kernel.orders import OrderSide  # noqa: E402
from engine.kernel.runner import KernelRunner  # noqa: E402
from engine.kernel.strategy import Strategy  # noqa: E402
from engine.synth import (  # noqa: E402
    BarPoint,
    PricePath,
    constant,
    explicit,
    from_fn,
    synth_bars,
    trend,
    universe_bars,
)

_JST = ZoneInfo("Asia/Tokyo")


@pytest.mark.scenario("SYNTH-01")
def test_daily_grid_weekday_close_label() -> None:
    """synth_bars Daily は [start,end] の平日のみを 15:30 JST 引けで {iid:[Bar]} 生成する。

    litmus: 週末を含める / 15:30 でない ts にすると ts 規約が崩れ RED。
    """
    # 2025-01-06(Mon)..01-12(Sun): 平日は 6,7,8,9,10 の 5 日（11 土・12 日は除外）。
    out = synth_bars(["7203.TSE"], "2025-01-06", "2025-01-12", "Daily", path=constant(100.0))
    bars = out["7203.TSE"]
    assert len(bars) == 5, "weekend bars leaked or weekdays dropped"
    for bar in bars:
        dt = datetime.fromtimestamp(bar.ts_event_ns / 1e9, tz=_JST)
        assert dt.weekday() < 5
        assert (dt.hour, dt.minute) == (15, 30)
    # 各日の OHLCV は constant の flat 足。
    assert all((b.open, b.high, b.low, b.close) == (100.0, 100.0, 100.0, 100.0) for b in bars)


@pytest.mark.scenario("SYNTH-02")
def test_minute_session_grid_and_override() -> None:
    """synth_bars Minute は東証セッション足、``session=`` でスロット上書きできる。

    litmus: session 上書きを無視して既定セッションを使うと本数が合わず RED。
    """
    full = synth_bars(["7203.TSE"], "2025-01-06", "2025-01-06", "Minute", path=constant(1.0))["7203.TSE"]
    # 前場 09:00–11:30 (151) + 後場 12:30–15:30 (181) = 332 本。
    assert len(full) == 332
    slots = [(9, 55), (9, 56), (10, 0), (14, 55)]
    sparse = synth_bars(
        ["7203.TSE"], "2025-01-06", "2025-01-06", "Minute", session=slots, path=constant(1.0)
    )["7203.TSE"]
    assert len(sparse) == len(slots)
    got = [tuple(datetime.fromtimestamp(b.ts_event_ns / 1e9, tz=_JST).timetuple()[3:5]) for b in sparse]
    assert got == slots
    # bar END ラベル（:59.999999）規約。
    first = datetime.fromtimestamp(sparse[0].ts_event_ns / 1e9, tz=_JST)
    assert (first.second, first.microsecond) == (59, 999999)

    # session が順不同で渡されても出力は ts 昇順（契約担保）。
    unsorted = synth_bars(
        ["7203.TSE"], "2025-01-06", "2025-01-06", "Minute", session=[(14, 55), (9, 55), (10, 0)],
        path=constant(1.0),
    )["7203.TSE"]
    ts_seq = [b.ts_event_ns for b in unsorted]
    assert ts_seq == sorted(ts_seq), "synth_bars must emit ts-ascending bars regardless of session order"


@pytest.mark.scenario("SYNTH-03")
def test_barpoint_open_default_expresses_gap() -> None:
    """BarPoint の OHLCV 既定: open 未指定→前足終値（gap 0）／trend(gap_pct) で寄り gap を表現できる。

    litmus: open 既定を close にすると gap=0 固定になり gap シナリオが死んで RED。
    """
    # close-only float 返し → open は前足終値（gap 0）。
    rising = synth_bars(
        ["X.TSE"], "2025-01-06", "2025-01-08", "Daily", path=from_fn(lambda i, ts, p: 100.0 + 10.0 * i)
    )["X.TSE"]
    assert rising[0].open == rising[0].close == 100.0  # 最初の足: open=close
    assert rising[1].open == 100.0 and rising[1].close == 110.0  # open=前足終値=100（gap 0）
    assert rising[1].high == 110.0 and rising[1].low == 100.0

    # trend(gap_pct=0.02): 各足の寄りに +2% gap → open/prev_close - 1 == 0.02。
    gapped = synth_bars(["X.TSE"], "2025-01-06", "2025-01-08", "Daily", path=trend(100.0, 0.0, gap_pct=0.02))["X.TSE"]
    assert gapped[1].open == pytest.approx(gapped[0].close * 1.02)
    assert gapped[1].open / gapped[0].close - 1.0 == pytest.approx(0.02)


@pytest.mark.scenario("SYNTH-04")
def test_four_deterministic_builders() -> None:
    """explicit / constant / trend / from_fn の 4 ビルダーで価格パスを宣言できる（trend は gap_pct/volume 制御）。

    litmus: trend の gap_pct/volume を無視すると本 assert が RED。
    """
    grid_args = (["X.TSE"], "2025-01-06", "2025-01-08", "Daily")
    exp = synth_bars(*grid_args, path=explicit([10.0, 11.0, 12.0], spread=1.0, volume=7.0))["X.TSE"]
    assert [b.close for b in exp] == [10.0, 11.0, 12.0]
    assert (exp[1].high, exp[1].low, exp[1].volume) == (12.0, 10.0, 7.0)  # spread=1, vol=7
    assert exp[1].open == 11.0  # explicit open="close" 既定

    con = synth_bars(*grid_args, path=constant(5.0, volume=3.0))["X.TSE"]
    assert all((b.open, b.high, b.low, b.close, b.volume) == (5.0, 5.0, 5.0, 5.0, 3.0) for b in con)

    tr = synth_bars(*grid_args, path=trend(100.0, 5.0, volume=9.0))["X.TSE"]
    assert [b.close for b in tr] == [100.0, 105.0, 110.0]
    assert all(b.volume == 9.0 for b in tr)

    ff = synth_bars(*grid_args, path=from_fn(lambda i, ts, p: BarPoint(close=2.0 * i, open=2.0 * i, volume=4.0)))["X.TSE"]
    assert [b.close for b in ff] == [0.0, 2.0, 4.0]
    assert all(b.volume == 4.0 for b in ff)

    # dict-path に無い銘柄は default_close の flat バス（path 未指定の fallback ＝ filler universe 用）。
    partial = synth_bars(
        ["X.TSE", "Y.TSE"], "2025-01-06", "2025-01-08", "Daily",
        path={"X.TSE": constant(7.0)}, default_close=55.0,
    )
    assert all(b.close == 7.0 for b in partial["X.TSE"])
    assert all((b.open, b.high, b.low, b.close) == (55.0, 55.0, 55.0, 55.0) for b in partial["Y.TSE"])


@pytest.mark.scenario("SYNTH-05")
def test_pricepath_protocol_extends_without_touching_core() -> None:
    """PricePath は callable Protocol＝生成器コアを変えずに新ビルダーを足せる（拡張性の floor）。

    litmus: synth_bars が具体ビルダー型に依存すると、この素の callable が通らず RED。
    """

    def my_builder(step: float) -> PricePath:
        # ユーザ定義ビルダー: core を一切 import / 改変せず PricePath を返すだけ。
        def _path(i: int, ts: int, prev_close):
            return BarPoint(close=1.0 + step * i, open=1.0 + step * i)
        return _path

    assert isinstance(my_builder(1.0), PricePath)  # runtime_checkable Protocol
    out = synth_bars(["X.TSE"], "2025-01-06", "2025-01-08", "Daily", path=my_builder(3.0))["X.TSE"]
    assert [b.close for b in out] == [1.0, 4.0, 7.0]

    # 素のラムダ（BarPoint も builder も使わず float を返すだけ）でも動く＝Protocol だけが契約。
    raw = synth_bars(["X.TSE"], "2025-01-06", "2025-01-08", "Daily", path=lambda i, ts, p: 42.0)["X.TSE"]
    assert [b.close for b in raw] == [42.0, 42.0, 42.0]


# ── SYNTH-06: Replay feed 経由で「価格設計が順位/選択を決める」決定論 ─────────────────────────
_A = "7203.TSE"
_C = "9984.TSE"


class _LeaderEntryStrategy(Strategy):
    """各 bar で最新 close 最大の銘柄が現在 bar の銘柄なら 1 lot 買う（synth_rank_cell と同セマンティクス）。"""

    def on_start(self) -> None:
        self.last_close: dict = {}
        self.bought: list = []

    def on_bar(self, bar) -> None:
        self.last_close[bar.instrument_id] = bar.close
        leader = max(self.last_close, key=lambda k: (self.last_close[k], k))
        if bar.instrument_id == leader and leader not in self.bought:
            self.submit_market(bar.instrument_id, OrderSide.BUY, 100)
            self.bought.append(leader)


def _run_replay(bars_by_iid, monkeypatch) -> list:
    """合成 {iid:[Bar]} を Replay feed（load_universe_bars 差し替え）で戦略に流し、買い銘柄列を返す。"""
    strat = _LeaderEntryStrategy(strategy_id="synth-rank", instrument_id=_A)
    monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a, **k: universe_bars(bars_by_iid))

    class _Sink:
        def __init__(self): self.buys: list = []
        def push_bar(self, bar): ...
        def push_order(self, fill):
            if fill.side is OrderSide.BUY:
                self.buys.append(fill.instrument_id)
        def push_portfolio(self, pf): ...
        def on_equity(self, ts_ms, equity, cash): ...
        def push_run_complete(self, run_id, summary): ...

    sink = _Sink()
    KernelRunner(
        data_root="/unused", instrument_ids=[_A, _C], granularity="Daily",
        start="2025-01-06", end="2025-01-10", initial_cash=10_000_000.0,
        strategy=strat, sink=sink,
    ).run()
    return sink.buys


@pytest.mark.scenario("SYNTH-06")
def test_price_design_drives_ranking_picks(monkeypatch) -> None:
    """合成シナリオを Replay feed 経由で戦略に流し、価格設計で順位/選択が決まることを assert する。

    A 下落・C 上昇で leadership が A→C に入れ替わると picks=[A,C]。A が常に優位なら picks=[A] のみ。
    litmus: ランキングを close 非依存にすると両シナリオで picks が同じになり RED。
    """
    # 順位入替シナリオ: A=7203 が 200→160 へ下落、C=9984 が 100→170 へ上昇（途中で C が A を抜く）。
    flip = synth_bars(
        [_A, _C], "2025-01-06", "2025-01-10", "Daily",
        path={_A: explicit([200, 190, 160, 150, 140]), _C: explicit([100, 130, 160, 165, 170])},
    )
    assert _run_replay(flip, monkeypatch) == [_A, _C], "rank flip should pick A then C"

    # A 優位シナリオ: A が常に C より高い → leadership 不動 → C は決して選ばれない。
    dominant = synth_bars(
        [_A, _C], "2025-01-06", "2025-01-10", "Daily",
        path={_A: constant(200.0), _C: constant(100.0)},
    )
    assert _run_replay(dominant, monkeypatch) == [_A], "A-dominant should pick only A"


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
