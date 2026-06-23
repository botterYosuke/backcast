import marimo

# marimo-cell twin of kernel_spike_buy_sell.py (#112 ADR-0025 D4 / S3).
#
# Same deterministic plan as the imperative kernel-native fixture, but written as a marimo cell
# driving the injected ``bt`` handle (``for bar in bt.replay(): bt.submit_market(...)``). Under
# Replay it streams the historical window; under Auto the SAME loop blocks on the live rendezvous
# (D2 A-1) and submits to the venue. Scenario lives in the sidecar ``kernel_spike_buy_sell_cell.json``
# (ADR-0016 D5: the startup panel owns config, not the cell).
#
# Used by test_live_auto_lifecycle_inproc_server (the bridge-version lifecycle gate): the live run
# registers, starts (worker reaches bt.replay, then idles — no bars fed by the MOCK in that test),
# and stops cleanly via the sentinel.

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _config():
    BUY_AT_BAR = 3
    SELL_AT_BAR = 40
    TRADE_QTY = 100  # exactly one lot (lot_size=100)
    return BUY_AT_BAR, SELL_AT_BAR, TRADE_QTY


@app.cell
def _strategy(bt, BUY_AT_BAR, SELL_AT_BAR, TRADE_QTY):
    # Per-run state lives as plain Python locals (the loop body sees them across bars) — no mo.state.
    n_bars = 0
    bought = False
    sold = False
    for bar in bt.replay():
        n_bars += 1
        if (not bought) and n_bars == BUY_AT_BAR:
            bt.submit_market(TRADE_QTY)   # BUY 1 lot
            bought = True
        elif bought and (not sold) and n_bars == SELL_AT_BAR:
            bt.submit_market(-TRADE_QTY)  # SELL 1 lot (signed delta)
            sold = True
    return


if __name__ == "__main__":
    app.run()
