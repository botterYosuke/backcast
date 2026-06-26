import marimo

# Dynamic-universe LIVE strategy fixture (#141-145 / ADR-0031) for the kabu-mock live-churn gate.
#
# The SAME cell runs under Replay and Auto (ADR-0025). Here it is driven under Auto by
# test_kabu_live_universe_churn.py: it replays the scenario instruments' venue bars and edits the
# live universe MID-STREAM via ``bt.universe.*``:
#   * add  → the symbol joins the live subscription + chart cascade (S4: Registry.Changed → subscribe)
#   * remove → the symbol is unsubscribed and its chart despawns (S5)
# Under Auto these edits enqueue on the engine universe channel; the host drains them into
# runner.subscribe / runner.unsubscribe. The gate plays the host (C#) role and replays the real
# prod kabu capture through a MockVenueAdapter, so the point is NOT trading — it is that the live
# data pipeline (codec → aggregator → reducer) survives membership churn without a bug. No orders.
#
# Bar schedule (cell-driving symbols = scenario instruments 7203/8306; the added 9984/285A are
# data/chart subscriptions and do NOT drive the cell — the live driver filters bars to the run's
# initial instrument set):
#   bar 1: add 9984.TSE, add 285A.TSE      (mid-stream ADD: brings two symbols into the live feed)
#   bar 2: remove 8306.TSE                  (REMOVE a cell-driving symbol: its bars must stop)
#   bar 3: remove 9984.TSE                  (REMOVE a previously-added symbol: unsubscribe)
# Final universe → {7203.TSE, 285A.TSE}.

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _strategy(bt):
    # Plain-Python per-run state (the loop body sees it across bars) — no mo.state (#112 D4).
    n_bars = 0
    for bar in bt.replay():
        n_bars += 1
        if n_bars == 1:
            bt.universe.add("9984.TSE")
            bt.universe.add("285A.TSE")
        elif n_bars == 2:
            bt.universe.remove("8306.TSE")
        elif n_bars == 3:
            bt.universe.remove("9984.TSE")
    return


if __name__ == "__main__":
    app.run()
