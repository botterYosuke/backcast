"""v19 core — pure numeric logic shared by the imperative strategy and the marimo port.

#76 S6b-α step2 (findings 0046 T8). These functions are the SINGLE canonical source for
v19's JST timing arithmetic, feature engineering, cross-sectional scoring, and ranking-budget
sizing. Both ``strategies/v19/v19_morning.py`` (imperative) and ``v19_morning_cell.py`` (the
marimo cell-DAG port) call them, so the two paths share byte-identical float math —
re-deriving any of this on one side is what breaks the deterministic parity gate.

Pure given inputs: no I/O, no model load. ``score_universe`` takes the model as an argument
(duck-typed ``.predict``) and lazy-imports pandas; it is imported by the imperative strategy
and by the host scorer factory, but NEVER by the marimo cell — the cell calls a host-injected
``score_v19_rows`` service, so sklearn / model loading never leak into cell globals (T1/T6).
"""
from __future__ import annotations

import math
from typing import Any

# JST is a fixed UTC+9 with no DST, so the JST day-ordinal and minute-of-day are pure integer
# arithmetic from the bar's ts — no tz-aware datetime on the per-bar hot path.
_JST_OFFSET_SEC = 9 * 3600

# Feature column order is a CONTRACT: score_universe builds the model matrix as df[_FEATURES],
# so this order fixes which model input column each feature is. (The v19 replay stub model
# ranks by the LAST column = price_at_decision; reordering would silently change its ranking.)
_FEATURES = [
    "ret_cum", "ret_hi", "ret_lo", "ret_1", "ret_3", "ret_5", "ret_15", "ret_30",
    "close_loc_range", "dd_from_high", "range_frac", "vwap_dist", "rel_turnover",
    "log_turnover", "ret_std", "up_frac", "n_bars", "or_pos", "or_break_up",
    "gap", "rs_vs_1306", "tier_mid_small", "price_at_decision",
]


def jst_day_minute(ts_event_ns: int) -> tuple[int, int]:
    """UTC ns → (JST day-ordinal, JST minute-of-day). The day-ordinal is the daily-reset key.

    Flooring ns→s keeps the bar's ``:59.999999`` end label from rounding up into the next
    minute (which would mis-classify the entry bar).
    """
    jsecs = ts_event_ns // 1_000_000_000 + _JST_OFFSET_SEC
    return jsecs // 86_400, (jsecs % 86_400) // 60


def current_price(snaps: list[dict]) -> float | None:
    """Last snapshot close, or None when no snapshot has been collected yet."""
    return float(snaps[-1]["close"]) if snaps else None


