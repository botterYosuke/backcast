"""engine.synth.feed — 合成シナリオを Replay と Auto の両経路へ流す feed（#152 / findings 0120）.

* Replay … ``universe_bars`` で ``{iid:[Bar]}`` を merge 済み ``list[Bar]`` にする（``load_universe_bars``
  と同型。テストは ``monkeypatch.setattr(runner_mod, "load_universe_bars", ...)`` で注入）。
* Auto   … ``auto_trades`` で各 ``Bar`` を **TickBarAggregator が同じ OHLCV を再構成する ``TradesUpdate``
  列**に変換し、subscribe-gated な ``MockVenueAdapter.inject_tick`` へ流す。kabu/tachibana と同じ
  ``decode(rec) -> TradesUpdate | None`` 正規化規約の ``"synthetic"`` feed として ``_make_feed`` に合流する。

**parity の肝**: 同じ ``{iid:[Bar]}`` を両経路に与えれば、Auto の aggregator は Replay と同一の OHLCV を
復元する（ts ラベルは Replay=bar END / Auto=bucket 開始で異なるが time-of-day は保存）。
"""
from __future__ import annotations

from typing import Mapping, Optional, Sequence

from engine.kernel.duckdb_bars import Bar, granularity_to_interval_ns, merge_bars_by_ts
from engine.live.adapter import TradesUpdate


def universe_bars(bars_by_iid: Mapping[str, Sequence[Bar]]) -> list[Bar]:
    """Replay feed: ``{iid:[Bar]}`` → ``load_universe_bars`` と同型の merge 済み ``list[Bar]``。"""
    return merge_bars_by_ts(list(bars_by_iid.values()))


def _bar_to_ticks(bar: Bar, prev_close: Optional[float]) -> list[tuple[int, float, float, str]]:
    """1 Bar → aggregator が同じ OHLCV を復元する (ts_ns, price, size, side) 列。

    flat 足（O==H==L==C）は 1 tick。一般足は O,H,L,C の 4 tick を**同一 bucket 内**に順注入し、
    open=first / high=max / low=min / close=last（bar 妥当性 L<=O,C<=H）で OHLC を厳密復元。volume は
    分配して Σ=V を厳密保持。side は price>=直前で "buy"、それ以外 "sell"（決定論・値に意味なし）。
    """
    o, h, l, c, v = bar.open, bar.high, bar.low, bar.close, bar.volume
    ts = bar.ts_event_ns

    def _side(price: float, ref: Optional[float]) -> str:
        return "buy" if ref is None or price >= ref else "sell"

    if o == h == l == c:
        return [(ts, c, v, _side(c, prev_close))]

    # O,H,L,C を同一 ts（= 同一 bucket）に順注入。volume を 4 等分し最後で端数を吸収（Σ=V 厳密）。
    prices = [o, h, l, c]
    each = v / 4.0
    sizes = [each, each, each, v - each * 3.0]
    ticks: list[tuple[int, float, float, str]] = []
    ref = prev_close
    for price, size in zip(prices, sizes):
        ticks.append((ts, float(price), float(size), _side(float(price), ref)))
        ref = float(price)
    return ticks


def auto_trades(
    bars_by_iid: Mapping[str, Sequence[Bar]],
    *,
    interval_ns: Optional[int] = None,
    granularity: Optional[str] = None,
    flush: bool = True,
) -> list[TradesUpdate]:
    """Auto feed: ``{iid:[Bar]}`` → subscribe-gated adapter へ流す ``TradesUpdate`` 列（ts 昇順・stable）。

    ``interval_ns`` か ``granularity`` のどちらかで bucket 幅を与える（aggregator と同じ間隔）。
    ``flush=True`` なら全 instrument の**最後の実 bar の次 bucket** に 1 tick を足し、最終 bar を
    close させる（never-closed partial を作るだけで on_bar は駆動しない・churn の future_ts と同型）。
    """
    if interval_ns is None:
        if granularity is None:
            raise ValueError("auto_trades requires interval_ns or granularity")
        interval_ns = granularity_to_interval_ns(granularity)
    interval_ns = int(interval_ns)

    # (ts, seq, iid, price, size, side) を貯めて ts 昇順 stable sort（同 ts は構築順を保持）。
    rows: list[tuple[int, int, str, float, float, str]] = []
    seq = 0
    max_bucket = -1
    last_close: dict[str, float] = {}
    for iid, bars in bars_by_iid.items():
        prev_close: Optional[float] = None
        prev_bucket: Optional[int] = None
        for bar in bars:
            # interval_ns が bar 間隔より粗いと複数 bar が同一 bucket に潰れ、aggregator が 1 本に
            # 統合してしまう（silent データ欠落）。fail-loud で弾く（grid/interval ミスマッチ検出）。
            bucket = bar.ts_event_ns // interval_ns
            if prev_bucket is not None and bucket <= prev_bucket:
                raise ValueError(
                    f"auto_trades: {iid} の bar が bucket {bucket} で衝突（interval_ns={interval_ns} が "
                    f"bar 間隔より粗い）。granularity/interval_ns と grid の整合を確認"
                )
            prev_bucket = bucket
            for (ts, price, size, side) in _bar_to_ticks(bar, prev_close):
                rows.append((ts, seq, iid, price, size, side))
                seq += 1
                max_bucket = max(max_bucket, ts // interval_ns)
            prev_close = bar.close
            last_close[iid] = bar.close

    if flush and max_bucket >= 0:
        flush_ts = (max_bucket + 1) * interval_ns
        for iid, bars in bars_by_iid.items():
            if not bars:
                continue
            rows.append((flush_ts, seq, iid, last_close[iid], 0.0, "buy"))
            seq += 1

    rows.sort(key=lambda r: (r[0], r[1]))
    return [
        TradesUpdate(kind="trades", instrument_id=iid, ts_ns=ts, price=price, size=size, aggressor_side=side)
        for (ts, _seq, iid, price, size, side) in rows
    ]


class SyntheticFeed:
    """``_make_feed`` 抽象（venue-selectable）に合流する ``"synthetic"`` feed（#152）.

    kabu/tachibana と同じ ``decode(rec) -> TradesUpdate | None`` 規約を満たす薄い codec。rec は
    ``auto_trades`` が産んだ ``TradesUpdate`` を ``{"trade": tu}`` で包んだもの（合成は実 frame を
    持たないので codec は素通し）。subscribe-gated な adapter へ流すコードは kabu/tachibana と同一。
    """

    venue = "synthetic"

    def __init__(self, instrument_ids: Sequence[str]) -> None:
        self._ids = set(instrument_ids)

    def decode(self, rec: dict) -> Optional[TradesUpdate]:
        tu = rec.get("trade")
        if tu is None or tu.instrument_id not in self._ids:
            return None
        return tu

    @staticmethod
    def records(trades: Sequence[TradesUpdate]) -> list[dict]:
        """``auto_trades`` の出力を ``_make_feed`` 駆動ループが食う rec 列に包む。"""
        return [{"trade": tu} for tu in trades]
