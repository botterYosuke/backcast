"""v19 morning ranker — marimo cell-DAG port (#76 S6b-α step2 / findings 0046 T7/T8).

A faithful reactive re-expression of ``V19MorningStrategy`` (the imperative kernel-native
v19). The imperative ``on_bar`` if/return chain — daily reset → exit → entry → snapshot —
maps to a single self-cycle ``_strategy`` cell using if/elif (the branches are mutually
exclusive per bar), so the entry bar is never accumulated and the decision reads only the
pre-entry morning (no look-ahead), exactly as the imperative path does.

Seams (all already host-provided — this file authors NO model I/O, sklearn, or lazy load):
  - ``get_bar()`` / ``get_portfolio()``        host-seeded per-bar drivers (bar + portfolio)
  - ``submit_market(qty, instrument_id=)``     S4 signed-qty injected action
  - ``score_v19_rows(rows)``                   host-injected SERVICE (= score_universe bound
                                               to the model; sklearn stays host-side, T1)
  - ``UNIVERSE`` / ``RS_REF``                  host-injected CONSTANTS (ordered universe + rs-ref)

The pure numeric logic (jst timing, features, build_rows, sizing) is imported from v19_core
— the SAME functions the imperative strategy calls — so the two paths share byte-identical
float math (the deterministic parity gate, test_v19_marimo_parity).

α scope: this is the parity-proven reference cell strategy. It is NOT yet the picker/canonical
production artifact and ships with no sidecar — the production scorer resolver (sidecar
scorer-spec → lazy joblib scorer + universe source) is a documented follow-up (T6/T9).
"""
import marimo

app = marimo.App()


@app.cell
def _config():
    # Author-owned strategy knobs (T3): these are v19's ctor params written as cell
    # constants. The host-injected UNIVERSE / RS_REF and the score_v19_rows service are
    # free refs. The deterministic parity gate pins these to the imperative twin's ctor.
    TOP_K = 5
    ENTRY_MINUTE = 10 * 60        # 10:00 JST
    EXIT_MINUTE = 14 * 60 + 55    # 14:55 JST
    ORDER_QTY = 100
    CASH_GATE = True
    SAFETY_MARGIN = 0.95
    ALLOC_POLICY = None
    LOT_SIZE = 1
    return (
        ALLOC_POLICY, CASH_GATE, ENTRY_MINUTE, EXIT_MINUTE,
        LOT_SIZE, ORDER_QTY, SAFETY_MARGIN, TOP_K,
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
        get_day, get_exited, get_placed, get_snaps,
        set_day, set_exited, set_placed, set_snaps,
    )


@app.cell
def _strategy(
    ALLOC_POLICY, CASH_GATE, ENTRY_MINUTE, EXIT_MINUTE, LOT_SIZE, ORDER_QTY,
    SAFETY_MARGIN, TOP_K, get_day, get_exited, get_placed, get_snaps,
    set_day, set_exited, set_placed, set_snaps,
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
        rows = v19_core.build_rows(snaps, UNIVERSE, RS_REF)  # noqa: F821  injected constants
        scores = score_v19_rows(rows)                        # noqa: F821  injected service
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
        # setter sees a new object (the reactive write), mirroring the spike.
        nxt = {k: list(v) for k, v in snaps.items()}
        nxt.setdefault(bar.instrument_id, []).append({
            "open": bar.open, "high": bar.high, "low": bar.low,
            "close": bar.close, "volume": bar.volume,
        })
        set_snaps(nxt)
    return
