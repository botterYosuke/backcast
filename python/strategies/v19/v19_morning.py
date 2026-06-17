# region region_001
"""v19 Morning Strategy — kernel-native port (backcast #72).

Faithful re-write (not a literal copy) of `_blacksheep/strategies/v19_live_morning.py`
for the backcast Execution Kernel (`engine.kernel.strategy.Strategy`, ADR-0006). v19 is a
morning momentum cross-sectional ranker: it collects 1-minute snapshots, scores the
universe with a trained HistGradientBoostingRegressor at 10:00 JST, market-buys the top-k,
and flattens everything at 14:55 JST.

The kernel has no clock/timer (only `on_bar`), so all timing is reconstructed from each
bar's `ts_event_ns` → JST (decision record: docs/findings/0029 decision 7):
  - Daily reset: when the JST date changes, per-day state is cleared so every business
    day trades (v19 lives "one process per day").
  - Entry: on the FIRST bar whose JST minute >= 10:00 (before appending it), score and
    market-buy the top-k. The bar stream is time-merged, so when the first 10:00 bar
    arrives every instrument already has its snapshots through 09:59 — the same decision
    window v19's 10:00 clock alert sees.
  - Snapshot collection: pre-entry morning bars only (JST minute < 10:00).
  - Exit: on the FIRST bar whose JST minute >= 14:55, market-sell all tracked positions.

Faithfulness is bounded (different data source + static prev_close): bit-identical replay
of the live strategy is NOT a goal; faithful logic + a result in the app is.

Order submission is market-only via `submit_market`; positions are tracked from
`on_order(OrderFilled)`. Buying-power gating uses `self.buying_power()` (#71). The heavy
model load (cold sklearn import ≈10s) is deferred to the first scoring so neither Replay
nor #74 Auto attach blocks on it.

scikit-learn is pinned to 1.8.x: the model is saved by 1.8.0 and fails to unpickle under
1.9.x (findings 0029 decision 3). The kernel/engine never import sklearn; it is a
strategy-only dependency loaded lazily here.
"""
from __future__ import annotations

import json
import math
import os

from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderFilled, OrderSide
from engine.kernel.strategy import Strategy

# The run scenario (universe / window / cash) lives in the co-located v19_morning.json
# sidecar `"scenario"` key — the engine-owned single source of truth (CONTEXT.md
# "scenario sidecar"). It is intentionally NOT duplicated here.

_FEATURES = [
    "ret_cum", "ret_hi", "ret_lo", "ret_1", "ret_3", "ret_5", "ret_15", "ret_30",
    "close_loc_range", "dd_from_high", "range_frac", "vwap_dist", "rel_turnover",
    "log_turnover", "ret_std", "up_frac", "n_bars", "or_pos", "or_break_up",
    "gap", "rs_vs_1306", "tier_mid_small", "price_at_decision",
]

# JST is a fixed UTC+9 with no DST, so the JST day-ordinal and minute-of-day are pure
# integer arithmetic from the bar's ts — no tz-aware datetime is built on the ~80k-call
# per-bar hot path. Flooring ns→s keeps the bar's `:59.999999` end label from rounding up
# into the next minute (which would mis-classify the entry bar).
_JST_OFFSET_SEC = 9 * 3600


def _jst_day_minute(ts_event_ns: int) -> tuple[int, int]:
    """UTC ns → (JST day-ordinal, JST minute-of-day). The day-ordinal is the daily-reset key."""
    jsecs = ts_event_ns // 1_000_000_000 + _JST_OFFSET_SEC
    return jsecs // 86_400, (jsecs % 86_400) // 60