def compute_features(
    snaps: list[dict], *, adv: float = 0.0, prev_close: float = 0.0,
    rs_snaps: "list[dict] | tuple" = (),
) -> dict | None:
    """Per-instrument feature vector from its morning OHLCV snapshots (verbatim v19 port).

    Pure: the caller passes the instrument's own snapshots, its ADV-baseline value, its
    prev-close value, and the rs-ref instrument's snapshots (for relative strength). Returns
    None when there are too few snapshots or a non-positive opening price.
    """
    if len(snaps) < 3:
        return None

    closes = [s["close"] for s in snaps]
    highs = [s["high"] for s in snaps]
    lows = [s["low"] for s in snaps]
    vols = [s.get("volume", 0) for s in snaps]

    o0 = snaps[0]["open"]
    if o0 <= 0:
        return None
    c_last = closes[-1]
    hi, lo = max(highs), min(lows)
    rng = hi - lo

    ret_cum = c_last / o0 - 1.0
    ret_hi = hi / o0 - 1.0
    ret_lo = lo / o0 - 1.0

    def ret_last_n(n: int) -> float:
        if len(closes) > n:
            base = closes[-(n + 1)]
            return c_last / base - 1.0 if base > 0 else 0.0
        return ret_cum

    ret_1 = ret_last_n(1)
    ret_3 = ret_last_n(3)
    ret_5 = ret_last_n(5)
    ret_15 = ret_last_n(15)
    ret_30 = ret_last_n(30)

    clr = (c_last - lo) / rng if rng > 0 else 0.5
    dd_from_high = (c_last - hi) / hi if hi > 0 else 0.0
    range_frac = rng / o0

    vals = [s.get("volume", 0) * s["close"] for s in snaps]
    tot_vol = sum(vols)
    vwap = sum(vals) / tot_vol if tot_vol > 0 else c_last
    vwap_dist = c_last / vwap - 1.0 if vwap > 0 else 0.0

    turnover = sum(vals)
    rel_turnover = turnover / adv if adv > 0 else 0.0
    log_turnover = math.log1p(turnover)

    import numpy as np

    c_arr = np.array(closes)
    if len(c_arr) >= 2:
        bar_rets = np.diff(c_arr) / c_arr[:-1]
        ret_std = float(np.std(bar_rets))
        up_frac = float(np.mean(np.diff(c_arr) > 0))
    else:
        ret_std = 0.0
        up_frac = 0.5

    or_snaps = snaps[:5]
    if or_snaps:
        orh = max(s["high"] for s in or_snaps)
        orl = min(s["low"] for s in or_snaps)
    else:
        orh, orl = hi, lo
    or_pos = (c_last - orl) / (orh - orl) if (orh - orl) > 0 else 0.5
    or_break_up = 1.0 if c_last > orh else 0.0

    gap = (o0 / prev_close - 1.0) if prev_close > 0 else 0.0

    rs = 0.0
    if rs_snaps and rs_snaps[0]["open"] > 0:
        ref_ret = rs_snaps[-1]["close"] / rs_snaps[0]["open"] - 1.0
        rs = ret_cum - ref_ret

    tier_mid_small = 0.0

    return {
        "ret_cum": ret_cum, "ret_hi": ret_hi, "ret_lo": ret_lo,
        "ret_1": ret_1, "ret_3": ret_3, "ret_5": ret_5,
        "ret_15": ret_15, "ret_30": ret_30,
        "close_loc_range": clr, "dd_from_high": dd_from_high,
        "range_frac": range_frac, "vwap_dist": vwap_dist,
        "rel_turnover": rel_turnover, "log_turnover": log_turnover,
        "ret_std": ret_std, "up_frac": up_frac, "n_bars": float(len(snaps)),
        "or_pos": or_pos, "or_break_up": or_break_up,
        "gap": gap, "rs_vs_1306": rs,
        "tier_mid_small": tier_mid_small, "price_at_decision": c_last,
    }


def build_rows(
    snapshots: dict, universe: "list[str] | tuple", rs_ref: str, *,
    adv_baseline: "dict | None" = None, prev_close: "dict | None" = None,
) -> dict:
    """Universe-ordered feature rows for scoring (the ordering CONTRACT — findings 0046 T2/T8).

    Iterate ``universe`` IN ORDER, skip the rs-ref instrument, compute each instrument's
    features, and drop instruments whose features are None (too few snapshots). Sharing this
    one assembler — not re-deriving the loop on each path — is what keeps the universe order /
    rs-ref skip / None-drop identical between the imperative strategy and the marimo cell, so
    top-k tie-breaks and stub-scorer parity hold.
    """
    adv_baseline = adv_baseline or {}
    prev_close = prev_close or {}
    rs_snaps = snapshots.get(rs_ref, [])
    rows: dict[str, dict] = {}
    for iid in universe:
        if iid == rs_ref:
            continue
        feat = compute_features(
            snapshots.get(iid, []),
            adv=adv_baseline.get(iid, 0),
            prev_close=prev_close.get(iid, 0),
            rs_snaps=rs_snaps,
        )
        if feat is not None:
            rows[iid] = feat
    return rows


