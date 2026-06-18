# 戦略を書く

Backcast の target authoring model は marimo cell-DAG 形式の Python 戦略です。移行期間中は従来の命令型戦略も互換性のために残りますが、新規作成は marimo 形式を使います。

## 基本形

```python
import marimo

app = marimo.App()

@app.cell
def _():
    bar = get_bar()
    pf = get_portfolio()
    return bar, pf
```

## 発注

戦略 cell から `submit_market(...)` を呼び出して成行注文を出します。

```python
@app.cell
def _():
    bar = get_bar()
    if should_buy(bar):
        submit_market(symbol=bar.symbol, side="BUY", qty=100)
```

## 保存

Strategy Editor の Save / Save As で `.py` ファイルとして保存します。Replay 設定は戦略ファイルと対応する sidecar に保存される場合があります。

## セル追加

Strategy Editor の cell 追加 UI がある場合は、そこから `@app.cell` の skeleton を挿入します。UI がない環境では、上記の形式を手で追加します。
