# 03 ポートフォリオを読む

## 概念

いまの建玉や現金は `get_portfolio()` で読めます。返るのはそのバー入口時点の
**スナップショット（不変）**です。

| フィールド | 意味 |
|---|---|
| `position` | 主銘柄の符号付き保有数（買い建て +、売り建て -） |
| `positions` | 建玉のある銘柄の保有数（`{銘柄: 数量}`。建玉ゼロの銘柄は含まれない） |
| `cash` | 現金残高 |
| `buying_power` | 買付余力（現金口座では `cash` と同じ） |
| `equity` | 時価評価額（現金 + 建玉の時価） |
| `realized_pnl` | 確定損益 |
| `net_qty(iid)` | 指定銘柄の保有数を返すヘルパー |

## 目標ポジションへのリバランス

「何株売買するか」より「最終的に何株持ちたいか（目標）」で考えるほうが多くの戦略は
書きやすくなります。目標と現在ポジションの**差分**を発注すれば、行き過ぎや二重発注を
避けられます。

```python
import marimo

app = marimo.App()


@app.cell
def _rebal():
    bar = get_bar()
    pf = get_portfolio()
    target = 10.0 if bar.close > 1010.0 else (-10.0 if bar.close < 990.0 else 0.0)
    delta = target - pf.position  # 目標に届かせる差分
    submit_market(delta)
    return (delta,)
```

完成形（コピー用）: [`02_rebalance.py`](../samples/index.md#rebalance)

## 買付余力でサイジングする

`buying_power` を見れば「買える間だけ買う」資金管理ができます。現金を使うほど余力が縮むため、
資金が尽きると自然に止まります。

```python
@app.cell
def _buy_while_affordable():
    bar = get_bar()
    pf = get_portfolio()
    qty = 1.0 if pf.buying_power >= bar.close else 0.0
    submit_market(qty)
    return (qty,)
```

完成形（コピー用）: [`03_cash_gate.py`](../samples/index.md#cash-gate)

## 確認

リバランス版は、目標が変わらない限り追加発注しません。余力版は、初期資金を小さくすると
数回買って止まります。
