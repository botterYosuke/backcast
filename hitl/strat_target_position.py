import marimo

app = marimo.App()


@app.cell
def _target_position():
    bar = get_bar()        # noqa: F821  host-seeded bar driver (S6a)
    pf = get_portfolio()   # noqa: F821  host-seeded PORTFOLIO driver — NEW this slice (#76)

    # Long-or-flat target-position with hysteresis (1306.TSE 2024 close ~2350..3070):
    #   close > 2800 → hold 10 units long
    #   close < 2600 → flat (0)
    #   2600..2800   → hold current position (reads pf.position → no churn in the band)
    target = 10.0 if bar.close > 2800.0 else (0.0 if bar.close < 2600.0 else pf.position)

    # delta = how much to trade THIS bar to reach target, off the PRE-FILL position.
    # If get_portfolio() leaked the POST-fill position (look-ahead), this would over-trade
    # and never sit still at the target — the visible no-look-ahead signal is "position
    # holds at the target until the band flips".
    delta = target - pf.position
    submit_market(delta)   # noqa: F821  S4 injected signed-qty adapter
    return (target, delta)
