import marimo

__generated_with = "0.18.4"
app = marimo.App()


@app.cell
def _():
    # Backcast Notebook
    return


@app.cell
def _():
    return


@app.cell
def _():
    a=1233
    print(a)
    return (a,)


@app.cell
def _(a):
    b=a+222
    print(b)
    return


@app.cell
def _():
    return


if __name__ == "__main__":
    app.run()