class V19MorningStrategy(Strategy):
    """v19 morning momentum ranker, kernel-native (Replay + Auto share one API)."""

    def __init__(
        self,
        *,
        strategy_id: str = "",
        instrument_id: str = "",
        model_path: str = "artifacts/v19_live_model_o3histgb_10h00.joblib",
        universe_path: str = "artifacts/v19_live_universe.json",
        adv_path: str = "artifacts/v19_live_adv_baseline.json",
        prev_close_path: str = "artifacts/v19_live_prev_close.json",
        order_qty: str = "100",
        top_k: str = "5",
        entry_time: str = "10:00",
        exit_time: str = "14:55",
        cash_gate: str = "1",
        cash_safety_margin: str = "0.95",
        **params: str,
    ) -> None:
        super().__init__(strategy_id=strategy_id, instrument_id=instrument_id, **params)

        self._model_path = self._resolve(model_path)
        self._universe_path = self._resolve(universe_path)
        self._adv_path = self._resolve(adv_path)
        self._prev_close_path = self._resolve(prev_close_path)

        self._order_qty = int(order_qty)
        self._top_k = int(top_k)
        self._entry_minute = self._parse_hhmm(entry_time)
        self._exit_minute = self._parse_hhmm(exit_time)
        self._cash_gate = str(cash_gate).strip().lower() in ("1", "true", "yes", "on")
        self._cash_safety_margin = float(cash_safety_margin)

        # Artifacts loaded in on_start (cheap JSON); model deferred to first scoring.
        self._model = None
        self._instruments: list[str] = []
        self._rs_ref = "1306.TSE"
        self._adv_baseline: dict[str, float] = {}
        self._prev_close: dict[str, float] = {}

        # Per-day state (reset on JST date change).
        self._cur_day = None
        self._snapshots: dict[str, list[dict]] = {}
        self._placed = False
        self._exited = False
        self._positions: dict[str, float] = {}  # iid -> signed qty (tracked from fills)

    # ------------------------------------------------------------------ helpers
    @staticmethod
    def _resolve(p: str) -> str:
        if os.path.isabs(p):
            return p
        try:
            here = os.path.dirname(os.path.abspath(__file__))
        except NameError:
            here = os.getcwd()
        cand = os.path.join(here, p)
        return os.path.abspath(cand)

    @staticmethod
    def _parse_hhmm(hh_mm: str) -> int:
        h, m = (int(x) for x in hh_mm.split(":"))
        return h * 60 + m

    # ------------------------------------------------------------------ lifecycle
    def on_start(self) -> None:
        self.log(f"v19 on_start — model={self._model_path}")
        self._check_required_artifacts()
        with open(self._universe_path, encoding="utf-8") as f:
            udata = json.load(f)
        self._instruments = udata["instruments"]
        self._rs_ref = udata.get("rs_ref", "1306.TSE")
        try:
            with open(self._adv_path, encoding="utf-8") as f:
                self._adv_baseline = json.load(f)
        except Exception as exc:  # noqa: BLE001 — adv is optional (rel_turnover → 0)
            self.log(f"v19 adv load failed: {exc!r}")
        try:
            with open(self._prev_close_path, encoding="utf-8") as f:
                self._prev_close = json.load(f)
        except Exception as exc:  # noqa: BLE001 — prev_close optional (gap → 0)
            self.log(f"v19 prev_close load failed: {exc!r}")
        self.log(f"v19 universe={len(self._instruments)} rs_ref={self._rs_ref}")

    def _check_required_artifacts(self) -> None:
        """Fail-loud when model/universe are missing — no heavy import, just existence."""
        missing = [
            f"{label}={path}"
            for label, path in (("model", self._model_path), ("universe", self._universe_path))
            if not os.path.exists(path)
        ]
        if missing:
            raise RuntimeError(
                "v19 startup aborted: required artifact(s) not found: " + ", ".join(missing)
            )

    def _ensure_model(self) -> bool:
        """Deferred model load (first scoring). Returns False on failure (entry skipped)."""
        if self._model is not None:
            return True
        try:
            import joblib  # noqa: PLC0415 — heavy/cold import deferred off the attach path
            self._model = joblib.load(self._model_path)
            self.log(f"v19 model loaded (deferred): {self._model_path}")
            return True
        except Exception as exc:  # noqa: BLE001 — survive: log + skip entry, do not die
            self.log(f"v19 model load FAILED (deferred): {exc!r}")
            self._model = None
            return False

    # ------------------------------------------------------------------ per-bar
    def on_bar(self, bar: Bar) -> None:
        day, minute = _jst_day_minute(bar.ts_event_ns)
        if day != self._cur_day:
            self._reset_day(day)

        # Exit first: flatten at/after 14:55 (once per day).
        if self._placed and not self._exited and minute >= self._exit_minute:
            self._exit_all()
            return

        # Entry: score + buy top-k on the first bar at/after 10:00 (and before exit).
        if not self._placed and self._entry_minute <= minute < self._exit_minute:
            self._enter()
            return

        # Snapshot collection: pre-entry morning bars only.
        if not self._placed and minute < self._entry_minute:
            self._snapshots.setdefault(bar.instrument_id, []).append({
                "open": bar.open, "high": bar.high, "low": bar.low,
                "close": bar.close, "volume": bar.volume,
            })

    def _reset_day(self, day) -> None:
        self._cur_day = day
        self._snapshots = {}
        self._placed = False
        self._exited = False
        self._positions = {}

    def on_order(self, event) -> None:
        if isinstance(event, OrderFilled):
            signed = event.last_qty if event.side is OrderSide.BUY else -event.last_qty
            iid = event.instrument_id
            self._positions[iid] = self._positions.get(iid, 0.0) + signed
            if self._positions[iid] == 0.0:
                self._positions.pop(iid, None)

    # ------------------------------------------------------------------ entry/exit
    def _enter(self) -> None:
        self._placed = True
        if not self._ensure_model():
            return
        scores = self._score_instruments()
        if not scores:
            self.log("v19 no scores at entry — skip")
            return
        top_k = sorted(scores, key=lambda x: scores[x], reverse=True)[: self._top_k]
        submissions = self._cash_aware_picks(top_k)
        self.log(f"v19 entry top{self._top_k}={top_k} submit={len(submissions)}")
        for sub in submissions:
            self.submit_market(sub["iid"], OrderSide.BUY, int(sub["shares"]))

    def _exit_all(self) -> None:
        self._exited = True
        held = [(iid, qty) for iid, qty in self._positions.items() if qty > 0]
        self.log(f"v19 exit — flattening {len(held)} positions")
        for iid, qty in held:
            self.submit_market(iid, OrderSide.SELL, int(qty))

    # ------------------------------------------------------------------ sizing (v0)
    def _current_price(self, iid: str) -> float | None:
        snaps = self._snapshots.get(iid, [])
        return float(snaps[-1]["close"]) if snaps else None

    def _cash_aware_picks(self, picks: list[str]) -> list[dict]:
        """v0 cumulative-greedy reducer under buying_power * safety_margin (lot_size=1,
        shares=order_qty). Gate disabled → every pick at order_qty (findings 0029 dec.5)."""
        if not self._cash_gate:
            return [{"iid": iid, "shares": int(self._order_qty)} for iid in picks]
        budget = float(self.buying_power()) * float(self._cash_safety_margin)
        submissions: list[dict] = []
        cum = 0.0
        order_qty = int(self._order_qty)
        for iid in picks:
            price = self._current_price(iid)
            if price is None or price <= 0:
                continue
            notional = float(order_qty) * float(price)
            if cum + notional <= budget:
                submissions.append({"iid": iid, "shares": order_qty})
                cum += notional
        return submissions

    # ------------------------------------------------------------------ features (verbatim port)
    def _compute_features(self, iid: str) -> dict | None:
        snaps = self._snapshots.get(iid, [])
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
        adv = self._adv_baseline.get(iid, 0)
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

        prev_close = self._prev_close.get(iid, 0)
        gap = (o0 / prev_close - 1.0) if prev_close > 0 else 0.0

        rs_snaps = self._snapshots.get(self._rs_ref, [])
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

    def _score_instruments(self) -> dict[str, float]:
        import pandas as pd

        rows: dict[str, dict] = {}
        for iid in self._instruments:
            if iid == self._rs_ref:
                continue
            feat = self._compute_features(iid)
            if feat is not None:
                rows[iid] = feat

        if not rows:
            return {}

        df = pd.DataFrame(rows).T[_FEATURES]
        mu = df.mean()
        sigma = df.std() + 1e-9
        X = ((df - mu) / sigma).values
        scores_arr = self._model.predict(X)
        return {iid: float(s) for iid, s in zip(rows.keys(), scores_arr)}
# endregion region_001
