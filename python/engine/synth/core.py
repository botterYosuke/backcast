"""engine.synth.core — 合成マーケットデータ生成器コア（#151 / findings 0120）.

テストから関数呼び出しで**任意期間・任意 OHLCV** の合成バーを生成する単一供給源。価格パターンは
``PricePath``（callable Protocol）で表現し、新パターンは**ビルダーを足すだけ**で拡張できる
（このコアは不変＝拡張性の floor・#154）。生成物は ``engine.kernel.duckdb_bars.Bar``
（nautilus-free dataclass）で、Replay/Auto どちらの経路にもそのまま流せる。

設計の正本は ``docs/findings/0120-synthetic-market-data-generator.md``。

ts 規約は ``duckdb_bars`` の private ヘルパを**再利用**して drift を構造的に断つ:
  * Daily  … 各営業日（平日）15:30 JST（東証引け）→ UTC ns
  * Minute … 各営業日 × セッションスロット ``HH:MM:59.999999`` JST（bar END ラベル）→ UTC ns
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import date as date_cls
from datetime import datetime, timedelta
from typing import Callable, Mapping, Optional, Protocol, Sequence, Union, runtime_checkable

from engine.kernel.duckdb_bars import (
    Bar,
    _date_to_ts_event_ns,  # ts 規約の単一真実源（drift 防止のため再利用）
    _JST,
    _minute_to_ts_event_ns,
    normalize_granularity,
)

# 新規シナリオの既定出来高（v19 系の慣習値に合わせる）。BarPoint.volume / synth_bars(volume=)
# / volume パスのいずれも未指定のときに使う。
DEFAULT_VOLUME = 1000.0

# 既定の東証セッション（分足スロット・bar END ラベルの HH:MM）。前場 09:00–11:30・後場 12:30–15:30。
# v19 系の疎なスロットは ``session=`` 上書きで byte-identical 再現する。
def _default_session() -> list[tuple[int, int]]:
    slots: list[tuple[int, int]] = []
    for hh in range(9, 12):  # 09:00..11:59 のうち
        for mm in range(60):
            if (hh, mm) <= (11, 30):
                slots.append((hh, mm))
    for hh in range(12, 16):  # 12:30..15:30
        for mm in range(60):
            if (12, 30) <= (hh, mm) <= (15, 30):
                slots.append((hh, mm))
    return slots


@dataclass(frozen=True)
class BarPoint:
    """1 足の値。``close`` だけ必須で、他は close（と前足終値）から賢く既定する。

    * ``open``   未指定 → **前足の終値**（最初の足は close）。``open != prev_close`` で gap を表現できる。
    * ``high``   未指定 → ``max(open, close)``
    * ``low``    未指定 → ``min(open, close)``
    * ``volume`` 未指定 → synth_bars の ``volume``（無ければ DEFAULT_VOLUME / volume パス値）
    """

    close: float
    open: Optional[float] = None
    high: Optional[float] = None
    low: Optional[float] = None
    volume: Optional[float] = None

    def resolve(self, prev_close: Optional[float], default_volume: float) -> tuple[float, float, float, float, float]:
        """(open, high, low, close, volume) を確定。bar の妥当性 ``low <= open,close <= high`` を保証。"""
        close = float(self.close)
        open_ = float(self.open) if self.open is not None else (float(prev_close) if prev_close is not None else close)
        hi = float(self.high) if self.high is not None else max(open_, close)
        lo = float(self.low) if self.low is not None else min(open_, close)
        # 妥当性を構造的に担保（high は全値の上限・low は下限）。
        hi = max(hi, open_, close)
        lo = min(lo, open_, close)
        vol = float(self.volume) if self.volume is not None else float(default_volume)
        return open_, hi, lo, close, vol


# PricePath = 拡張の要。``(bar_index, ts_event_ns, prev_close) -> BarPoint | float``。
# float を返せば close だけ指定の簡易形（BarPoint(close=x) と等価）。synth_bars コアは
# この Protocol しか知らない＝新ビルダーは PricePath を返す関数を足すだけ（コア無改変）。
PathReturn = Union[BarPoint, float, int]


@runtime_checkable
class PricePath(Protocol):
    def __call__(self, bar_index: int, ts_event_ns: int, prev_close: Optional[float]) -> PathReturn: ...


# volume も価格パスと同じ callable パターンで時間変化させられる（ADV/turnover ランキング用・#154）。
VolumePath = Callable[[int, int, Optional[float]], float]


def _as_bar_point(value: PathReturn) -> BarPoint:
    if isinstance(value, BarPoint):
        return value
    return BarPoint(close=float(value))


def calendar_grid(
    start: str,
    end: str,
    granularity: str,
    *,
    session: Optional[Sequence[tuple[int, int]]] = None,
) -> list[int]:
    """[start, end]（YYYY-MM-DD・両端含む）の **営業日（平日）** から ts_event_ns グリッドを作る。

    Daily  → 各日 1 点（15:30 JST）。
    Minute → 各日 × ``session`` スロット（既定＝東証セッション全分・``session`` で上書き）。
    """
    g = normalize_granularity(granularity)
    d0 = date_cls.fromisoformat(start)
    d1 = date_cls.fromisoformat(end)
    if d1 < d0:
        raise ValueError(f"end {end} precedes start {start}")
    days: list[date_cls] = []
    d = d0
    while d <= d1:
        if d.weekday() < 5:  # 平日のみ（土日を除外）
            days.append(d)
        d += timedelta(days=1)

    grid: list[int] = []
    if g == "Daily":
        for day in days:
            grid.append(_date_to_ts_event_ns(day))
    else:  # Minute
        # session は ts 昇順に正規化（出力の「各リストは ts 昇順」契約を担保。順不同で渡されても
        # prev_close/gap が時系列で狂わない）。_default_session は既に昇順。
        slots = sorted(session) if session is not None else _default_session()
        for day in days:
            for (hh, mm) in slots:
                grid.append(_minute_to_ts_event_ns(day, f"{hh}:{mm}"))
    return grid


def linear_grid(n: int, *, base: int, step: int) -> list[int]:
    """``[base, base+step, …]`` の等間隔グリッド（カレンダー非依存の合成バー用・midstream の 1s grid 等）。"""
    if n < 0:
        raise ValueError("n must be non-negative")
    return [int(base) + i * int(step) for i in range(n)]


def _resolve_for_symbol(value, symbol: str):
    """per-symbol 解決（path・volume 共通）: None→None / Mapping→``value.get(symbol)`` / それ以外→共有値。"""
    if value is None:
        return None
    if isinstance(value, Mapping):
        return value.get(symbol)
    return value


def synth_bars(
    symbols: Sequence[str],
    start: Optional[str] = None,
    end: Optional[str] = None,
    granularity: str = "Daily",
    *,
    path: Union[PricePath, Mapping[str, PricePath], None] = None,
    volume: Union[float, int, VolumePath, Mapping[str, Union[float, int, VolumePath]], None] = None,
    session: Optional[Sequence[tuple[int, int]]] = None,
    grid: Optional[Sequence[int]] = None,
    default_close: float = 1000.0,
) -> dict[str, list[Bar]]:
    """合成バーを ``{instrument_id: [Bar]}`` で返す（#151 中核）.

    時間グリッドは ``grid`` 明示があればそれを、無ければ ``calendar_grid(start, end, granularity,
    session)`` を使う。``path`` は単一 ``PricePath`` か ``{symbol: PricePath}``（銘柄ごとに別パス＝
    ランキングシナリオの肝）。``volume`` は定数 / ``VolumePath`` / dict。返り値の各リストは ts 昇順。

    ``path`` 未指定の銘柄は ``default_close`` の flat（定数）バスになる。
    """
    if grid is not None:
        ts_grid = [int(t) for t in grid]
    else:
        if start is None or end is None:
            raise ValueError("synth_bars requires start/end (or an explicit grid)")
        ts_grid = calendar_grid(start, end, granularity, session=session)

    out: dict[str, list[Bar]] = {}
    for sym in symbols:
        sym_path = _resolve_for_symbol(path, sym)
        vol_path = _resolve_for_symbol(volume, sym)
        bars: list[Bar] = []
        prev_close: Optional[float] = None
        for i, ts in enumerate(ts_grid):
            if sym_path is None:
                bp = BarPoint(close=float(default_close))
            else:
                bp = _as_bar_point(sym_path(i, ts, prev_close))
            # volume パスは BarPoint.volume を上書きしない限りの既定として効く。
            if bp.volume is None and vol_path is not None:
                vol_val = vol_path(i, ts, prev_close) if callable(vol_path) else float(vol_path)
                bp = BarPoint(close=bp.close, open=bp.open, high=bp.high, low=bp.low, volume=float(vol_val))
            o, h, l, c, v = bp.resolve(prev_close, DEFAULT_VOLUME)
            bars.append(Bar(instrument_id=sym, ts_event_ns=ts, open=o, high=h, low=l, close=c, volume=v))
            prev_close = c
        out[sym] = bars
    return out


def ts_to_jst_minute(ts_event_ns: int) -> int:
    """ts_event_ns → その JST 時刻の絶対分（hh*60+mm）。価格を時刻依存にする from_fn 用ヘルパ。"""
    dt = datetime.fromtimestamp(ts_event_ns / 1_000_000_000, tz=_JST)
    return dt.hour * 60 + dt.minute
