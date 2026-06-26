import marimo

# Synthetic ranking strategy cell (#151 AC#4 / #152 parity · findings 0120).
#
# The SAME cell runs under Replay (``for bar in bt.replay()``) and Auto (LiveCellBridge), driven by
# test_synth_auto_parity.py. It is PRICE-DRIVEN and venue/artifact-free: each bar it ranks every
# instrument it has seen by latest close and, when the current bar IS the leader and that leader has
# not been bought yet, buys one lot. So the SEQUENCE of picks is decided purely by the price design:
#   * a flat scenario where A dominates → picks = [A] (leadership never moves).
#   * a scenario where A falls and C rises → leadership flips A→C → picks = [A, C].
# Deterministic tie-break: ``max`` key = (close, instrument_id). Scenario lives in the sidecar
# ``synth_rank_cell.json`` (ADR-0016 D5: the startup panel owns config, not the cell).

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _strategy(bt):
    # Plain-Python per-run state (the loop body sees it across bars) — no mo.state (#112 D4).
    last_close = {}
    bought = []
    for bar in bt.replay():
        last_close[bar.instrument_id] = bar.close
        leader = max(last_close, key=lambda k: (last_close[k], k))
        if bar.instrument_id == leader and leader not in bought:
            bt.submit_market(100)  # BUY one lot of the current leader
            bought.append(leader)
    return


if __name__ == "__main__":
    app.run()
