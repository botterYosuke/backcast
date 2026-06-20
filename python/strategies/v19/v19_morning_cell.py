import marimo

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _artifacts():
    # v19's scorer + universe artifacts, self-loaded from the cell-adjacent ``artifacts`` dir
    # (so the shipped cell + shipped artifacts run with NO sidecar scorer key — the cell owns
    # its own scoring inputs). ``V19_ARTIFACTS_DIR`` overrides the directory (the parity gate
    # points it at a tmp dir with a stub model + synthetic universe).
    # All imports are private to this cell (leading-_ for marimo temporaries, or scoped inside
    # the closure) so the cell exposes ONLY the scoring constants and the score_v19_rows
    # service — v19_core is also imported by _strategy and a top-level import here would be a
    # multi-define.
    import json as _json
    import os as _os
    from pathlib import Path as _Path
    from types import MappingProxyType as _MappingProxyType

    _base = _Path(
        _os.environ.get("V19_ARTIFACTS_DIR")
        or _Path(__file__).resolve().parent / "artifacts"
    )

    _universe_doc = _json.loads(
        (_base / "v19_live_universe.json").read_text(encoding="utf-8")
    )
    V19_UNIVERSE = tuple(_universe_doc["instruments"])
    V19_RS_REF = _universe_doc.get("rs_ref", "1306.TSE")
    V19_ADV_BASELINE = _MappingProxyType(
        _json.loads((_base / "v19_live_adv_baseline.json").read_text(encoding="utf-8"))
    )
    V19_PREV_CLOSE = _MappingProxyType(
        _json.loads((_base / "v19_live_prev_close.json").read_text(encoding="utf-8"))
    )

    _model_path = _base / "v19_live_model_o3histgb_10h00.joblib"
    _cache: dict = {}

    def score_v19_rows(rows: dict) -> dict:
        # Lazy joblib + sklearn unpickle on the FIRST score call (the entry bar) — keeps cold
        # compile and module load free of joblib/sklearn (mirrors the imperative v19's deferred
        # load). Cached so subsequent days reuse the same model. v19_core is imported inside
        # the closure to avoid a multi-define with _strategy's top-of-cell import.
        model = _cache.get("model")
        if model is None:
            import joblib  # noqa: PLC0415

            model = joblib.load(_model_path)
            _cache["model"] = model
        from strategies.v19 import v19_core  # noqa: PLC0415

        return v19_core.score_universe(rows, model)

    return V19_ADV_BASELINE, V19_PREV_CLOSE, V19_RS_REF, V19_UNIVERSE, score_v19_rows


@app.cell
def _config():
    # Author-owned strategy knobs (T3): these are v19's ctor params written as cell
    # constants. V19_UNIVERSE / V19_RS_REF / V19_ADV_BASELINE / V19_PREV_CLOSE and
    # score_v19_rows come from the _artifacts cell (read as free refs by _strategy).
    TOP_K = 5
    ENTRY_MINUTE = 10 * 60        # 10:00 JST
    EXIT_MINUTE = 14 * 60 + 55    # 14:55 JST
    ORDER_QTY = 100
    CASH_GATE = True
    SAFETY_MARGIN = 0.95
    ALLOC_POLICY = None
    LOT_SIZE = 1
    return (
        ALLOC_POLICY,
        CASH_GATE,
        ENTRY_MINUTE,
        EXIT_MINUTE,
        LOT_SIZE,
        ORDER_QTY,
        SAFETY_MARGIN,
        TOP_K,
    )


@app.cell
def _feedback():
    import marimo as mo

    # Bar-crossing feedback (D4 self-cycle): the morning-snapshot accumulator, the
    # once-per-day entry/exit flags, and the daily-reset key. All read + written by
    # _strategy (a state read+written by the same cell is the self-loop-skip form, so no
    # out-of-list cid fires). Created once cold — _feedback is an ancestor of _strategy,
    # not a per-bar hot cell, so the State objects persist across bars.
    get_day, set_day = mo.state(None)
    get_snaps, set_snaps = mo.state({})
    get_placed, set_placed = mo.state(False)
    get_exited, set_exited = mo.state(False)
    return (
        get_day,
        get_exited,
        get_placed,
        get_snaps,
        set_day,
        set_exited,
        set_placed,
        set_snaps,
    )


