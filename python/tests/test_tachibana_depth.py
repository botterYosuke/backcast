"""INV-T2-DEPTH — 立花 FD frame の板 (_extract_depth) 抽出契約 (findings/0009)。

skill (_extract_depth) と data-mapping。現挙動 (characterization) を pin:
- p_{row}_GBP{i}/GBV{i} (bid) と GAP{i}/GAV{i} (ask) を i=1..10 で走査。
- level は price と size の **両方が非空** のときだけ採用 (`if bp and bv`)。
  片方欠落の段は落ちる。空 size を float("") に渡すことは無い。
- bid も ask も空なら depth=None。発行ごとに sequence_id を +1。
片側欠落時の前値保持 / 0 補完などの「正しい」統合方式は一次資料から一意に
決まらないため、現挙動を契約として固定し、将来変更は実 payload 裏取りを要する。
"""
from __future__ import annotations

import math

from engine.exchanges.tachibana_ws_codec import FdFrameProcessor, _is_finite_quote

# _extract_depth は f"p_{row}_GBP{i}" 形式のキーを引く。row="1" → "p_1_GBP1"。
ROW = "1"
PFX = f"p_{ROW}"


def _proc() -> FdFrameProcessor:
    return FdFrameProcessor(row=ROW)


def _depth(fields: dict[str, str]) -> dict | None:
    # _extract_depth は I/O を持たない純メソッド。直接叩いて契約を固定する。
    return _proc()._extract_depth(fields, recv_ts_ms=111)


def test_depth_bid_and_ask_levels() -> None:
    fields = {
        f"{PFX}_GBP1": "2480", f"{PFX}_GBV1": "100",
        f"{PFX}_GAP1": "2481", f"{PFX}_GAV1": "200",
    }
    d = _depth(fields)
    assert d is not None
    assert d["bids"] == [{"price": "2480", "size": "100"}]
    assert d["asks"] == [{"price": "2481", "size": "200"}]
    assert d["recv_ts_ms"] == 111


def test_depth_skips_level_when_size_missing() -> None:
    """price はあるが size が空の段は落とす ('if bp and bv')。空を float に渡さない。"""
    fields = {
        f"{PFX}_GBP1": "2480", f"{PFX}_GBV1": "",   # size 欠落 → skip
        f"{PFX}_GAP1": "2481", f"{PFX}_GAV1": "200",
    }
    d = _depth(fields)
    assert d is not None
    assert d["bids"] == []
    assert d["asks"] == [{"price": "2481", "size": "200"}]


def test_is_finite_quote_implies_float_is_safe() -> None:
    """段フィルタの不変条件: ``_is_finite_quote(v)`` が True なら adapter (_cb) の
    ``float(v)`` は必ず raise せず有限値になる (#27)。Decimal ベースだと underscore 区切り
    ("1_" 等) を取りこぼし float() が ValueError を投げる乖離があったため float() で判定する。"""
    # 空欄・非数値・非有限・float() を割る underscore は弾く。
    for bad in ("", "—", "nan", "inf", "-inf", "_1", "1_", "1__0", "1e400"):
        assert _is_finite_quote(bad) is False, bad
    # guard True を返す値は float() が必ず通り有限。
    for good in ("2480", "0", "1e3", "  100  "):
        assert _is_finite_quote(good) is True, good
        assert math.isfinite(float(good))  # 不変条件: 割れない


def test_depth_skips_non_numeric_level() -> None:
    """truthy だが非数値/非有限な段値は段ごと落とす (#27 AC: 不正段は当該段を skip し
    feed を止めない)。これらを通すと _cb の float() (tachibana.py) が ValueError を投げ、
    tachibana_ws.py の recv loop は callback を try で包まないため落ちて接続断する。"""
    fields = {
        f"{PFX}_GBP1": "—", f"{PFX}_GBV1": "100",      # 非数値 price → skip
        f"{PFX}_GBP2": "2480", f"{PFX}_GBV2": "100",   # 正常 → 残る
        f"{PFX}_GAP1": "2481", f"{PFX}_GAV1": "nan",   # 非有限 size → skip
        f"{PFX}_GAP2": "2482", f"{PFX}_GAV2": "200",   # 正常 → 残る
    }
    d = _depth(fields)
    assert d is not None
    assert d["bids"] == [{"price": "2480", "size": "100"}]
    assert d["asks"] == [{"price": "2482", "size": "200"}]


def test_depth_keeps_zero_and_scientific_notation() -> None:
    """price='0' / 指数表記は有限数なので段は残す (price<=0 の弾きは DepthCache の
    gt=0 が担う二段防御。codec は『float() を割らない』ことだけ保証する)。"""
    fields = {
        f"{PFX}_GBP1": "0", f"{PFX}_GBV1": "100",
        f"{PFX}_GAP1": "1e3", f"{PFX}_GAV1": "200",
    }
    d = _depth(fields)
    assert d is not None
    assert d["bids"] == [{"price": "0", "size": "100"}]
    assert d["asks"] == [{"price": "1e3", "size": "200"}]


def test_depth_collects_up_to_10_levels() -> None:
    fields: dict[str, str] = {}
    for i in range(1, 11):
        fields[f"{PFX}_GBP{i}"] = str(2480 - i)
        fields[f"{PFX}_GBV{i}"] = str(10 * i)
    d = _depth(fields)
    assert d is not None
    assert len(d["bids"]) == 10
    assert d["asks"] == []


def test_depth_none_when_no_levels() -> None:
    assert _depth({}) is None


def test_sequence_id_increments_per_emit() -> None:
    proc = _proc()
    fields = {f"{PFX}_GBP1": "2480", f"{PFX}_GBV1": "100"}
    d1 = proc._extract_depth(fields, recv_ts_ms=1)
    d2 = proc._extract_depth(fields, recv_ts_ms=2)
    assert d1 is not None and d2 is not None
    assert d1["sequence_id"] == 1
    assert d2["sequence_id"] == 2
