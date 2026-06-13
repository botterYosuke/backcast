"""INV-K4-PUSH-CODEC — kabu PUSH JSON frame → (trade, depth) 正規化の契約。

findings/0009。skill R8: PUSH は WebSocket frame=1 JSON (UTF-8)。
KabuPushFrameProcessor (kabusapi_ws_codec) の確定挙動を固定する。

- depth は best-effort で常に dict (bids/asks 空でも) を返す。
- trade は first/reset frame ではなく、volume diff>0 かつ side 確定時のみ。
- side: current_price>=prev_ask1→buy / <=prev_bid1→sell / 中間は prev_side 維持
  (prev_side None なら trade 抑制)。"unknown" side は発行しない。
- state 更新は trade 生成の後 (quote rule)。
- depth levels は Sell1..10 / Buy1..10、None entry / None Price|Qty は skip。
- ts_ns は CurrentPriceTime(JST) を UTC ns へ。None/空文字は ts_ns=None。
"""
from __future__ import annotations

from engine.exchanges.kabusapi_ws_codec import KabuPushFrameProcessor


def _lvl(price: float, qty: float) -> dict:
    return {"Price": price, "Qty": qty}


def test_first_frame_emits_depth_no_trade() -> None:
    proc = KabuPushFrameProcessor("5401")
    trade, depth = proc.process(
        {
            "CurrentPrice": 2480.0,
            "TradingVolume": 1000,
            "Sell1": _lvl(2481.0, 200),
            "Buy1": _lvl(2480.0, 100),
            "CurrentPriceTime": "2020-07-22T15:00:00+09:00",
        }
    )
    assert trade is None
    assert depth is not None
    assert depth["bids"] == [(2480.0, 100.0)]
    assert depth["asks"] == [(2481.0, 200.0)]


def test_trade_buy_when_price_lifts_offer() -> None:
    proc = KabuPushFrameProcessor("5401")
    proc.process({"CurrentPrice": 2480.0, "TradingVolume": 1000,
                  "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    trade, _ = proc.process({"CurrentPrice": 2481.0, "TradingVolume": 1100,
                             "Sell1": _lvl(2482.0, 50), "Buy1": _lvl(2481.0, 80)})
    assert trade is not None
    assert trade["aggressor_side"] == "buy"
    assert trade["size"] == 100.0  # volume diff
    assert trade["price"] == 2481.0


def test_trade_sell_when_price_hits_bid() -> None:
    proc = KabuPushFrameProcessor("5401")
    proc.process({"CurrentPrice": 2480.0, "TradingVolume": 1000,
                  "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    trade, _ = proc.process({"CurrentPrice": 2480.0, "TradingVolume": 1050,
                             "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 90)})
    assert trade is not None
    assert trade["aggressor_side"] == "sell"


def test_midpoint_first_trade_suppressed() -> None:
    """中間値で prev_side が無いとき trade は抑制される ('unknown' を出さない)。"""
    proc = KabuPushFrameProcessor("5401")
    proc.process({"CurrentPrice": 2480.5, "TradingVolume": 1000,
                  "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    trade, _ = proc.process({"CurrentPrice": 2480.5, "TradingVolume": 1100,
                             "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    assert trade is None


def test_volume_reset_reinitializes_no_trade() -> None:
    """TradingVolume が減少 (日替り/セッション境界) したら再初期化し trade なし。"""
    proc = KabuPushFrameProcessor("5401")
    proc.process({"CurrentPrice": 2480.0, "TradingVolume": 5000,
                  "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    trade, depth = proc.process({"CurrentPrice": 2480.0, "TradingVolume": 10,
                                 "Sell1": _lvl(2481.0, 200), "Buy1": _lvl(2480.0, 100)})
    assert trade is None
    assert depth is not None


def test_depth_collects_up_to_10_levels_and_skips_gaps() -> None:
    proc = KabuPushFrameProcessor("5401")
    frame = {"CurrentPrice": 100.0, "TradingVolume": 1}
    for i in range(1, 11):
        frame[f"Sell{i}"] = _lvl(100.0 + i, 10 * i)
        frame[f"Buy{i}"] = _lvl(100.0 - i, 10 * i)
    # 欠損段: Sell5 が None / Buy3 の Qty 欠落 → どちらも skip される。
    frame["Sell5"] = None
    frame["Buy3"] = {"Price": 97.0, "Qty": None}
    _, depth = proc.process(frame)
    assert depth is not None
    assert len(depth["asks"]) == 9  # Sell5 を除く
    assert len(depth["bids"]) == 9  # Buy3 を除く


def test_ts_ns_none_when_no_time() -> None:
    proc = KabuPushFrameProcessor("5401")
    _, depth = proc.process({"CurrentPrice": 100.0, "TradingVolume": 1,
                             "Buy1": _lvl(99.0, 10)})
    assert depth is not None
    assert depth["ts_ns"] is None


def test_ts_ns_parses_jst_to_utc_ns() -> None:
    proc = KabuPushFrameProcessor("5401")
    _, depth = proc.process({"CurrentPrice": 100.0, "TradingVolume": 1,
                             "Buy1": _lvl(99.0, 10),
                             "CurrentPriceTime": "2020-07-22T15:00:00+09:00"})
    assert depth is not None
    # 2020-07-22T15:00:00+09:00 == 2020-07-22T06:00:00Z
    assert depth["ts_ns"] == 1595397600 * 1_000_000_000
