import marimo

app = marimo.App()


@app.cell
def _observe():
    # 最小の戦略: バーを読むだけで発注はしない（観察専用）。
    # get_bar() は「いまのバー」を返す host-seeded driver。1 つ以上の cell が
    # host driver を読むことが戦略の最低条件なので、これだけで有効な戦略になる。
    bar = get_bar()  # noqa: F821  host が注入する driver
    note = f"{bar.instrument_id} close={bar.close}"
    return (note,)
