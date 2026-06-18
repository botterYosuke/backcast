import marimo

app = marimo.App()


@app.cell
def _buy_while_affordable():
    # 現金ゲート付きサイジング: 買付余力が 1 株分を賄える間だけ 1 株ずつ買う。
    # buying_power は現金を使うほど縮むので、資金が尽きると自動的に止まる。
    bar = get_bar()  # noqa: F821
    pf = get_portfolio()  # noqa: F821
    qty = 1.0 if pf.buying_power >= bar.close else 0.0
    submit_market(qty)  # noqa: F821
    return (qty,)
