import marimo

app = marimo.App()


@app.cell
def _state():
    # バーをまたいで値を覚えておく「フィードバック状態」は mo.state で持つ。
    # この cell は get_bar() を読まないので毎バー再実行されず、状態は走行中ずっと残る。
    import marimo as mo

    closes_get, closes_set = mo.state([])
    return closes_get, closes_set


@app.cell
def _sma(closes_get, closes_set):
    # 移動平均（SMA）クロス: close が直近 window 本の平均を上回れば建玉 10 株、割れば手仕舞い。
    bar = get_bar()  # noqa: F821
    window = 5

    # close>0 のバーだけ履歴に積む。戦略ロード時にエンジンは各 cell を「中立バー
    # （close=0）」で 1 度だけ試走するため、無条件に積むとその 0 が紛れ込む。
    hist = closes_get()
    if bar.close > 0.0:
        hist = (hist + [bar.close])[-window:]
        closes_set(hist)

    target = 0.0
    if len(hist) >= window:
        sma = sum(hist) / len(hist)
        target = 10.0 if bar.close > sma else 0.0

    pf = get_portfolio()  # noqa: F821
    submit_market(target - pf.position)  # noqa: F821  リバランス（差分発注）
    return (target,)
