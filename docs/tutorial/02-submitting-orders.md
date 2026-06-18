# 02 発注する

## 概念

marimo 戦略の発注は `submit_market(qty)` の 1 本だけです。**符号付き数量**で向きと大きさを
表します。

| `qty` | 動作 |
|---|---|
| `> 0` | `abs(qty)` 株を **成行買い** |
| `< 0` | `abs(qty)` 株を **成行売り** |
| `0`（`-0.0` 含む） | 何もしない（no-op） |
| `NaN` / `inf` | エラー（壊れた値が約定に流れないよう即停止） |

`qty` は「**このバーで売買する数量（デルタ）**」であって、目標保有数ではありません。目標で
考えたい場合は [次のレッスン](03-reading-portfolio.md) のリバランスを使います。

成行注文はそのバーの終値で約定します。

## コード

`close` がバンドを抜けた向きにシグナルを立て、`シグナル × ロット` を発注します。

```python
import marimo

app = marimo.App()


@app.cell
def _signal():
    bar = get_bar()
    signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
    qty = signal * 10.0  # +10=買い / -10=売り / 0=何もしない
    submit_market(qty)
    return (qty,)
```

完成形（コピー用）: [`01_threshold.py`](../samples/index.md#threshold)

!!! tip "`signal × size` のイディオム"
    向きを `signal`（-1/0/+1）、大きさを `size` で表し、掛けて `submit_market` に渡すと、
    分岐を書かずに「売り・買い・様子見」を 1 行で表現できます。

## 確認

価格がバンドを上下に跨ぐ期間で Replay すると、注文履歴に BUY と SELL の両方が出ます。
