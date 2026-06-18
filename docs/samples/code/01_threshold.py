import marimo

app = marimo.App()


@app.cell
def _signal():
    # 閾値ルール: close が上band超で買い、下band割れで売り、間は何もしない。
    # submit_market(qty) は符号付き数量 — qty>0 で BUY、qty<0 で SELL、0 は no-op。
    bar = get_bar()  # noqa: F821
    signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
    qty = signal * 10.0
    submit_market(qty)  # noqa: F821
    return (qty,)
