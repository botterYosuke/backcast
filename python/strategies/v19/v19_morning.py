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
import os

from engine.kernel.duckdb_bars import Bar
from engine.kernel.orders import OrderFilled, OrderSide
from engine.kernel.strategy import Strategy
from strategies.v19 import v19_core

# The run scenario (universe / window / cash) lives in the co-located v19_morning.json
# sidecar `"scenario"` key — the engine-owned single source of truth (CONTEXT.md
# "scenario sidecar"). It is intentionally NOT duplicated here.
#
# v19's pure numeric logic (JST timing, feature engineering, cross-sectional scoring,
# ranking-budget sizing) lives in v19_core — the single canonical source shared with the
# marimo cell-DAG port so both paths keep byte-identical float math (#76 S6b-α step2 / T8).
# The methods below stay as thin instance-state adapters that delegate to v19_core.


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
        alloc_policy: str = "",
        lot_size: str = "1",
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
        # Alloc policy (#75): "" → None = v0 cumulative-greedy (default, bit-exact);
        # "A0_EQUAL_NOMINAL_E1" → equal-yen + lot-floor + +1-lot redistribute. _blacksheep
        # reads these from env (V19LIVE_ALLOC_POLICY/V19LIVE_LOT_SIZE); the kernel port takes
        # them as ctor params like every other v19 knob (findings 0029 dec.7).
        self._alloc_policy = str(alloc_policy).strip() or None
        self._lot_size = int(lot_size)

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
        day, minute = v19_core.jst_day_minute(bar.ts_event_ns)
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
    # These methods are thin instance-state adapters over the pure v19_core functions: they
    # read self._snapshots / knobs / buying_power and hand the values to v19_core, so the
    # numeric logic stays the single source shared with the marimo port (T8). Signatures are
    # preserved for the direct-call gates (test_v19_alloc_a0).
    def _current_price(self, iid: str) -> float | None:
        return v19_core.current_price(self._snapshots.get(iid, []))

    def _cash_aware_picks(self, picks: list[str]) -> list[dict]:
        """Reduce rank-ordered picks under buying_power * safety_margin (findings 0029 dec.5).

        Gate-off reads neither buying_power nor prices (preserved); gate-on prices every pick
        and delegates the policy dispatch to v19_core.cash_aware_picks."""
        prices = {iid: self._current_price(iid) for iid in picks} if self._cash_gate else {}
        buying_power = float(self.buying_power()) if self._cash_gate else 0.0
        return v19_core.cash_aware_picks(
            picks,
            cash_gate=self._cash_gate,
            order_qty=self._order_qty,
            safety_margin=self._cash_safety_margin,
            alloc_policy=self._alloc_policy,
            lot_size=self._lot_size,
            buying_power=buying_power,
            prices=prices,
            log=self.log,
        )

    def _alloc_a0_equal_nominal_e1(
        self, picks: list[str], budget: float, lot_size: int
    ) -> list[dict]:
        """A0_EQUAL_NOMINAL_E1 — instance adapter over v19_core (prices from self snapshots)."""
        prices = {iid: self._current_price(iid) for iid in picks}
        return v19_core.alloc_a0_equal_nominal_e1(picks, budget, lot_size, prices)

    # ------------------------------------------------------------------ features / scoring
    def _compute_features(self, iid: str) -> dict | None:
        return v19_core.compute_features(
            self._snapshots.get(iid, []),
            adv=self._adv_baseline.get(iid, 0),
            prev_close=self._prev_close.get(iid, 0),
            rs_snaps=self._snapshots.get(self._rs_ref, []),
        )

    def _score_instruments(self) -> dict[str, float]:
        rows = v19_core.build_rows(
            self._snapshots, self._instruments, self._rs_ref,
            adv_baseline=self._adv_baseline, prev_close=self._prev_close,
        )
        return v19_core.score_universe(rows, self._model)
# endregion region_001
