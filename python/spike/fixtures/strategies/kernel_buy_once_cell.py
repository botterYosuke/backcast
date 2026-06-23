import marimo

# marimo-cell twin of kernel_buy_and_rest.py / kernel_on_start_buy.py (#112 ADR-0025 D4 / S6).
#
# Buys exactly one lot on the FIRST bar and never sells. The mock venue outcome decides the rest:
#   - ACCEPTED (filled_qty=0)  → a resting order (graceful-stop cancel path・run_mock_live).
#   - FILLED                   → the order reaches the venue full-chain (post-trade gate test).
#
# The cell model has no "on_start submission" — the submit happens on the first bt.replay() bar
# (the run is already RUNNING by then), which is the marimo-cell equivalent the imperative
# on_start fixtures expressed. Scenario lives in kernel_buy_once_cell.json (ADR-0016 D5).

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _strategy(bt):
    submitted = False
    for bar in bt.replay():
        if not submitted:
            submitted = True
            bt.submit_market(100)  # BUY 1 lot on the first bar
    return


if __name__ == "__main__":
    app.run()