def score_universe(rows: dict, model: Any) -> dict:
    """Cross-sectional z-norm + ``model.predict`` over universe-ordered feature rows.

    ``rows`` maps iid → feature dict (from build_rows). Standardize each feature across the
    universe (the model's expected preprocessing), then predict. The matrix column order is
    ``_FEATURES`` (a contract). Returns iid → score. The model is duck-typed (``.predict``);
    sklearn is never imported here — that lives behind the host scorer factory.
    """
    if not rows:
        return {}
    import pandas as pd

    df = pd.DataFrame(rows).T[_FEATURES]
    mu = df.mean()
    sigma = df.std() + 1e-9
    X = ((df - mu) / sigma).values
    scores_arr = model.predict(X)
    return {iid: float(s) for iid, s in zip(rows.keys(), scores_arr)}


def cash_aware_picks(
    picks: "list[str]", *, cash_gate: bool, order_qty: int, safety_margin: float,
    alloc_policy: "str | None", lot_size: int, buying_power: float, prices: dict,
    log=None,
) -> list[dict]:
    """Reduce rank-ordered picks under ``buying_power * safety_margin`` (verbatim v19 port).

    Gate off → every pick at ``order_qty``. Gate on → dispatch on ``alloc_policy``: None = v0
    cumulative-greedy (default, bit-exact); ``"A0_EQUAL_NOMINAL_E1"`` = equal-yen + lot-floor
    + +1-lot redistribute; unknown → warn (via ``log``) + v0 fallback. ``prices`` maps iid →
    price (None / <= 0 skips the pick).
    """
    if not cash_gate:
        return [{"iid": iid, "shares": int(order_qty)} for iid in picks]
    budget = float(buying_power) * float(safety_margin)
    if alloc_policy == "A0_EQUAL_NOMINAL_E1":
        return alloc_a0_equal_nominal_e1(picks, budget, lot_size, prices)
    if alloc_policy is not None and log is not None:
        log(f"v19 unknown alloc_policy={alloc_policy!r} — v0 fallback")
    # v0 path: cumulative-greedy, shares = order_qty.
    submissions: list[dict] = []
    cum = 0.0
    order_qty = int(order_qty)
    for iid in picks:
        price = prices.get(iid)
        if price is None or price <= 0:
            continue
        notional = float(order_qty) * float(price)
        if cum + notional <= budget:
            submissions.append({"iid": iid, "shares": order_qty})
            cum += notional
    return submissions


def alloc_a0_equal_nominal_e1(
    picks: "list[str]", budget: float, lot_size: int, prices: dict,
) -> list[dict]:
    """A0_EQUAL_NOMINAL_E1 (verbatim port of _blacksheep _alloc_a0_equal_nominal_e1).

    Pass 1: per_pick_budget = budget / K; for each iid in rank order, n_lots =
    floor(per_pick_budget / (price*lot_size)); commit n_lots*lot_size shares, push the
    leftover (incl. NO_PRICE / BELOW_1_LOT skips) into ``remainder``.
    Pass 2 (E1): while progress, add +1 lot to each affordable submission in rank order.
    ``prices`` maps iid → price (None / <= 0 = NO_PRICE).
    """
    picks = list(picks)
    K = len(picks)
    if K == 0:
        return []
    per_pick_budget = float(budget) / float(K)
    remainder = 0.0
    submissions: list[dict] = []
    for iid in picks:
        price = prices.get(iid)
        lot_value = float(price) * float(lot_size) if (price and price > 0) else 0.0
        if lot_value <= 0:
            remainder += per_pick_budget  # NO_PRICE
            continue
        n_lots = int(per_pick_budget // lot_value)
        if n_lots <= 0:
            remainder += per_pick_budget  # BELOW_1_LOT
            continue
        shares = int(n_lots * int(lot_size))
        submissions.append({"iid": iid, "shares": shares, "_price": float(price)})
        remainder += per_pick_budget - shares * float(price)
    # Pass 2 (E1): rank-order +1 lot redistribute.
    progress = True
    while progress and remainder > 0 and submissions:
        progress = False
        for sub in submissions:
            lot_value = sub["_price"] * float(lot_size)
            if lot_value <= 0:
                continue
            if remainder + 1e-9 >= lot_value:
                sub["shares"] = int(sub["shares"]) + int(lot_size)
                remainder -= lot_value
                progress = True
    return [{"iid": s["iid"], "shares": s["shares"]} for s in submissions]
