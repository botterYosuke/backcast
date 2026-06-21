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
    # constants. Scenario config (universe/start/end/cash/granularity) lives in the startup
    # panel — ADR-0016 D5 — and is NOT duplicated here. V19_UNIVERSE / V19_RS_REF /
    # V19_ADV_BASELINE / V19_PREV_CLOSE and score_v19_rows come from the _artifacts cell.
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
    bt,
    score_v19_rows,
):
    # #95 port: the cell drives the real engine via the injected ``bt`` handle (ADR-0016 D4).
    # ``bt.replay()`` streams every bar of the scenario window; per-day state (snapshots,
    # entry/exit flags, the queue of remaining picks) lives as plain Python locals — mo.state
    # is no longer needed because the loop body sees the same variables across bars.
    #
    # Multi-instrument port (ADR-0016 D4 lock — ``bt.submit_market(qty)`` targets ONLY the open
    # bar's instrument): v19's entry is conceptually "at 10:00, buy top-k". The bar stream is
    # time-merged so the top-k decision still happens on the first bar at minute >= 10:00 (every
    # instrument already has its snapshots through 09:59 — same no-look-ahead window as the
    # imperative v19). The picks become a ``pending_buys`` dict keyed by instrument_id; the loop
    # submits each pick when THAT instrument's bar arrives at minute >= 10:00. Exit at 14:55
    # works the same way — each held instrument is flattened when its >= 14:55 bar streams by.
    from strategies.v19 import v19_core

    cur_day = None
    snaps: dict = {}
    placed = False
    pending_buys: dict = {}

    for bar in bt.replay():
        day, minute = v19_core.jst_day_minute(bar.ts_event_ns)

        if day != cur_day:
            # Daily reset (v19 "one process per day"): clear the morning snapshots, the
            # entry flag, and any unsubmitted picks. Engine positions persist across days —
            # the exit loop below flattens what was bought during the same JST day.
            cur_day = day
            snaps = {}
            placed = False
            pending_buys = {}

        if placed and minute >= EXIT_MINUTE:
            # Exit: each held instrument is flattened when its own >= 14:55 bar streams in.
            # pf.positions is the live engine book; a 0 (or missing) entry means already flat.
            pf = bt.portfolio()
            held = pf.positions.get(bar.instrument_id, 0.0)
            if held > 0:
                bt.submit_market(-held)

        elif (not placed) and ENTRY_MINUTE <= minute < EXIT_MINUTE:
            # Entry: on the FIRST bar at/after 10:00 score the universe off the morning
            # snapshots (through 09:59 — this bar is NOT accumulated, so no look-ahead) and
            # queue the cash-aware top-k. ``placed`` is set first so the entry block never
            # re-fires (matches _enter). The current bar is itself a >= 10:00 bar, so if its
            # instrument is in the picks it submits immediately at the bottom of this block.
            placed = True
            rows = v19_core.build_rows(
                snaps, V19_UNIVERSE, V19_RS_REF,
                adv_baseline=V19_ADV_BASELINE,
                prev_close=V19_PREV_CLOSE,
            )
            scores = score_v19_rows(rows)
            if scores:
                top = sorted(scores, key=lambda k: scores[k], reverse=True)[:TOP_K]
                prices = (
                    {iid: v19_core.current_price(snaps.get(iid, [])) for iid in top}
                    if CASH_GATE else {}
                )
                pf = bt.portfolio()
                subs = v19_core.cash_aware_picks(
                    top, cash_gate=CASH_GATE, order_qty=ORDER_QTY,
                    safety_margin=SAFETY_MARGIN, alloc_policy=ALLOC_POLICY,
                    lot_size=LOT_SIZE, buying_power=pf.buying_power, prices=prices,
                )
                pending_buys = {sub["iid"]: float(sub["shares"]) for sub in subs}
            # Submit on THIS bar if its instrument is one of the picks (so the 10:00 entry
            # bar for a top-k instrument fills at its own 10:00 close — same per-bar contract
            # as the imperative v19 whose 10:00 submit fills against the same bar).
            qty = pending_buys.pop(bar.instrument_id, 0.0)
            if qty > 0:
                bt.submit_market(qty)

        elif placed and minute < EXIT_MINUTE:
            # Between entry and exit: drain queued buys as each picked instrument's bar
            # arrives. Buys that never see their instrument's bar before EXIT silently expire
            # — preferable to fighting the same flat at 14:55.
            qty = pending_buys.pop(bar.instrument_id, 0.0)
            if qty > 0:
                bt.submit_market(qty)

        elif (not placed) and minute < ENTRY_MINUTE:
            # Snapshot collection: pre-entry morning bars only — appended in-place because
            # snaps is a private local now (the mo.state copy-on-write was only needed when
            # reactive value semantics demanded a fresh dict for set_snaps to observe).
            snaps.setdefault(bar.instrument_id, []).append({
                "open": bar.open, "high": bar.high, "low": bar.low,
                "close": bar.close, "volume": bar.volume,
            })
    return


@app.cell
def _():
    return


if __name__ == "__main__":
    app.run()
