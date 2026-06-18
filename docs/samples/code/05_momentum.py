import marimo

app = marimo.App()


@app.cell
def _state():
    # 直近 lookback 本の終値履歴をフィードバック状態として保持する。
    import marimo as mo

    hist_get, hist_set = mo.state([])
    return hist_get, hist_set


@app.cell
def _momentum(hist_get, hist_set):
    # モメンタム: lookback 本前と比べて上昇していれば建玉、下落していれば手仕舞い。
    bar = get_bar()  # noqa: F821
    lookback = 20

    # close>0 のバーだけ履歴に積む（戦略ロード時の中立バー close=0 を混ぜない）。
    hist = hist_get()
    if bar.close > 0.0:
        hist = (hist + [bar.close])[-(lookback + 1):]
        hist_set(hist)

    target = 0.0
    if len(hist) > lookback and hist[0] > 0.0:
        ret = bar.close / hist[0] - 1.0  # lookback 本前からの騰落率
        target = 10.0 if ret > 0.0 else 0.0

    pf = get_portfolio()  # noqa: F821
    submit_market(target - pf.position)  # noqa: F821
    return (target,)
