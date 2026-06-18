# 戦略を書く

Backcast の戦略は marimo の cell-DAG 形式の Python です。移行期間中は従来の命令型
（imperative）戦略も互換のために残りますが、新規作成は marimo 形式を使います。

書き方を一から学ぶなら [チュートリアル](../tutorial/index.md)、すぐ動かすなら
[サンプル戦略](../samples/index.md) を見てください。このページは要点のまとめです。

## 基本形

```python
import marimo

app = marimo.App()


@app.cell
def _():
    bar = get_bar()        # いまのバー（OHLCV）
    pf = get_portfolio()   # fill 前のポートフォリオ
    return bar, pf
```

`get_bar` / `get_portfolio` / `submit_market` は、戦略実行時に Backcast が cell へ注入します
（import 不要）。

## 発注

`submit_market(qty)` に**符号付き数量**を渡します。`qty > 0` で買い、`qty < 0` で売り、
`0` は何もしません。`qty` は「このバーで売買するデルタ」です。

```python
@app.cell
def _signal():
    bar = get_bar()
    signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
    submit_market(signal * 10.0)
    return ()
```

銘柄を指定するときは `submit_market(qty, instrument_id="7203.T")`。詳しくは
[チュートリアル 02](../tutorial/02-submitting-orders.md) / [06](../tutorial/06-multi-instrument.md)。

## 保存とシナリオ

Strategy Editor の Save / Save As で `.py` として保存します。検証条件（期間・足種・銘柄・
初期資金）は戦略と同名のサイドカー `.json` に保存されます。詳しくは
[チュートリアル 05](../tutorial/05-scenario-universe.md)。

## imperative 形式

命令型の `Strategy` サブクラスは [チュートリアル付録](../tutorial/99-imperative.md) を参照
してください。
