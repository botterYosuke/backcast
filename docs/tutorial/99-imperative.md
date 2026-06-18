# 付録: imperative 戦略

新規作成は marimo 形式を推奨しますが、互換のため命令型の `Strategy` サブクラスも使えます。
v19 など既存の実戦略はこの形式です。

## ライフサイクル

`Strategy` を継承し、必要なフックだけ実装します。

| フック | 呼ばれるタイミング |
|---|---|
| `on_start(self)` | 走り始めに 1 回 |
| `on_bar(self, bar)` | バーごとに 1 回 |
| `on_order(self, event)` | 約定・拒否などの注文イベント時 |
| `on_stop(self)` | 正常終了時に 1 回 |

## 発注とポートフォリオ

marimo の符号付き `submit_market(qty)` と違い、imperative では **`OrderSide` と正の数量**で
発注します。

| API | 用途 |
|---|---|
| `self.submit_market(iid, OrderSide.BUY/SELL, qty)` | 成行発注（`qty` は正の数） |
| `self.buying_power()` | 買付余力 |
| `self.portfolio_snapshot()` | 主銘柄の fill 前スナップショット |
| `self.log(msg)` | ログ出力 |

## コード

marimo 版 [02 の閾値ルール](02-submitting-orders.md) を imperative で書くとこうなります。

```python
from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy


class ThresholdStrategy(Strategy):
    def on_bar(self, bar) -> None:
        signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
        qty = signal * 10.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)
        elif qty < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(qty))
```

完成形（コピー用）: [`99_imperative.py`](../samples/index.md#imperative)

!!! note "どちらを使うべきか"
    リアクティブに書け、状態管理も marimo に任せられる cell-DAG 形式が新規の既定です。
    imperative は、フック単位の細かい制御が要るときや既存戦略の保守に使います。
