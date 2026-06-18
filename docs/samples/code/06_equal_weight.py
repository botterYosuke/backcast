import marimo

app = marimo.App()


@app.cell
def _equal_weight():
    # 複数銘柄を等しく持つ: バーは銘柄ごとに順番に流れてくる。get_bar() で「いまどの
    # 銘柄か」を見て、その銘柄の現在ポジション net_qty(iid) との差分だけ発注する。
    # submit_market(qty, instrument_id=iid) で銘柄を指定できる（省略時は主銘柄）。
    bar = get_bar()  # noqa: F821
    pf = get_portfolio()  # noqa: F821
    iid = bar.instrument_id
    target_qty = 5.0  # 各銘柄を 5 株ずつ保有する単純な等株配分
    delta = target_qty - pf.net_qty(iid)
    submit_market(delta, instrument_id=iid)  # noqa: F821
    return (delta,)
