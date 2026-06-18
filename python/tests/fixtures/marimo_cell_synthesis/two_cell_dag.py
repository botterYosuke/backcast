import marimo

__generated_with = "0.20.4"
app = marimo.App()


@app.cell
def _(get_bar):
    bar = get_bar()  # noqa: F821
    qty = (1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)) * 10.0
    return (qty,)


@app.cell
def _(qty, submit_market):
    submit_market(qty)  # noqa: F821
    return


if __name__ == "__main__":
    app.run()