@app.cell
def _strategy(
    ALLOC_POLICY,
    CASH_GATE,
    ENTRY_MINUTE,
    EXIT_MINUTE,
    LOT_SIZE,
    ORDER_QTY,
    SAFETY_MARGIN,
    TOP_K,
    V19_ADV_BASELINE,
    V19_PREV_CLOSE,
    V19_RS_REF,
    V19_UNIVERSE,
    get_bar,
    get_day,
    get_exited,
    get_placed,
    get_portfolio,
    get_snaps,
    score_v19_rows,
    set_day,
    set_exited,
    set_placed,
    set_snaps,
    submit_market,
):
    from strategies.v19 import v19_core

    bar = get_bar()          # noqa: F821  host-seeded bar driver
    pf = get_portfolio()     # noqa: F821  host-seeded portfolio driver (positions + buying_power)
    day, minute = v19_core.jst_day_minute(bar.ts_event_ns)

    # Daily reset (v19 lives "one process per day"): on a JST date change clear the morning
    # snapshots and the entry/exit flags — exactly V19MorningStrategy._reset_day. Read the
    # feedback into locals so the branches below see the post-reset values.
    snaps = get_snaps()
    placed = get_placed()
    exited = get_exited()
    if day != get_day():
        set_day(day)
        snaps, placed, exited = {}, False, False
        set_snaps(snaps)
        set_placed(False)
        set_exited(False)

    if placed and (not exited) and minute >= EXIT_MINUTE:
        # Exit: flatten every long position at/after 14:55. Positions come from the portfolio
        # driver — the same pre-fill book the imperative tracks from fills.
        for iid, qty in pf.positions.items():
            if qty > 0:
                submit_market(-qty, instrument_id=iid)  # noqa: F821
        set_exited(True)
    elif (not placed) and ENTRY_MINUTE <= minute < EXIT_MINUTE:
        # Entry: on the first bar at/after 10:00, score the universe off the morning snapshots
        # (through 09:59 — this bar is NOT accumulated, so no look-ahead) and buy the
        # cash-aware top-k. set_placed first so a later bar never re-enters (matches _enter).
        set_placed(True)
        rows = v19_core.build_rows(
            snaps, V19_UNIVERSE, V19_RS_REF,            # noqa: F821  injected constants
            adv_baseline=V19_ADV_BASELINE,              # noqa: F821  (rel_turnover feature)
            prev_close=V19_PREV_CLOSE,                  # noqa: F821  (gap feature)
        )
        scores = score_v19_rows(rows)                   # noqa: F821  injected service
        if scores:
            top = sorted(scores, key=lambda k: scores[k], reverse=True)[:TOP_K]
            prices = (
                {iid: v19_core.current_price(snaps.get(iid, [])) for iid in top}
                if CASH_GATE else {}
            )
            subs = v19_core.cash_aware_picks(
                top, cash_gate=CASH_GATE, order_qty=ORDER_QTY, safety_margin=SAFETY_MARGIN,
                alloc_policy=ALLOC_POLICY, lot_size=LOT_SIZE, buying_power=pf.buying_power,
                prices=prices,
            )
            for sub in subs:
                submit_market(float(sub["shares"]), instrument_id=sub["iid"])  # noqa: F821
    elif (not placed) and minute < ENTRY_MINUTE:
        # Snapshot collection: pre-entry morning bars only. Copy-on-write so the mo.state
        # setter sees a new object (the reactive write): a shallow dict copy plus a fresh list
        # for just this instrument — the other instruments' lists are shared but never mutated
        # (append only ever builds a new list), so a large universe is not re-copied each bar.
        iid = bar.instrument_id
        nxt = dict(snaps)
        nxt[iid] = list(snaps.get(iid, [])) + [{
            "open": bar.open, "high": bar.high, "low": bar.low,
            "close": bar.close, "volume": bar.volume,
        }]
        set_snaps(nxt)
    return


@app.cell
def _():
    return


if __name__ == "__main__":
    app.run()
