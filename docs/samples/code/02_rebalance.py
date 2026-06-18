import marimo

app = marimo.App()


@app.cell
def _rebal():
    # 目標ポジションへのリバランス: 「いくら買うか」ではなく「最終的に何株持ちたいか」を
    # 決め、現在ポジションとの差分だけ発注する。get_portfolio() は bar 入口時点
    # （fill 前 = 先読みなし）のスナップショット。
    bar = get_bar()  # noqa: F821
    pf = get_portfolio()  # noqa: F821
    target = 10.0 if bar.close > 1010.0 else (-10.0 if bar.close < 990.0 else 0.0)
    delta = target - pf.position  # 目標に届かせる差分（fill 前ポジション基準）
    submit_market(delta)  # noqa: F821
    return (delta,)
